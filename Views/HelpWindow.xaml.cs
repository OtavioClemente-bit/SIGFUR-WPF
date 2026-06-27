using System.Windows;

namespace SIGFUR.Wpf.Views;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        App.UiState.Attach(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
