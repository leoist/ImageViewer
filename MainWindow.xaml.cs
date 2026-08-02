using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> WicExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif", ".ico"
    };
    private static readonly HashSet<string> HeifExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heif", ".heic", ".avif"
    };
    private static readonly HashSet<string> RawExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2", ".pef",
        ".raf", ".3fr", ".kdc", ".mrw", ".nrw", ".raw", ".rwl", ".srw",
        ".x3f", ".erf", ".mef", ".mos", ".iiq"
    };

    private readonly List<string> folder = new();
    private int index = -1;
    private BitmapSource? sourceBitmap;
    private BitmapSource? minimapSource;
    private double imageW, imageH;
    private double zoom = 1.0;
    private double offsetX, offsetY;
    private bool panning;
    private Point panStart;
    private double panStartX, panStartY;
    private bool isFullscreen;
    private int rotation;
    private const double MinimapSize = 150;
    private System.Windows.Controls.Image? _mainImage;
    private double _targetZoom, _targetOffsetX, _targetOffsetY;
    private bool _zoomAnimating;
    private bool _thumbStripVisible;
    private double _thumbScrollTarget;
    private bool _thumbScrollAnimating;
    private bool minimapDragging;
    private double minimapW, minimapH;
    private bool infoPanelVisible;
    private int infoToggleGeneration;
    private bool pendingOpen;
    private bool magnifierVisible;
    private bool cropMode;
    private bool _suppressNextContextMenu;
    private ContextMenu? _savedContextMenu;
    private readonly HashSet<int> _thumbLoading = new();
    private FormatConvertedBitmap? _pickerCfb;
    private BitmapSource? _pickerCfbSource;
    private bool cropDragging;
    private bool cropRepositioning;
    private Point cropStart, cropEnd;
    private double cropRatio; // 0 = free, >0 = width/height ratio
    private double cropReposW, cropReposH;
    private Point cropReposClick;
    private readonly Border viewportRect = new()
    {
        BorderBrush = new SolidColorBrush(Colors.White),
        BorderThickness = new Thickness(1.5),
        Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
        CornerRadius = new CornerRadius(1)
    };

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x0040;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ImageViewer", "settings.json");

    public MainWindow()
    {
        InitializeComponent();
        MinimapGrid.Children.Add(viewportRect);
        SourceInitialized += OnSourceInitialized;
        PreviewMouseDown += OnPreviewMouseDown;
        Canvas.ContextMenuOpening += OnContextMenuOpening;
        LoadWindowState();

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]) && IsImage(args[1]))
            Dispatcher.BeginInvoke(() => _ = LoadFileAsync(args[1]));

        CompositionTarget.Rendering += OnRendering;
        ThumbScroll.ScrollChanged += OnThumbScrollChanged;
        ThumbScroll.AddHandler(UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnThumbStripMouseWheel), true);
        ThumbScroll.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int val = 1;
        DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int));
        DwmSetWindowAttribute(hwnd, 3, ref val, sizeof(int));
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => RootGrid.Focus();

    private void ForceForeground()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        SetForegroundWindow(hwnd);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files is { Length: > 0 } && IsImage(files[0]))
                await LoadFileAsync(files[0]);
        }
        ForceForeground();
        RootGrid.Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: Navigate(-1); e.Handled = true; break;
            case Key.Right: Navigate(1); e.Handled = true; break;
            case Key.F11:
            case Key.F: ToggleFullscreen(); e.Handled = true; break;
            case Key.Escape: if (cropMode) ExitCropMode(); else if (isFullscreen) ToggleFullscreen(); e.Handled = true; break;
            case Key.D0: FitToWindow(); e.Handled = true; break;
            case Key.D1: ZoomTo(1.0); e.Handled = true; break;
            case Key.W: FitToWidth(); e.Handled = true; break;
            case Key.H: FitToHeight(); e.Handled = true; break;
            case Key.R: RotateImage(); e.Handled = true; break;
            case Key.I: ToggleInfoPanel(); e.Handled = true; break;
            case Key.P: magnifierVisible = !magnifierVisible; MagnifierBorder.Visibility = magnifierVisible ? Visibility.Visible : Visibility.Collapsed; e.Handled = true; break;
            case Key.O: ShowOpenDialog(); e.Handled = true; break;
            case Key.C: CopyToClipboard(); e.Handled = true; break;
            case Key.X: if (cropMode) ExitCropMode(); else EnterCropMode(); e.Handled = true; break;
            case Key.T: ToggleThumbStrip(); e.Handled = true; break;
            case Key.Delete: DeleteCurrentImage(); e.Handled = true; break;
        }
    }

    private void OnMagnifierCopy(object sender, MouseButtonEventArgs e)
    {
        if (magnifierVisible && !string.IsNullOrEmpty(MagnifierColor.Text))
        {
            var text = MagnifierColor.Text;
            CopyColorAsync(text);
            e.Handled = true;
        }
    }

    private static void CopyColorAsync(string text)
    {
        var t = new Thread(() =>
        {
            try
            {
                var disp = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                disp.BeginInvoke(new Action(() =>
                {
                    try { Clipboard.SetText(text); }
                    catch { }
                    disp.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }));
                System.Windows.Threading.Dispatcher.Run();
            }
            catch { }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sourceBitmap == null || cropMode) return;
        var pos = Mouse.GetPosition(RootGrid);
        double oldZoom = zoom;
        double newZoom = e.Delta > 0 ? Math.Min(zoom * 1.3, 64.0) : Math.Max(zoom / 1.3, 0.02);
        double newOffX = pos.X - (pos.X - offsetX) * (newZoom / oldZoom);
        double newOffY = pos.Y - (pos.Y - offsetY) * (newZoom / oldZoom);
        ClampOffsetFor(ref newZoom, ref newOffX, ref newOffY);
        AnimateTo(newZoom, newOffX, newOffY);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (cropMode && sourceBitmap != null)
        {
            if (CropToolbar.Visibility == Visibility.Visible)
            {
                if (CropToolbar.IsMouseOver) return;
                var pos = Mouse.GetPosition(RootGrid);
                double x = Math.Min(cropStart.X, cropEnd.X);
                double y = Math.Min(cropStart.Y, cropEnd.Y);
                double w = Math.Abs(cropEnd.X - cropStart.X);
                double h = Math.Abs(cropEnd.Y - cropStart.Y);
                if (pos.X >= x && pos.X <= x + w && pos.Y >= y && pos.Y <= y + h)
                {
                    cropRepositioning = true;
                    cropReposW = w;
                    cropReposH = h;
                    cropReposClick = pos;
                    CropToolbar.Visibility = Visibility.Collapsed;
                    CropHint.Visibility = Visibility.Collapsed;
                    RootGrid.CaptureMouse();
                    e.Handled = true;
                    return;
                }
                CropToolbar.Visibility = Visibility.Collapsed;
                CropHint.Visibility = Visibility.Visible;
                CropRectBorder.Visibility = Visibility.Collapsed;
                CropSizeLabel.Visibility = Visibility.Collapsed;
                CropDarkBg.Clip = null;
            }
            cropDragging = true;
            cropStart = Mouse.GetPosition(RootGrid);
            cropEnd = cropStart;
            CropToolbar.Visibility = Visibility.Collapsed;
            CropHint.Visibility = Visibility.Collapsed;
            RootGrid.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2)
        {
            if (Math.Abs(zoom - FitScale()) < 0.01)
                ZoomTo(1.0);
            else
                FitToWindow();
            e.Handled = true;
            return;
        }

        if (sourceBitmap == null) return;
        if (zoom > FitScale() * 1.05)
        {
            panning = true;
            _zoomAnimating = false;
            panStart = Mouse.GetPosition(RootGrid);
            panStartX = offsetX;
            panStartY = offsetY;
            RootGrid.CaptureMouse();
            e.Handled = true;
        }
        else
        {
            RootGrid.Focus();
        }
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_suppressNextContextMenu)
        {
            _suppressNextContextMenu = false;
            e.Handled = true;
        }
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right)
        {
            if (cropMode) { _suppressNextContextMenu = true; ExitCropMode(); return; }
            if (magnifierVisible) { _suppressNextContextMenu = true; magnifierVisible = false; MagnifierBorder.Visibility = Visibility.Collapsed; return; }
        }
        if (e.ChangedButton != MouseButton.Middle) return;
        if (!cropMode || sourceBitmap == null) return;
        if (CropToolbar.Visibility != Visibility.Visible) return;

        var pos = Mouse.GetPosition(RootGrid);
        double x = Math.Min(cropStart.X, cropEnd.X);
        double y = Math.Min(cropStart.Y, cropEnd.Y);
        double w = Math.Abs(cropEnd.X - cropStart.X);
        double h = Math.Abs(cropEnd.Y - cropStart.Y);
        if (w < 5 || h < 5) return;
        if (pos.X < x || pos.X > x + w || pos.Y < y || pos.Y > y + h) return;

        try
        {
            var p1 = ScreenToImage(new Point(x, y));
            var p2 = ScreenToImage(new Point(x + w, y + h));
            int ix = Math.Max(0, (int)Math.Min(p1.X, p2.X));
            int iy = Math.Max(0, (int)Math.Min(p1.Y, p2.Y));
            int iw = Math.Min((int)Math.Abs(p2.X - p1.X), (int)imageW - ix);
            int ih = Math.Min((int)Math.Abs(p2.Y - p1.Y), (int)imageH - iy);
            if (iw <= 0 || ih <= 0) return;

            var cropped = new CroppedBitmap(sourceBitmap, new Int32Rect(ix, iy, iw, ih));
            BitmapSource finalBmp = cropped;
            if (rotation != 0)
            {
                var rotated = new TransformedBitmap(cropped, new RotateTransform(rotation));
                rotated.Freeze();
                finalBmp = rotated;
            }
            finalBmp.Freeze();
            Clipboard.SetImage(finalBmp);
        }
        catch { }
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (cropDragging)
        {
            cropEnd = Mouse.GetPosition(RootGrid);
            ClampCropToImage();
            ConstrainCropEnd();
            ClampCropToImage();
            UpdateCropVisual();
            return;
        }
        if (cropRepositioning)
        {
            var pos = Mouse.GetPosition(RootGrid);
            double dx = pos.X - cropReposClick.X;
            double dy = pos.Y - cropReposClick.Y;
            double x = Math.Min(cropStart.X, cropEnd.X) + dx;
            double y = Math.Min(cropStart.Y, cropEnd.Y) + dy;
            double w = cropReposW;
            double h = cropReposH;
            double imgLeft = offsetX;
            double imgTop = offsetY;
            double imgRight = offsetX + imageW * zoom;
            double imgBottom = offsetY + imageH * zoom;
            x = Math.Clamp(x, imgLeft, imgRight - w);
            y = Math.Clamp(y, imgTop, imgBottom - h);
            cropStart = new Point(x, y);
            cropEnd = new Point(x + w, y + h);
            cropReposClick = pos;
            UpdateCropVisual();
            return;
        }
        if (panning)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                panning = false;
                RootGrid.ReleaseMouseCapture();
                return;
            }
            var pos = Mouse.GetPosition(RootGrid);
            offsetX = panStartX + (pos.X - panStart.X);
            offsetY = panStartY + (pos.Y - panStart.Y);
            ClampOffset();
            UpdateView();
        }
        if (magnifierVisible && sourceBitmap != null)
            UpdateMagnifier();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (cropDragging)
        {
            cropDragging = false;
            RootGrid.ReleaseMouseCapture();
            double w = Math.Abs(cropEnd.X - cropStart.X);
            double h = Math.Abs(cropEnd.Y - cropStart.Y);
            if (w > 5 && h > 5)
                ShowCropToolbar();
            else
                ResetCropSelection();
            e.Handled = true;
            return;
        }
        if (cropRepositioning)
        {
            cropRepositioning = false;
            RootGrid.ReleaseMouseCapture();
            ShowCropToolbar();
            e.Handled = true;
            return;
        }
        if (panning)
        {
            panning = false;
            RootGrid.ReleaseMouseCapture();
        }
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (panning)
        {
            panning = false;
            RootGrid.ReleaseMouseCapture();
        }
    }

    private void OnMinimapPressed(object sender, MouseButtonEventArgs e)
    {
        if (sourceBitmap == null) return;
        minimapDragging = true;
        _zoomAnimating = false;
        UpdateMinimapClick(e);
        MinimapBorder.CaptureMouse();
        e.Handled = true;
    }

    private void OnMinimapMouseMove(object sender, MouseEventArgs e)
    {
        if (!minimapDragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            minimapDragging = false;
            MinimapBorder.ReleaseMouseCapture();
            return;
        }
        UpdateMinimapClick(e);
    }

    private void OnMinimapReleased(object sender, MouseButtonEventArgs e)
    {
        minimapDragging = false;
        MinimapBorder.ReleaseMouseCapture();
    }

    private void UpdateMinimapClick(MouseEventArgs e)
    {
        CalcMinimapSize();
        double mmScale = minimapW / imageW;
        double vw = RootGrid.ActualWidth;
        double vh = RootGrid.ActualHeight;
        var pt = Mouse.GetPosition(MinimapGrid);
        double imgX = pt.X / mmScale;
        double imgY = pt.Y / mmScale;
        offsetX = vw / 2.0 - imgX * zoom;
        offsetY = vh / 2.0 - imgY * zoom;
        ClampOffset();
        UpdateView();
    }

    private void CalcMinimapSize()
    {
        if (imageW > imageH)
        {
            minimapW = MinimapSize * (imageW / imageH);
            minimapH = MinimapSize;
        }
        else
        {
            minimapW = MinimapSize;
            minimapH = MinimapSize * (imageH / imageW);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCanvasClip();
        if (cropMode) ResetCropSelection();
        if (sourceBitmap != null)
        {
            zoom = FitScale();
            double vw = RootGrid.ActualWidth;
            double vh = RootGrid.ActualHeight;
            offsetX = (vw - imageW * zoom) / 2.0;
            offsetY = (vh - imageH * zoom) / 2.0;
            _targetZoom = zoom;
            _targetOffsetX = offsetX;
            _targetOffsetY = offsetY;
            _zoomAnimating = false;
        }
        UpdateView();
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveWindowState();
        CompositionTarget.Rendering -= OnRendering;
    }

    private void SaveWindowState()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var state = WindowState == WindowState.Maximized && isFullscreen ? "Maximized" : "Normal";
            var content = $"{{\"Left\":{Left},\"Top\":{Top},\"Width\":{Width},\"Height\":{Height},\"State\":\"{state}\"}}";
            File.WriteAllText(SettingsPath, content);
        }
        catch { }
    }

    private void LoadWindowState()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var json = File.ReadAllText(SettingsPath);
            double left = ParseJsonDouble(json, "Left");
            double top = ParseJsonDouble(json, "Top");
            double w = ParseJsonDouble(json, "Width");
            double h = ParseJsonDouble(json, "Height");
            if (w > 0 && h > 0)
            {
                Left = left;
                Top = top;
                Width = w;
                Height = h;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
        }
        catch { }
    }

    private static double ParseJsonDouble(string json, string key)
    {
        var search = $"\"{key}\":";
        int start = json.IndexOf(search, StringComparison.Ordinal);
        if (start < 0) return 0;
        start += search.Length;
        int end = start;
        while (end < json.Length && json[end] != ',' && json[end] != '}') end++;
        return double.TryParse(json[start..end], System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private void UpdateCanvasClip()
    {
        Canvas.Clip = new RectangleGeometry(new Rect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight));
    }

    private void Navigate(int delta)
    {
        if (folder.Count == 0) return;
        int next = index + delta;
        if (next >= 0 && next < folder.Count) _ = ShowImageAsync(next);
    }

    private void DeleteCurrentImage()
    {
        if (folder.Count == 0 || index < 0 || index >= folder.Count) return;
        var path = folder[index];
        var name = Path.GetFileName(path);
        var dialog = new ConfirmDialog("移动到回收站?", name);
        dialog.ShowDialog();
        if (!dialog.Confirmed) return;
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var fo = new SHFILEOPSTRUCT
            {
                hwnd = hwnd,
                wFunc = FO_DELETE,
                pFrom = path + '\0' + '\0',
                fFlags = FOF_ALLOWUNDO
            };
            var ret = SHFileOperationW(ref fo);
            if (ret != 0) throw new Exception($"SHFileOperation returned {ret}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        folder.RemoveAt(index);
        if (index < ThumbStack.Children.Count)
            ThumbStack.Children.RemoveAt(index);
        if (folder.Count == 0)
        {
            index = -1;
            sourceBitmap = null;
            minimapSource = null;
            Canvas.Children.Clear();
            _mainImage = null;
            Title = "ImageViewer";
            return;
        }
        if (index >= folder.Count) index = folder.Count - 1;
        if (_thumbStripVisible) UpdateThumbSelection();
        _ = ShowImageAsync(index);
    }

    private async Task LoadFileAsync(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        folder.Clear();
        ThumbStack.Children.Clear();
        folder.AddRange(
            Directory.GetFiles(dir)
                .Where(f => IsImage(f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        );
        index = folder.IndexOf(Path.GetFullPath(path));
        if (index < 0) index = 0;
        await ShowImageAsync(index);
        if (_thumbStripVisible) PopulateThumbStrip();
    }

    private async Task ShowImageAsync(int idx)
    {
        sourceBitmap = null;
        minimapSource = null;
        _mainImage = null;
        _pickerCfb = null;
        _pickerCfbSource = null;
        Canvas.Children.Clear();
        _ = Task.Run(() => GC.Collect());

        index = idx;
        if (_thumbStripVisible) UpdateThumbSelection();
        var path = folder[idx];
        var ext = Path.GetExtension(path);
        rotation = 0;

        if (infoPanelVisible)
        {
            var infoPath = path;
            _ = Task.Run(() => BuildImageInfoRows(infoPath)).ContinueWith(t =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (infoPanelVisible && folder.Count > 0 && index >= 0 && index < folder.Count
                        && folder[index] == infoPath)
                        PopulateInfoGrid(t.Result);
                });
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        var sw = Stopwatch.StartNew();

        if (HeifExts.Contains(ext))
        {
            try
            {
                var result = await Task.Run(() => HeifNative.Decode(path));
                var decodeMs = sw.ElapsedMilliseconds;
                if (result is { } data)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        LoadFromBgra(data.bgra, data.width, data.height, path);
                        Title = $"{Path.GetFileName(path)}  [{data.width}x{data.height}]";
                    });
                    return;
                }
            }
            catch { }
        }

        if (RawExts.Contains(ext))
        {
            try
            {
                var thumbData = await Task.Run(() => RawNative.DecodeThumbnail(path));
                var decodeMs = sw.ElapsedMilliseconds;
                if (thumbData != null)
                {
                    var bmp = new BitmapImage();
                    using (var ms = new MemoryStream(thumbData))
                    {
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                    }
                    bmp.Freeze();
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        sourceBitmap = bmp;
                        minimapSource = bmp;
                        imageW = bmp.PixelWidth;
                        imageH = bmp.PixelHeight;
                        FitToWindow();
                        RootGrid.Focus();
                        Title = $"{Path.GetFileName(path)}  [{imageW}x{imageH}]";
                    });
                    return;
                }
            }
            catch { }
        }

        BitmapImage? bmp2 = null;
        if (WicExts.Contains(ext))
        {
            try { bmp2 = await LoadWicAsync(path); }
            catch { }
        }

        var p = path;
        _ = Dispatcher.BeginInvoke(() => SetBitmap(bmp2, p));
    }

    private void LoadFromBgra(byte[] bgra, int w, int h, string path)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        bmp.Freeze();
        sourceBitmap = bmp;
        imageW = w;
        imageH = h;
        minimapSource = CreateScaledMinimap(bmp);
        FitToWindow();
    }

    private static BitmapSource CreateScaledMinimap(BitmapSource src)
    {
        double scale = (MinimapSize * 2) / (double)Math.Max(src.PixelWidth, src.PixelHeight);
        if (scale >= 1.0) return src;
        var scaled = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private void SetBitmap(BitmapImage? bmp, string path)
    {
        if (bmp == null)
        {
            Title = $"无法加载: {Path.GetFileName(path)}";
            sourceBitmap = null;
            minimapSource = null;
            Canvas.Children.Clear();
            _mainImage = null;
            return;
        }
        sourceBitmap = bmp;
        imageW = bmp.PixelWidth;
        imageH = bmp.PixelHeight;
        CreateMinimapSource(path, (int)(MinimapSize * 2));
        FitToWindow();
        Title = $"{Path.GetFileName(path)}  [{imageW}x{imageH}]";
    }

    private async void CreateMinimapSource(string path, int decodeSize)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.DecodePixelWidth = decodeSize;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            minimapSource = bmp;
        }
        catch { minimapSource = null; }
    }

    private static async Task<BitmapImage?> LoadWicAsync(string path)
    {
        using var fs = File.OpenRead(path);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = fs;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private double FitScale()
    {
        var (dw, dh) = EffectiveSize();
        if (dw <= 0 || dh <= 0) return 1.0;
        double vw = RootGrid.ActualWidth;
        double vh = RootGrid.ActualHeight;
        return Math.Min(vw / dw, vh / dh);
    }

    private (double w, double h) EffectiveSize()
    {
        bool swapped = rotation is 90 or 270;
        return swapped ? (imageH, imageW) : (imageW, imageH);
    }

    private void RotateImage()
    {
        if (sourceBitmap == null) return;
        rotation = (rotation + 90) % 360;
        _zoomAnimating = false;
        UpdateView();
    }

    private void FitToWidth()
    {
        if (sourceBitmap == null) return;
        var (dw, dh) = EffectiveSize();
        double vw = RootGrid.ActualWidth;
        double newZoom = vw / dw;
        double vh = RootGrid.ActualHeight;
        double newOffX = 0;
        double newOffY = (vh - dh * newZoom) / 2.0;
        AnimateTo(newZoom, newOffX, newOffY);
    }

    private void FitToHeight()
    {
        if (sourceBitmap == null) return;
        var (dw, dh) = EffectiveSize();
        double vh = RootGrid.ActualHeight;
        double newZoom = vh / dh;
        double vw = RootGrid.ActualWidth;
        double newOffX = (vw - dw * newZoom) / 2.0;
        double newOffY = 0;
        AnimateTo(newZoom, newOffX, newOffY);
    }

    private void FitToWindow()
    {
        if (sourceBitmap == null) return;
        double newZoom = FitScale();
        double vw = RootGrid.ActualWidth;
        double vh = RootGrid.ActualHeight;
        double newOffX = (vw - imageW * newZoom) / 2.0;
        double newOffY = (vh - imageH * newZoom) / 2.0;
        AnimateTo(newZoom, newOffX, newOffY);
    }

    private void ZoomTo(double z)
    {
        if (sourceBitmap == null) return;
        double vw = RootGrid.ActualWidth;
        double vh = RootGrid.ActualHeight;
        double cx = vw / 2.0;
        double cy = vh / 2.0;
        double newOffX = cx - (cx - offsetX) * (z / zoom);
        double newOffY = cy - (cy - offsetY) * (z / zoom);
        ClampOffsetFor(ref z, ref newOffX, ref newOffY);
        AnimateTo(z, newOffX, newOffY);
    }

    private void ClampOffset()
    {
        double oz = zoom;
        ClampOffsetFor(ref oz, ref offsetX, ref offsetY);
        zoom = oz;
    }

    private void ClampOffsetFor(ref double z, ref double ox, ref double oy)
    {
        var (dw, dh) = EffectiveSize();
        double vw = RootGrid.ActualWidth;
        double vh = RootGrid.ActualHeight;
        double iw = dw * z;
        double ih = dh * z;
        double vpLeft, vpTop;
        if (rotation is 90 or 270)
        {
            vpLeft = ox + (imageW * z - iw) / 2.0;
            vpTop = oy + (imageH * z - ih) / 2.0;
        }
        else { vpLeft = ox; vpTop = oy; }
        if (iw <= vw) vpLeft = (vw - iw) / 2.0;
        else vpLeft = Math.Clamp(vpLeft, vw - iw, 0);
        if (ih <= vh) vpTop = (vh - ih) / 2.0;
        else vpTop = Math.Clamp(vpTop, vh - ih, 0);
        if (rotation is 90 or 270)
        {
            ox = vpLeft - (imageW * z - iw) / 2.0;
            oy = vpTop - (imageH * z - ih) / 2.0;
        }
        else { ox = vpLeft; oy = vpTop; }
    }

    private void AnimateTo(double newZoom, double newOffX, double newOffY)
    {
        _targetZoom = newZoom;
        _targetOffsetX = newOffX;
        _targetOffsetY = newOffY;
        _zoomAnimating = true;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_zoomAnimating && sourceBitmap != null)
        {
            double t = 0.18;
            double dz = _targetZoom - zoom;
            double dx = _targetOffsetX - offsetX;
            double dy = _targetOffsetY - offsetY;
            if (Math.Abs(dz) < 0.0005 && Math.Abs(dx) < 0.3 && Math.Abs(dy) < 0.3)
            {
                zoom = _targetZoom;
                offsetX = _targetOffsetX;
                offsetY = _targetOffsetY;
                _zoomAnimating = false;
            }
            else
            {
                zoom += dz * t;
                offsetX += dx * t;
                offsetY += dy * t;
            }
            ApplyView();
            UpdateMinimap();
            UpdateZoomText();
        }
        if (_thumbScrollAnimating)
        {
            double cur = ThumbScroll.HorizontalOffset;
            double diff = _thumbScrollTarget - cur;
            if (Math.Abs(diff) < 0.5)
            {
                ThumbScroll.ScrollToHorizontalOffset(_thumbScrollTarget);
                _thumbScrollAnimating = false;
            }
            else
            {
                ThumbScroll.ScrollToHorizontalOffset(cur + diff * 0.3);
            }
        }
    }

    private void ApplyView()
    {
        if (_mainImage == null)
        {
            _mainImage = new System.Windows.Controls.Image { Stretch = Stretch.Fill };
            Canvas.Children.Add(_mainImage);
        }
        if (_mainImage.Source != sourceBitmap)
            _mainImage.Source = sourceBitmap;
        _mainImage.Width = imageW * zoom;
        _mainImage.Height = imageH * zoom;
        Canvas.SetLeft(_mainImage, offsetX);
        Canvas.SetTop(_mainImage, offsetY);
    }

    private void UpdateView()
    {
        if (sourceBitmap == null) return;
        ApplyView();
        if (rotation is 90 or 270 or 180)
        {
            _mainImage!.RenderTransform = new RotateTransform { Angle = rotation };
            _mainImage.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        else
        {
            _mainImage!.RenderTransform = null;
        }
        UpdateMinimap();
        UpdateZoomText();
    }

    private void UpdateMinimap()
    {
        if (sourceBitmap == null || cropMode || ThumbStrip.Visibility == Visibility.Visible)
        {
            MinimapBorder.Visibility = Visibility.Collapsed;
            return;
        }
        double fit = FitScale();
                bool showMinimap = zoom > fit * 1.5;
        MinimapBorder.Visibility = showMinimap ? Visibility.Visible : Visibility.Collapsed;
        if (!showMinimap) return;
        CalcMinimapSize();
        MinimapBorder.Width = minimapW + 2;
        MinimapBorder.Height = minimapH + 2;
        MinimapGrid.Children.Clear();
        var src = minimapSource ?? sourceBitmap;
        var mmImg = new System.Windows.Controls.Image
        {
            Source = src,
            Width = minimapW,
            Height = minimapH,
            Stretch = Stretch.Fill
        };
        MinimapGrid.Children.Add(mmImg);
        MinimapGrid.Children.Add(viewportRect);
        double mmScale = minimapW / imageW;
        double vw = RootGrid.ActualWidth;
        double vh = RootGrid.ActualHeight;
        double vpLeft = (-offsetX / zoom) * mmScale;
        double vpTop = (-offsetY / zoom) * mmScale;
        double vpW = (vw / zoom) * mmScale;
        double vpH = (vh / zoom) * mmScale;
        vpLeft = Math.Max(0, vpLeft);
        vpTop = Math.Max(0, vpTop);
        vpW = Math.Min(vpW, minimapW - vpLeft);
        vpH = Math.Min(vpH, minimapH - vpTop);
        Canvas.SetLeft(viewportRect, vpLeft);
        Canvas.SetTop(viewportRect, vpTop);
        viewportRect.Width = Math.Max(1, vpW);
        viewportRect.Height = Math.Max(1, vpH);
    }

    private void UpdateZoomText()
    {
        double fit = FitScale();
        bool show = zoom > fit * 1.2;
        ZoomText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ZoomText.Text = $"{(zoom / fit * 100):F0}%";
    }

    private void ToggleFullscreen()
    {
        if (isFullscreen)
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            isFullscreen = false;
        }
        else
        {
            WindowState = WindowState.Maximized;
            WindowStyle = WindowStyle.None;
            isFullscreen = true;
        }
        UpdateCanvasClip();
    }

    private static bool IsImage(string path)
    {
        var ext = Path.GetExtension(path);
        return WicExts.Contains(ext) || HeifExts.Contains(ext) || RawExts.Contains(ext);
    }

    private void ShowOpenDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open Image",
            Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tiff;*.tif;*.ico;*.heif;*.heic;*.avif;*.cr2;*.cr3;*.nef;*.arw;*.dng;*.orf;*.rw2;*.pef;*.raf;*.raw;*.erf;*.mef;*.x3f;*.srw;*.nrw;*.kdc;*.mrw|All Files|*.*",
            Multiselect = false
        };
        if (dlg.ShowDialog() == true)
        {
            _ = LoadFileAsync(dlg.FileName);
            ForceForeground();
            RootGrid.Focus();
        }
    }

    private void CtxOpen_Click(object sender, RoutedEventArgs e)
    {
        pendingOpen = true;
        Canvas.ContextMenu.Closed += OnCtxMenuClosedForOpen;
    }

    private void OnCtxMenuClosedForOpen(object? sender, EventArgs e)
    {
        Canvas.ContextMenu.Closed -= OnCtxMenuClosedForOpen;
        if (!pendingOpen) return;
        pendingOpen = false;
        ShowOpenDialog();
    }

    private void CtxRotateLeft_Click(object sender, RoutedEventArgs e)
    {
        rotation = (rotation + 270) % 360;
        _zoomAnimating = false;
        UpdateView();
    }

    private void CtxRotateRight_Click(object sender, RoutedEventArgs e)
    {
        rotation = (rotation + 90) % 360;
        _zoomAnimating = false;
        UpdateView();
    }

    private void CtxFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void CtxMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (magnifierVisible)
        {
            magnifierVisible = false;
            MagnifierBorder.Visibility = Visibility.Collapsed;
        }
        if (cropDragging || cropRepositioning)
        {
            cropDragging = false;
            cropRepositioning = false;
            RootGrid.ReleaseMouseCapture();
            ResetCropSelection();
        }
        var menu = Canvas.ContextMenu;
        if (menu == null) return;
        foreach (var item in menu.Items)
        {
            if (item is MenuItem mi && mi.Name == "CtxFullscreen")
            {
                mi.Header = isFullscreen ? "退出全屏" : "全屏";
            }
            else if (item is MenuItem mi2 && mi2.Name == "CtxCrop")
            {
                mi2.Header = cropMode ? "退出裁剪" : "裁剪";
            }
        }
    }

    private void CtxInfo_Click(object sender, RoutedEventArgs e) => ToggleInfoPanel();
    private void CtxCopy_Click(object sender, RoutedEventArgs e) => CopyToClipboard();
    private void CtxWallpaper_Click(object sender, RoutedEventArgs e) => SetWallpaper();
    private void CtxMagnifier_Click(object sender, RoutedEventArgs e)
    {
        magnifierVisible = !magnifierVisible;
        MagnifierBorder.Visibility = magnifierVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CtxDelete_Click(object sender, RoutedEventArgs e) => DeleteCurrentImage();

    private void CtxCrop_Click(object sender, RoutedEventArgs e)
    {
        if (cropMode) ExitCropMode();
        else EnterCropMode();
    }

    private void EnterCropMode()
    {
        if (sourceBitmap == null) return;
        cropMode = true;
        if (magnifierVisible) { magnifierVisible = false; MagnifierBorder.Visibility = Visibility.Collapsed; }
        _savedContextMenu = Canvas.ContextMenu;
        Canvas.ContextMenu = null;
        if (_thumbStripVisible) ThumbStrip.Visibility = Visibility.Collapsed;
        MinimapBorder.Visibility = Visibility.Collapsed;
        CropLayer.Visibility = Visibility.Visible;
        CropHint.Visibility = Visibility.Visible;
        CropToolbar.Visibility = Visibility.Collapsed;
        CropRectBorder.Visibility = Visibility.Collapsed;
        CropSizeLabel.Visibility = Visibility.Collapsed;
        CropDarkBg.Clip = null;
        RootGrid.Cursor = Cursors.Cross;
    }

    private void ExitCropMode()
    {
        cropMode = false;
        cropDragging = false;
        cropRepositioning = false;
        if (_thumbStripVisible) ThumbStrip.Visibility = Visibility.Visible;
        CropLayer.Visibility = Visibility.Collapsed;
        CropHint.Visibility = Visibility.Collapsed;
        CropToolbar.Visibility = Visibility.Collapsed;
        CropRectBorder.Visibility = Visibility.Collapsed;
        CropSizeLabel.Visibility = Visibility.Collapsed;
        CropDarkBg.Clip = null;
        Canvas.ContextMenu = _savedContextMenu;
        RootGrid.Cursor = Cursors.Arrow;
    }

    private void ResetCropSelection()
    {
        CropRectBorder.Visibility = Visibility.Collapsed;
        CropSizeLabel.Visibility = Visibility.Collapsed;
        CropToolbar.Visibility = Visibility.Collapsed;
        if (cropMode) CropHint.Visibility = Visibility.Visible;
    }

    private void ClampCropToImage()
    {
        double imgLeft = offsetX;
        double imgTop = offsetY;
        double imgRight = offsetX + imageW * zoom;
        double imgBottom = offsetY + imageH * zoom;
        cropEnd = new Point(
            Math.Clamp(cropEnd.X, imgLeft, imgRight),
            Math.Clamp(cropEnd.Y, imgTop, imgBottom));
    }

    private void ConstrainCropEnd()
    {
        if (cropRatio <= 0) return;
        double dx = cropEnd.X - cropStart.X;
        double dy = cropEnd.Y - cropStart.Y;
        if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1) return;
        double absDx = Math.Abs(dx);
        double absDy = Math.Abs(dy);
        double newH = absDx / cropRatio;
        double newW = absDy * cropRatio;
        if (newH > absDy)
            absDy = newH;
        else
            absDx = newW;
        cropEnd = new Point(
            cropStart.X + Math.Sign(dx) * absDx,
            cropStart.Y + Math.Sign(dy) * absDy);
    }

    private void UpdateCropVisual()
    {
        double x = Math.Min(cropStart.X, cropEnd.X);
        double y = Math.Min(cropStart.Y, cropEnd.Y);
        double w = Math.Abs(cropEnd.X - cropStart.X);
        double h = Math.Abs(cropEnd.Y - cropStart.Y);
        if (w < 1 || h < 1)
        {
            CropRectBorder.Visibility = Visibility.Collapsed;
            CropSizeLabel.Visibility = Visibility.Collapsed;
            CropDarkBg.Clip = null;
            return;
        }

        CropRectBorder.Visibility = Visibility.Visible;
        CropSizeLabel.Visibility = Visibility.Visible;
        var full = new Rect(0, 0, CropLayer.ActualWidth, CropLayer.ActualHeight);
        CropDarkBg.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(full), new RectangleGeometry(new Rect(x, y, w, h)));

        Canvas.SetLeft(CropRectBorder, x);
        Canvas.SetTop(CropRectBorder, y);
        CropRectBorder.Width = w;
        CropRectBorder.Height = h;

        var (iw, ih) = ScreenToImageSize(w, h);
        CropSizeLabel.Text = $"{iw} \u00d7 {ih}";
        CropSizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(CropSizeLabel, x + w / 2 - CropSizeLabel.DesiredSize.Width / 2);
        Canvas.SetTop(CropSizeLabel, y + h + 8);
    }

    private Point ScreenToImage(Point screen)
    {
        double cx = offsetX + imageW * zoom / 2.0;
        double cy = offsetY + imageH * zoom / 2.0;
        double dx = (screen.X - cx) / zoom;
        double dy = (screen.Y - cy) / zoom;
        double angle = -rotation * Math.PI / 180.0;
        double ix = dx * Math.Cos(angle) - dy * Math.Sin(angle) + imageW / 2.0;
        double iy = dx * Math.Sin(angle) + dy * Math.Cos(angle) + imageH / 2.0;
        return new Point(ix, iy);
    }

    private (int w, int h) ScreenToImageSize(double sw, double sh)
    {
        if (rotation is 90 or 270) return ((int)(sh / zoom), (int)(sw / zoom));
        return ((int)(sw / zoom), (int)(sh / zoom));
    }

    private void ShowCropToolbar()
    {
        double x = Math.Min(cropStart.X, cropEnd.X);
        double y = Math.Min(cropStart.Y, cropEnd.Y);
        double w = Math.Abs(cropEnd.X - cropStart.X);
        double h = Math.Abs(cropEnd.Y - cropStart.Y);
        var (iw, ih) = ScreenToImageSize(w, h);
        CropDimText.Text = $"{iw} \u00d7 {ih}";
        CropToolbar.Visibility = Visibility.Visible;
    }

    private void CropRatio_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CropRatioCombo == null) return;
        if (CropRatioCombo.SelectedItem is not ComboBoxItem sel) return;
        cropRatio = sel.Tag is string s && double.TryParse(s,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        if (cropDragging)
        {
            ConstrainCropEnd();
            UpdateCropVisual();
        }
    }

    private void CropSave_Click(object sender, RoutedEventArgs e)
    {
        if (sourceBitmap == null) return;
        double x = Math.Min(cropStart.X, cropEnd.X);
        double y = Math.Min(cropStart.Y, cropEnd.Y);
        double w = Math.Abs(cropEnd.X - cropStart.X);
        double h = Math.Abs(cropEnd.Y - cropStart.Y);

        var p1 = ScreenToImage(new Point(x, y));
        var p2 = ScreenToImage(new Point(x + w, y + h));
        int ix = Math.Max(0, (int)Math.Min(p1.X, p2.X));
        int iy = Math.Max(0, (int)Math.Min(p1.Y, p2.Y));
        int iw = Math.Min((int)Math.Abs(p2.X - p1.X), (int)imageW - ix);
        int ih = Math.Min((int)Math.Abs(p2.Y - p1.Y), (int)imageH - iy);
        if (iw <= 0 || ih <= 0) return;

        try
        {
            var cropped = new CroppedBitmap(sourceBitmap, new Int32Rect(ix, iy, iw, ih));
            BitmapSource finalBmp = cropped;
            if (rotation != 0)
            {
                var rotated = new TransformedBitmap(cropped, new RotateTransform(rotation));
                rotated.Freeze();
                finalBmp = rotated;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存裁剪图片",
                Filter = "PNG|*.png",
                FileName = Path.GetFileNameWithoutExtension(folder[index]) + "_crop"
            };
            if (dlg.ShowDialog() == true)
            {
                using var fs = File.Create(dlg.FileName);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(finalBmp));
                encoder.Save(fs);
            }
        }
        catch { }
        ExitCropMode();
    }

    private void CropCancel_Click(object sender, RoutedEventArgs e) => ExitCropMode();

    private void SetWallpaper()
    {
        if (sourceBitmap == null) return;
        try
        {
            var path = folder[index];
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".jpg" or ".jpeg" or ".bmp")
            {
                SystemParametersInfo(0x0014, 0, path, 0x0001 | 0x0002);
                return;
            }
            var tempPath = Path.Combine(Path.GetTempPath(), "wallpaper_temp.bmp");
            using (var fs = File.Create(tempPath))
            {
                var encoder = new BmpBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(sourceBitmap));
                encoder.Save(fs);
            }
            SystemParametersInfo(0x0014, 0, tempPath, 0x0001 | 0x0002);
        }
        catch { }
    }

    private void CopyToClipboard()
    {
        if (sourceBitmap == null) return;
        try
        {
            Clipboard.SetImage(sourceBitmap);
        }
        catch { }
    }

    private void UpdateMagnifier()
    {
        if (sourceBitmap == null) return;
        var mousePos = Mouse.GetPosition(RootGrid);
        double imgX = (mousePos.X - offsetX) / zoom;
        double imgY = (mousePos.Y - offsetY) / zoom;
        int px = (int)imgX;
        int py = (int)imgY;
        if (px < 0 || py < 0 || px >= sourceBitmap.PixelWidth || py >= sourceBitmap.PixelHeight)
        {
            MagnifierBorder.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            if (_pickerCfbSource != sourceBitmap)
            {
                var cfb = new FormatConvertedBitmap(sourceBitmap, PixelFormats.Bgra32, null, 0);
                cfb.Freeze();
                _pickerCfb = cfb;
                _pickerCfbSource = sourceBitmap;
            }
            var buf = new byte[4];
            _pickerCfb!.CopyPixels(new Int32Rect(px, py, 1, 1), buf, 4, 0);
            MagnifierColor.Text = $"#{buf[2]:X2}{buf[1]:X2}{buf[0]:X2}";
            MagnifierSwatch.Background = new SolidColorBrush(Color.FromRgb(buf[2], buf[1], buf[0]));
            MagnifierBorder.Visibility = Visibility.Visible;
        }
        catch { MagnifierBorder.Visibility = Visibility.Collapsed; }
        double mx = mousePos.X + 20;
        double my = mousePos.Y + 20;
        if (mx + 100 > RootGrid.ActualWidth) mx = mousePos.X - 100;
        if (my + 40 > RootGrid.ActualHeight) my = mousePos.Y - 40;
        MagnifierBorder.Margin = new Thickness(mx, my, 0, 0);
    }

    private async void ToggleInfoPanel()
    {
        if (index < 0 || index >= folder.Count) return;
        var gen = ++infoToggleGeneration;
        infoPanelVisible = !infoPanelVisible;
        if (!infoPanelVisible)
        {
            InfoPanel.Visibility = Visibility.Collapsed;
            return;
        }
        InfoPanel.Visibility = Visibility.Visible;
        var path = folder[index];
        try
        {
            var rows = await Task.Run(() => BuildImageInfoRows(path));
            if (infoToggleGeneration == gen && infoPanelVisible)
                PopulateInfoGrid(rows);
        }
        catch
        {
            if (infoToggleGeneration == gen && infoPanelVisible)
                PopulateInfoGrid(new List<(string, string)> { ("错误", "无法读取图片信息") });
        }
    }

    private static List<(string label, string value)> BuildImageInfoRows(string path)
    {
        var rows = new List<(string, string)>();
        var fi = new FileInfo(path);
        rows.Add(("文件名", fi.Name));
        rows.Add(("文件大小", FormatSize(fi.Length)));
        rows.Add(("修改时间", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")));
        var ext = Path.GetExtension(path);
        var exif = ReadTiffExif(path);
        if (exif.TryGetValue("ImageWidth", out var w) && exif.TryGetValue("ImageHeight", out var h))
            rows.Add(("图片尺寸", $"{w} x {h}"));
        else if (!RawExts.Contains(ext))
        {
            try
            {
                using var fs = File.OpenRead(path);
                var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                rows.Add(("图片尺寸", $"{decoder.Frames[0].PixelWidth} x {decoder.Frames[0].PixelHeight}"));
            }
            catch { }
        }
        if (exif.Count > 0)
        {
            foreach (var kv in exif)
            {
                if (kv.Key.StartsWith("Image")) continue;
                rows.Add((kv.Key, kv.Value));
            }
        }
        return rows;
    }

    private void PopulateInfoGrid(List<(string label, string value)> rows)
    {
        InfoGrid.RowDefinitions.Clear();
        InfoGrid.Children.Clear();
        var font = new FontFamily("Cascadia Mono, Consolas");
        var labelBrush = new SolidColorBrush(Color.FromArgb(255, 0xAA, 0xAA, 0xAA));
        var valueBrush = new SolidColorBrush(Color.FromArgb(255, 0xDD, 0xDD, 0xDD));
        for (int i = 0; i < rows.Count; i++)
        {
            InfoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var labelText = new TextBlock
            {
                Text = rows[i].label,
                Foreground = labelBrush,
                FontSize = 12,
                FontFamily = font,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 2)
            };
            Grid.SetRow(labelText, i);
            Grid.SetColumn(labelText, 0);
            InfoGrid.Children.Add(labelText);
            var valueText = new TextBlock
            {
                Text = rows[i].value,
                Foreground = valueBrush,
                FontSize = 12,
                FontFamily = font,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 2)
            };
            Grid.SetRow(valueText, i);
            Grid.SetColumn(valueText, 2);
            InfoGrid.Children.Add(valueText);
        }
    }

    private static Dictionary<string, string> ReadTiffExif(string path)
    {
        var result = new Dictionary<string, string>();
        try
        {
            var fi = new FileInfo(path);
            int readSize = (int)Math.Min(fi.Length, 524288);
            var buf = new byte[readSize];
            using var fs = File.OpenRead(path);
            fs.ReadExactly(buf, 0, readSize);

            int tiffStart = -1;
            if (buf[0] == 'I' && buf[1] == 'I' && buf[2] == 0x2a && buf[3] == 0x00)
                tiffStart = 0;
            else if (buf[0] == 'M' && buf[1] == 'M' && buf[2] == 0x00 && buf[3] == 0x2a)
                tiffStart = 0;
            else
            {
                tiffStart = FindTiffHeader(buf, readSize);
                if (tiffStart < 0)
                {
                    int exifOff = FindExifMarker(buf, readSize);
                    if (exifOff >= 0) ParseJpegExif(buf, exifOff, result);
                    return result;
                }
            }

            bool le = buf[tiffStart + 1] == 'I';
            uint ifdOff = ReadU32(buf, tiffStart + 4, le) + (uint)tiffStart;
            if (ifdOff + 2 > readSize) return result;
            uint? exifSubIfd = null;
            ParseIfd(buf, (int)ifdOff, readSize, le, tiffStart, result, ref exifSubIfd);
            if (exifSubIfd.HasValue && exifSubIfd.Value + 2 <= readSize)
                ParseIfd(buf, (int)exifSubIfd.Value, readSize, le, tiffStart, result, ref exifSubIfd);
        }
        catch { }
        return result;
    }

    private static int FindTiffHeader(byte[] buf, int len)
    {
        for (int i = 0; i < len - 4; i++)
        {
            if (buf[i] == 'I' && buf[i + 1] == 'I' && buf[i + 2] == 0x2a && buf[i + 3] == 0x00)
                return i;
            if (buf[i] == 'M' && buf[i + 1] == 'M' && buf[i + 2] == 0x00 && buf[i + 3] == 0x2a)
                return i;
        }
        return -1;
    }

    private static void ParseIfd(byte[] buf, int ifdPos, int bufLen, bool le, int baseOff,
        Dictionary<string, string> result, ref uint? exifSubIfd)
    {
        if (ifdPos + 2 > bufLen) return;
        ushort entryCount = ReadU16(buf, ifdPos, le);
        for (int i = 0; i < entryCount; i++)
        {
            int e = ifdPos + 2 + i * 12;
            if (e + 12 > bufLen) break;
            ushort tag = ReadU16(buf, e, le);
            ushort type = ReadU16(buf, e + 2, le);
            uint cnt = ReadU32(buf, e + 4, le);
            if (tag == 0x8769)
            {
                exifSubIfd = ReadU32(buf, e + 8, le) + (uint)baseOff;
                continue;
            }
            string? tagName = tag switch
            {
                0x0100 => "ImageWidth",
                0x0101 => "ImageHeight",
                0x010F => "相机品牌",
                0x0110 => "相机型号",
                0x829d => "光圈",
                0x8827 => "ISO",
                0x829a => "快门",
                0x920a => "焦距",
                0x9003 => "拍摄时间",
                0x9209 => "闪光补偿",
                0xA434 => "镜头型号",
                0xA433 => "镜头品牌",
                _ => null
            };
            if (tagName == null) continue;
            string val = ReadIfdValue(buf, type, cnt, e, le, bufLen, tag, baseOff);
            if (!string.IsNullOrEmpty(val))
                result[tagName] = val;
        }
    }

    private static string ReadIfdValue(byte[] buf, ushort type, uint cnt, int entryOff, bool le, int bufLen, ushort tag, int baseOff)
    {
        try
        {
            if (type == 2)
            {
                uint strOff = cnt <= 4 ? (uint)(entryOff + 8) : ReadU32(buf, entryOff + 8, le) + (uint)baseOff;
                if (strOff + cnt <= bufLen)
                    return Encoding.ASCII.GetString(buf, (int)strOff, (int)Math.Min(cnt, 200)).TrimEnd('\0').Trim();
                return "";
            }
            else if (type == 3 && cnt == 1)
                return FormatExifShort(ReadU16(buf, entryOff + 8, le));
            else if (type == 4 && cnt == 1)
                return ReadU32(buf, entryOff + 8, le).ToString();
            else if (type == 5 && cnt == 1)
            {
                uint num = ReadU32(buf, entryOff + 8, le);
                uint den = ReadU32(buf, entryOff + 12, le);
                return FormatExifRational(tag, num, den);
            }
            else if (type == 5 && cnt > 1)
            {
                uint off = ReadU32(buf, entryOff + 8, le) + (uint)baseOff;
                if (off + 8 <= bufLen)
                    return FormatExifRational(tag, ReadU32(buf, (int)off, le), ReadU32(buf, (int)off + 4, le));
            }
            else if (type == 10 && cnt == 1)
            {
                int num = (int)ReadU32(buf, entryOff + 8, le);
                int den = (int)ReadU32(buf, entryOff + 12, le);
                return den != 0 ? FormatExifRational(tag, (uint)Math.Abs(num), (uint)Math.Abs(den)) : num.ToString();
            }
        }
        catch { }
        return "";
    }

    private static int FindExifMarker(byte[] buf, int len)
    {
        for (int i = 0; i < len - 10; i++)
        {
            if (buf[i] == 0xFF && buf[i + 1] == 0xE1
                && buf[i + 4] == 'E' && buf[i + 5] == 'x'
                && buf[i + 6] == 'i' && buf[i + 7] == 'f')
                return i + 10;
        }
        return -1;
    }

    private static void ParseJpegExif(byte[] buf, int exifStart, Dictionary<string, string> result)
    {
        if (exifStart + 8 > buf.Length) return;
        bool le = buf[exifStart + 1] == 'I';
        if (buf[exifStart] == 'M') le = false;
        else if (buf[exifStart] != 'I') return;
        uint ifdOff = ReadU32(buf, exifStart + 4, le) + (uint)exifStart;
        if (ifdOff + 2 > buf.Length) return;
        uint? exifSubIfd = null;
        ParseIfd(buf, (int)ifdOff, buf.Length, le, exifStart, result, ref exifSubIfd);
        if (exifSubIfd.HasValue && exifSubIfd.Value + 2 <= buf.Length)
        {
            uint? dummy = null;
            ParseIfd(buf, (int)exifSubIfd.Value, buf.Length, le, exifStart, result, ref dummy);
        }
    }

    private static ushort ReadU16(byte[] b, int off, bool le)
        => le ? (ushort)(b[off] | (b[off + 1] << 8)) : (ushort)((b[off] << 8) | b[off + 1]);

    private static uint ReadU32(byte[] b, int off, bool le)
        => le ? (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24))
              : (uint)((b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3]);

    private static string FormatExifRational(ushort tag, uint num, uint den)
    {
        if (den == 0) return "";
        double val = (double)num / den;
        return tag switch
        {
            0x829d => $"f/{val:F1}",
            0x829a => val >= 1 ? $"{val:F1}s" : $"1/{1.0 / val:F0}s",
            0x920a => $"{val:F1} mm",
            _ => $"{num}/{den}"
        };
    }

    private static string FormatExifShort(ushort v) => v.ToString();

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1048576) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1073741824) return $"{bytes / 1048576.0:F1} MB";
        return $"{bytes / 1073741824.0:F2} GB";
    }

    private void ToggleThumbStrip()
    {
        _thumbStripVisible = !_thumbStripVisible;
        if (_thumbStripVisible)
        {
            ThumbStrip.Visibility = Visibility.Visible;
            MinimapBorder.Visibility = Visibility.Collapsed;
            if (ThumbStack.Children.Count == 0 && folder.Count > 0)
                PopulateThumbStrip();
        }
        else
        {
            ThumbStrip.Visibility = Visibility.Collapsed;
            UpdateMinimap();
        }
    }

    private readonly Color _selBgColor = Color.FromRgb(0x3E, 0x3E, 0x42);
    private readonly Color _hoverColor = Color.FromArgb(60, 255, 255, 255);

    private void PopulateThumbStrip()
    {
        ThumbStack.Children.Clear();
        foreach (var path in folder)
        {
            var container = new Border
            {
                Width = 100,
                Height = 94,
                Margin = new Thickness(5, 0, 5, 0),
                Cursor = Cursors.Hand,
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                ToolTip = Path.GetFileName(path)
            };

            var innerGrid = new Grid();

            var imgBorder = new Border
            {
                CornerRadius = new CornerRadius(4),
                ClipToBounds = true,
                Margin = new Thickness(4, 4, 4, 0),
            };
            var img = new System.Windows.Controls.Image
            {
                Stretch = Stretch.UniformToFill,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            imgBorder.Child = img;

            var selOverlay = new Border
            {
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(4),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(2)
            };

            var idx = ThumbStack.Children.Count;

            container.MouseEnter += (_, _) =>
            {
                if (idx != index)
                    container.Background = new SolidColorBrush(_hoverColor);
            };
            container.MouseLeave += (_, _) =>
            {
                if (idx != index)
                    container.Background = Brushes.Transparent;
            };
            container.MouseLeftButtonDown += (s, e) =>
            {
                if (e.StylusDevice != null) return;
                if (idx != index) _ = ShowImageAsync(idx);
            };

            innerGrid.Children.Add(imgBorder);
            innerGrid.Children.Add(selOverlay);
            container.Child = innerGrid;
            ThumbStack.Children.Add(container);
        }
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            () => LoadVisibleThumbnails());
        UpdateThumbSelection();
    }

    private void OnThumbScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        LoadVisibleThumbnails();
    }

    private void LoadVisibleThumbnails()
    {
        const double itemWidth = 110; // 100 width + 5+5 margin
        double viewLeft = ThumbScroll.HorizontalOffset;
        double viewRight = viewLeft + ThumbScroll.ViewportWidth;
        int first = Math.Max(0, (int)(viewLeft / itemWidth) - 10);
        int last = Math.Min(ThumbStack.Children.Count - 1,
            (int)Math.Ceiling(viewRight / itemWidth) + 10);

        for (int i = 0; i < ThumbStack.Children.Count; i++)
        {
            if (ThumbStack.Children[i] is Border container
                && container.Child is Grid grid
                && grid.Children.Count > 0
                && grid.Children[0] is Border imgBorder
                && imgBorder.Child is System.Windows.Controls.Image img)
            {
                if (i >= first && i <= last)
                {
                    if (img.Source == null && !_thumbLoading.Contains(i))
                    {
                        _thumbLoading.Add(i);
                        LoadThumbnailAsync(folder[i], img, i);
                    }
                }
                else
                {
                    _thumbLoading.Remove(i);
                    img.Source = null;
                }
            }
        }
    }

    private async void LoadThumbnailAsync(string path, System.Windows.Controls.Image img, int idx)
    {
        try
        {
            var bmp = await Task.Run(() =>
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.DecodePixelWidth = 160;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(path, UriKind.Absolute);
                bi.EndInit();
                bi.Freeze();
                return bi;
            });
            if (img.Source == null)
                img.Source = bmp;
            _thumbLoading.Remove(idx);
        }
        catch { _thumbLoading.Remove(idx); }
    }

    private void UpdateThumbSelection()
    {
        if (ThumbStack.Children.Count == 0) return;
        for (int i = 0; i < ThumbStack.Children.Count; i++)
        {
            if (ThumbStack.Children[i] is Border container
                && container.Child is Grid grid
                && grid.Children.Count == 2
                && grid.Children[1] is Border overlay)
            {
                bool selected = i == index;
                container.Background = selected
                    ? new SolidColorBrush(_selBgColor) : Brushes.Transparent;
                overlay.Visibility = selected
                    ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        if (index >= 0 && index < ThumbStack.Children.Count
            && ThumbStack.Children[index] is FrameworkElement el)
        {
            el.BringIntoView(new Rect(0, 0, 104, 94));
        }
    }

    private void OnThumbStripMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _thumbScrollTarget = ThumbScroll.HorizontalOffset - e.Delta * 2.0;
        _thumbScrollTarget = Math.Clamp(_thumbScrollTarget, 0,
            ThumbScroll.ScrollableWidth);
        _thumbScrollAnimating = true;
        e.Handled = true;
    }
}
