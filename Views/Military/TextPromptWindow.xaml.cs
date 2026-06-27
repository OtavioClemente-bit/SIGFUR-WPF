using System.Windows;

namespace SIGFUR.Wpf.Views.Military;

public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string description, string value = "")
    {
        InitializeComponent();
        App.UiState.Attach(this);
        Title = title;
        TitleText.Text = title;
        DescriptionText.Text = description;
        ValueBox.Text = value;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.CaretIndex = ValueBox.Text.Length; };
    }

    public string Value => ValueBox.Text.Trim();
    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
