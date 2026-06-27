using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SIGFUR.Wpf.Views.Finance;

public partial class BizurometroSpedWindow : Window
{
    private readonly string _stateFile = Path.Combine(App.Paths.DataDirectory, "bizurometro_sped_estado_wpf.json");
    private readonly ObservableCollection<CheckRow> _checks = [];
    private readonly ObservableCollection<BizuMilitaryOption> _relation = [];
    private readonly List<BizuMilitaryOption> _allMilitary = [];

    private string _diexFile = string.Empty;
    private string _bulletinFile = string.Empty;
    private string _reportFile = string.Empty;
    private string _pfFile = string.Empty;
    private string _otherFile = string.Empty;
    private string _lastGeneratedDiex = string.Empty;
    private string _lastGeneratedDispatch = string.Empty;
    private bool _loading = true;
    private bool _forceGenerateTexts;

    private static readonly string[] Steps =
    [
        "1. ESCOLHA O CASO E CONFIRA A ORIGEM\nEscolha Inconsistência Bancária, TDBLOQ ou PP760 e confirme o relatório gerador correto.\nErro comum: começar sem saber de qual relatório saiu o valor.",
        "2. CRIAR O DIEx REQUISITÓRIO\nNo SPED, crie o DIEx pedindo o pagamento dos valores publicados e mencione o relatório gerador e a PF, quando houver.\nErro comum: DIEx genérico, sem valor, boletim ou relatório.",
        "3. NÃO ENCAMINHAR O DIEx AUTOMATICAMENTE\nAo finalizar, marque para não encaminhar automaticamente. O DIEx será incluído no processo; quem tramita é o processo completo.\nErro comum: encaminhar o DIEx sozinho.",
        "4. ENTRAR NO MÓDULO PROCESSOS\nVolte à tela inicial do SPED e entre no Módulo Processos para criar, organizar, autuar e encaminhar.\nErro comum: tentar resolver tudo no módulo de documentos.",
        "5. CRIAR O PROCESSO E INCLUIR O DIEx\nCrie o processo, localize o DIEx recém-produzido e inclua-o como documento.\nErro comum: criar o processo e esquecer de vincular o DIEx.",
        "6. PREENCHER O ASSUNTO NO PADRÃO DA 4ª CIA PE\nUse: DEPÓSITOS DE TERCEIROS – 4ª CIA PE – tipo do caso – militar ou Relação Anexa.\nErro comum: assunto vago, sem OM ou sem o tipo do caso.",
        "7. ANEXAR OS DOCUMENTOS NA ORDEM\nAnexe separadamente DIEx, boletim, relatório gerador, PF quando houver e demais documentos de conferência.\nErro comum: arquivo errado, anexos misturados ou boletim ausente.",
        "8. CONFERIR ANTES DE AUTUAR\nSe o painel estiver vermelho, não autue; amarelo, revise; verde, prossiga após a leitura final.\nErro comum: autuar com checklist incompleto.",
        "9. DESPACHO E ENCAMINHAMENTO\nRevise o despacho e encaminhe o processo ao Ordenador de Despesas pelo chefe responsável.\nErro comum: despacho sem dizer o pedido e sem citar os anexos principais."
    ];

    public BizurometroSpedWindow()
    {
        InitializeComponent();
        App.UiState.Attach(this);

        StepsList.ItemsSource = Steps.Select((text, index) => new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 9),
            FontWeight = index == 0 ? FontWeights.SemiBold : FontWeights.Normal
        });
        ChecklistList.ItemsSource = _checks;
        RelationGrid.ItemsSource = _relation;

        FlowBox.SelectedIndex = 0;
        BeneficiaryModeBox.SelectedIndex = 0;
        BulletinDatePicker.SelectedDate = DateTime.Today;
        ConfigureFlow(resetReport: true);
        BuildChecks();

        Loaded += async (_, _) =>
        {
            await LoadMilitaryOptionsAsync();
            await LoadStateAsync();
            _loading = false;
            Refresh();
        };
    }

    private string Flow => (FlowBox.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim()
                           ?? "Inconsistência Bancária";

    private bool IsRelationMode => BeneficiaryModeBox.SelectedIndex == 1;

    private async Task LoadMilitaryOptionsAsync()
    {
        try
        {
            var activeTask = App.MilitaryRepository.GetAllAsync();
            var licensedTask = App.LicensedTransferred.GetAllAsync(true);
            await Task.WhenAll(activeTask, licensedTask);

            _allMilitary.Clear();
            _allMilitary.AddRange(activeTask.Result.Select(item => BizuMilitaryOption.FromMilitary(item, "Ativo")));
            _allMilitary.AddRange(licensedTask.Result.Select(item => BizuMilitaryOption.FromLicensed(item, "Licenciado/Transferido")));

            var unique = _allMilitary
                .GroupBy(item => string.IsNullOrWhiteSpace(MilitaryFormatting.Digits(item.Cpf))
                    ? $"{Normalize(item.Name)}|{Normalize(item.Source)}"
                    : $"{MilitaryFormatting.Digits(item.Cpf)}|{Normalize(item.Source)}")
                .Select(group => group.First())
                .OrderBy(item => MilitaryRankService.GetOrder(item.Rank))
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            _allMilitary.Clear();
            _allMilitary.AddRange(unique);
            MilitaryPickerBox.ItemsSource = _allMilitary;
        }
        catch (Exception ex)
        {
            _ = App.Log.WriteAsync("Não foi possível carregar o efetivo no Bizurômetro SPED.", ex);
            StatusText.Text = "O efetivo não pôde ser carregado. Ainda é possível preencher a identificação manualmente.";
        }
    }

    private void ConfigureFlow(bool resetReport)
    {
        var reports = Flow.Contains("TDBLOQ", StringComparison.OrdinalIgnoreCase)
            ? new[] { "TDBLOQ" }
            : Flow.Contains("PP760", StringComparison.OrdinalIgnoreCase)
                ? new[] { "PP760" }
                : new[] { "TDINC", "TDCNAB" };

        ReportBox.ItemsSource = reports;
        if (resetReport || string.IsNullOrWhiteSpace(ReportBox.Text))
            ReportBox.Text = reports[0];
    }

    private void BuildChecks(IReadOnlyDictionary<string, bool>? previous = null)
    {
        previous ??= _checks.ToDictionary(item => item.Text, item => item.Checked, StringComparer.OrdinalIgnoreCase);
        string[] items;

        if (Flow.Contains("TDBLOQ", StringComparison.OrdinalIgnoreCase))
        {
            items =
            [
                "Consultei a conta TDBLOQ no SIAFI.",
                "Conferi o valor que chegou na conta TDBLOQ.",
                "Publiquei ou localizei a publicação em boletim.",
                "Consultei a PF.",
                "Anexei ou mencionei a PF no DIEx.",
                "Conferi o percentual de pagamento ao CPF.",
                "Conferi o percentual de reversão ao CPEx.",
                "O valor do DIEx está igual ao valor do boletim.",
                "Tenho o boletim salvo em PDF.",
                "Tenho o relatório/consulta geradora salvo em PDF.",
                "O chefe fará o despacho para o Ordenador de Despesas."
            ];
        }
        else if (Flow.Contains("PP760", StringComparison.OrdinalIgnoreCase))
        {
            items =
            [
                "Consultei o relatório PP760.",
                "Conferi se o caso realmente pertence ao PP760.",
                "Conferi o militar ou a relação de militares.",
                "Publiquei ou localizei a publicação em boletim.",
                "O valor do DIEx está igual ao valor do boletim.",
                "O valor do boletim está igual ao relatório PP760.",
                "Mencionei a PF ou PFs no texto do DIEx, quando houver.",
                "Tenho o boletim salvo em PDF.",
                "Tenho o relatório PP760 salvo em PDF.",
                "O chefe fará o despacho para o Ordenador de Despesas."
            ];
        }
        else
        {
            items =
            [
                "Consultei o relatório TDINC/TDCNAB.",
                "Conferi o militar ou a relação de militares.",
                "Conferi o valor no relatório gerador.",
                "Publiquei ou localizei a publicação em boletim.",
                "O valor do DIEx está igual ao valor do boletim.",
                "O valor do boletim está igual ao relatório gerador.",
                "Mencionei a PF ou PFs no texto do DIEx, quando houver.",
                "Tenho o boletim salvo em PDF.",
                "Tenho o relatório gerador salvo em PDF.",
                "O chefe fará o despacho para o Ordenador de Despesas."
            ];
        }

        _checks.Clear();
        foreach (var text in items)
        {
            _checks.Add(new CheckRow
            {
                Text = text,
                Checked = previous.TryGetValue(text, out var isChecked) && isChecked
            });
        }
    }

    private void Form_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded)
            return;

        if (ReferenceEquals(sender, FlowBox))
        {
            var previous = _checks.ToDictionary(item => item.Text, item => item.Checked, StringComparer.OrdinalIgnoreCase);
            ConfigureFlow(resetReport: true);
            BuildChecks(previous);
        }

        Refresh();
    }

    private void Checklist_Changed(object sender, RoutedEventArgs e) => Refresh();

    private string FlowWarning => Flow.Contains("TDBLOQ", StringComparison.OrdinalIgnoreCase)
        ? "No TDBLOQ, PF e percentuais são obrigatórios na conferência."
        : Flow.Contains("PP760", StringComparison.OrdinalIgnoreCase)
            ? "PP760 tem que nascer do relatório PP760. Não chute a origem."
            : "Se o valor não bater em 3 lugares, NÃO AUTUE.";

    private string GeneratedSubject()
    {
        var flowSubject = Flow.Contains("TDBLOQ", StringComparison.OrdinalIgnoreCase)
            ? "Bloqueio Bancário"
            : Flow.Contains("PP760", StringComparison.OrdinalIgnoreCase) ? "PP760" : "Inconsistência Bancária";
        var beneficiary = IsRelationMode || _relation.Count > 1 ? "Relação Anexa" : SubjectBeneficiary();
        var parts = new List<string> { "DEPÓSITOS DE TERCEIROS – 4ª CIA PE", flowSubject };
        if (!string.IsNullOrWhiteSpace(beneficiary)) parts.Add(beneficiary);
        if (!string.IsNullOrWhiteSpace(ObservationBox.Text)) parts.Add(ObservationBox.Text.Trim());
        return string.Join(" – ", parts);
    }

    private void Refresh()
    {
        if (_loading)
            return;

        var report = Placeholder(ReportBox.Text, "[RELATÓRIO]");
        var beneficiaries = BeneficiariesText();
        var bulletin = Placeholder(BulletinBox.Text, "[BI]");
        var bulletinDate = BulletinDatePicker.SelectedDate is DateTime date
            ? date.ToString("dd/MM/yyyy")
            : "[DATA]";
        var value = Placeholder(ValueBox.Text, "[VALOR]");
        var pf = Placeholder(PfBox.Text, "[INFORMAR QUANDO HOUVER]");
        var observation = ObservationBox.Text.Trim();

        var flowDetail = Flow.Contains("TDBLOQ", StringComparison.OrdinalIgnoreCase)
            ? "bloqueio bancário, com conferência da PF, do percentual de pagamento ao CPF e da reversão ao CPEx"
            : Flow.Contains("PP760", StringComparison.OrdinalIgnoreCase)
                ? "pagamento originado no relatório PP760/CPEx"
                : "inconsistência bancária identificada no relatório gerador";

        var flowSubject = Flow.Contains("TDBLOQ", StringComparison.OrdinalIgnoreCase)
            ? "Bloqueio Bancário"
            : Flow.Contains("PP760", StringComparison.OrdinalIgnoreCase)
                ? "PP760"
                : "Inconsistência Bancária";
        var valueText = IsRelationMode || _relation.Count > 0
            ? "conforme relação anexa"
            : $"no valor de {value}";

        var generatedDiex =
            $"Solicito providências quanto ao pagamento referente a {flowSubject}, identificado no relatório {report}, " +
            $"{valueText}, conforme publicação em {bulletin}, de {bulletinDate}.\n\n" +
            $"Beneficiário(s):\n{beneficiaries}\n\n" +
            $"PF relacionada: {pf}.\n\n" +
            "Declaro, para fins de conferência, que os valores constantes neste DIEx devem estar exatamente iguais aos " +
            "valores publicados em boletim e ao relatório gerador anexado ao processo.\n\n" +
            "Documentos a serem anexados ao processo:\n" +
            "- Cópia do boletim da publicação dos pagamentos;\n" +
            $"- Cópia do relatório gerador {report};\n" +
            "- PF ou PFs dos pagamentos, quando houver;\n" +
            "- Demais documentos de conferência necessários.";

        if (Flow.Contains("TDBLOQ", StringComparison.OrdinalIgnoreCase))
        {
            generatedDiex +=
                "\n\nObservação obrigatória para TDBLOQ: conferir a PF, o percentual de pagamento ao CPF " +
                "e o percentual de reversão ao CPEx antes da autuação.";
        }

        if (!string.IsNullOrWhiteSpace(observation))
            generatedDiex += $"\n\nObservação complementar: {observation}";

        var generatedDispatch = "Encaminho o presente processo para apreciação e providências quanto ao pagamento " +
                                $"referente a {flowDetail}, conforme informações constantes no DIEx requisitório, publicação no BI nº {bulletin}, " +
                                $"relatório gerador {report} e demais documentos anexados.\n\n" +
                                "Solicito análise e providências cabíveis pelo Ordenador de Despesas.";

        ReplaceGeneratedText(DiexText, generatedDiex, ref _lastGeneratedDiex, _forceGenerateTexts);
        ReplaceGeneratedText(DispatchText, generatedDispatch, ref _lastGeneratedDispatch, _forceGenerateTexts);

        var beneficiaryReady = IsRelationMode ? _relation.Count > 0 : !string.IsNullOrWhiteSpace(MilitaryBox.Text);
        var essentialReady = !string.IsNullOrWhiteSpace(ReportBox.Text)
                             && beneficiaryReady
                             && !string.IsNullOrWhiteSpace(ValueBox.Text)
                             && !string.IsNullOrWhiteSpace(BulletinBox.Text);
        var attachmentsReady = !string.IsNullOrWhiteSpace(_diexFile)
                               && !string.IsNullOrWhiteSpace(_bulletinFile)
                               && !string.IsNullOrWhiteSpace(_reportFile);
        var checkedCount = _checks.Count(item => item.Checked);
        var allChecked = _checks.Count > 0 && checkedCount == _checks.Count;

        if (essentialReady && attachmentsReady && allChecked)
        {
            StatusTitle.Text = "PRONTO PARA REVISÃO FINAL";
            StatusText.Text = $"Dados essenciais, anexos mínimos e checklist concluídos. Faça a leitura final antes de autuar. {FlowWarning}";
            StatusCard.Background = new SolidColorBrush(Color.FromRgb(220, 252, 231));
            StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
        }
        else if (essentialReady)
        {
            StatusTitle.Text = "CONFERÊNCIA EM ANDAMENTO";
            StatusText.Text = $"{checkedCount}/{_checks.Count} conferências marcadas. Anexos mínimos: {(attachmentsReady ? "OK" : "pendentes")}. {FlowWarning}";
            StatusCard.Background = new SolidColorBrush(Color.FromRgb(254, 243, 199));
            StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(161, 98, 7));
        }
        else
        {
            StatusTitle.Text = "PREENCHIMENTO INCOMPLETO";
            StatusText.Text = $"Preencha relatório, beneficiário/relação, valor e boletim para iniciar a conferência. {FlowWarning}";
            StatusCard.Background = new SolidColorBrush(Color.FromRgb(254, 226, 226));
            StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
        }
    }

    private static void ReplaceGeneratedText(TextBox box, string generated, ref string previousGenerated, bool force = false)
    {
        if (force || string.IsNullOrWhiteSpace(box.Text) || string.Equals(box.Text, previousGenerated, StringComparison.Ordinal))
            box.Text = generated;
        previousGenerated = generated;
    }

    private void GenerateTexts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _forceGenerateTexts = true;
            Refresh();
            StatusText.Text = "DIEx e despacho gerados. Os campos continuam editáveis antes de copiar/salvar.";
        }
        finally
        {
            _forceGenerateTexts = false;
        }
    }

    private string BeneficiariesText()
    {
        if (IsRelationMode)
        {
            if (_relation.Count == 0)
                return "[RELAÇÃO ANEXA A PREENCHER]";
            return string.Join("\n", _relation.Select((item, index) => $"{index + 1}. {BeneficiaryIdentity(item)}"));
        }

        var identity = MilitaryPickerBox.SelectedItem is BizuMilitaryOption selected
            ? BeneficiaryIdentity(selected)
            : Placeholder(MilitaryBox.Text, "[INFORMAR BENEFICIÁRIO OU RELAÇÃO ANEXA]");
        var line = $"1. {identity}";
        if (!string.IsNullOrWhiteSpace(ValueBox.Text)) line += $" | Valor: {ValueBox.Text.Trim()}";
        if (!string.IsNullOrWhiteSpace(PfBox.Text)) line += $" | PF: {PfBox.Text.Trim()}";
        return line;
    }

    private static string BeneficiaryIdentity(BizuMilitaryOption item)
    {
        var rank = MilitaryRankService.ShortName(item.Rank);
        var war = item.WarName?.Trim().ToUpperInvariant() ?? string.Empty;
        var name = item.Name?.Trim().ToUpperInvariant() ?? string.Empty;
        var prefix = string.Join(" ", new[] { rank, war }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (!string.IsNullOrWhiteSpace(name))
            return string.IsNullOrWhiteSpace(prefix) ? name : $"{prefix} - {name}";
        return string.IsNullOrWhiteSpace(prefix) ? "[MILITAR]" : prefix;
    }

    private string SubjectBeneficiary()
    {
        if (MilitaryPickerBox.SelectedItem is BizuMilitaryOption selected)
            return string.IsNullOrWhiteSpace(selected.WarName) ? selected.Name.ToUpperInvariant() : selected.WarName.ToUpperInvariant();

        var firstLine = MilitaryBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(firstLine) ? "BENEFICIÁRIO" : firstLine.ToUpperInvariant();
    }

    private static string Placeholder(string? value, string placeholder)
        => string.IsNullOrWhiteSpace(value) ? placeholder : value.Trim();

    private void MilitaryPickerBox_KeyUp(object sender, KeyEventArgs e)
    {
        if (_loading || e.Key is Key.Up or Key.Down or Key.Enter or Key.Tab or Key.Escape)
            return;

        var query = MilitaryPickerBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allMilitary
            : _allMilitary.Where(item => item.SearchText.Contains(Normalize(query), StringComparison.Ordinal)).Take(80).ToList();

        MilitaryPickerBox.ItemsSource = filtered;
        MilitaryPickerBox.IsDropDownOpen = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            MilitaryPickerBox.Text = query;
        }));
    }

    private void UseMilitary_Click(object sender, RoutedEventArgs e)
    {
        if (MilitaryPickerBox.SelectedItem is not BizuMilitaryOption selected)
        {
            SigfurDialog.Show(this, "Selecione um militar na pesquisa.", "Bizurômetro SPED", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MilitaryBox.Text = selected.IdentityText;
        Refresh();
    }

    private void AddRelation_Click(object sender, RoutedEventArgs e)
    {
        BizuMilitaryOption? item = MilitaryPickerBox.SelectedItem as BizuMilitaryOption;
        if (item is null && !string.IsNullOrWhiteSpace(MilitaryBox.Text))
            item = BizuMilitaryOption.FromManual(MilitaryBox.Text);

        if (item is null || string.IsNullOrWhiteSpace(item.Name))
        {
            SigfurDialog.Show(this, "Selecione um militar ou preencha uma identificação manual antes de adicionar.", "Relação anexa", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cpf = MilitaryFormatting.Digits(item.Cpf);
        var exists = _relation.Any(existing =>
            (!string.IsNullOrWhiteSpace(cpf) && MilitaryFormatting.Digits(existing.Cpf) == cpf)
            || (string.IsNullOrWhiteSpace(cpf) && Normalize(existing.Name) == Normalize(item.Name)));
        if (!exists)
            _relation.Add(item.Clone());

        BeneficiaryModeBox.SelectedIndex = 1;
        Refresh();
    }

    private void RemoveRelation_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in RelationGrid.SelectedItems.Cast<BizuMilitaryOption>().ToList())
            _relation.Remove(item);
        Refresh();
    }

    private void ClearRelation_Click(object sender, RoutedEventArgs e)
    {
        if (_relation.Count == 0)
            return;
        if (SigfurDialog.Show(this, "Remover todos os beneficiários da relação?", "Relação anexa", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _relation.Clear();
        Refresh();
    }

    private void PickDiex_Click(object sender, RoutedEventArgs e) => PickSingle(ref _diexFile, DiexFileText);
    private void PickBulletin_Click(object sender, RoutedEventArgs e) => PickSingle(ref _bulletinFile, BulletinFileText);
    private void PickReport_Click(object sender, RoutedEventArgs e) => PickSingle(ref _reportFile, ReportFileText);
    private void PickPf_Click(object sender, RoutedEventArgs e) => PickSingle(ref _pfFile, PfFileText);

    private void PickOther_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Documentos|*.pdf;*.doc;*.docx;*.odt;*.xlsx;*.xls;*.ods;*.png;*.jpg;*.jpeg|Todos os arquivos|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        _otherFile = string.Join("|", dialog.FileNames);
        OtherFileText.Text = dialog.FileNames.Length == 1
            ? Path.GetFileName(dialog.FileNames[0])
            : $"{dialog.FileNames.Length} arquivos selecionados";
        Refresh();
    }

    private void PickSingle(ref string field, TextBlock label)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Documentos|*.pdf;*.doc;*.docx;*.odt;*.xlsx;*.xls;*.ods;*.png;*.jpg;*.jpeg|Todos os arquivos|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        field = dialog.FileName;
        label.Text = Path.GetFileName(field);
        Refresh();
    }

    private void MarkAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _checks)
            item.Checked = true;
        ChecklistList.Items.Refresh();
        Refresh();
    }

    private void UnmarkAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _checks)
            item.Checked = false;
        ChecklistList.Items.Refresh();
        Refresh();
    }

    private void CopyDiex_Click(object sender, RoutedEventArgs e) => CopyText(DiexText.Text, "DIEx copiado.");
    private void CopyDispatch_Click(object sender, RoutedEventArgs e) => CopyText(DispatchText.Text, "Despacho copiado.");
    private void CopyChecklist_Click(object sender, RoutedEventArgs e) => CopyText(BuildChecklistText(), "Checklist copiado.");

    private void CopyText(string text, string confirmation)
    {
        Clipboard.SetText(text ?? string.Empty);
        StatusText.Text = confirmation;
    }

    private string BuildChecklistText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("CHECKLIST FINAL — BIZURÔMETRO SPED 4ª CIA PE");
        builder.AppendLine();
        builder.AppendLine($"Tipo: {Flow}");
        builder.AppendLine($"Relatório: {ReportBox.Text.Trim()}");
        builder.AppendLine($"Assunto: {GeneratedSubject()}");
        builder.AppendLine($"Beneficiário(s): {BeneficiariesText()}");
        builder.AppendLine($"Regra crítica: {FlowWarning}");
        builder.AppendLine();
        foreach (var item in _checks)
            builder.AppendLine($"[{(item.Checked ? "OK" : "PENDENTE")}] {item.Text}");
        return builder.ToString().TrimEnd();
    }

    private async void SaveState_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveStateAsync();
            SigfurDialog.Show(this, "Preenchimento salvo.", "Bizurômetro SPED", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, $"Não foi possível salvar o preenchimento.\n\n{ex.Message}", "Bizurômetro SPED", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveStateAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        var state = new State
        {
            Flow = Flow,
            Report = ReportBox.Text,
            BeneficiaryMode = IsRelationMode ? "Relação anexa" : "Um militar",
            Military = MilitaryBox.Text,
            Value = ValueBox.Text,
            Bulletin = BulletinBox.Text,
            BulletinDate = BulletinDatePicker.SelectedDate?.ToString("yyyy-MM-dd") ?? string.Empty,
            Pf = PfBox.Text,
            Observation = ObservationBox.Text,
            Checks = _checks.Select(item => new CheckState { Text = item.Text, Checked = item.Checked }).ToList(),
            LegacyChecks = _checks.Select(item => item.Checked).ToList(),
            Relation = _relation.Select(item => item.Clone()).ToList(),
            DiexFile = _diexFile,
            BulletinFile = _bulletinFile,
            ReportFile = _reportFile,
            PfFile = _pfFile,
            OtherFile = _otherFile,
            DiexText = DiexText.Text,
            DispatchText = DispatchText.Text
        };
        await File.WriteAllTextAsync(_stateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task LoadStateAsync()
    {
        try
        {
            if (!File.Exists(_stateFile))
                return;

            var state = DeserializeState(await File.ReadAllTextAsync(_stateFile));
            if (state is null)
                return;

            FlowBox.SelectedIndex = state.Flow.Contains("TDBLOQ", StringComparison.OrdinalIgnoreCase)
                ? 1
                : state.Flow.Contains("PP760", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
            ConfigureFlow(resetReport: true);
            ReportBox.Text = state.Report;
            BeneficiaryModeBox.SelectedIndex = state.BeneficiaryMode.Contains("Relação", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            MilitaryBox.Text = state.Military;
            ValueBox.Text = state.Value;
            BulletinBox.Text = state.Bulletin;
            if (DateTime.TryParse(state.BulletinDate, out var bulletinDate))
                BulletinDatePicker.SelectedDate = bulletinDate;
            PfBox.Text = state.Pf;
            ObservationBox.Text = state.Observation;

            var checkMap = state.Checks.ToDictionary(item => item.Text, item => item.Checked, StringComparer.OrdinalIgnoreCase);
            BuildChecks(checkMap);
            if (state.Checks.Count == 0 && state.LegacyChecks.Count > 0)
            {
                for (var index = 0; index < Math.Min(state.LegacyChecks.Count, _checks.Count); index++)
                    _checks[index].Checked = state.LegacyChecks[index];
            }

            _relation.Clear();
            foreach (var item in state.Relation)
                _relation.Add(item);

            _diexFile = state.DiexFile;
            _bulletinFile = state.BulletinFile;
            _reportFile = state.ReportFile;
            _pfFile = state.PfFile;
            _otherFile = state.OtherFile;
            DiexFileText.Text = FileLabel(_diexFile);
            BulletinFileText.Text = FileLabel(_bulletinFile);
            ReportFileText.Text = FileLabel(_reportFile);
            PfFileText.Text = FileLabel(_pfFile);
            OtherFileText.Text = OtherFilesLabel(_otherFile);

            DiexText.Text = state.DiexText;
            DispatchText.Text = state.DispatchText;
            ChecklistList.Items.Refresh();
        }
        catch (Exception ex)
        {
            _ = App.Log.WriteAsync("Não foi possível restaurar o preenchimento do Bizurômetro SPED.", ex);
        }
    }

    private static State? DeserializeState(string json)
    {
        try { return JsonSerializer.Deserialize<State>(json); }
        catch (JsonException)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            static string Get(JsonElement element, string name)
                => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
            var state = new State
            {
                Flow = Get(root, "Flow"), Report = Get(root, "Report"), Military = Get(root, "Military"),
                Value = Get(root, "Value"), Bulletin = Get(root, "Bulletin"), Pf = Get(root, "Pf"),
                Observation = Get(root, "Observation"), DiexFile = Get(root, "DiexFile"),
                BulletinFile = Get(root, "BulletinFile"), ReportFile = Get(root, "ReportFile")
            };
            if (root.TryGetProperty("Checks", out var checks) && checks.ValueKind == JsonValueKind.Array)
                state.LegacyChecks = checks.EnumerateArray().Where(x => x.ValueKind is JsonValueKind.True or JsonValueKind.False).Select(x => x.GetBoolean()).ToList();
            return state;
        }
    }

    private void ExportSummary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Salvar resumo do Bizurômetro SPED",
            Filter = "Arquivo de texto|*.txt",
            DefaultExt = ".txt",
            FileName = $"resumo_bizurometro_sped_{DateTime.Now:yyyyMMdd_HHmm}.txt"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var content = new StringBuilder()
            .AppendLine("BIZURÔMETRO SPED — RESUMO DO PROCESSO")
            .AppendLine(new string('=', 54))
            .AppendLine()
            .AppendLine(DiexText.Text)
            .AppendLine()
            .AppendLine(new string('-', 54))
            .AppendLine(BuildChecklistText())
            .AppendLine()
            .AppendLine(new string('-', 54))
            .AppendLine("DESPACHO")
            .AppendLine(DispatchText.Text)
            .AppendLine()
            .AppendLine(new string('-', 54))
            .AppendLine("ANEXOS SELECIONADOS")
            .AppendLine($"DIEx: {FileLabel(_diexFile)}")
            .AppendLine($"Boletim: {FileLabel(_bulletinFile)}")
            .AppendLine($"Relatório: {FileLabel(_reportFile)}")
            .AppendLine($"PF/consulta: {FileLabel(_pfFile)}")
            .AppendLine($"Outros: {OtherFilesLabel(_otherFile)}")
            .ToString();

        File.WriteAllText(dialog.FileName, content, Encoding.UTF8);
        SigfurDialog.Show(this, "Resumo exportado com sucesso.", "Bizurômetro SPED", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (SigfurDialog.Show(this, "Limpar todo o preenchimento atual?", "Bizurômetro SPED", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _loading = true;
        FlowBox.SelectedIndex = 0;
        BeneficiaryModeBox.SelectedIndex = 0;
        ConfigureFlow(resetReport: true);
        MilitaryPickerBox.SelectedItem = null;
        MilitaryPickerBox.Text = string.Empty;
        MilitaryBox.Clear();
        ValueBox.Clear();
        BulletinBox.Clear();
        BulletinDatePicker.SelectedDate = DateTime.Today;
        PfBox.Clear();
        ObservationBox.Clear();
        _relation.Clear();
        _diexFile = _bulletinFile = _reportFile = _pfFile = _otherFile = string.Empty;
        DiexFileText.Text = BulletinFileText.Text = ReportFileText.Text = PfFileText.Text = OtherFileText.Text = "Não selecionado";
        BuildChecks();
        _lastGeneratedDiex = _lastGeneratedDispatch = string.Empty;
        DiexText.Clear();
        DispatchText.Clear();
        _loading = false;
        Refresh();
    }

    private static string FileLabel(string path)
        => string.IsNullOrWhiteSpace(path) ? "Não selecionado" : Path.GetFileName(path);

    private static string OtherFilesLabel(string paths)
    {
        if (string.IsNullOrWhiteSpace(paths))
            return "Não selecionado";
        var files = paths.Split('|', StringSplitOptions.RemoveEmptyEntries);
        return files.Length == 1 ? Path.GetFileName(files[0]) : $"{files.Length} arquivos selecionados";
    }

    private static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Normalize(System.Text.NormalizationForm.FormD);
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(character));
        }
        return string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public sealed class CheckRow
    {
        public string Text { get; set; } = string.Empty;
        public bool Checked { get; set; }
    }

    public sealed class BizuMilitaryOption
    {
        public string Rank { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string WarName { get; set; } = string.Empty;
        public string Cpf { get; set; } = string.Empty;
        public string PrecCp { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Display => $"{MilitaryRankService.ShortName(Rank)} {(string.IsNullOrWhiteSpace(WarName) ? Name : WarName)} — {Name} | CPF {MilitaryFormatting.FormatCpf(Cpf)} | {Source}".Trim();
        public string SearchText => Normalize($"{Rank} {Name} {WarName} {Cpf} {PrecCp} {Source}");
        public string IdentityText
        {
            get
            {
                var rank = MilitaryRankService.ShortName(Rank);
                var firstLine = $"{rank} {Name}".Trim();
                var prec = MilitaryFormatting.Digits(PrecCp);
                var cpf = MilitaryFormatting.FormatCpf(Cpf);
                return $"{firstLine}\nPrec-CP {(string.IsNullOrWhiteSpace(prec) ? "—" : prec)} CPF {(string.IsNullOrWhiteSpace(cpf) ? "—" : cpf)}";
            }
        }

        public BizuMilitaryOption Clone() => new()
        {
            Rank = Rank,
            Name = Name,
            WarName = WarName,
            Cpf = Cpf,
            PrecCp = PrecCp,
            Source = Source
        };

        public static BizuMilitaryOption FromMilitary(MilitaryRecord item, string source) => new()
        {
            Rank = item.Rank,
            Name = item.Name,
            WarName = item.WarName,
            Cpf = item.Cpf,
            PrecCp = item.PrecCp,
            Source = source
        };

        public static BizuMilitaryOption FromLicensed(LicensedTransferredRecord item, string source) => new()
        {
            Rank = item.Rank,
            Name = item.Name,
            WarName = item.WarName,
            Cpf = item.Cpf,
            PrecCp = item.PrecCp,
            Source = source
        };

        public static BizuMilitaryOption FromManual(string text)
        {
            var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var name = lines.FirstOrDefault() ?? text.Trim();
            var cpfMatch = System.Text.RegularExpressions.Regex.Match(text, @"CPF\s*([0-9.\-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var precMatch = System.Text.RegularExpressions.Regex.Match(text, @"PREC(?:-CP)?\s*([0-9.\-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return new BizuMilitaryOption
            {
                Name = name,
                Cpf = cpfMatch.Success ? cpfMatch.Groups[1].Value : string.Empty,
                PrecCp = precMatch.Success ? precMatch.Groups[1].Value : string.Empty,
                Source = "Manual"
            };
        }
    }

    private sealed class CheckState
    {
        public string Text { get; set; } = string.Empty;
        public bool Checked { get; set; }
    }

    private sealed class State
    {
        public string Flow { get; set; } = string.Empty;
        public string Report { get; set; } = string.Empty;
        public string BeneficiaryMode { get; set; } = "Um militar";
        public string Military { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string Bulletin { get; set; } = string.Empty;
        public string BulletinDate { get; set; } = string.Empty;
        public string Pf { get; set; } = string.Empty;
        public string Observation { get; set; } = string.Empty;
        public List<CheckState> Checks { get; set; } = [];
        public List<bool> LegacyChecks { get; set; } = [];
        public List<BizuMilitaryOption> Relation { get; set; } = [];
        public string DiexFile { get; set; } = string.Empty;
        public string BulletinFile { get; set; } = string.Empty;
        public string ReportFile { get; set; } = string.Empty;
        public string PfFile { get; set; } = string.Empty;
        public string OtherFile { get; set; } = string.Empty;
        public string DiexText { get; set; } = string.Empty;
        public string DispatchText { get; set; } = string.Empty;
    }
}
