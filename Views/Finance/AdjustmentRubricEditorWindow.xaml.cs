using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public partial class AdjustmentRubricEditorWindow : Window
{
    public AdjustmentRubric Rubric { get; private set; }

    public AdjustmentRubricEditorWindow(AdjustmentRubric rubric)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        Rubric = rubric.Clone();
        CodeBox.Text = Rubric.Code;
        DescriptionBox.Text = Rubric.Description;
        SelectByTag(ReferenceBox, Rubric.Reference);
        SelectByTag(SignBox, Rubric.Sign);
        SelectByTag(BaseBox, Rubric.Base);
        SelectByTag(KindBox, Rubric.Kind);
        ValueBox.Text = Rubric.Value.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"));
        IncludedCheck.IsChecked = Rubric.IsIncluded;
        Loaded += (_, _) => RefreshOfficialInfo();
    }

    private static void SelectByTag(ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (!string.Equals(Convert.ToString(item.Tag), value, StringComparison.OrdinalIgnoreCase)) continue;
            comboBox.SelectedItem = item;
            return;
        }
        comboBox.SelectedIndex = 0;
    }

    private static string SelectedTag(ComboBox comboBox, string fallback)
        => comboBox.SelectedItem is ComboBoxItem item ? Convert.ToString(item.Tag) ?? fallback : fallback;

    private void CodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        RefreshOfficialInfo();
    }

    private void RefreshOfficialInfo()
    {
        var official = SippesRubricCatalog.Find(CodeBox.Text);
        if (official is null)
        {
            OfficialInfoText.Text = "Código não localizado no catálogo. A rubrica pode ser mantida como manual.";
            return;
        }
        OfficialInfoText.Text = $"SIPPES: {official.Description} • {official.RubricType} • {official.Nature} • {official.EffectText}.";
    }

    private void CodeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var official = SippesRubricCatalog.Find(CodeBox.Text);
        if (official is null) return;
        var currentDescription = DescriptionBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(currentDescription) || currentDescription.Equals("NOVA RUBRICA", StringComparison.OrdinalIgnoreCase))
            DescriptionBox.Text = official.Description;
        SelectByTag(ReferenceBox, official.Effect == "D" ? "D" : "R");
        SelectByTag(SignBox, official.Effect == "D" ? "-" : "+");
        RefreshOfficialInfo();
    }

    private void SearchSippes_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SippesRubricPickerWindow(CodeBox.Text) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedRubric is null) return;
        ApplyOfficialRubric(picker.SelectedRubric);
    }

    private void ApplyOfficialRubric(SippesRubricRecord official)
    {
        CodeBox.Text = official.Code;
        DescriptionBox.Text = official.Description;
        SelectByTag(ReferenceBox, official.Effect == "D" ? "D" : "R");
        SelectByTag(SignBox, official.Effect == "D" ? "-" : "+");
        RefreshOfficialInfo();
    }

    private void ReferenceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        SelectByTag(SignBox, SelectedTag(ReferenceBox, "R") == "D" ? "-" : "+");
    }

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var kind = SelectedTag(KindBox, "FIXO");
        ValueBox.IsEnabled = kind is "FIXO" or "PERC_SOLDO" or "PERC_VENC";
        if (!ValueBox.IsEnabled) ValueBox.Text = "0";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim().ToUpperInvariant();
        var description = DescriptionBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(description))
        {
            SigfurDialog.Show(this, "Informe o código e a descrição da rubrica.", "Ajuste de Contas", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryParseDecimal(ValueBox.Text, out var value))
        {
            SigfurDialog.Show(this, "Valor ou percentual inválido.", "Ajuste de Contas", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selectedReference = SelectedTag(ReferenceBox, "R");
        var selectedSign = SelectedTag(SignBox, selectedReference == "D" ? "-" : "+");
        var official = SippesRubricCatalog.Find(code);
        if (official is not null)
        {
            var officialReference = official.Effect == "D" ? "D" : "R";
            var officialSign = official.Effect == "D" ? "-" : "+";
            if (selectedReference != officialReference || selectedSign != officialSign)
            {
                var keep = SigfurDialog.Show(this,
                    $"A rubrica {code} consta no catálogo SIPPES como {(official.Effect == "D" ? "desconto" : "rendimento")} ({officialReference}{officialSign}).\n\nVocê está salvando como {selectedReference}{selectedSign}.\n\nDeseja manter essa divergência?",
                    "Conferir efeito da rubrica", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (keep != MessageBoxResult.Yes) return;
            }
        }

        Rubric = new AdjustmentRubric
        {
            Id = Rubric.Id,
            Code = code,
            Description = description,
            Reference = selectedReference,
            Sign = selectedSign,
            Base = SelectedTag(BaseBox, "DIA"),
            Kind = SelectedTag(KindBox, "FIXO"),
            Value = value,
            IsIncluded = IncludedCheck.IsChecked == true,
            IsCustom = true
        };
        DialogResult = true;
    }

    private static bool TryParseDecimal(string? text, out decimal value)
    {
        var cleaned = (text ?? string.Empty).Trim().Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"), out value)
               || decimal.TryParse(cleaned.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}
