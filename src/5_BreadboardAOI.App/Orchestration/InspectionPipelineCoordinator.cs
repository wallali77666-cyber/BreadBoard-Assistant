// ============================================================================
// FILE: src/5_BreadboardAOI.App/Orchestration/InspectionPipelineCoordinator.cs
// AUTHOR: Senior Systems Architect
// PURPOSE: Orchestrates the 60 FPS E-Core Reflex Loop and the Async P-Core 
//          Qwen3-VL-30B-A3B Coaching Loop using lock-free bounded channels.
// ============================================================================

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.DrcEngine;

namespace BreadboardAOI.App.Orchestration;

public sealed class InspectionPipelineCoordinator : IDisposable
{
    // Lock-free channel between 60 FPS Reflex Loop and Qwen3-VL Tutor.
    // Capacity = 1 with DropOldest guarantees Qwen NEVER processes stale frames!
    private readonly Channel<DrcStatePacket> _cognitiveTutorChannel =
        Channel.CreateBounded<DrcStatePacket>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

    private readonly ElectricalRulesChecker _drcEngine = new();
    private readonly CancellationTokenSource _cts = new();
    private int _lastProcessedStateHash = 0;

    // Events subscribed to by the Adobe-style MainWindow UI & SkiaSharp Viewport
    public event Action<DrcStatePacket>? OnReflexDrcUpdated;
    public event Action<string>? OnEmergencyShortAudioTrigger;
#pragma warning disable CS0067 // Wired to the Kokoro TTS transcript in the Phase 5 tutor loop.
    public event Action<string>? OnTutorTranscriptTokenReceived;
#pragma warning restore CS0067

    public void StartPipelines()
    {
        // Loop 1: 60 FPS Vision + DRC Reflex Thread (Pinned to E-Cores 12..18)
        Task.Factory.StartNew(
            () => RunFastReflexLoopAsync(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // Loop 2: Qwen3-VL-30B-A3B + Kokoro TTS Thread (Pinned to P-Cores 0..11)
        Task.Factory.StartNew(
            () => RunCognitiveTutorLoopAsync(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private async Task RunFastReflexLoopAsync(CancellationToken ct)
    {
        // [JUNIOR TASK - ISSUE #1A]: Call CpuAffinityManager.PinCurrentThreadToMask(0x3F000)
        // here so this entire loop stays strictly on the i5-13600K E-Cores.

        long frameCounter = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16.66)); // 60 FPS

        while (await timer.WaitForNextTickAsync(ct))
        {
            frameCounter++;

            // [JUNIOR TASK - ISSUE #1B]: 
            // 1. Pull latest 1080p frame from CameraCaptureService
            // 2. Warp to 1000x500 via HomographyCalibrator
            // 3. Check HandMotionGate.IsBoardStable(). If hand is moving inside frame, continue.
            // 4. Run YoloPoseInferenceEngine.Infer(warpedFrame) -> returns DetectedComponent[]

            DetectedComponent[] simulatedDetections = Array.Empty<DetectedComponent>();

            // 5. Run sub-millisecond KiCad DRC check
            DrcStatePacket packet = _drcEngine.EvaluateBoard(
                frameId: frameCounter,
                yoloMs: 5.8,
                rawDetections: simulatedDetections,
                snapLookup: (px, py) => (false, default) // Replace with _pinMatrix.TrySnapToHole
            );

            // 6. Push immediately to SkiaSharp 60 FPS CAD Viewport
            OnReflexDrcUpdated?.Invoke(packet);

            // 7. Only wake up Qwen3-VL if the physical circuit topology actually changed!
            if (packet.StateHash != _lastProcessedStateHash)
            {
                _lastProcessedStateHash = packet.StateHash;

                // Instant <10ms hardware reflex alarm if VCC is shorted to GND
                if (packet.HasCriticalShort)
                {
                    OnEmergencyShortAudioTrigger?.Invoke("assets/audio_reflex/alarm_vcc_gnd_short.wav");
                }

                // Non-blocking write to Qwen3-VL channel
                _cognitiveTutorChannel.Writer.TryWrite(packet);
            }
        }
    }

    private async Task RunCognitiveTutorLoopAsync(CancellationToken ct)
    {
        // [JUNIOR TASK - ISSUE #4A]: Call CpuAffinityManager.PinCurrentThreadToMask(0x00FFF)
        // to dedicate all 12 P-Core threads to Qwen3-VL-30B-A3B DDR4 memory streaming & TTS.

        await foreach (DrcStatePacket state in _cognitiveTutorChannel.Reader.ReadAllAsync(ct))
        {
            if (state.Violations.Count == 0 && state.Components.Count == 0)
                continue;

            // [JUNIOR TASK - ISSUE #4B]:
            // 1. Build prompt using DiagnosticPromptBuilder.Build(state.Violations, state.Components)
            // 2. Send 256x256 state.MicroCropPngBytes + Prompt to QwenMoEService (llama-server HTTP/gRPC)
            // 3. Pipe token stream through SentenceStreamSplitter -> VoiceSynthesisEngine (Kokoro ONNX)
            // 4. Fire OnTutorTranscriptTokenReceived?.Invoke(token) to update the Right Dock Log.
            await Task.Delay(50, ct);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}