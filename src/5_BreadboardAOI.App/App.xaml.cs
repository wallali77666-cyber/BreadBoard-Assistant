// ============================================================================
// FILE: src/5_BreadboardAOI.App/App.xaml.cs
// PURPOSE: Application bootstrap. Keeps the workstation alive even if an
//          optional subsystem (camera, NVML, Kokoro) is missing, and writes
//          unhandled dispatcher exceptions to %TEMP% instead of hard-crashing.
// ============================================================================

using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace BreadboardAOI.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                TryLog(ex);
            else
                TryLog(new Exception($"Non-exception unhandled error: {args.ExceptionObject}"));
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            TryLog(args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Never take the whole workstation down for a UI-thread glitch.
        TryLog(e.Exception);
        MessageBox.Show(
            $"BreadboardAOI encountered an unexpected error and kept the session alive.\n\n{e.Exception.Message}\n\nDetails were written to %TEMP%\\BreadboardAOI-error.log.",
            "BreadboardAOI",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true;
    }

    private static void TryLog(Exception exception)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "BreadboardAOI-error.log");
            File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Telemetry logging must never mask the original failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
