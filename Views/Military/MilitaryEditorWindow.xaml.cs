using Microsoft.Win32;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private readonly MilitaryRecord _sourceMilitary;
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
        _sourceMilitary = military.Clone();
        _vm = new MilitaryEditorViewModel(repository, military);
        DataContext = _vm;
        Title = military.Id <= 0 ? "SIGFUR — Novo Militar" : $"SIGFUR — Editar {military.Name}";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.InitializeAsync();
            SyncEditorFieldsFromModel();
            // Reaplica depois do layout: alguns templates de ComboBox editável
            // só mostram o texto depois que o ItemsSource terminou de renderizar.
            Dispatcher.BeginInvoke(new Action(SyncEditorFieldsFromModel), System.Windows.Threading.DispatcherPriority.ContextIdle);
            _ = ReinforceCadastroIdentityFieldsAsync();
            LoadPhoto(_vm.Military.PhotoPath);
            WireValidationControls();
            _validationReady = true;
            ApplyCadastroValidation(focusFirstInvalid: false);
        }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Window_ContentRendered(object? sender, EventArgs e)
    {
        // O serviço global de estado/escala também atua após a renderização.
        // Rodamos esta sequência depois dele para impedir que o editor abra com
        // Posto/Graduação ou Banco visualmente vazios quando os dados existem.
        _ = ReinforceCadastroIdentityFieldsAsync();
    }

    private async Task ReinforceCadastroIdentityFieldsAsync()
    {
        foreach (var delay in new[] { 60, 260, 650 })
        {
            await Task.Delay(delay);
            if (!IsLoaded) return;
            await Dispatcher.InvokeAsync(SyncEditorFieldsFromModel, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    private void ChoosePhoto_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Selecionar foto do militar", Filter = "Imagens|*.png;*.jpg;*.jpeg;*.webp;*.bmp|Todos os arquivos|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var storedPath = StorePhotoInSigfurData(dialog.FileName, _vm.Military);
            _vm.Military.PhotoPath = storedPath;
            LoadPhoto(storedPath);
            _vm.StatusText = "Foto copiada para a pasta do SIGFUR. Salve para confirmar.";
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "Foto do militar", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            CommitEditorFieldsToModel();
            if (string.IsNullOrWhiteSpace(_vm.Military.CareerType))
                _vm.Military.CareerType = MilitaryCareerTypeService.SuggestForRank(_vm.Military.Rank) ?? string.Empty;

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

    private async void LookupZipCode_Click(object sender, RoutedEventArgs e)
    {
        var cep = Digits(ZipCodeBox.Text);
        if (cep.Length != 8)
        {
            MarkInvalid(ZipCodeBox, "Informe um CEP com 8 dígitos para pesquisar.");
            ZipCodeBox.Focus();
            ZipCodeBox.SelectAll();
            SigfurDialog.Show(this, "Informe um CEP com 8 dígitos para pesquisar.", "Pesquisar CEP", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _vm.IsBusy = true;
            _vm.StatusText = "Pesquisando CEP e preparando endereço…";
            var address = await App.PlanCall.LookupZipCodeAsync(cep);
            if (address is null)
                throw new InvalidOperationException("CEP não encontrado. Confira os números digitados.");

            ApplyZipLookupToCadastro(address);
            MarkValid(ZipCodeBox);
            MarkValid(AddressBox);
            _vm.StatusText = "Endereço encontrado. Confira e troque o S/N pelo número real, se houver.";
            ApplyCadastroValidation(focusFirstInvalid: false);
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "Pesquisar CEP", MessageBoxButton.OK, MessageBoxImage.Warning);
            _vm.StatusText = "Não foi possível consultar o CEP informado.";
        }
        finally
        {
            _vm.IsBusy = false;
        }
    }

    private async void LookupZipFromAddress_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitEditorFieldsToModel();
            var parts = ParseCadastroAddress(AddressBox.Text);
            var lookupStreet = PlanCallService.NormalizeStreetForLookup(parts.Street);
            var lookupCityState = PlanCallService.NormalizeCityStateForLookup(parts.CityState);
            if (string.IsNullOrWhiteSpace(lookupStreet))
                throw new InvalidOperationException("Informe o logradouro no endereço antes de buscar o CEP. Exemplo: Rua X, S/N - Bairro - Belo Horizonte/MG.");
            if (string.IsNullOrWhiteSpace(lookupCityState) || !lookupCityState.Contains('/'))
                throw new InvalidOperationException("Para buscar o CEP pelo endereço, o campo Endereço precisa conter Cidade/UF. Exemplo: Rua X, S/N - Bairro - Belo Horizonte/MG.");

            _vm.IsBusy = true;
            _vm.StatusText = $"Buscando CEP por {lookupStreet} — {lookupCityState}…";
            var results = await App.PlanCall.LookupAddressAsync(lookupStreet, lookupCityState);
            if (results.Count == 0)
                throw new InvalidOperationException($"Nenhum CEP foi encontrado para '{lookupStreet}' em {lookupCityState}. Confira se rua, cidade e UF estão corretas.");

            var selected = results.Count == 1 ? results[0] : SelectViaCepAddress(results);
            if (selected is null)
            {
                _vm.StatusText = "Busca de CEP cancelada.";
                return;
            }

            ApplyZipLookupToCadastro(selected, string.IsNullOrWhiteSpace(parts.Number) ? "S/N" : parts.Number);
            MarkValid(ZipCodeBox);
            MarkValid(AddressBox);
            _vm.StatusText = "CEP localizado pelo endereço. Confira o número e salve as alterações.";
            ApplyCadastroValidation(focusFirstInvalid: false);
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "Buscar CEP pelo endereço", MessageBoxButton.OK, MessageBoxImage.Warning);
            _vm.StatusText = "Não foi possível localizar o CEP pelo endereço.";
        }
        finally
        {
            _vm.IsBusy = false;
        }
    }

    private void SyncEditorFieldsFromModel()
    {
        // Garante que a edição abra com P/G, Banco, CEP e Endereço exatamente como
        // estão salvos, usando a linha original como reserva caso o recarregamento
        // do SQLite venha parcial.
        var rawRank = FirstNonBlankOrEmpty(_vm.Military.Rank, _sourceMilitary.Rank);
        var rank = MilitaryRankService.GetOrder(rawRank) < 999 ? MilitaryRankService.Canonicalize(rawRank) : string.Empty;
        var bank = FirstNonBlankOrEmpty(_vm.Military.Bank, _sourceMilitary.Bank);
        var zip = PlanCallService.FormatZipCode(FirstNonBlankOrEmpty(_vm.Military.ZipCode, _sourceMilitary.ZipCode));
        var address = FirstNonBlankOrEmpty(_vm.Military.Address, _sourceMilitary.Address);

        SetRankComboValue(RankBox, _vm.Ranks, rank);
        SetBankComboValue(BankBox, _vm.Banks, bank);

        if (!string.IsNullOrWhiteSpace(rank)) _vm.Military.Rank = rank;
        if (!string.IsNullOrWhiteSpace(bank))
            _vm.Military.Bank = _vm.Banks.FirstOrDefault(x => MilitaryBankService.SameBank(x, bank)) ?? bank;
        if (!string.IsNullOrWhiteSpace(zip))
        {
            ZipCodeBox.Text = zip;
            _vm.Military.ZipCode = zip;
        }
        if (!string.IsNullOrWhiteSpace(address))
        {
            AddressBox.Text = address;
            _vm.Military.Address = address;
        }
    }

    private static void SetRankComboValue(ComboBox combo, ICollection<string> items, string value)
    {
        value = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            combo.SetCurrentValue(Selector.SelectedIndexProperty, -1);
            return;
        }

        var existing = items.FirstOrDefault(x => string.Equals(x?.Trim(), value, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(existing)) return;

        combo.SetCurrentValue(Selector.SelectedItemProperty, existing);
        combo.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateSource();
    }

    private static void SetBankComboValue(ComboBox combo, ICollection<string> items, string value)
    {
        value = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value)) return;

        var existing = items.FirstOrDefault(x => MilitaryBankService.SameBank(x, value));
        if (string.IsNullOrWhiteSpace(existing))
        {
            items.Add(value);
            existing = value;
        }

        // O banco usa somente a ligação Text. Isso evita a disputa entre Text e
        // SelectedItem que fazia um ComboBox editável sobrescrever outro campo.
        combo.SetCurrentValue(ComboBox.TextProperty, existing);
        combo.SetCurrentValue(Selector.SelectedItemProperty, existing);

        // Em alguns temas o texto visível fica no PART_EditableTextBox. Atualiza
        // também o editor interno para garantir que a tela mostre o valor salvo
        // logo ao abrir, sem depender do usuário clicar no campo.
        combo.ApplyTemplate();
        if (combo.Template.FindName("PART_EditableTextBox", combo) is TextBox editor)
        {
            editor.Text = existing;
            editor.CaretIndex = editor.Text.Length;
        }

        combo.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
    }

    private void CommitEditorFieldsToModel()
    {
        var rankText = RankBox.SelectedItem?.ToString()?.Trim() ?? string.Empty;
        var bankText = BankBox.Text?.Trim() ?? string.Empty;
        var zipText = ZipCodeBox.Text?.Trim() ?? string.Empty;
        var addressText = AddressBox.Text?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(rankText) || string.IsNullOrWhiteSpace(_vm.Military.Rank)) _vm.Military.Rank = rankText;
        if (!string.IsNullOrWhiteSpace(bankText) || string.IsNullOrWhiteSpace(_vm.Military.Bank)) _vm.Military.Bank = bankText;
        if (!string.IsNullOrWhiteSpace(zipText) || string.IsNullOrWhiteSpace(_vm.Military.ZipCode)) _vm.Military.ZipCode = PlanCallService.FormatZipCode(zipText);
        if (!string.IsNullOrWhiteSpace(addressText) || string.IsNullOrWhiteSpace(_vm.Military.Address)) _vm.Military.Address = addressText;
    }

    private void ApplyZipLookupToCadastro(ViaCepAddress address, string? number = null)
    {
        var formattedZip = PlanCallService.FormatZipCode(address.ZipCode);
        var formattedAddress = FormatViaCepAddressForCadastro(address, string.IsNullOrWhiteSpace(number) ? "S/N" : number.Trim());

        _vm.Military.ZipCode = formattedZip;
        _vm.Military.Address = formattedAddress;
        ZipCodeBox.Text = formattedZip;
        AddressBox.Text = formattedAddress;

        AddressBox.Focus();
        var numberToSelect = string.IsNullOrWhiteSpace(number) ? "S/N" : number.Trim();
        var numberIndex = AddressBox.Text.IndexOf(numberToSelect, StringComparison.OrdinalIgnoreCase);
        if (numberIndex >= 0)
            AddressBox.Select(numberIndex, numberToSelect.Length);
        else
            AddressBox.CaretIndex = AddressBox.Text.Length;
    }

    private static string FormatViaCepAddressForCadastro(ViaCepAddress address, string number)
    {
        var formatted = PlanCallService.FormatAddress(address.Street, string.IsNullOrWhiteSpace(number) ? "S/N" : number, string.Empty, address.District, address.CityState, string.Empty);
        if (!string.IsNullOrWhiteSpace(formatted)) return formatted;
        return string.Join(" - ", new[] { address.District, address.CityState }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
    }

    private ViaCepAddress? SelectViaCepAddress(IReadOnlyList<ViaCepAddress> results)
    {
        var window = new Window
        {
            Title = "Escolher endereço encontrado",
            Owner = this,
            Width = 840,
            Height = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            Icon = Icon
        };

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = "Foram encontrados mais de um endereço. Escolha o correto para preencher o CEP no cadastro.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var list = new ListBox { ItemsSource = results, DisplayMemberPath = "Display" };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is not null) window.DialogResult = true; };
        Grid.SetRow(list, 1);
        root.Children.Add(list);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Cancelar", MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 7, 12, 7) };
        cancel.Click += (_, _) => window.DialogResult = false;
        var ok = new Button { Content = "Usar selecionado", MinWidth = 150, Padding = new Thickness(12, 7, 12, 7) };
        ok.Click += (_, _) => { if (list.SelectedItem is not null) window.DialogResult = true; };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        window.Content = root;
        list.SelectedIndex = 0;
        return window.ShowDialog() == true ? list.SelectedItem as ViaCepAddress : null;
    }

    private static AddressParts ParseCadastroAddress(string? address)
    {
        var raw = Regex.Replace(address ?? string.Empty, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(raw)) return new AddressParts();

        var pieces = Regex.Split(raw, @"\s+-\s+")
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var first = pieces.Count > 0 ? pieces[0] : raw;
        var street = first;
        var number = string.Empty;

        // Também aceita endereços antigos separados por vírgula:
        // Rua X, 123, Bairro, Belo Horizonte/MG
        var commaParts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (pieces.Count == 1 && commaParts.Count >= 2)
        {
            street = commaParts[0];
            var possibleNumber = commaParts[1];
            if (Regex.IsMatch(possibleNumber, @"^(?:N[ºo°]?\s*)?(?:\d+[A-Za-z]?|S/?N)$", RegexOptions.IgnoreCase))
                number = Regex.Replace(possibleNumber, @"^N[ºo°]?\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
        }

        var firstMatch = Regex.Match(street, @"^(?<street>.*?)(?:\s*,\s*|\s+)(?<number>\d+[A-Za-z]?|S/?N)\s*$", RegexOptions.IgnoreCase);
        if (firstMatch.Success)
        {
            street = firstMatch.Groups["street"].Value.Trim();
            if (string.IsNullOrWhiteSpace(number)) number = firstMatch.Groups["number"].Value.Trim();
        }

        var cityState = pieces.LastOrDefault(x => Regex.IsMatch(x, @"/[A-Za-z]{2}\b")) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cityState) && commaParts.Count >= 2)
            cityState = commaParts.LastOrDefault(x => Regex.IsMatch(x, @"/[A-Za-z]{2}\b")) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cityState)) cityState = raw;
        cityState = PlanCallService.NormalizeCityStateForLookup(cityState);

        var district = string.Empty;
        if (pieces.Count >= 3) district = pieces[^2];
        else if (pieces.Count == 2 && !pieces[1].Equals(cityState, StringComparison.OrdinalIgnoreCase)) district = pieces[1];
        else if (commaParts.Count >= 4) district = commaParts[^2];

        return new AddressParts
        {
            Street = street.Trim(' ', ',', '-'),
            Number = number,
            District = district,
            CityState = cityState
        };
    }

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

    private static string StorePhotoInSigfurData(string sourcePath, MilitaryRecord military)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("Foto de origem nao encontrada.", sourcePath);

        var fullSource = Path.GetFullPath(sourcePath);
        if (IsInsideDirectory(fullSource, App.Paths.DataDirectory))
            return fullSource;

        Directory.CreateDirectory(App.Paths.MilitaryPhotosDirectory);
        var extension = Path.GetExtension(fullSource);
        var identity = FirstNonBlank(military.Cpf, military.PrecCp, military.MilitaryId, military.Name, "foto");
        var fileName = $"{SafePhotoFileName(identity)}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";
        var destination = UniquePath(App.Paths.MilitaryPhotosDirectory, fileName);
        File.Copy(fullSource, destination, false);
        return destination;
    }

    private static bool IsInsideDirectory(string file, string directory)
    {
        try
        {
            var fullFile = Path.GetFullPath(file);
            var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullFile.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "foto";

    private static string FirstNonBlankOrEmpty(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private static string SafePhotoFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        value = Regex.Replace(value, @"\s+", "_", RegexOptions.CultureInvariant).Trim('_', '.', ' ');
        return string.IsNullOrWhiteSpace(value) ? "foto" : value[..Math.Min(value.Length, 80)];
    }

    private static string UniquePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; index < 1000; index++)
        {
            path = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(path)) return path;
        }
        return Path.Combine(directory, $"{stem}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}");
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
        BirthDateBox, EnlistmentDateBox, PhoneBox, EmailBox, ZipCodeBox, AddressBox, BankBox, AgencyBox, AccountBox,
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
