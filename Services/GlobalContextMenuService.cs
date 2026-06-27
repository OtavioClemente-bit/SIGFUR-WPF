using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Data;
using System.Windows.Media;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Views.Military;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Instala menus de contexto e revisão ortográfica coerentes em todas as telas nativas do SIGFUR.
/// Campos que não devem usar o corretor podem definir Tag="SpellCheck.Disabled".
/// </summary>
public static class GlobalContextMenuService
{
    private const string GeneratedTextMenuTag = "SIGFUR.GLOBAL.TEXT.CONTEXT";
    private const string GeneratedGridMenuTag = "SIGFUR.GLOBAL.GRID.CONTEXT";
    private const string GeneratedSelectorMenuTag = "SIGFUR.GLOBAL.SELECTOR.CONTEXT";
    private const string GeneratedSimpleMenuTag = "SIGFUR.GLOBAL.SIMPLE.CONTEXT";
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        EventManager.RegisterClassHandler(typeof(TextBoxBase), FrameworkElement.LoadedEvent, new RoutedEventHandler(TextBoxLoaded));
        EventManager.RegisterClassHandler(typeof(TextBoxBase), ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler(TextContextOpening));
        EventManager.RegisterClassHandler(typeof(PasswordBox), ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler(PasswordContextOpening));
        EventManager.RegisterClassHandler(typeof(DataGrid), FrameworkElement.LoadedEvent, new RoutedEventHandler(GridLoaded));
        EventManager.RegisterClassHandler(typeof(DataGrid), ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler(GridContextOpening));
        EventManager.RegisterClassHandler(typeof(ListBox), ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler(SelectorContextOpening));
        EventManager.RegisterClassHandler(typeof(TreeView), ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler(TreeContextOpening));
        EventManager.RegisterClassHandler(typeof(ComboBox), ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler(ComboBoxContextOpening));
        EventManager.RegisterClassHandler(typeof(DatePicker), ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler(DatePickerContextOpening));
        EventManager.RegisterClassHandler(typeof(FlowDocumentScrollViewer), ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler(FlowDocumentContextOpening));
        EventManager.RegisterClassHandler(typeof(FlowDocumentReader), ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler(FlowDocumentContextOpening));
        EventManager.RegisterClassHandler(typeof(FlowDocumentPageViewer), ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler(FlowDocumentContextOpening));
        EventManager.RegisterClassHandler(typeof(TextBlock), ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler(TextBlockContextOpening));
    }

    private static void TextBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBoxBase box || IsSpellCheckDisabled(box)) return;
        try
        {
            box.Language = XmlLanguage.GetLanguage("pt-BR");
            SpellCheck.SetIsEnabled(box, true);
            SpellCheck.SetSpellingReform(box, SpellingReform.Postreform);
        }
        catch
        {
            // O dicionário instalado no Windows pode variar. O campo continua utilizável.
        }
    }

    private static bool IsSpellCheckDisabled(FrameworkElement element)
        => element.Tag?.ToString()?.Contains("SpellCheck.Disabled", StringComparison.OrdinalIgnoreCase) == true;

    private static MenuItem CommandItem(string text, ICommand command, IInputElement target)
        => new() { Header = text, Command = command, CommandTarget = target };

    private static void TextContextOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TextBoxBase box) return;
        if (box.ContextMenu is not null && !Equals(box.ContextMenu.Tag, GeneratedTextMenuTag)) return;

        var menu = new ContextMenu { Tag = GeneratedTextMenuTag };
        AddSpellingItems(menu, box);

        if (!box.IsReadOnly)
        {
            menu.Items.Add(CommandItem("Desfazer", ApplicationCommands.Undo, box));
            menu.Items.Add(new Separator());
            menu.Items.Add(CommandItem("Recortar", ApplicationCommands.Cut, box));
        }
        menu.Items.Add(CommandItem("Copiar", ApplicationCommands.Copy, box));
        if (!box.IsReadOnly) menu.Items.Add(CommandItem("Colar", ApplicationCommands.Paste, box));
        menu.Items.Add(new Separator());
        menu.Items.Add(CommandItem("Selecionar tudo", ApplicationCommands.SelectAll, box));

        if (!box.IsReadOnly && !IsSpellCheckDisabled(box))
        {
            menu.Items.Add(new Separator());
            var review = new MenuItem { Header = "✦ Corrigir português com IA" };
            review.Click += async (_, _) => await ReviewWithAiAsync(box,
                "Corrija integralmente o português brasileiro: ortografia, acentuação, concordância, regência e pontuação. Preserve rigorosamente os fatos, nomes, números, siglas e referências.");
            menu.Items.Add(review);

            var formal = new MenuItem { Header = "✦ Ajustar para linguagem administrativa" };
            formal.Click += async (_, _) => await ReviewWithAiAsync(box,
                "Reescreva em linguagem administrativa militar formal, clara, objetiva e respeitosa. Corrija também ortografia e pontuação. Preserve fatos, nomes, números, datas, siglas e referências.");
            menu.Items.Add(formal);

            var custom = new MenuItem { Header = "✦ Revisar com instrução específica..." };
            custom.Click += async (_, _) =>
            {
                var owner = Window.GetWindow(box);
                var prompt = new TextPromptWindow(
                    "Revisar texto com IA",
                    "Informe como o trecho selecionado ou o texto completo deve ser revisado.",
                    "Corrija e melhore a clareza sem alterar os fatos.") { Owner = owner };
                if (prompt.ShowDialog() == true && !string.IsNullOrWhiteSpace(prompt.Value))
                    await ReviewWithAiAsync(box, prompt.Value);
            };
            menu.Items.Add(custom);
        }

        box.ContextMenu = menu;
    }

    private static void AddSpellingItems(ContextMenu menu, TextBoxBase box)
    {
        if (IsSpellCheckDisabled(box) || !SpellCheck.GetIsEnabled(box)) return;
        SpellingError? error = null;
        try
        {
            error = box switch
            {
                TextBox textBox => textBox.GetSpellingError(textBox.CaretIndex),
                RichTextBox richTextBox => richTextBox.GetSpellingError(richTextBox.CaretPosition),
                _ => null
            };
        }
        catch { }
        if (error is null) return;

        var suggestions = error.Suggestions.Cast<string>().Take(8).ToList();
        if (suggestions.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Nenhuma sugestão ortográfica", IsEnabled = false });
        else
        {
            foreach (var suggestion in suggestions)
            {
                var item = new MenuItem { Header = suggestion, FontWeight = FontWeights.SemiBold };
                item.Click += (_, _) => { try { error.Correct(suggestion); } catch { } };
                menu.Items.Add(item);
            }
        }
        var ignore = new MenuItem { Header = "Ignorar todas as ocorrências" };
        ignore.Click += (_, _) => { try { error.IgnoreAll(); } catch { } };
        menu.Items.Add(ignore);
        menu.Items.Add(new Separator());
    }

    private static async Task ReviewWithAiAsync(TextBoxBase box, string instruction)
    {
        var source = GetTextForReview(box, out var replaceSelectionOnly);
        if (string.IsNullOrWhiteSpace(source))
        {
            SigfurDialog.Show(Window.GetWindow(box), "Selecione um trecho ou informe algum texto antes de solicitar a revisão.",
                "Revisão de texto", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var owner = Window.GetWindow(box);
        var oldCursor = owner?.Cursor;
        try
        {
            if (owner is not null) owner.Cursor = Cursors.Wait;
            box.IsEnabled = false;
            var settings = await App.AssistantStorage.LoadSettingsAsync();
            var revised = await App.Assistant.RewriteTextAsync(source, instruction, settings);
            ReplaceReviewedText(box, revised, replaceSelectionOnly);
        }
        catch (Exception ex)
        {
            if (owner is not null)
                SigfurDialog.Show(owner, ex.Message, "Revisão de texto", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                SigfurDialog.Show(ex.Message, "Revisão de texto", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            box.IsEnabled = true;
            if (owner is not null) owner.Cursor = oldCursor;
            box.Focus();
        }
    }

    private static string GetTextForReview(TextBoxBase box, out bool selectionOnly)
    {
        selectionOnly = false;
        if (box is TextBox textBox)
        {
            selectionOnly = textBox.SelectionLength > 0;
            return selectionOnly ? textBox.SelectedText : textBox.Text;
        }
        if (box is RichTextBox richTextBox)
        {
            selectionOnly = !richTextBox.Selection.IsEmpty;
            return selectionOnly
                ? richTextBox.Selection.Text
                : new TextRange(richTextBox.Document.ContentStart, richTextBox.Document.ContentEnd).Text.TrimEnd('\r', '\n');
        }
        return string.Empty;
    }

    private static void ReplaceReviewedText(TextBoxBase box, string revised, bool selectionOnly)
    {
        if (box is TextBox textBox)
        {
            if (selectionOnly) textBox.SelectedText = revised;
            else
            {
                textBox.Text = revised;
                textBox.CaretIndex = textBox.Text.Length;
            }
            return;
        }
        if (box is RichTextBox richTextBox)
        {
            if (selectionOnly) richTextBox.Selection.Text = revised;
            else new TextRange(richTextBox.Document.ContentStart, richTextBox.Document.ContentEnd).Text = revised;
        }
    }

    private static void PasswordContextOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not PasswordBox box || box.ContextMenu is not null) return;
        var menu = new ContextMenu();
        var paste = new MenuItem { Header = "Colar" };
        paste.Click += (_, _) => { if (Clipboard.ContainsText()) box.Password = Clipboard.GetText(); };
        var clear = new MenuItem { Header = "Limpar" };
        clear.Click += (_, _) => box.Clear();
        menu.Items.Add(paste);
        menu.Items.Add(clear);
        box.ContextMenu = menu;
    }

    private static void GridLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        try
        {
            if (grid.IsReadOnly && grid.SelectionUnit == DataGridSelectionUnit.Cell)
                grid.SelectionUnit = DataGridSelectionUnit.FullRow;
            if (grid.ClipboardCopyMode == DataGridClipboardCopyMode.None)
                grid.ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader;
        }
        catch
        {
            // Menu global não pode impedir a abertura de nenhuma janela.
        }
    }

    private static void GridContextOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (grid.ContextMenu is not null && !Equals(grid.ContextMenu.Tag, GeneratedGridMenuTag)) return;

        var menu = new ContextMenu { Tag = GeneratedGridMenuTag };

        var copyCell = new MenuItem { Header = "Copiar célula/linha atual" };
        copyCell.Click += (_, _) =>
        {
            try
            {
                if (ApplicationCommands.Copy.CanExecute(null, grid))
                    ApplicationCommands.Copy.Execute(null, grid);
                else
                    CopySelected(GetGridTargetRows(grid));
            }
            catch { CopySelected(GetGridTargetRows(grid)); }
        };

        var copyRows = new MenuItem { Header = "Copiar linha(s) selecionada(s)" };
        copyRows.Click += (_, _) => CopySelected(GetGridTargetRows(grid));

        var copyAll = new MenuItem { Header = "Copiar tabela visível" };
        copyAll.Click += (_, _) => CopySelected(grid.Items.Cast<object>().Where(x => x is not CollectionViewGroup));

        var selectAll = new MenuItem { Header = "Selecionar tudo" };
        selectAll.Click += (_, _) => { try { grid.SelectAll(); } catch { } };

        var clearSelection = new MenuItem { Header = "Limpar seleção" };
        clearSelection.Click += (_, _) => { try { grid.UnselectAll(); } catch { } };

        menu.Items.Add(copyCell);
        menu.Items.Add(copyRows);
        menu.Items.Add(copyAll);
        menu.Items.Add(new Separator());
        menu.Items.Add(selectAll);
        menu.Items.Add(clearSelection);

        var boolProperty = FindCheckBoxProperty(grid);
        if (!string.IsNullOrWhiteSpace(boolProperty))
        {
            menu.Items.Add(new Separator());
            var mark = new MenuItem { Header = "Marcar selecionados" };
            mark.Click += (_, _) => SetBooleanOnSelectedRows(grid, boolProperty, true);
            var unmark = new MenuItem { Header = "Desmarcar selecionados" };
            unmark.Click += (_, _) => SetBooleanOnSelectedRows(grid, boolProperty, false);
            var invert = new MenuItem { Header = "Inverter marcação dos selecionados" };
            invert.Click += (_, _) => InvertBooleanOnSelectedRows(grid, boolProperty);
            menu.Items.Add(mark);
            menu.Items.Add(unmark);
            menu.Items.Add(invert);
        }

        grid.ContextMenu = menu;
    }

    private static IEnumerable<object> GetGridTargetRows(DataGrid grid)
    {
        var selected = grid.SelectedItems.Cast<object>().Where(x => x is not CollectionViewGroup).ToList();
        if (selected.Count > 0) return selected;
        return grid.SelectedItem is null ? [] : [grid.SelectedItem];
    }

    private static string? FindCheckBoxProperty(DataGrid grid)
    {
        try
        {
            foreach (var column in grid.Columns.OfType<DataGridCheckBoxColumn>())
            {
                if (column.Binding is not Binding binding) continue;
                var path = binding.Path?.Path;
                if (string.IsNullOrWhiteSpace(path) || path.Contains('.', StringComparison.Ordinal)) continue;
                var sample = grid.SelectedItem ?? grid.Items.Cast<object>().FirstOrDefault(x => x is not CollectionViewGroup);
                if (sample is null) continue;
                var property = sample.GetType().GetProperty(path, BindingFlags.Instance | BindingFlags.Public);
                if (property is null || !property.CanWrite) continue;
                var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (type == typeof(bool)) return path;
            }
        }
        catch { }
        return null;
    }

    private static void SetBooleanOnSelectedRows(DataGrid grid, string propertyName, bool value)
    {
        foreach (var row in GetGridTargetRows(grid)) SetBoolean(row, propertyName, value);
        try { grid.Items.Refresh(); } catch { }
    }

    private static void InvertBooleanOnSelectedRows(DataGrid grid, string propertyName)
    {
        foreach (var row in GetGridTargetRows(grid))
        {
            try
            {
                var property = row.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                if (property is null || !property.CanRead || !property.CanWrite) continue;
                var current = property.GetValue(row);
                var currentBool = current is bool b && b;
                property.SetValue(row, !currentBool);
            }
            catch { }
        }
        try { grid.Items.Refresh(); } catch { }
    }

    private static void SetBoolean(object row, string propertyName, bool value)
    {
        try
        {
            var property = row.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property is null || !property.CanWrite) return;
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (type != typeof(bool)) return;
            property.SetValue(row, value);
        }
        catch { }
    }

    private static void SelectorContextOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Selector selector) return;
        if (selector.ContextMenu is not null && !Equals(selector.ContextMenu.Tag, GeneratedSelectorMenuTag)) return;
        var menu = new ContextMenu { Tag = GeneratedSelectorMenuTag };
        var copy = new MenuItem { Header = "Copiar selecionado" };
        copy.Click += (_, _) => CopySelected(selector.SelectedItemsOrSingle());
        var selectAll = new MenuItem { Header = "Selecionar tudo" };
        selectAll.Click += (_, _) =>
        {
            if (selector is ListBox list)
            {
                try { list.SelectAll(); } catch { }
            }
        };
        var clear = new MenuItem { Header = "Limpar seleção" };
        clear.Click += (_, _) =>
        {
            if (selector is ListBox list)
            {
                try { list.UnselectAll(); } catch { }
            }
            else selector.SelectedIndex = -1;
        };
        menu.Items.Add(copy);
        if (selector is ListBox) { menu.Items.Add(new Separator()); menu.Items.Add(selectAll); }
        menu.Items.Add(clear);
        selector.ContextMenu = menu;
    }

    private static void TreeContextOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TreeView tree) return;
        if (tree.ContextMenu is not null && !Equals(tree.ContextMenu.Tag, GeneratedSimpleMenuTag)) return;
        var menu = new ContextMenu { Tag = GeneratedSimpleMenuTag };
        var copy = new MenuItem { Header = "Copiar selecionado" };
        copy.Click += (_, _) => { if (tree.SelectedItem is not null) CopySelected([tree.SelectedItem]); };
        menu.Items.Add(copy);
        tree.ContextMenu = menu;
    }

    private static void ComboBoxContextOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (combo.ContextMenu is not null && !Equals(combo.ContextMenu.Tag, GeneratedSimpleMenuTag)) return;
        var menu = new ContextMenu { Tag = GeneratedSimpleMenuTag };
        var copy = new MenuItem { Header = "Copiar valor" };
        copy.Click += (_, _) =>
        {
            var text = combo.IsEditable ? combo.Text : combo.SelectionBoxItem?.ToString();
            if (!string.IsNullOrWhiteSpace(text)) Clipboard.SetText(text);
        };
        var paste = new MenuItem { Header = "Colar" };
        paste.IsEnabled = combo.IsEditable;
        paste.Click += (_, _) => { if (Clipboard.ContainsText()) combo.Text = Clipboard.GetText(); };
        var clear = new MenuItem { Header = "Limpar seleção" };
        clear.Click += (_, _) => { combo.SelectedIndex = -1; if (combo.IsEditable) combo.Text = string.Empty; };
        menu.Items.Add(copy);
        if (combo.IsEditable) menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        menu.Items.Add(clear);
        combo.ContextMenu = menu;
    }

    private static void DatePickerContextOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DatePicker picker) return;
        if (picker.ContextMenu is not null && !Equals(picker.ContextMenu.Tag, GeneratedSimpleMenuTag)) return;
        var menu = new ContextMenu { Tag = GeneratedSimpleMenuTag };
        var today = new MenuItem { Header = "Usar data de hoje" };
        today.Click += (_, _) => picker.SelectedDate = DateTime.Today;
        var copy = new MenuItem { Header = "Copiar data" };
        copy.Click += (_, _) =>
        {
            if (picker.SelectedDate is DateTime d) Clipboard.SetText(d.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR")));
        };
        var paste = new MenuItem { Header = "Colar data" };
        paste.Click += (_, _) =>
        {
            if (Clipboard.ContainsText() && DateTime.TryParse(Clipboard.GetText(), CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var date))
                picker.SelectedDate = date;
        };
        var clear = new MenuItem { Header = "Limpar data" };
        clear.Click += (_, _) => picker.SelectedDate = null;
        menu.Items.Add(today);
        menu.Items.Add(new Separator());
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(clear);
        picker.ContextMenu = menu;
    }

    private static void FlowDocumentContextOpening(object sender, ContextMenuEventArgs e)
    {
        FlowDocument? document = sender switch
        {
            FlowDocumentScrollViewer scrollViewer => scrollViewer.Document,
            FlowDocumentReader reader => reader.Document as FlowDocument,
            FlowDocumentPageViewer pageViewer => pageViewer.Document as FlowDocument,
            _ => null
        };
        if (document is null) return;
        if (sender is not FrameworkElement element) return;
        if (element.ContextMenu is not null && !Equals(element.ContextMenu.Tag, GeneratedSimpleMenuTag)) return;
        var menu = new ContextMenu { Tag = GeneratedSimpleMenuTag };
        var copyAll = new MenuItem { Header = "Copiar texto da prévia" };
        copyAll.Click += (_, _) =>
        {
            var text = new TextRange(document.ContentStart, document.ContentEnd).Text.Trim();
            if (!string.IsNullOrWhiteSpace(text)) Clipboard.SetText(text);
        };
        menu.Items.Add(copyAll);
        element.ContextMenu = menu;
    }

    private static void TextBlockContextOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TextBlock block) return;
        if (IsInsideInteractiveContextHost(block)) return;
        if (block.ContextMenu is not null && !Equals(block.ContextMenu.Tag, GeneratedSimpleMenuTag)) return;
        var text = GetTextBlockText(block);
        if (string.IsNullOrWhiteSpace(text)) return;
        var menu = new ContextMenu { Tag = GeneratedSimpleMenuTag };
        var copy = new MenuItem { Header = "Copiar texto" };
        copy.Click += (_, _) => Clipboard.SetText(GetTextBlockText(block));
        menu.Items.Add(copy);
        block.ContextMenu = menu;
    }

    private static string GetTextBlockText(TextBlock block)
    {
        if (!string.IsNullOrWhiteSpace(block.Text)) return block.Text;
        var sb = new StringBuilder();
        foreach (var inline in block.Inlines)
        {
            switch (inline)
            {
                case Run run:
                    sb.Append(run.Text);
                    break;
                case LineBreak:
                    sb.AppendLine();
                    break;
                case Span span:
                    foreach (var child in span.Inlines.OfType<Run>()) sb.Append(child.Text);
                    break;
            }
        }
        return sb.ToString().Trim();
    }

    private static bool IsInsideInteractiveContextHost(DependencyObject source)
    {
        try
        {
            var current = VisualTreeHelper.GetParent(source);
            while (current is not null)
            {
                if (current is DataGrid or ListBox or TreeView or ComboBox or ButtonBase)
                    return true;
                current = VisualTreeHelper.GetParent(current);
            }
        }
        catch { }
        return false;
    }

    private static IEnumerable<object> SelectedItemsOrSingle(this Selector selector)
    {
        if (selector is ListBox list) return list.SelectedItems.Cast<object>();
        return selector.SelectedItem is null ? [] : [selector.SelectedItem];
    }

    private static void CopySelected(IEnumerable<object> rows)
    {
        var lines = rows.Select(ToUsefulText).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (lines.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, lines));
    }

    private static string ToUsefulText(object value)
    {
        if (value is AssistantMessageView assistantView) return assistantView.Content;
        if (value is AssistantConversationMessage assistantMessage) return assistantMessage.Content;

        var props = value.GetType().GetProperties()
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Where(p => p.PropertyType == typeof(string) || p.PropertyType.IsPrimitive
                        || p.PropertyType == typeof(DateTime) || p.PropertyType == typeof(DateTime?))
            .Take(12);
        var parts = new List<string>();
        foreach (var property in props)
        {
            try
            {
                var v = property.GetValue(value)?.ToString();
                if (!string.IsNullOrWhiteSpace(v)) parts.Add(v);
            }
            catch { }
        }
        return parts.Count == 0 ? value.ToString() ?? string.Empty : string.Join(" | ", parts.Distinct());
    }
}
