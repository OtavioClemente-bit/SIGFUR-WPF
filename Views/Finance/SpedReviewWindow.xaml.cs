using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public partial class SpedReviewWindow : Window
{
    private readonly string _processDirectory;

    public SpedReviewWindow(SpedAutomationResult result, string processDirectory)
    {
        InitializeComponent();
        _processDirectory = processDirectory;
        MessageText.Text = result.Message;
        UrlText.Text = result.Url;
        ScreenshotList.ItemsSource = result.ScreenshotPaths.Where(File.Exists).Select(path => new ScreenshotItem(path)).ToList();
        if (ScreenshotList.Items.Count > 0) ScreenshotList.SelectedIndex = 0;
        App.UiState.Attach(this);
    }

    private void ScreenshotList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScreenshotList.SelectedItem is not ScreenshotItem item) return;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(item.Path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        PreviewImage.Source = image;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => ShellService.OpenPath(_processDirectory);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record ScreenshotItem(string Path)
    {
        public string Name => System.IO.Path.GetFileName(Path);
    }
}
