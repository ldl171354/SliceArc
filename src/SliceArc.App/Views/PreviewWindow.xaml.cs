using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SliceArc.Core.Extraction;

namespace SliceArc.App.Views;

/// <summary>
/// In-archive image preview, Bandizip-style: double-click a picture in the browser and
/// see it without extracting anything to disk.
/// <para>
/// The bytes come straight out of the archive through ExtractionOptions.OutputFactory -
/// the same path drag-out uses - so a preview leaves no temp file behind, which is the
/// product's promise. Images are decoded into memory; an entry larger than
/// <see cref="MaxEntryBytes"/> is refused rather than OOM-ing the process on a zip bomb.
/// </para>
/// </summary>
public partial class PreviewWindow : Window
{
    /// <summary>Refuse to buffer anything bigger than this into a preview.</summary>
    public const long MaxEntryBytes = 64L * 1024 * 1024;

    /// <summary>Extensions the decoder is actually likely to handle.</summary>
    public static bool IsPreviewable(string entryPath)
    {
        var ext = Path.GetExtension(entryPath).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp"
            or ".webp" or ".tif" or ".tiff" or ".ico";
    }

    private readonly string _archivePath;
    private readonly IReadOnlyList<string> _images;   // entry paths, in list order
    private int _index;
    private string? _password;
    private double _zoom = 1;
    private Point _panOrigin;
    private Point _dragStart;
    private bool _dragging;

    /// <param name="archivePath">Archive to read from.</param>
    /// <param name="imageEntryPaths">Previewable image entry paths, in browser order.</param>
    /// <param name="startIndex">Which entry to open on.</param>
    /// <param name="password">Password already known for this archive, if any.</param>
    public PreviewWindow(string archivePath, IReadOnlyList<string> imageEntryPaths, int startIndex, string? password)
    {
        InitializeComponent();
        _archivePath = archivePath;
        _images = imageEntryPaths;
        _index = Math.Clamp(startIndex, 0, Math.Max(0, _images.Count - 1));
        _password = password;
        PrevButton.IsEnabled = _images.Count > 1;
        NextButton.IsEnabled = _images.Count > 1;
        // Grid has no double-click event; detect it from click count on mouse-up.
        Viewport.MouseLeftButtonUp += OnViewportClick;
        Loaded += async (_, _) => await LoadAsync();
    }

    private void UpdateCount()
    {
        CountText.Text = _images.Count > 1
            ? $"第 {_index + 1} / {_images.Count} 张"
            : "1 张图片";
    }

    private async Task LoadAsync()
    {
        var entry = _images[_index];
        TitleText.Text = entry.Substring(entry.LastIndexOf('/') + 1);
        UpdateCount();
        ImageHost.Source = null;
        HintText.Text = "正在从压缩包读取…";
        InfoText.Text = "";

        var bytes = await Task.Run(() => ReadEntryBytes(entry));
        if (bytes is null)
        {
            HintText.Text = "读取失败：口令不对，或压缩包已损坏。";
            return;
        }

        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // decode now; the stream can close
            bmp.EndInit();
            bmp.Freeze();                                  // cross-thread safety
            ImageHost.Source = bmp;
            HintText.Text = "";
            ResetView(bmp);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException)
        {
            HintText.Text = "这个图片格式 Windows 解码器不支持（如部分 WebP/AVIF）。";
        }
    }

    /// <summary>
    /// Pull exactly one entry into memory through the extraction engine. OutputFactory
    /// keeps it off the disk; IncludePaths keeps the engine from walking the rest.
    /// </summary>
    private byte[]? ReadEntryBytes(string entryPath)
    {
        try
        {
            MemoryStream? buffer = null;
            var options = new ExtractionOptions
            {
                OutputDirectory = Path.GetTempPath(),          // never used: OutputFactory writes nothing
                IncludePaths = new[] { entryPath },
                Password = _password,
                Overwrite = true,
                OutputFactory = _ => new CapturedStream(buffer ??= new MemoryStream()),
                TestOnly = false,
            };
            using var engine = new ExtractionEngine(_archivePath);
            engine.Password = _password;
            var result = engine.Extract(options);
            if (buffer is null)
            {
                if (result.NeedsPassword || result.WrongPassword)
                {
                    var entered = Dispatcher.Invoke(() =>
                        PasswordWindow.Prompt(this, Path.GetFileName(_archivePath), retry: result.WrongPassword));
                    if (entered is null) return null;
                    _password = entered;
                    return ReadEntryBytes(entryPath);          // one retry with the new password
                }
                return null;
            }
            return buffer.ToArray();
        }
        catch (PasswordRequiredException)
        {
            var entered = Dispatcher.Invoke(() => PasswordWindow.Prompt(this, Path.GetFileName(_archivePath)));
            if (entered is null) return null;
            _password = entered;
            return ReadEntryBytes(entryPath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Write-through view over one buffer: the engine's writer lands in the MemoryStream,
    /// the caller reads the bytes afterwards. Seek support matters because image decoders
    /// need it.
    /// </summary>
    private sealed class CapturedStream : Stream
    {
        private readonly MemoryStream _inner;
        public CapturedStream(MemoryStream inner) => _inner = inner;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }

    // ---------------- zoom / pan ----------------

    private void ResetView(BitmapSource bmp)
    {
        var vw = Viewport.ActualWidth > 0 ? Viewport.ActualWidth : ActualWidth;
        var vh = Viewport.ActualHeight > 0 ? Viewport.ActualHeight : ActualHeight;
        var fit = Math.Min((vw - 40) / bmp.PixelWidth, (vh - 40) / bmp.PixelHeight);
        SetZoom(Math.Clamp(double.IsNaN(fit) || fit <= 0 ? 1 : fit, 0.02, 1.0), centered: true);
        InfoText.Text = $"{bmp.PixelWidth} × {bmp.PixelHeight} px";
    }

    private void SetZoom(double value, bool centered)
    {
        var old = _zoom;
        _zoom = Math.Clamp(value, 0.02, 32);
        Zoom.ScaleX = _zoom;
        Zoom.ScaleY = _zoom;
        if (centered && ImageHost.Source is BitmapSource src)
        {
            Pan.X = (Viewport.ActualWidth - src.PixelWidth * _zoom) / 2;
            Pan.Y = (Viewport.ActualHeight - src.PixelHeight * _zoom) / 2;
        }
        else if (ImageHost.Source is not null)
        {
            // Keep the cursor's point roughly anchored while wheel-zooming.
            var k = _zoom / old;
            var c = Mouse.GetPosition(Viewport);
            Pan.X = c.X - (c.X - Pan.X) * k;
            Pan.Y = c.Y - (c.Y - Pan.Y) * k;
        }
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        if (ImageHost.Source is BitmapSource src)
            InfoText.Text = $"{src.PixelWidth} × {src.PixelHeight} px · {_zoom:P0}";
    }

    private void OnWheel(object sender, MouseWheelEventArgs e) =>
        SetZoom(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), centered: false);

    private void OnZoomIn(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.25, centered: false);
    private void OnZoomOut(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.25, centered: false);

    private void OnPanStart(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _panOrigin = new Point(Pan.X, Pan.Y);
        Viewport.CaptureMouse();
    }

    private void OnPanMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        Pan.X = _panOrigin.X + p.X - _dragStart.X;
        Pan.Y = _panOrigin.Y + p.Y - _dragStart.Y;
    }

    private void OnPanEnd(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Viewport.ReleaseMouseCapture();
    }

    /// <summary>Double-click on the canvas resets to fit-the-window.</summary>
    private void OnViewportClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && ImageHost.Source is BitmapSource src) ResetView(src);
    }

    private void OnFitClick(object sender, RoutedEventArgs e)
    {
        if (ImageHost.Source is BitmapSource src) ResetView(src);
    }

    // ---------------- navigation ----------------

    private void OnPrev(object sender, RoutedEventArgs e) => Step(-1);
    private void OnNext(object sender, RoutedEventArgs e) => Step(1);

    private void Step(int delta)
    {
        if (_images.Count < 2) return;
        _index = (_index + delta + _images.Count) % _images.Count;
        _ = LoadAsync();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.Left) Step(-1);
        else if (e.Key == Key.Right) Step(1);
        else if (e.Key == Key.D0 || e.Key == Key.NumPad0) { if (ImageHost.Source is BitmapSource s) ResetView(s); }
    }
}
