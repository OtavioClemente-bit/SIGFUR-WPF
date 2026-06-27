using Microsoft.Win32;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.ViewModels.Military;

namespace SIGFUR.Wpf.Views.Military;

public partial class MilitaryEditorWindow : Window
{
    private readonly MilitaryEditorViewModel _vm;
    private readonly MilitaryPreferenceService? _preferences;
    private readonly Dictionary<Control, object?> _originalToolTips = [];
    private bool _validationWired;
    private bool _validationReady;
    public int SavedMilitaryId { get; private set; }

    public MilitaryEditorWindow(MilitaryRepository repository, MilitaryRecord military, MilitaryPreferenceService? preferences = null)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _preferences = preferences;
        _vm = new MilitaryEditorViewModel(repository, military);
        DataContext = _vm;
        Title = military.Id <= 0 ? "SIGFUR — Novo Militar" : $"SIGFUR — Editar {military.Name}";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.InitializeAsync();
            LoadPhoto(_vm.Military.PhotoPath);
            WireValidationControls();
            _validationReady = true;
            ApplyCadastroValidation(focusFirstInvalid: false);
        }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ChoosePhoto_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Selecionar foto do militar", Filter = "Imagens|*.png;*.jpg;*.jpeg;*.webp;*.bmp|Todos os arquivos|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        _vm.Military.PhotoPath = dialog.FileName;
        LoadPhoto(dialog.FileName);
        _vm.StatusText = "Foto selecionada. Salve para confirmar.";
    }

    private void RemovePhoto_Click(object sender, RoutedEventArgs e)
    {
        _vm.Military.PhotoPath = string.Empty;
        PhotoPreview.Source = null;
        _vm.StatusText = "Foto removida do cadastro. Salve para confirmar.";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (IsLiveValidationEnabled() && !ApplyCadastroValidation(focusFirstInvalid: true))
            {
                _vm.StatusText = "Corrija os campos destacados em vermelho antes de salvar.";
                return;
            }

            SavedMilitaryId = await _vm.SaveAsync();
            if (_preferences is not null)
            {
                try { await _preferences.SetAttachedAsync(_vm.Military, _vm.Military.IsAttached); }
                catch { /* O cadastro já foi salvo no SQLite; não força uma segunda gravação. */ }
            }
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ApplyCadastroValidation(focusFirstInvalid: true);
            SigfurDialog.Show(this, ex.Message, "Não foi possível salvar", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void LoadPhoto(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { PhotoPreview.Source = null; return; }
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path, UriKind.Absolute); image.EndInit(); image.Freeze();
            PhotoPreview.Source = image;
        }
        catch { PhotoPreview.Source = null; }
    }

    private void WireValidationControls()
    {
        if (_validationWired) return;
        _validationWired = true;

        foreach (var control in CadastroValidationControls())
        {
            _originalToolTips.TryAdd(control, control.ToolTip);
            switch (control)
            {
                case TextBox box:
                    box.TextChanged += (_, _) => ApplyCadastroValidation(focusFirstInvalid: false);
                    break;
                case ComboBox combo:
                    combo.SelectionChanged += (_, _) => ApplyCadastroValidation(focusFirstInvalid: false);
                    combo.LostKeyboardFocus += (_, _) => ApplyCadastroValidation(focusFirstInvalid: false);
                    break;
            }
        }
    }

    private IReadOnlyList<Control> CadastroValidationControls() =>
    [
        RankBox, FormationYearBox, NameBox, WarNameBox, CpfBox, PrecBox, MilitaryIdBox,
        BirthDateBox, EnlistmentDateBox, PhoneBox, EmailBox, ZipCodeBox, BankBox, AgencyBox, AccountBox,
        PreSchoolCombo, PreSchoolValueBox, TransportCombo, TransportAidValueBox, AlimonyCombo, AlimonyValueBox
    ];

    private bool ApplyCadastroValidation(bool focusFirstInvalid)
    {
        if (!_validationReady) return true;
        if (!IsLiveValidationEnabled())
        {
            ClearCadastroValidationVisuals();
            return true;
        }

        var invalid = new List<(Control Control, string Message)>();
        void Check(Control control, bool ok, string message)
        {
            if (ok) MarkValid(control);
            else
            {
                MarkInvalid(control, message);
                invalid.Add((control, message));
            }
        }

        Check(RankBox, !IsBlank(_vm.Military.Rank), "Posto/graduação é obrigatório.");
        Check(NameBox, !IsBlank(_vm.Military.Name), "Nome completo é obrigatório.");
        Check(WarNameBox, !IsBlank(_vm.Military.WarName), "Nome de guerra é obrigatório.");
        Check(CpfBox, IsValidCpf(_vm.Military.Cpf), "CPF inválido. Informe 11 dígitos corretos.");
        Check(PrecBox, !IsBlank(_vm.Military.PrecCp), "PREC-CP é obrigatório.");
        Check(MilitaryIdBox, !IsBlank(_vm.Military.MilitaryId), "IDT militar é obrigatório.");

        Check(FormationYearBox, IsValidOptionalYear(_vm.Military.FormationYear), "Ano de formação inválido.");
        Check(BirthDateBox, IsValidOptionalDate(_vm.Military.BirthDate), "Data de nascimento inválida. Use dd/mm/aaaa.");
        Check(EnlistmentDateBox, IsValidOptionalDate(_vm.Military.EnlistmentDate), "Data de praça inválida. Use dd/mm/aaaa.");
        Check(PhoneBox, IsValidOptionalPhone(_vm.Military.Phone), "Telefone inválido. Use DDD + número.");
        Check(EmailBox, IsValidOptionalEmail(_vm.Military.Email), "E-mail inválido.");
        Check(ZipCodeBox, IsValidOptionalCep(_vm.Military.ZipCode), "CEP inválido. Informe 8 dígitos.");

        var hasAnyBankData = !IsBlank(_vm.Military.Bank) || !IsBlank(_vm.Military.Agency) || !IsBlank(_vm.Military.Account);
        Check(BankBox, !hasAnyBankData || !IsBlank(_vm.Military.Bank), "Banco obrigatório quando agência ou conta forem informadas.");
        Check(AgencyBox, !hasAnyBankData || !IsBlank(_vm.Military.Agency), "Agência obrigatória quando houver dados bancários.");
        Check(AccountBox, !hasAnyBankData || !IsBlank(_vm.Military.Account), "Conta obrigatória quando houver dados bancários.");

        Check(PreSchoolValueBox, IsValidMoneyForBenefit(_vm.Military.PreSchoolValue, _vm.Military.ReceivesPreSchool), "Valor do Pré-Escolar inválido.");
        Check(TransportAidValueBox, IsValidMoneyForBenefit(_vm.Military.TransportAidValue, _vm.Military.ReceivesTransportAid), "Valor do Auxílio-Transporte inválido.");
        Check(AlimonyValueBox, IsValidMoneyForBenefit(_vm.Military.AlimonyValue, _vm.Military.Alimony), "Valor da pensão inválido.");

        ValidationSummaryText.Text = invalid.Count == 0
            ? string.Empty
            : $"{invalid.Count} campo(s) precisam de atenção. Passe o mouse no campo vermelho para ver o motivo.";

        if (invalid.Count == 0)
        {
            if (_validationReady && !IsBusySafe()) _vm.StatusText = "Cadastro validado. Pode salvar.";
            return true;
        }

        if (focusFirstInvalid)
        {
            invalid[0].Control.Focus();
            if (invalid[0].Control is TextBox box)
            {
                box.SelectAll();
            }
        }

        if (_validationReady && !IsBusySafe()) _vm.StatusText = $"Corrija {invalid.Count} campo(s) destacado(s) em vermelho.";
        return false;
    }

    private bool IsBusySafe()
    {
        try { return _vm.IsBusy; }
        catch { return false; }
    }

    private bool IsLiveValidationEnabled()
        => LiveValidationCheck?.IsChecked != false;

    private void ValidationMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_validationReady) return;
        if (IsLiveValidationEnabled()) ApplyCadastroValidation(focusFirstInvalid: false);
        else ClearCadastroValidationVisuals();
    }

    private void ClearCadastroValidationVisuals()
    {
        foreach (var control in CadastroValidationControls()) MarkValid(control);
        ValidationSummaryText.Text = string.Empty;
        if (_validationReady && !IsBusySafe()) _vm.StatusText = "Validação visual desativada. O salvamento ainda confere os obrigatórios.";
    }

    private void MarkInvalid(Control control, string message)
    {
        var border = new SolidColorBrush(Color.FromRgb(211, 47, 47));
        var background = new SolidColorBrush(Color.FromRgb(255, 242, 242));
        control.BorderBrush = border;
        control.BorderThickness = new Thickness(2);
        control.Background = background;
        control.ToolTip = message;
    }

    private void MarkValid(Control control)
    {
        control.ClearValue(Control.BorderBrushProperty);
        control.ClearValue(Control.BorderThicknessProperty);
        control.ClearValue(Control.BackgroundProperty);
        control.ToolTip = _originalToolTips.TryGetValue(control, out var tip) ? tip : null;
    }

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);
    private static string Digits(string? value) => MilitaryFormatting.Digits(value);

    private static bool IsValidCpf(string? value)
    {
        var cpf = Digits(value);
        if (cpf.Length != 11) return false;
        if (cpf.Distinct().Count() == 1) return false;

        static int Digit(string cpf, int length)
        {
            var sum = 0;
            for (var i = 0; i < length; i++) sum += (cpf[i] - '0') * (length + 1 - i);
            var mod = sum % 11;
            return mod < 2 ? 0 : 11 - mod;
        }

        return Digit(cpf, 9) == cpf[9] - '0' && Digit(cpf, 10) == cpf[10] - '0';
    }

    private static bool IsValidOptionalDate(string? value)
        => string.IsNullOrWhiteSpace(value) || MilitaryFormatting.ParseDate(value) is not null;

    private static bool IsValidOptionalYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var digits = Digits(value);
        if (digits.Length != 4 || !int.TryParse(digits, out var year)) return false;
        return year is >= 1900 and <= 2100;
    }

    private static bool IsValidOptionalPhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var digits = Digits(value);
        return digits.Length is 10 or 11;
    }

    private static bool IsValidOptionalEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        return Regex.IsMatch(value.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.IgnoreCase);
    }

    private static bool IsValidOptionalCep(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        return Digits(value).Length == 8;
    }

    private static bool IsValidMoneyForBenefit(string? value, string? yesNo)
    {
        var required = MilitaryRecord.IsYes(yesNo);
        if (string.IsNullOrWhiteSpace(value)) return !required;
        if (!TryParseMoney(value, out var amount)) return false;
        return !required || amount > 0m;
    }

    private static bool TryParseMoney(string? value, out decimal amount)
    {
        amount = 0m;
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.GetCultureInfo("pt-BR"), out amount)
            || decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize(); else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
