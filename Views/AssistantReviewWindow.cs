using System.Windows;
using System.Windows.Controls;

namespace SIGFUR.Wpf.Views;

/// <summary>Review and explicit application of an AI draft, isolated from bound domain fields.</summary>
public sealed class AssistantReviewWindow : Window
{
    private readonly TextBox _text;
    public string ReviewedText => _text.Text.Trim();

    public AssistantReviewWindow(Window owner, string title, string text, bool canApply = false, string original = "", string sources = "")
    {
        Owner = owner; Title = "SIGFUR — " + title;
        Width = 960; Height = 740; MinWidth = 680; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        var root = new DockPanel { Margin = new Thickness(18) };
        var heading = new TextBlock { Text = canApply ? "Revise e edite a minuta. O campo atual só muda ao aplicar." : "Parecer IA para revisão — consulte os documentos citados antes de decidir.", TextWrapping = TextWrapping.Wrap, FontSize = 16, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        if (!string.IsNullOrWhiteSpace(sources))
        {
            var references = new Expander { Header = "Fontes recuperadas para esta minuta", Margin = new Thickness(0, 0, 0, 10),
                Content = new TextBox { Text = sources, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            DockPanel.SetDock(references, Dock.Top); root.Children.Add(references);
        }
        if (canApply && !string.IsNullOrWhiteSpace(original))
        {
            var previous = new Expander { Header = "Texto atual do campo (preservado)", Margin = new Thickness(0, 0, 0, 10), Content = new TextBox { Text = original, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            DockPanel.SetDock(previous, Dock.Top); root.Children.Add(previous);
        }
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        _text = new TextBox { Text = text, IsReadOnly = !canApply, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12), FontSize = 14 };
        AddButton(buttons, "Copiar", () => Clipboard.SetText(_text.Text));
        if (canApply) AddButton(buttons, "Aplicar minuta revisada", () => { if (!string.IsNullOrWhiteSpace(ReviewedText)) DialogResult = true; });
        AddButton(buttons, canApply ? "Descartar" : "Fechar", Close);
        root.Children.Add(_text); Content = root;
    }

    private void AddButton(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
        button.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }
}
