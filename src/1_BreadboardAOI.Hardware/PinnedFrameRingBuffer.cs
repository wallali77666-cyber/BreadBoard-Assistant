// ============================================================================
// FILE: src/1_BreadboardAOI.Hardware/PinnedFrameRingBuffer.cs
// PURPOSE: Zero-copy, lock-free 3-slot triple buffer for 1080p camera frames.
//          Slots are allocated once with NativeMemory.AlignedAlloc (64-byte
//          aligned for AVX2) and VirtualLock-ed into physical RAM.
// THREADING: Exactly ONE producer (camera capture thread) and ONE consumer
//            (vision/DRC thread). No locks, no allocations on the hot path.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace BreadboardAOI.Hardware;

/// <summary>
/// Triple-buffered native frame ring. The producer writes into any free slot and
/// publishes it; the consumer always sees the newest complete frame. If all three
/// slots are busy (consumer holding one, producer writing one, newest pending) the
/// producer drops its frame instead of blocking the 60 FPS capture loop.
/// </summary>
public sealed class PinnedFrameRingBuffer : IDisposable
{
    private const int SlotFree = 0;
    private const int SlotWriting = 1;
    private const int SlotReady = 2;
    private const int SlotReading = 3;

    private const int DefaultSlotCount = 3;
    private const int Alignment = 64; // AVX2 cache-line alignment

    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly int _bytesPerPixel;
    private readonly int _frameBytes;
    private readonly int _slotCount;
    private readonly IntPtr[] _slotPointers;
    private readonly int[] _slotStates;
    private readonly long[] _slotSequence;
    private readonly bool[] _slotLocked;

    private int _publishedSlot = -1;
    private long _publishCounter;
    private long _droppedWrites;
    private bool _disposed;

    public PinnedFrameRingBuffer(int width, int height, int bytesPerPixel = 4, int slotCount = DefaultSlotCount)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Frame width must be positive.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "Frame height must be positive.");
        if (bytesPerPixel is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(bytesPerPixel), bytesPerPixel, "Bytes per pixel must be 1..8.");
        if (slotCount < 2)
            throw new ArgumentOutOfRangeException(nameof(slotCount), slotCount, "A ring buffer needs at least 2 slots.");

        _width = width;
        _height = height;
        _bytesPerPixel = bytesPerPixel;
        _stride = checked(width * bytesPerPixel);
        _frameBytes = checked(_stride * height);
        _slotCount = slotCount;
        _slotPointers = new IntPtr[slotCount];
        _slotStates = new int[slotCount];
        _slotSequence = new long[slotCount];
        _slotLocked = new bool[slotCount];

        int allocated = 0;
        try
        {
            for (int i = 0; i < slotCount; i++)
            {
                unsafe
                {
                    void* memory = NativeMemory.AlignedAlloc((nuint)_frameBytes, Alignment);
                    if (memory is null)
                        throw new OutOfMemoryException($"Unable to allocate {_frameBytes} byte frame slot {i}.");

                    NativeMemory.Clear(memory, (nuint)_frameBytes);
                    _slotPointers[i] = (IntPtr)memory;
                }

                _slotLocked[i] = CpuAffinityManager.TryLockMemory(_slotPointers[i], (nuint)_frameBytes);
                allocated++;
            }
        }
        catch
        {
            for (int i = 0; i < allocated; i++)
                FreeSlot(i);
            throw;
        }
    }

    public int Width => _width;

    public int Height => _height;

    public int Stride => _stride;

    public int BytesPerPixel => _bytesPerPixel;

    public int FrameBytes => _frameBytes;

    public int SlotCount => _slotCount;

    /// <summary>Total frames published since construction.</summary>
    public long PublishedFrames => Interlocked.Read(ref _publishCounter);

    /// <summary>Frames dropped because all slots were busy (producer must never block).</summary>
    public long DroppedWrites => Interlocked.Read(ref _droppedWrites);

    /// <summary>True when every slot was successfully VirtualLock-ed into physical RAM.</summary>
    public bool AllSlotsLocked
    {
        get
        {
            for (int i = 0; i < _slotCount; i++)
            {
                if (!_slotLocked[i])
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Claims a free slot for the producer. Dispose (or Publish) the lease; failing
    /// to do either leaks a slot until the next process restart.
    /// </summary>
    public unsafe bool TryBeginWrite(out FrameWriteLease lease)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        for (int i = 0; i < _slotCount; i++)
        {
            if (Interlocked.CompareExchange(ref _slotStates[i], SlotWriting, SlotFree) == SlotFree)
            {
                lease = new FrameWriteLease(
                    this,
                    i,
                    (byte*)_slotPointers[i],
                    _frameBytes,
                    _stride,
                    _width,
                    _height);
                return true;
            }
        }

        Interlocked.Increment(ref _droppedWrites);
        lease = default;
        return false;
    }

    /// <summary>Grabs the newest published frame for the consumer.</summary>
    public unsafe bool TryReadLatest(out FrameReadLease lease)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int slot = Volatile.Read(ref _publishedSlot);
        if (slot < 0)
        {
            lease = default;
            return false;
        }

        if (Interlocked.CompareExchange(ref _slotStates[slot], SlotReading, SlotReady) != SlotReady)
        {
            lease = default;
            return false;
        }

        lease = new FrameReadLease(
            this,
            slot,
            (byte*)_slotPointers[slot],
            _frameBytes,
            _stride,
            _width,
            _height,
            _slotSequence[slot]);
        return true;
    }

    internal void PublishSlot(int slotIndex)
    {
        if ((uint)slotIndex >= (uint)_slotCount)
            return;

        if (Volatile.Read(ref _slotStates[slotIndex]) != SlotWriting)
            return;

        _slotSequence[slotIndex] = Interlocked.Increment(ref _publishCounter);
        Volatile.Write(ref _slotStates[slotIndex], SlotReady);

        int previous = Interlocked.Exchange(ref _publishedSlot, slotIndex);
        if (previous >= 0 && previous != slotIndex)
        {
            // If a consumer is mid-read the CAS fails and the consumer frees the
            // slot when it disposes its lease. Otherwise the slot returns to the pool.
            Interlocked.CompareExchange(ref _slotStates[previous], SlotFree, SlotReady);
        }
    }

    internal void AbortWrite(int slotIndex)
    {
        if ((uint)slotIndex >= (uint)_slotCount)
            return;

        Interlocked.CompareExchange(ref _slotStates[slotIndex], SlotFree, SlotWriting);
    }

    internal void ReleaseRead(int slotIndex)
    {
        if ((uint)slotIndex >= (uint)_slotCount)
            return;

        Interlocked.CompareExchange(ref _slotStates[slotIndex], SlotFree, SlotReading);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        for (int i = 0; i < _slotCount; i++)
            FreeSlot(i);
    }

    private unsafe void FreeSlot(int index)
    {
        IntPtr pointer = _slotPointers[index];
        if (pointer == IntPtr.Zero)
            return;

        if (_slotLocked[index])
        {
            CpuAffinityManager.TryUnlockMemory(pointer, (nuint)_frameBytes);
            _slotLocked[index] = false;
        }

        NativeMemory.AlignedFree((void*)pointer);
        _slotPointers[index] = IntPtr.Zero;
    }
}

/// <summary>Producer-side lease over one native frame slot.</summary>
public unsafe ref struct FrameWriteLease
{
    private readonly PinnedFrameRingBuffer? _owner;
    private readonly byte* _data;
    private readonly int _slotIndex;
    private readonly int _length;
    private bool _completed;

    internal FrameWriteLease(
        PinnedFrameRingBuffer owner,
        int slotIndex,
        byte* data,
        int length,
        int stride,
        int width,
        int height)
    {
        _owner = owner;
        _slotIndex = slotIndex;
        _data = data;
        _length = length;
        _completed = false;
        Stride = stride;
        Width = width;
        Height = height;
    }

    public readonly bool IsValid => _owner is not null && _data is not null;

    public readonly int SlotIndex => _slotIndex;

    public readonly int Stride { get; }

    public readonly int Width { get; }

    public readonly int Height { get; }

    /// <summary>Writable view over the full BGRA frame (Width * Height * 4 bytes).</summary>
    public readonly Span<byte> Span => _data is null ? default : new Span<byte>(_data, _length);

    /// <summary>Marks the frame complete and atomically publishes it to the consumer.</summary>
    public void Publish()
    {
        if (_owner is null || _completed)
            return;

        _owner.PublishSlot(_slotIndex);
        _completed = true;
    }

    /// <summary>Returns an unpublished slot to the pool.</summary>
    public void Dispose()
    {
        if (_owner is null || _completed)
            return;

        _owner.AbortWrite(_slotIndex);
        _completed = true;
    }
}

/// <summary>Consumer-side lease over the newest published frame.</summary>
public unsafe ref struct FrameReadLease
{
    private readonly PinnedFrameRingBuffer? _owner;
    private readonly byte* _data;
    private readonly int _slotIndex;
    private readonly int _length;
    private bool _released;

    internal FrameReadLease(
        PinnedFrameRingBuffer owner,
        int slotIndex,
        byte* data,
        int length,
        int stride,
        int width,
        int height,
        long sequence)
    {
        _owner = owner;
        _slotIndex = slotIndex;
        _data = data;
        _length = length;
        _released = false;
        Stride = stride;
        Width = width;
        Height = height;
        Sequence = sequence;
    }

    public readonly bool IsValid => _owner is not null && _data is not null;

    public readonly long Sequence { get; }

    public readonly int Stride { get; }

    public readonly int Width { get; }

    public readonly int Height { get; }

    public readonly ReadOnlySpan<byte> Span => _data is null ? default : new ReadOnlySpan<byte>(_data, _length);

    /// <summary>Releases the slot back to the producer pool.</summary>
    public void Dispose()
    {
        if (_owner is null || _released)
            return;

        _owner.ReleaseRead(_slotIndex);
        _released = true;
    }
}
