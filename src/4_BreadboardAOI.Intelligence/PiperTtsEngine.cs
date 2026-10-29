// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/PiperTtsEngine.cs
// PURPOSE: Optional local BMO voice. Runs the Rhasspy Piper CLI (the same engine
//          rogeriochaves/bmo uses for local speech) against a Piper/VITS voice
//          such as bmo.onnx. Piper reads the .onnx.json next to the model, so a
//          community BMO voice works without conversion or extra NuGet packages.
//
//          Only the cognitive voice lane touches this class; the 60 FPS reflex
//          loop never waits for a process or an audio device. If the executable,
//          model or JSON is missing the voice engine falls back to Kokoro/SAPI.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace BreadboardAOI.Intelligence;

/// <summary>Piper generation parameters. Length scale &gt; 1 speaks more slowly.</summary>
public readonly record struct PiperSynthesisSettings(
    int SpeakerId = -1,
    float LengthScale = 1.0f,
    float NoiseScale = 0.667f,
    float NoiseW = 0.8f,
    int SentenceSilenceMs = 200);

/// <summary>
/// Thin, deterministic wrapper around the Piper command line. One process is
/// started per utterance and writes a complete WAV, which keeps the end of the
/// clip unambiguous. Model loading dominates the cost (~0.3-1 s), so the tutor
/// joins its three sentences into one call before reaching this class.
/// </summary>
public sealed class PiperTtsEngine
{
    private readonly string _workingDirectory;

    public PiperTtsEngine(string exePath, string modelPath, string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        ExePath = Path.GetFullPath(exePath);
        ModelPath = Path.GetFullPath(modelPath);
        ConfigPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(configPath) ? ModelPath + ".json" : configPath);
        _workingDirectory = Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;
    }

    public string ExePath { get; }

    public string ModelPath { get; }

    public string ConfigPath { get; }

    /// <summary>Model stem, e.g. <c>bmo</c> for <c>bmo.onnx</c>.</summary>
    public string VoiceName => Path.GetFileNameWithoutExtension(ModelPath);

    public static bool IsAvailable(string? exePath, string? modelPath, string? configPath = null)
    {
        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(modelPath))
            return false;

        if (!File.Exists(exePath) || !File.Exists(modelPath))
            return false;

        string config = string.IsNullOrWhiteSpace(configPath)
            ? modelPath + ".json"
            : configPath;

        return File.Exists(config);
    }

    /// <summary>
    /// Arguments for the Piper 0.0.x CLI. Public so the process contract can be
    /// unit-tested without a Piper binary or an audio device.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(
        string modelPath,
        string? configPath,
        PiperSynthesisSettings settings,
        string outputFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);

        float lengthScale = float.IsFinite(settings.LengthScale)
            ? Math.Clamp(settings.LengthScale, 0.5f, 2.0f)
            : 1.0f;
        float noiseScale = float.IsFinite(settings.NoiseScale)
            ? Math.Clamp(settings.NoiseScale, 0.0f, 1.5f)
            : 0.667f;
        float noiseW = float.IsFinite(settings.NoiseW)
            ? Math.Clamp(settings.NoiseW, 0.0f, 1.5f)
            : 0.8f;
        int sentenceSilenceMs = Math.Clamp(settings.SentenceSilenceMs, 0, 2000);

        var arguments = new List<string>(18)
        {
            "--model",
            modelPath,
            "--output_file",
            outputFile,
            "--length_scale",
            lengthScale.ToString("0.###", CultureInfo.InvariantCulture),
            "--noise_scale",
            noiseScale.ToString("0.###", CultureInfo.InvariantCulture),
            "--noise_w",
            noiseW.ToString("0.###", CultureInfo.InvariantCulture),
            "--sentence_silence",
            (sentenceSilenceMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)
        };

        if (!string.IsNullOrWhiteSpace(configPath))
        {
            arguments.Add("--config");
            arguments.Add(configPath);
        }

        // -1 / auto leaves the choice to the voice's JSON. Single-speaker BMO
        // models should not receive an explicit --speaker flag.
        if (settings.SpeakerId >= 0)
        {
            arguments.Add("--speaker");
            arguments.Add(settings.SpeakerId.ToString(CultureInfo.InvariantCulture));
        }

        return arguments;
    }

    /// <summary>
    /// Synthesizes one utterance and returns the WAV bytes. Throws
    /// <see cref="InvalidOperationException"/> when Piper fails, so the caller can
    /// surface a precise diagnostic; the temporary WAV is always removed.
    /// </summary>
    public async Task<byte[]> SynthesizeWavAsync(
        string text,
        PiperSynthesisSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        string outputDirectory = Path.Combine(Path.GetTempPath(), "BreadboardAOI");
        Directory.CreateDirectory(outputDirectory);
        string outputFile = Path.Combine(
            outputDirectory,
            string.Create(CultureInfo.InvariantCulture, $"piper-{Guid.NewGuid():N}.wav"));

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ExePath,
                WorkingDirectory = _workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (string argument in BuildArguments(ModelPath, ConfigPath, settings, outputFile))
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new InvalidOperationException($"Piper could not start: '{ExePath}'.");

            // Speech work belongs on the P-Cores on the specified hybrid CPU.
            TryPinToPCore(process);

            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();

            try
            {
                await process.StandardInput.WriteLineAsync(text.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                process.StandardInput.Close();

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await Task.WhenAll(errorTask, outputTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            string error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Piper exited with code {process.ExitCode}. {Summarize(error)}");
            }

            if (!File.Exists(outputFile))
            {
                throw new InvalidOperationException(
                    $"Piper did not create '{outputFile}'. {Summarize(error)}");
            }

            return await File.ReadAllBytesAsync(outputFile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(outputFile);
        }
    }

    /// <summary>
    /// Reads a Piper WAV into raw 16-bit mono PCM for the shared NAudio
    /// playback path. Piper emits 22.05 kHz mono PCM; the format is rejected
    /// rather than guessed if a future build changes it.
    /// </summary>
    public static bool TryReadPcm16Mono(byte[]? wav, out byte[] pcm, out int sampleRate)
    {
        pcm = Array.Empty<byte>();
        sampleRate = 0;

        if (wav is null || wav.Length <= 44)
            return false;

        try
        {
            using var reader = new WaveFileReader(new MemoryStream(wav, writable: false));
            if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm
                || reader.WaveFormat.BitsPerSample != 16
                || reader.WaveFormat.Channels != 1)
            {
                return false;
            }

            sampleRate = reader.WaveFormat.SampleRate;
            using var data = new MemoryStream((int)Math.Min(reader.Length, int.MaxValue));
            reader.CopyTo(data);
            pcm = data.ToArray();
            return pcm.Length > 0 && sampleRate > 0;
        }
        catch (Exception ex) when (ex is IOException
                                   or InvalidOperationException
                                   or FormatException
                                   or ArgumentException)
        {
            return false;
        }
    }

    private static void TryPinToPCore(Process process)
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessorCount < 12)
            return;

        try
        {
            process.ProcessorAffinity = (IntPtr)0x00FFF;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or NotSupportedException
                                   or System.ComponentModel.Win32Exception)
        {
            // Pinning is an optimisation; Piper works on any core.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or NotSupportedException
                                   or System.ComponentModel.Win32Exception)
        {
            // The process already exited.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale temp file is harmless.
        }
    }

    private static string Summarize(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return string.Empty;

        string trimmed = error.Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[..400];
    }
}
