using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views;

public partial class HelpWindow : Window
{
    private readonly List<ActionDefinition> _modules = [];
    private ICollectionView? _moduleView;

    public HelpWindow()
    {
        InitializeComponent();
        App.UiState.Attach(this);
        Loaded += HelpWindow_Loaded;
    }

    private async void HelpWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "SIGFUR" : $"Versão {version.Major}.{version.Minor}.{version.Build}";
        ThemeText.Text = $"Tema: {App.Theme.Current.DisplayName}";
        DeveloperOrganizationText.Text = $"Desenvolvido por 3º Sgt Otávio · {OrganizationIdentity.Name}";
        var profileName = App.StartupSession.IsProfileMode
            ? App.StartupSession.Profile?.ProfileName ?? "Perfil SIGFUR"
            : "dados deste computador";
        DataPathText.Text = $"Perfil: {profileName}\nPasta ativa: {App.Paths.DataDirectory}";
        SystemInfoBox.Text = BuildDiagnostics();

        Dictionary<string, string> hotkeys;
        try { hotkeys = await App.Settings.LoadHotkeysAsync(); }
        catch { hotkeys = SettingsService.DefaultHotkeys(); }

        _modules.Clear();
        _modules.AddRange(ActionCatalog.Create(hotkeys));
        _moduleView = CollectionViewSource.GetDefaultView(_modules);
        _moduleView.Filter = FilterModule;
        _moduleView.SortDescriptions.Add(new SortDescription(nameof(ActionDefinition.Category), ListSortDirection.Ascending));
        _moduleView.SortDescriptions.Add(new SortDescription(nameof(ActionDefinition.Title), ListSortDirection.Ascending));
        ModuleList.ItemsSource = _moduleView;

        ModuleCategoryBox.Items.Add("Todas as categorias");
        foreach (var category in _modules.Select(item => item.Category).Distinct().OrderBy(value => value))
            ModuleCategoryBox.Items.Add(category);
        ModuleCategoryBox.SelectedIndex = 0;
    }

    private bool FilterModule(object item)
    {
        if (item is not ActionDefinition module) return false;
        var category = ModuleCategoryBox.SelectedItem?.ToString() ?? "Todas as categorias";
        if (category != "Todas as categorias" && module.Category != category) return false;
        var query = Normalize(ModuleSearchBox.Text);
        return string.IsNullOrWhiteSpace(query)
               || Normalize($"{module.Title} {module.Description} {module.Category} {module.Id}").Contains(query, StringComparison.Ordinal);
    }

    private void ModuleFilter_Changed(object sender, RoutedEventArgs e)
    {
        _moduleView?.Refresh();
        if (StatusText is not null)
            StatusText.Text = $"{_moduleView?.Cast<object>().Count() ?? 0} recurso(s) encontrado(s).";
    }

    private void HelpTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, HelpTabs) || HelpTabs.SelectedIndex != 1) return;
        Dispatcher.BeginInvoke(() => ModuleSearchBox.Focus());
    }

    private string BuildDiagnostics()
    {
        var database = App.Database.LastReport;
        var databaseState = database?.Official.IsValid == true
            ? "íntegro"
            : database is null ? "ainda não verificado" : "requer atenção";
        var profile = App.StartupSession.IsProfileMode
            ? App.StartupSession.Profile?.ProfileName ?? "Perfil SIGFUR"
            : "sem perfil";
        return $"SIGFUR: {VersionText.Text}\n" +
               $"Perfil: {profile}\n" +
               $"Tema: {App.Theme.Current.DisplayName}\n" +
               $"Banco: {databaseState}\n" +
               $"Pasta de dados: {App.Paths.DataDirectory}\n" +
               $"Windows: {Environment.OSVersion.VersionString}\n" +
               $".NET: {Environment.Version}\n" +
               $"Gerado em: {DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.GetCultureInfo("pt-BR"))}";
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        ShellService.OpenPath(App.Paths.DataDirectory);
        StatusText.Text = "Pasta de dados aberta no Explorador.";
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        SystemInfoBox.Text = BuildDiagnostics();
        Clipboard.SetText(SystemInfoBox.Text);
        StatusText.Text = "Diagnóstico copiado. Cole-o junto com a descrição do problema.";
    }

    private static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(text.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            .Select(char.ToLowerInvariant).ToArray());
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
