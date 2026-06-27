using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Military;

public partial class PaystubAuditWindow : Window
{
    private sealed record MonthOption(int Number, string Name);
    private readonly MilitaryRepository _repository;
    private readonly PaystubService _paystubs;
    private readonly List<MilitaryRecord> _military;
    private readonly Dictionary<int, MilitaryRecord> _militaryById;
    private readonly ObservableCollection<PaystubAuditRow> _rows = [];
    private readonly ICollectionView _view;
    private readonly PaystubAuditService _nativeAudit;
    private string _rootFolder;
    private CancellationTokenSource? _auditCts;
    private bool _running;
    private bool _closing;
    private DataGridColumn? _sortColumn;
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    private static readonly string[] Filters =
    [
        "Todos", "Somente com achado", "Divergências / verificar", "Sem contracheque",
        "Com FUSEx 3%", "Sem FUSEx 3%", "Com Aux Transporte no contracheque", "Sem Aux Transporte no contracheque",
        "Aux Transporte divergente", "Aux Transporte OK", "Com desconto DR de Aux Transporte",
        "Com Salário Família", "Sem Salário Família", "Com Pré-escolar", "Sem Pré-escolar",
        "Com Férias", "Sem Férias", "Com Aux Alimentação", "Sem Aux Alimentação",
        "Com DR/AR/Atrasados", "Sem DR/AR/Atrasados", "Com Pensão", "Com Pensão Alimentícia",
        "Com IRRF", "Com Adicional Habilitação", "Com PNR", "Com Empréstimo/Seguro/FHE",
        "Com Desconto Dependente FUSEx", "Com Despesa Médica FUSEx",
        "Banco/Conta divergente", "Banco/Conta OK", "Situação diferente de Normal",
        "Pagamento suspenso", "Situação Normal"
    ];

    public PaystubAuditWindow(
        MilitaryRepository repository,
        PaystubService paystubs,
        IReadOnlyList<MilitaryRecord> military,
        string rootFolder)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _repository = repository;
        _paystubs = paystubs;
        _military = military.DistinctBy(x => x.Id).OrderBy(x => MilitaryRankService.GetOrder(x.Rank)).ThenBy(x => x.Name).ToList();
        _militaryById = _military.Where(x => x.Id > 0).ToDictionary(x => x.Id);
        _rootFolder = string.IsNullOrWhiteSpace(rootFolder) ? App.Paths.PaystubsDirectory : rootFolder;
        _nativeAudit = new PaystubAuditService(_paystubs, App.PdfText, App.Log);
        AuditGrid.ItemsSource = _rows;
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterRow;
        if (_view is ListCollectionView listView)
            listView.CustomSort = Comparer<object>.Create((left, right) => CompareRows(left as PaystubAuditRow, right as PaystubAuditRow, RankColumn, ListSortDirection.Ascending));
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        YearCombo.ItemsSource = Enumerable.Range(DateTime.Today.Year - 7, 9).OrderByDescending(x => x).ToList();
        YearCombo.SelectedItem = DateTime.Today.Year;
        MonthCombo.ItemsSource = Enumerable.Range(1, 12)
            .Select(x => new MonthOption(x, new DateTime(2000, x, 1).ToString("MMMM", CultureInfo.GetCultureInfo("pt-BR"))))
            .ToList();
        MonthCombo.DisplayMemberPath = nameof(MonthOption.Name);
        MonthCombo.SelectedValuePath = nameof(MonthOption.Number);
        MonthCombo.SelectedValue = DateTime.Today.Month;
        FilterCombo.ItemsSource = Filters;
        FilterCombo.SelectedIndex = 0;
        RankCombo.ItemsSource = new[] { "Todos" }.Concat(_military.Select(x => x.ShortRank).Distinct().OrderBy(x => MilitaryRankService.GetOrder(x))).ToList();
        RankCombo.SelectedIndex = 0;
        ScopeText.Text = $"{_military.Count} militar(es) • pasta: {_rootFolder}";
        CountText.Text = "0 resultado(s)";
    }

    private async void Audit_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (_military.Count == 0)
        {
            SigfurDialog.Show(this, "Nenhum militar disponível para a auditoria.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var year = YearCombo.SelectedItem is int y ? y : DateTime.Today.Year;
        var month = MonthCombo.SelectedValue is int m ? m : DateTime.Today.Month;
        _rows.Clear();
        LogBox.Clear();
        CountText.Text = "0 resultado(s)";
        AuditProgress.Minimum = 0;
        AuditProgress.Maximum = Math.Max(1, _military.Count);
        AuditProgress.Value = 0;
        ProgressText.Text = $"0 / {_military.Count}";
        _running = true;
        AuditButton.IsEnabled = false;
        StopCloseButton.Content = "Parar";
        AuditStatusText.Text = "Iniciando auditoria nativa em C#…";
        _auditCts = new CancellationTokenSource();

        try
        {
            _paystubs.InvalidateCache();
            var logs = new List<string>();
            for (var index = 0; index < _military.Count; index++)
            {
                _auditCts.Token.ThrowIfCancellationRequested();
                var military = _military[index];
                AuditStatusText.Text = $"Conferindo {index + 1}/{_military.Count}: {military.ShortRank} {military.Name}";
                var row = await _nativeAudit.AuditAsync(military, year, month, _rootFolder, _auditCts.Token);
                _rows.Add(row);
                if (row.Severity is "Critical" or "Warning")
                    logs.Add($"⚠ {military.ShortRank} {military.Name}: {row.Situation}");
                AuditProgress.Value = index + 1;
                ProgressText.Text = $"{index + 1} / {_military.Count}";
                LogBox.Text = string.Join(Environment.NewLine, logs.TakeLast(300));
                LogBox.ScrollToEnd();
                RefreshView();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
            var missing = _rows.Count(x => !x.PdfOk);
            var failures = _rows.Count(x => x.Situation.StartsWith("Erro", StringComparison.OrdinalIgnoreCase));
            AuditStatusText.Text = $"Auditoria finalizada em C#. Lidos: {_rows.Count - missing} | Sem contracheque: {missing} | Falhas: {failures}.";
        }
        catch (OperationCanceledException)
        {
            AuditStatusText.Text = "Auditoria interrompida.";
        }
        catch (Exception ex)
        {
            AuditStatusText.Text = "A auditoria não foi concluída.";
            SigfurDialog.Show(this, ex.Message, "Auditoria de contracheques", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _running = false;
            AuditButton.IsEnabled = true;
            StopCloseButton.Content = "Fechar";
            _auditCts?.Dispose();
            _auditCts = null;
        }
    }

    private MilitaryRecord? ResolveMilitary(PaystubAuditRow row)
    {
        if (row.MilitaryId > 0 && _militaryById.TryGetValue(row.MilitaryId, out var byId)) return byId;
        var cpf = MilitaryFormatting.Digits(row.Cpf);
        if (cpf.Length == 11)
        {
            var byCpf = _military.FirstOrDefault(x => MilitaryFormatting.Digits(x.Cpf) == cpf);
            if (byCpf is not null) return byCpf;
        }
        return _military.FirstOrDefault(x => x.Name.Equals(row.Name, StringComparison.CurrentCultureIgnoreCase));
    }

    private bool FilterRow(object item)
    {
        if (item is not PaystubAuditRow row) return false;
        var rank = RankCombo.SelectedItem?.ToString() ?? "Todos";
        if (!rank.Equals("Todos", StringComparison.OrdinalIgnoreCase) && !row.ShortRank.Equals(rank, StringComparison.CurrentCultureIgnoreCase)) return false;
        var search = MilitaryRankService.Normalize(SearchBox.Text);
        if (!string.IsNullOrWhiteSpace(search) && !row.SearchBlob.Contains(search, StringComparison.OrdinalIgnoreCase)) return false;
        var filter = FilterCombo.SelectedItem?.ToString() ?? "Todos";
        return filter switch
        {
            "Somente com achado" => row.HasFinding,
            "Divergências / verificar" => row.Severity is "Critical" or "Warning",
            "Sem contracheque" => !row.PdfOk,
            "Com FUSEx 3%" => row.PdfOk && row.Fusex > 0,
            "Sem FUSEx 3%" => row.PdfOk && row.Fusex <= 0,
            "Com Aux Transporte no contracheque" => row.PdfOk && row.AuxPdf > 0,
            "Sem Aux Transporte no contracheque" => row.PdfOk && row.AuxPdf <= 0,
            "Aux Transporte divergente" => row.AuxStatus.Contains("DIVERG", StringComparison.OrdinalIgnoreCase) || row.AuxStatus.Contains("NÃO RECEBEU", StringComparison.OrdinalIgnoreCase),
            "Aux Transporte OK" => row.AuxStatus.Equals("OK", StringComparison.OrdinalIgnoreCase),
            "Com desconto DR de Aux Transporte" => row.AuxDr > 0,
            "Com Salário Família" => row.FamilySalary > 0,
            "Sem Salário Família" => row.PdfOk && row.FamilySalary <= 0,
            "Com Pré-escolar" => row.PreSchool > 0,
            "Sem Pré-escolar" => row.PdfOk && row.PreSchool <= 0,
            "Com Férias" => row.Vacation > 0,
            "Sem Férias" => row.PdfOk && row.Vacation <= 0,
            "Com Aux Alimentação" => row.FoodAid > 0,
            "Sem Aux Alimentação" => row.PdfOk && row.FoodAid <= 0,
            "Com DR/AR/Atrasados" => row.Differences > 0,
            "Sem DR/AR/Atrasados" => row.PdfOk && row.Differences <= 0,
            "Com Pensão" => row.Pension > 0,
            "Com Pensão Alimentícia" => row.Alimony > 0,
            "Com IRRF" => row.Irrf > 0,
            "Com Adicional Habilitação" => row.QualificationAdditional > 0,
            "Com PNR" => row.Pnr > 0,
            "Com Empréstimo/Seguro/FHE" => row.Loans > 0,
            "Com Desconto Dependente FUSEx" => row.DependentFusex > 0,
            "Com Despesa Médica FUSEx" => row.MedicalFusex > 0,
            "Banco/Conta divergente" => row.BankDivergent || row.BankStatus.Contains("DIVERG", StringComparison.OrdinalIgnoreCase),
            "Banco/Conta OK" => row.BankStatus.Equals("OK", StringComparison.OrdinalIgnoreCase),
            "Situação diferente de Normal" => row.PaymentDifferent,
            "Pagamento suspenso" => row.PaymentStatus.Contains("SUSPENS", StringComparison.OrdinalIgnoreCase),
            "Situação Normal" => row.PaymentStatus.Equals("NORMAL", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    private void RefreshView()
    {
        _view.Refresh();
        CountText.Text = $"{_view.Cast<object>().Count()} resultado(s) de {_rows.Count}";
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded) RefreshView(); }
    private void Search_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) RefreshView(); }

    private PaystubAuditRow? SelectedRow => AuditGrid.SelectedItem as PaystubAuditRow;

    private void AuditGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = VisualTreeUtilities.FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is not null) row.IsSelected = true;
    }

    private void AuditGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenPdf();

    private void AuditGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OpenPdf();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            AuditGrid.UnselectAll();
            e.Handled = true;
        }
    }

    private void AuditGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var direction = e.Column == _sortColumn && _sortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        foreach (var column in AuditGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = direction;
        _sortColumn = e.Column;
        _sortDirection = direction;
        if (_view is ListCollectionView listView)
            listView.CustomSort = Comparer<object>.Create((left, right) => CompareRows(left as PaystubAuditRow, right as PaystubAuditRow, e.Column, direction));
        RefreshView();
    }

    private int CompareRows(PaystubAuditRow? left, PaystubAuditRow? right, DataGridColumn column, ListSortDirection direction)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return direction == ListSortDirection.Ascending ? -1 : 1;
        if (right is null) return direction == ListSortDirection.Ascending ? 1 : -1;
        int result;
        if (column == RankColumn)
            result = MilitaryRankService.Compare(left.Rank, left.Name, right.Rank, right.Name);
        else if (column == NameColumn) result = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
        else if (column == PdfColumn) result = StringComparer.CurrentCultureIgnoreCase.Compare(left.PdfStatus, right.PdfStatus);
        else if (column == BankColumn) result = StringComparer.CurrentCultureIgnoreCase.Compare(left.BankStatus, right.BankStatus);
        else if (column == PaymentColumn) result = StringComparer.CurrentCultureIgnoreCase.Compare(left.PaymentStatus, right.PaymentStatus);
        else if (column == AuxPdfColumn) result = left.AuxPdf.CompareTo(right.AuxPdf);
        else if (column == AuxDbColumn) result = Nullable.Compare(left.AuxDatabase, right.AuxDatabase);
        else if (column == AuxStatusColumn) result = StringComparer.CurrentCultureIgnoreCase.Compare(left.AuxStatus, right.AuxStatus);
        else if (column == AuxDrColumn) result = left.AuxDr.CompareTo(right.AuxDr);
        else if (column == AuxArColumn) result = left.AuxAr.CompareTo(right.AuxAr);
        else if (column == FusexColumn) result = left.Fusex.CompareTo(right.Fusex);
        else if (column == DependentFusexColumn) result = left.DependentFusex.CompareTo(right.DependentFusex);
        else if (column == MedicalFusexColumn) result = left.MedicalFusex.CompareTo(right.MedicalFusex);
        else if (column == FamilyColumn) result = left.FamilySalary.CompareTo(right.FamilySalary);
        else if (column == DependentsColumn) result = left.Dependents.CompareTo(right.Dependents);
        else if (column == PreSchoolColumn) result = left.PreSchool.CompareTo(right.PreSchool);
        else if (column == VacationColumn) result = left.Vacation.CompareTo(right.Vacation);
        else if (column == FoodColumn) result = left.FoodAid.CompareTo(right.FoodAid);
        else if (column == DifferencesColumn) result = left.Differences.CompareTo(right.Differences);
        else if (column == PensionColumn) result = left.Pension.CompareTo(right.Pension);
        else if (column == AlimonyColumn) result = left.Alimony.CompareTo(right.Alimony);
        else if (column == IrrfColumn) result = left.Irrf.CompareTo(right.Irrf);
        else if (column == QualificationColumn) result = left.QualificationAdditional.CompareTo(right.QualificationAdditional);
        else if (column == AvailabilityColumn) result = left.AvailabilityCompensation.CompareTo(right.AvailabilityCompensation);
        else if (column == PnrColumn) result = left.Pnr.CompareTo(right.Pnr);
        else if (column == LoansColumn) result = left.Loans.CompareTo(right.Loans);
        else if (column == RevenueColumn) result = left.Revenue.CompareTo(right.Revenue);
        else if (column == ExpenseColumn) result = left.Expense.CompareTo(right.Expense);
        else if (column == NetColumn) result = left.Net.CompareTo(right.Net);
        else result = StringComparer.CurrentCultureIgnoreCase.Compare(left.Summary, right.Summary);
        if (result == 0) result = MilitaryRankService.Compare(left.Rank, left.Name, right.Rank, right.Name);
        return direction == ListSortDirection.Ascending ? result : -result;
    }

    private void OpenPdf_Click(object sender, RoutedEventArgs e) => OpenPdf();

    private void OpenPdf()
    {
        var row = SelectedRow;
        if (row is null) { WarnSelect(); return; }
        if (!row.PdfOk || string.IsNullOrWhiteSpace(row.PdfPath) || !File.Exists(row.PdfPath))
        {
            SigfurDialog.Show(this, "O PDF desta competência não foi localizado.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ShellService.OpenPath(row.PdfPath);
    }

    private void OpenPdfFolder_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null) { WarnSelect(); return; }
        var folder = Path.GetDirectoryName(row.PdfPath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) folder = _rootFolder;
        Directory.CreateDirectory(folder);
        ShellService.OpenPath(folder);
    }

    private void OpenRootFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_rootFolder);
        ShellService.OpenPath(_rootFolder);
    }

    private void ChooseRootFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Pasta raiz dos contracheques para auditoria",
            InitialDirectory = Directory.Exists(_rootFolder) ? _rootFolder : App.Paths.PaystubsDirectory,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        _rootFolder = dialog.FolderName;
        ScopeText.Text = $"{_military.Count} militar(es) • pasta: {_rootFolder}";
        AuditStatusText.Text = "Pasta da auditoria atualizada.";
    }

    private void OpenPaystubs_Click(object sender, RoutedEventArgs e)
    {
        var military = SelectedRow?.Military;
        if (military is null) { WarnSelect(); return; }
        var window = new PaystubCenterWindow(_repository, _paystubs, [military], initialTab: 0, restrictedToSelection: true) { Owner = this };
        window.Show();
    }

    private void OpenWallet_Click(object sender, RoutedEventArgs e)
    {
        var military = SelectedRow?.Military;
        if (military is null) { WarnSelect(); return; }
        var window = new MilitaryWalletWindow(_repository, _paystubs, military, 6) { Owner = this };
        window.Show();
        window.Activate();
    }

    private void EditMilitary_Click(object sender, RoutedEventArgs e)
    {
        var military = SelectedRow?.Military;
        if (military is null) { WarnSelect(); return; }
        var window = new MilitaryEditorWindow(_repository, military, App.MilitaryPreferences) { Owner = this };
        window.ShowDialog();
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null) { WarnSelect(); return; }
        var window = new Window
        {
            Title = $"Auditoria — {row.ShortRank} {row.Name}", Owner = this,
            Width = 900, Height = 720, MinWidth = 680, MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.SingleBorderWindow, ResizeMode = ResizeMode.CanResize,
            Icon = Icon, Background = (System.Windows.Media.Brush)FindResource("AppBackgroundBrush")
        };
        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new TextBlock { FontSize = 17, FontWeight = FontWeights.Bold, Text = $"{row.ShortRank} {row.Name}" };
        grid.Children.Add(title);
        var text = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12, Margin = new Thickness(0, 12, 0, 12), Text = BuildDetails(row)
        };
        Grid.SetRow(text, 1); grid.Children.Add(text);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var open = MakeButton("Abrir PDF", () => OpenPdf());
        var folder = MakeButton("Abrir pasta", () => OpenPdfFolder_Click(this, new RoutedEventArgs()));
        var copy = MakeButton("Copiar relatório", () => Clipboard.SetText(text.Text));
        var close = MakeButton("Fechar", window.Close);
        buttons.Children.Add(open); buttons.Children.Add(folder); buttons.Children.Add(copy); buttons.Children.Add(close);
        Grid.SetRow(buttons, 2); grid.Children.Add(buttons);
        window.Content = grid;
        window.Show();
    }

    private Button MakeButton(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(14, 7, 14, 7) };
        button.Click += (_, _) => action();
        return button;
    }

    private static string BuildDetails(PaystubAuditRow row)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"MILITAR: {row.ShortRank} {row.Name}");
        sb.AppendLine($"CPF: {MilitaryFormatting.FormatCpf(row.Cpf)}   PREC-CP: {row.Prec}   IDT: {row.Idt}");
        sb.AppendLine($"PDF: {row.PdfStatus}");
        sb.AppendLine($"CAMINHO: {row.PdfPath}");
        sb.AppendLine();
        sb.AppendLine($"BANCO/CONTA: {row.BankStatus}");
        sb.AppendLine($"  PDF: banco {row.BankPdf} | agência {row.AgencyPdf} | conta {row.AccountPdf}");
        sb.AppendLine($"  Cadastro: banco {row.BankDatabase} | agência {row.AgencyDatabase} | conta {row.AccountDatabase}");
        sb.AppendLine($"SITUAÇÃO DO PAGAMENTO: {row.PaymentStatus}");
        sb.AppendLine($"AUXÍLIO-TRANSPORTE PDF: {row.AuxPdfText}");
        sb.AppendLine($"AUXÍLIO-TRANSPORTE CADASTRO: {row.AuxDatabaseText}");
        sb.AppendLine($"DIFERENÇA AT: {row.AuxDifferenceText}");
        sb.AppendLine($"SITUAÇÃO AT: {row.AuxStatus}");
        sb.AppendLine($"DESCONTO DR AT: {row.AuxDrText}");
        sb.AppendLine($"AT AR: {row.AuxArText}");
        sb.AppendLine($"FUSEx 3%: {row.FusexText}");
        sb.AppendLine($"FUSEx dependente: {row.DependentFusexText}");
        sb.AppendLine($"Despesa médica FUSEx: {row.MedicalFusexText}");
        sb.AppendLine($"Salário-família: {row.FamilySalaryText}");
        sb.AppendLine($"Pré-escolar: {row.PreSchoolText}");
        sb.AppendLine($"Férias: {row.VacationText}");
        sb.AppendLine($"Auxílio-alimentação: {row.FoodAidText}");
        sb.AppendLine($"DR/AR/Atrasados: {row.DifferencesText}");
        sb.AppendLine($"Pensão militar: {row.MilitaryPensionText}");
        sb.AppendLine($"Pensão alimentícia: {row.AlimonyText}");
        sb.AppendLine($"Pensão total: {row.PensionText}");
        sb.AppendLine($"IRRF: {row.IrrfText}");
        sb.AppendLine($"Adicional de habilitação: {row.QualificationAdditionalText}");
        sb.AppendLine($"AD C DISP MIL: {row.AvailabilityCompensationText}");
        sb.AppendLine($"PNR: {row.PnrText}");
        sb.AppendLine($"Empréstimo/Seguro/FHE: {row.LoansText}");
        sb.AppendLine($"Receita: {row.RevenueText}");
        sb.AppendLine($"Despesa: {row.ExpenseText}");
        sb.AppendLine($"Líquido: {row.NetText}");
        sb.AppendLine();
        sb.AppendLine("RESUMO:");
        sb.AppendLine(row.Summary);
        if (row.Lines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("LINHAS IDENTIFICADAS NO PDF:");
            foreach (var pair in row.Lines.Where(x => x.Value.Count > 0))
            {
                sb.AppendLine();
                sb.AppendLine($"[{pair.Key}]");
                foreach (var line in pair.Value) sb.AppendLine(line);
            }
        }
        return sb.ToString();
    }

    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        var entries = new List<ColumnChooserWindow.ColumnEntry>
        {
            new("P/G", RankColumn, true, true), new("Nome completo", NameColumn, true, true),
            new("PDF", PdfColumn, false, true), new("Banco/conta", BankColumn, false, true),
            new("Situação pagamento", PaymentColumn, false, true), new("AT no PDF", AuxPdfColumn, false, true),
            new("AT no cadastro", AuxDbColumn, false, true), new("AT situação", AuxStatusColumn, false, true),
            new("AT desconto DR", AuxDrColumn, false, false), new("AT AR", AuxArColumn, false, false),
            new("FUSEx 3%", FusexColumn, false, true), new("FUSEx dependente", DependentFusexColumn, false, false),
            new("Despesa médica", MedicalFusexColumn, false, false), new("Salário-família", FamilyColumn, false, true),
            new("Dependentes", DependentsColumn, false, false), new("Pré-escolar", PreSchoolColumn, false, true),
            new("Férias", VacationColumn, false, true), new("Aux. alimentação", FoodColumn, false, true),
            new("DR/AR/Atrasados", DifferencesColumn, false, true), new("Pensão", PensionColumn, false, false),
            new("Pensão alimentícia", AlimonyColumn, false, false), new("IRRF", IrrfColumn, false, false),
            new("Adic. habilitação", QualificationColumn, false, false), new("AD C DISP MIL", AvailabilityColumn, false, false),
            new("PNR", PnrColumn, false, false), new("Empréstimo/Seguro/FHE", LoansColumn, false, false),
            new("Receita", RevenueColumn, false, false), new("Despesa", ExpenseColumn, false, false),
            new("Líquido", NetColumn, false, false), new("Resumo / achados", SummaryColumn, false, true)
        };
        new ColumnChooserWindow(entries) { Owner = this }.ShowDialog();
    }

    private void CopyRowSummary_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null) { WarnSelect(); return; }
        Clipboard.SetText(BuildDetails(row));
        AuditStatusText.Text = "Resumo do militar copiado.";
    }

    private void QuickFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string filter)
        {
            FilterCombo.SelectedItem = filter;
            SearchBox.Clear();
            RefreshView();
        }
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        FilterCombo.SelectedItem = "Todos";
        RankCombo.SelectedItem = "Todos";
        SearchBox.Clear();
        RefreshView();
    }

    private void ExportFilteredCsv_Click(object sender, RoutedEventArgs e) => ExportCsv(_view.Cast<PaystubAuditRow>().ToList(), "filtrado", visibleOnly: true);
    private void ExportAllCsv_Click(object sender, RoutedEventArgs e) => ExportCsv(_rows.ToList(), "completo", visibleOnly: false);

    private List<(string Header, DataGridColumn Column, Func<PaystubAuditRow, string> Value)> ExportColumns()
    {
        return
        [
            ("P/G", RankColumn, row => row.ShortRank),
            ("NOME", NameColumn, row => row.Name),
            ("CPF", NameColumn, row => MilitaryFormatting.Digits(row.Cpf)),
            ("PDF", PdfColumn, row => row.PdfStatus),
            ("BANCO/CONTA", BankColumn, row => row.BankStatus),
            ("SITUAÇÃO PAGAMENTO", PaymentColumn, row => row.PaymentStatus),
            ("AT PDF", AuxPdfColumn, row => row.AuxPdfText),
            ("AT CADASTRO", AuxDbColumn, row => row.AuxDatabaseText),
            ("AT SITUAÇÃO", AuxStatusColumn, row => row.AuxStatus),
            ("AT DR", AuxDrColumn, row => row.AuxDrText),
            ("AT AR", AuxArColumn, row => row.AuxArText),
            ("FUSEx 3%", FusexColumn, row => row.FusexText),
            ("FUSEx DEPENDENTE", DependentFusexColumn, row => row.DependentFusexText),
            ("DESPESA MÉDICA", MedicalFusexColumn, row => row.MedicalFusexText),
            ("SALÁRIO FAMÍLIA", FamilyColumn, row => row.FamilySalaryText),
            ("DEPENDENTES", DependentsColumn, row => row.Dependents.ToString(CultureInfo.InvariantCulture)),
            ("PRÉ-ESCOLAR", PreSchoolColumn, row => row.PreSchoolText),
            ("FÉRIAS", VacationColumn, row => row.VacationText),
            ("AUX. ALIMENTAÇÃO", FoodColumn, row => row.FoodAidText),
            ("DR/AR/ATRASADOS", DifferencesColumn, row => row.DifferencesText),
            ("PENSÃO", PensionColumn, row => row.PensionText),
            ("PENSÃO ALIMENTÍCIA", AlimonyColumn, row => row.AlimonyText),
            ("IRRF", IrrfColumn, row => row.IrrfText),
            ("ADIC. HABILITAÇÃO", QualificationColumn, row => row.QualificationAdditionalText),
            ("AD C DISP MIL", AvailabilityColumn, row => row.AvailabilityCompensationText),
            ("PNR", PnrColumn, row => row.PnrText),
            ("EMPRÉSTIMO/SEGURO/FHE", LoansColumn, row => row.LoansText),
            ("RECEITA", RevenueColumn, row => row.RevenueText),
            ("DESPESA", ExpenseColumn, row => row.ExpenseText),
            ("LÍQUIDO", NetColumn, row => row.NetText),
            ("RESUMO", SummaryColumn, row => row.Summary)
        ];
    }

    private void ExportCsv(IReadOnlyList<PaystubAuditRow> rows, string suffix, bool visibleOnly)
    {
        if (rows.Count == 0) { WarnNoResults(); return; }
        var year = YearCombo.SelectedItem is int y ? y : DateTime.Today.Year;
        var month = MonthCombo.SelectedValue is int m ? m : DateTime.Today.Month;
        var dialog = new SaveFileDialog
        {
            Title = "Salvar relatório de auditoria", Filter = "CSV|*.csv|Todos os arquivos|*.*",
            FileName = $"auditoria_contracheques_{suffix}_{year}_{month:00}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        var columns = ExportColumns()
            .Where(x => !visibleOnly || x.Header is "P/G" or "NOME" or "CPF" || x.Column.Visibility == Visibility.Visible)
            .ToList();
        var lines = new List<string>
        {
            "Auditoria de Contracheques",
            $"Mês/Ano;{month:00}/{year};Filtro;{FilterCombo.SelectedItem};P/G;{RankCombo.SelectedItem};Gerado em;{DateTime.Now:dd/MM/yyyy HH:mm}",
            string.Join(';', columns.Select(x => Csv(x.Header)))
        };
        lines.AddRange(rows.Select(row => string.Join(';', columns.Select(x => Csv(x.Value(row))))));
        File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true));
        ShellService.OpenPath(dialog.FileName);
    }

    private void ExportWord_Click(object sender, RoutedEventArgs e)
    {
        var rows = _view.Cast<PaystubAuditRow>().ToList();
        if (rows.Count == 0) { WarnNoResults(); return; }
        var year = YearCombo.SelectedItem is int y ? y : DateTime.Today.Year;
        var month = MonthCombo.SelectedValue is int m ? m : DateTime.Today.Month;
        var dialog = new SaveFileDialog
        {
            Title = "Gerar lista Word filtrada", Filter = "Documento Word|*.docx",
            FileName = $"lista_auditoria_{year}_{month:00}.docx"
        };
        if (dialog.ShowDialog(this) != true) return;
        GenerateDocx(dialog.FileName, rows, year, month);
        ShellService.OpenPath(dialog.FileName);
    }

    private void GenerateDocx(string path, IReadOnlyList<PaystubAuditRow> rows, int year, int month)
    {
        static string Esc(string value) => SecurityElement.Escape(value) ?? string.Empty;
        static string Run(string text, bool bold = false) => $"<w:r>{(bold ? "<w:rPr><w:b/></w:rPr>" : string.Empty)}<w:t xml:space=\"preserve\">{Esc(text)}</w:t></w:r>";
        static string Paragraph(string runs, string? style = null) => $"<w:p>{(string.IsNullOrWhiteSpace(style) ? string.Empty : $"<w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>")}{runs}</w:p>";
        var body = new StringBuilder();
        body.Append(Paragraph(Run("AUDITORIA DE CONTRACHEQUES", true), "Title"));
        body.Append(Paragraph(Run($"Competência: {month:00}/{year}    Filtro: {FilterCombo.SelectedItem}    P/G: {RankCombo.SelectedItem}")));
        body.Append(Paragraph(Run($"Gerado em {DateTime.Now:dd/MM/yyyy HH:mm} — {rows.Count} militar(es).")));
        var visibleColumns = ExportColumns()
            .Where(x => x.Header is not ("P/G" or "NOME" or "CPF") && x.Column.Visibility == Visibility.Visible)
            .ToList();
        var position = 0;
        foreach (var row in rows)
        {
            position++;
            var nameRuns = Run($"{position}. ", true) + Run(row.ShortRank + " ") + string.Concat(NameHighlightHelper.BuildSegments(row.Name, row.WarName).Select(segment => Run(segment.Text, segment.IsBold)));
            body.Append(Paragraph(nameRuns));
            var details = string.Join(" | ", visibleColumns
                .Select(x => (x.Header, Value: x.Value(row)))
                .Where(x => !string.IsNullOrWhiteSpace(x.Value) && x.Value is not "—" and not "R$ 0,00" and not "0")
                .Select(x => $"{x.Header}: {x.Value}"));
            if (!string.IsNullOrWhiteSpace(details)) body.Append(Paragraph(Run("   " + details)));
            body.Append(Paragraph(Run(string.Empty)));
        }
        var document = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>{body}<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"900\" w:right=\"900\" w:bottom=\"900\" w:left=\"900\"/></w:sectPr></w:body></w:document>";
        var contentTypes = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>";
        var relationships = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>";
        var styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/><w:rPr><w:b/><w:sz w:val=\"32\"/></w:rPr></w:style></w:styles>";
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(zip, "[Content_Types].xml", contentTypes);
        WriteEntry(zip, "_rels/.rels", relationships);
        WriteEntry(zip, "word/document.xml", document);
        WriteEntry(zip, "word/styles.xml", styles);
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string Csv(string value) => '"' + (value ?? string.Empty).Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + '"';

    private async void StopClose_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            try
            {
                _auditCts?.Cancel();
                AuditStatusText.Text = "Parada solicitada…";
            }
            catch { }
            return;
        }
        Close();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        if (_running)
        {
            try
            {
                _auditCts?.Cancel();
            }
            catch { }
        }
    }

    private void WarnSelect() => SigfurDialog.Show(this, "Selecione um militar na tabela.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
    private void WarnNoResults() => SigfurDialog.Show(this, "Nenhum resultado no filtro atual.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
}
