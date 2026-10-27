// ============================================================================
// FILE: src/5_BreadboardAOI.App/Views/MainWindow.xaml.cs
// PURPOSE: Hosts the SkiaSharp CAD viewport, drives its 60 FPS invalidation and
//          forwards mouse-hover pin snapping to the view-model.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BreadboardAOI.App.ViewModels;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace BreadboardAOI.App.Views;

public partial class MainWindow : Window
{
    private readonly MainWorkstationViewModel _viewModel;
    private bool _disposed;

    public MainWindow()
    {
        InitializeComponent();

        FitToDesktop();

        _viewModel = new MainWorkstationViewModel(Dispatcher);
        DataContext = _viewModel;

        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;

        CompositionTarget.Rendering += OnCompositionTargetRendering;
    }

    /// <summary>
    /// Shrinks the initial window to the desktop and centres it. WorkArea is
    /// reported in physical pixels while WPF sizes are DIPs, so the current DPI
    /// scale must be divided out before the geometry is applied.
    /// </summary>
    private void FitToDesktop()
    {
        Rect workArea = SystemParameters.WorkArea;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0)
            scale = 1d;

        double workLeft = workArea.Left / scale;
        double workTop = workArea.Top / scale;
        double availableWidth = workArea.Width / scale - 24d;
        double availableHeight = workArea.Height / scale - 24d;

        if (availableWidth > 0)
            Width = Math.Min(Width, Math.Max(MinWidth, availableWidth));

        if (availableHeight > 0)
            Height = Math.Min(Height, Math.Max(MinHeight, availableHeight));

        Left = workLeft + Math.Max(0d, (workArea.Width / scale - Width) * 0.5d);
        Top = workTop + Math.Max(0d, (workArea.Height / scale - Height) * 0.5d);
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;
        _viewModel.Initialize();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableDarkTitleBar();
    }

    /// <summary>Matches the Windows chrome to the Spectrum-dark workspace; ignored on old builds.</summary>
    private void TryEnableDarkTitleBar()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        int enabled = 1;

        try
        {
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+), 19 = pre-2004.
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // dwmapi.dll is present on every supported Windows release; stay safe anyway.
        }
        catch (EntryPointNotFoundException)
        {
            // Attribute unsupported on this build.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void OnViewportPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        _viewModel.Render(e.Surface.Canvas, e.Info.Width, e.Info.Height);
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        Point position = e.GetPosition(Viewport);
        _viewModel.UpdateHover(position.X, position.Y);
    }

    private void OnViewportMouseLeave(object sender, MouseEventArgs e)
    {
        _viewModel.ClearHover();
    }

    private void OnTranscriptTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box)
            box.ScrollToEnd();
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        Viewport.InvalidateVisual();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        _disposed = true;
        CompositionTarget.Rendering -= OnCompositionTargetRendering;
        Loaded -= OnWindowLoaded;
        Closed -= OnWindowClosed;

        _viewModel.Dispose();
    }
}
