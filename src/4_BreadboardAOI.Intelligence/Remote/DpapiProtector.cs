// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/Remote/DpapiProtector.cs
// PURPOSE: Encrypts the OAuth grant at rest with Windows DPAPI (crypt32.dll).
//          A token file protected with CurrentUser scope can only be decrypted
//          by the same Windows account, so copying %LOCALAPPDATA% to another
//          machine (or another user profile) yields nothing.
//
// NOTE: No NuGet package is required: this is the same pure P/Invoke approach
//       the hardware layer uses, and it degrades to "unsupported" off Windows.
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace BreadboardAOI.Intelligence.Remote;

/// <summary>Thin wrapper over CryptProtectData / CryptUnprotectData.</summary>
internal static class DpapiProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree", SetLastError = false)]
    private static extern IntPtr LocalFree(IntPtr handle);

    /// <summary>True on Windows, where DPAPI is available.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Encrypts <paramref name="plaintext"/> for the current user. Null on failure.</summary>
    public static byte[]? Protect(byte[] plaintext)
    {
        if (!IsSupported || plaintext.Length == 0)
            return null;

        IntPtr input = Marshal.AllocHGlobal(plaintext.Length);
        try
        {
            Marshal.Copy(plaintext, 0, input, plaintext.Length);

            var dataIn = new DataBlob { Size = plaintext.Length, Data = input };
            if (!CryptProtectData(
                    ref dataIn,
                    "BreadboardAOI OAuth grant",
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out DataBlob dataOut))
            {
                return null;
            }

            try
            {
                return CopyBlob(dataOut);
            }
            finally
            {
                if (dataOut.Data != IntPtr.Zero)
                    LocalFree(dataOut.Data);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(input);
        }
    }

    /// <summary>Decrypts a blob produced by <see cref="Protect"/>. Null on failure.</summary>
    public static byte[]? Unprotect(byte[] protectedData)
    {
        if (!IsSupported || protectedData.Length == 0)
            return null;

        IntPtr input = Marshal.AllocHGlobal(protectedData.Length);
        try
        {
            Marshal.Copy(protectedData, 0, input, protectedData.Length);

            var dataIn = new DataBlob { Size = protectedData.Length, Data = input };
            if (!CryptUnprotectData(
                    ref dataIn,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out DataBlob dataOut))
            {
                return null;
            }

            try
            {
                return CopyBlob(dataOut);
            }
            finally
            {
                if (dataOut.Data != IntPtr.Zero)
                    LocalFree(dataOut.Data);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(input);
        }
    }

    private static byte[] CopyBlob(in DataBlob blob)
    {
        if (blob.Size <= 0 || blob.Data == IntPtr.Zero)
            return Array.Empty<byte>();

        var buffer = new byte[blob.Size];
        Marshal.Copy(blob.Data, buffer, 0, blob.Size);
        return buffer;
    }
}
