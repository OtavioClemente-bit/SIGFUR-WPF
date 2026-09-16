using System.Windows;
using System.Windows.Controls;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Bulletin;

namespace SIGFUR.Wpf.Controls;

public sealed class BulletinTableInputControl : Grid
{
    private BulletinTableData _table;
    private readonly TextBlock _summary;

    public BulletinTableInputControl(string? value, BulletinFieldDefinition field)
    {
        var initial = string.IsNullOrWhiteSpace(value) ? field.TableDefaultValue : value;
        _table = BulletinTableCodec.ParseOrCreate(initial, field.TableRows, field.TableColumns, field.TableWidthPercent, field.TableHasHeader);
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var summaryBorder = new Border { Padding = new Thickness(11, 8, 11, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) };
        summaryBorder.SetResourceReference(Border.BackgroundProperty, "PrimarySurfaceBrush");
        summaryBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        _summary = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        summaryBorder.Child = _summary;
        Children.Add(summaryBorder);

        var paste = new Button { Content = "Colar tabela", Padding = new Thickness(13, 7, 13, 7), MinWidth = 112, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Stretch };
        paste.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        paste.Click += Paste_Click;
        SetColumn(paste, 1);
        Children.Add(paste);

        var edit = new Button { Content = "Criar/editar", Padding = new Thickness(13, 7, 13, 7), MinWidth = 112, VerticalAlignment = VerticalAlignment.Stretch };
        edit.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        edit.Click += Edit_Click;
        SetColumn(edit, 2);
        Children.Add(edit);
        UpdateSummary();
    }

    public string Value => _table.Cells.Any(row => row.Any(cell => !string.IsNullOrWhiteSpace(cell)))
        ? BulletinTableCodec.Serialize(_table)
        : string.Empty;
    public event RoutedEventHandler? ValueChanged;

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (!BulletinTableClipboardImporter.TryReadClipboard(out var imported, out _))
        {
            SigfurDialog.Show(Window.GetWindow(this),
                "Não encontrei uma tabela copiada. No Word ou Excel, selecione a tabela inteira, pressione Ctrl+C e tente novamente.",
                "Colar tabela", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _table = imported;
        UpdateSummary();
        ValueChanged?.Invoke(this, new RoutedEventArgs());
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var window = new BulletinTableEditorWindow(_table) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        _table = window.Table;
        UpdateSummary();
        ValueChanged?.Invoke(this, new RoutedEventArgs());
    }

    private void UpdateSummary()
    {
        var filled = _table.Cells.Sum(row => row.Count(cell => !string.IsNullOrWhiteSpace(cell)));
        _summary.Text = filled == 0
            ? "Nenhuma tabela informada • copie do Word/Excel ou crie manualmente"
            : $"{_table.Rows} linhas × {_table.Columns} colunas • largura {_table.WidthPercent}% • {filled} célula(s) preenchida(s)";
    }
}
