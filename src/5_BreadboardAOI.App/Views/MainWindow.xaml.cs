// ============================================================================
// FILE: src/5_BreadboardAOI.App/Views/MainWindow.xaml.cs
// PURPOSE: Hosts the SkiaSharp CAD viewport, drives its 60 FPS invalidation and
//          forwards mouse-hover pin snapping to the view-model.
// ============================================================================

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BreadboardAOI.App.ViewModels;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace BreadboardAOI.App.Views;

public partial class MainWindow : Window
{
    private readonly MainWorkstationViewModel _viewModel;
    private bool _disposed;
    private Point _panStartPoint;
    private bool _isPanning;
    private bool _isRightPanning;
    private bool _isDraggingCable;
    private Point _cableDragStartPoint;

    public MainWindow()
    {
        InitializeComponent();

        FitToDesktop();

        _viewModel = new MainWorkstationViewModel(Dispatcher);
        DataContext = _viewModel;

        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
        Closing += OnWindowClosing;
        PreviewKeyDown += OnWindowPreviewKeyDown;

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

        if (LeftDockTabs.SelectedIndex == 1)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => BuilderScroll.ScrollToTop()));
    }

    /// <summary>
    /// WPF focuses the first list in a tab as soon as it is selected, which used
    /// to scroll the BUILDER panel past BUILD MODE. Opening the tab always starts
    /// at the top; the user can scroll from there.
    /// </summary>
    private void OnLeftDockTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, LeftDockTabs))
            return;

        if (LeftDockTabs.SelectedIndex == 1)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => BuilderScroll.ScrollToTop()));
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
        Point position = GetViewportPoint(e);
        if (_isPanning)
        {
            float dx = (float)(position.X - _panStartPoint.X);
            float dy = (float)(position.Y - _panStartPoint.Y);
            _panStartPoint = position;
            _viewModel.PanViewport(dx, dy);
            return;
        }

        if (_isDraggingCable || _viewModel.PendingCableDragKind != BreadboardAOI.Core.BoxCoding.UsbCableKind.None)
            _viewModel.UpdateCablePlacementCursor((float)position.X, (float)position.Y);

        _viewModel.UpdateHover(position.X, position.Y);
    }

    /// <summary>
    /// SKElement paints into a surface measured in device pixels (ActualWidth x DPI
    /// scale) but WPF reports the mouse in DIPs. Converting here keeps the snapped
    /// pin exactly under the cursor on 125 %/150 % displays instead of drifting
    /// toward the top-left of the board.
    /// </summary>
    private Point GetViewportPoint(MouseEventArgs e)
    {
        Point position = e.GetPosition(Viewport);
        DpiScale dpi = VisualTreeHelper.GetDpi(Viewport);
        return new Point(position.X * dpi.DpiScaleX, position.Y * dpi.DpiScaleY);
    }

    private void OnViewportMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isPanning)
            _viewModel.ClearHover();
    }

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.MiddleButton == MouseButtonState.Pressed)
        {
            _isPanning = true;
            _isRightPanning = false;
            _panStartPoint = GetViewportPoint(e);
            Viewport.CaptureMouse();
            Viewport.Cursor = Cursors.SizeAll;
            e.Handled = true;
        }
        else if (e.RightButton == MouseButtonState.Pressed && !_viewModel.IsBuildMode)
        {
            Point position = GetViewportPoint(e);
            if (!_viewModel.ShouldInterceptSecondaryViewportAction(position.X, position.Y))
            {
                _isPanning = true;
                _isRightPanning = true;
                _panStartPoint = position;
                Viewport.CaptureMouse();
                Viewport.Cursor = Cursors.SizeAll;
                e.Handled = true;
            }
        }
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanning)
        {
            bool middleReleased = e.MiddleButton == MouseButtonState.Released;
            bool rightReleased = e.RightButton == MouseButtonState.Released;
            if ((!_isRightPanning && middleReleased) || (_isRightPanning && rightReleased))
            {
                _isPanning = false;
                _isRightPanning = false;
                Viewport.ReleaseMouseCapture();
                Viewport.Cursor = Cursors.Arrow;
                e.Handled = true;
            }
        }

        if (_isDraggingCable && e.ChangedButton == MouseButton.Left)
        {
            _isDraggingCable = false;
            if (Viewport.IsMouseCaptured)
                Viewport.ReleaseMouseCapture();
            Viewport.Cursor = Cursors.Arrow;

            Point position = GetViewportPoint(e);
            double dx = position.X - _cableDragStartPoint.X;
            double dy = position.Y - _cableDragStartPoint.Y;
            if ((dx * dx + dy * dy) > 64.0)
            {
                _viewModel.TryHandleProgrammerViewportAction(position.X, position.Y, secondary: false);
                e.Handled = true;
            }
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool isTextControl = e.OriginalSource is TextBox || e.OriginalSource is PasswordBox;
        bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (isCtrl && !isTextControl)
        {
            if (e.Key == Key.Z)
            {
                if (isShift)
                    _viewModel.RedoCircuitPart();
                else
                    _viewModel.UndoCircuitPart();
                e.Handled = true;
            }
            else if (e.Key == Key.Y)
            {
                _viewModel.RedoCircuitPart();
                e.Handled = true;
            }
            else if (e.Key is Key.OemPlus or Key.Add)
            {
                _viewModel.ZoomIn();
                e.Handled = true;
            }
            else if (e.Key is Key.OemMinus or Key.Subtract)
            {
                _viewModel.ZoomOut();
                e.Handled = true;
            }
            else if (e.Key is Key.D0 or Key.NumPad0)
            {
                _viewModel.ResetZoom();
                e.Handled = true;
            }
        }
        if (!isTextControl && !isCtrl)
        {
            if (e.Key == Key.Left || e.Key == Key.A)
            {
                _viewModel.PanViewport(50f, 0f);
                e.Handled = true;
            }
            else if (e.Key == Key.Right || e.Key == Key.D)
            {
                _viewModel.PanViewport(-50f, 0f);
                e.Handled = true;
            }
            else if (e.Key == Key.Up || e.Key == Key.W)
            {
                _viewModel.PanViewport(0f, 50f);
                e.Handled = true;
            }
            else if (e.Key == Key.Down || e.Key == Key.S)
            {
                _viewModel.PanViewport(0f, -50f);
                e.Handled = true;
            }
        }
    }

    private void OnViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Point position = GetViewportPoint(e);
        double factor = e.Delta > 0 ? 1.15 : 0.87;
        _viewModel.ZoomCadViewport(factor, ((float)position.X, (float)position.Y));
        e.Handled = true;
    }

    private void OnViewportMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Point position = GetViewportPoint(e);
        bool hadPendingDrag = _viewModel.PendingCableDragKind != BreadboardAOI.Core.BoxCoding.UsbCableKind.None;
        _viewModel.HandleViewportClick(position.X, position.Y, secondary: false);

        if (!hadPendingDrag && _viewModel.PendingCableDragKind != BreadboardAOI.Core.BoxCoding.UsbCableKind.None)
        {
            _isDraggingCable = true;
            _cableDragStartPoint = position;
            Viewport.CaptureMouse();
            Viewport.Cursor = Cursors.Hand;
        }
    }

    private void OnViewportMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        Point position = GetViewportPoint(e);
        if (_isDraggingCable)
        {
            _isDraggingCable = false;
            if (Viewport.IsMouseCaptured)
                Viewport.ReleaseMouseCapture();
            Viewport.Cursor = Cursors.Arrow;
        }

        if (_viewModel.TryHandleProgrammerViewportAction(position.X, position.Y, secondary: true))
        {
            e.Handled = true;
            return;
        }

        if (!_viewModel.IsBuildMode)
            return;

        _viewModel.HandleViewportClick(position.X, position.Y, secondary: true);
        e.Handled = true;
    }

    private void OnViewportDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedImage(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnViewportDrop(object sender, DragEventArgs e)
    {
        if (TryGetDroppedImage(e.Data, out string? imagePath) && imagePath is not null)
            _viewModel.LoadImage(imagePath);

        e.Handled = true;
    }

    private static bool TryGetDroppedImage(IDataObject data, out string? imagePath)
    {
        imagePath = null;

        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return false;

        string candidate = files[0];
        string extension = System.IO.Path.GetExtension(candidate).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".tif" or ".tiff"))
            return false;

        imagePath = candidate;
        return true;
    }

    private int _lastTranscriptLength;

    private void OnTranscriptTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box)
            return;

        // Keep the beginning of a fresh explanation visible. Auto-scrolling to
        // the end on every token used to clip the first visible line mid-glyph.
        if (box.Text.Length < _lastTranscriptLength)
            box.ScrollToHome();

        _lastTranscriptLength = box.Text.Length;
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        Viewport.InvalidateVisual();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // Persist the dock layout immediately; the debounce timer may not get
        // another dispatcher turn before the process exits.
        _viewModel.SaveWorkspaceNow();
        Closing -= OnWindowClosing;
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
