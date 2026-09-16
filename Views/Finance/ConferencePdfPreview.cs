using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

/// <summary>Local PDF preview: no browser, external process, or temporary copy of personnel files.</summary>
public sealed class ConferencePdfPreview : UserControl
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _status = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ComboBox _zoom = new() { Width = 105, ItemsSource = new[] { "Ajustar largura", "100%", "125%", "150%", "200%" }, SelectedIndex = 0 };
    private PdfDocument? _document;
    private string _path = "";
    private int _page;
    private int _request;
    private string _highlightName = "";

    public ConferencePdfPreview(string title)
    {
        var root = new DockPanel { Background = Brushes.White, Margin = new Thickness(4) };
        var heading = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8) };
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        var previous = new Button { Content = "◀ Página", Padding = new Thickness(8, 4, 8, 4) };
        var next = new Button { Content = "Página ▶", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(4, 0, 8, 0) };
        previous.Click += async (_, _) => { if (_document is not null && _page > 0) await RenderAsync(_page - 1); };
        next.Click += async (_, _) => { if (_document is not null && _page + 1 < _document.PageCount) await RenderAsync(_page + 1); };
        toolbar.Children.Add(previous); toolbar.Children.Add(next); toolbar.Children.Add(_zoom);
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        DockPanel.SetDock(_status, Dock.Top); root.Children.Add(_status);
        _scroll.Content = _image; root.Children.Add(_scroll); Content = root;
        _zoom.SelectionChanged += (_, _) => ResizeImage();
        _scroll.SizeChanged += (_, _) => ResizeImage();
    }

    public async Task ShowAsync(string path, int page = 1, string highlightName = "")
    {
        var request = ++_request;
        _highlightName = highlightName;
        _image.Source = null; _status.Text = "Carregando prévia...";
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        { _document = null; _path = ""; _status.Text = "Não há PDF localizado para este item na competência selecionada."; return; }
        try
        {
            var document = _path == path && _document is not null ? _document
                : await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)));
            if (request != _request) return;
            _document = document; _path = path;
            await RenderAsync(Math.Clamp(page - 1, 0, (int)document.PageCount - 1));
        }
        catch (Exception ex) { if (request == _request) _status.Text = "Não foi possível exibir o PDF: " + ex.Message; }
    }

    private async Task RenderAsync(int pageIndex)
    {
        if (_document is null) return;
        var request = ++_request;
        var document = _document;
        var path = _path; var highlightName = _highlightName;
        _page = pageIndex; _image.Source = null;
        _status.Text = $"{Path.GetFileName(_path)} · página {_page + 1}/{document.PageCount}";
        try
        {
            using var page = document.GetPage((uint)pageIndex);
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = 1800 });
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
            using var memory = new MemoryStream(bytes);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory; bitmap.EndInit(); bitmap.Freeze();
            if (request != _request) return;
            IReadOnlyList<ConferenceNameHighlight.Box> boxes = [];
            if (highlightName.Length > 0)
            {
                try { boxes = await Task.Run(() => ConferenceNameHighlight.Find(path, pageIndex + 1, highlightName)); }
                catch { /* A text-location failure must not hide the readable PDF preview. */ }
            }
            if (request != _request) return;
            if (boxes.Count > 0)
            {
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
                    var yellow = new SolidColorBrush(Color.FromArgb(105, 255, 220, 0));
                    foreach (var box in boxes) drawing.DrawRectangle(yellow, null,
                        new Rect(box.X * bitmap.PixelWidth - 1, box.Y * bitmap.PixelHeight - 2,
                            box.Width * bitmap.PixelWidth + 2, box.Height * bitmap.PixelHeight + 4));
                }
                var marked = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
                marked.Render(visual); marked.Freeze(); _image.Source = marked;
            }
            else { _image.Source = bitmap; if (highlightName.Length > 0) _status.Text += " · Nome não localizado nesta página"; }
            ResizeImage(); _scroll.ScrollToTop(); _scroll.ScrollToLeftEnd();
            if (boxes.Count > 0) _scroll.ScrollToVerticalOffset(Math.Max(0, boxes[0].Y * _image.Width * bitmap.PixelHeight / bitmap.PixelWidth - 100));
        }
        catch (Exception ex) { if (request == _request) _status.Text = "Falha ao renderizar página: " + ex.Message; }
    }

    private void ResizeImage()
    {
        _image.Width = _zoom.SelectedIndex switch { 1 => 800, 2 => 1000, 3 => 1200, 4 => 1600, _ => Math.Max(200, _scroll.ActualWidth - 22) };
    }
}
