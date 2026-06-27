using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views;

public partial class ProfileWindow : Window
{
    private readonly List<string> _organizations;
    private readonly Dictionary<string, string> _images;
    private readonly OrganizationCatalogService _catalogService;
    private readonly ObservableCollection<OrganizationCatalogEntry> _visibleOfficial = [];
    private List<OrganizationCatalogEntry> _official = [];
    private string _lastOrganization = string.Empty;
    private bool _loading;

    public ProfileWindow(UiProfile profile)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _catalogService = new OrganizationCatalogService(App.Paths, App.Json, App.Log);
        _organizations = (profile.OrganizationCatalog ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        if (!string.IsNullOrWhiteSpace(profile.Organization) && !_organizations.Contains(profile.Organization, StringComparer.OrdinalIgnoreCase))
            _organizations.Insert(0, profile.Organization);
        _images = new Dictionary<string, string>(profile.OrganizationImages ?? new(), StringComparer.OrdinalIgnoreCase);
        Profile = new UiProfile
        {
            Rank = profile.Rank,
            Operator = profile.Operator,
            Function = profile.Function,
            Organization = profile.Organization,
            LogoPath = profile.LogoPath,
            CommanderName = profile.CommanderName,
            CommanderRank = profile.CommanderRank,
            LegacyProjectRoot = profile.LegacyProjectRoot,
            OrganizationCatalog = [.. _organizations],
            OrganizationImages = new(_images, StringComparer.OrdinalIgnoreCase)
        };

        RankBox.ItemsSource = MilitaryRankService.AllRanks.Where(x => !x.Contains("Marechal", StringComparison.OrdinalIgnoreCase)).ToList();
        RankBox.SelectedItem = Profile.Rank;
        OperatorBox.Text = Profile.Operator;
        FunctionBox.Text = Profile.Function;
        OfficialOrganizationGrid.ItemsSource = _visibleOfficial;
        StateFilterBox.ItemsSource = new[] { "Todos" };
        StateFilterBox.SelectedIndex = 0;
        RefreshOrganizations(Profile.Organization);
        _lastOrganization = Profile.Organization;
        LoadOrganizationImage(Profile.Organization, Profile.LogoPath);
        Loaded += async (_, _) => await LoadOfficialCacheAsync();
    }

    public UiProfile Profile { get; private set; }
    private string CurrentOrganization => (OrganizationBox.Text ?? string.Empty).Trim();

    private async Task LoadOfficialCacheAsync()
    {
        _official = (await _catalogService.LoadCachedAsync()).ToList();
        if (_official.Count == 0)
        {
            CatalogStatusText.Text = "Catálogo oficial ainda não baixado";
            RefreshOfficialView();
            return;
        }
        CatalogStatusText.Text = $"Catálogo oficial: {_official.Count:N0} OMs em cache";
        PopulateStates();
        MergeOfficialNames();
        RefreshOfficialView();
    }

    private void PopulateStates()
    {
        var states = new[] { "Todos" }.Concat(_official.Select(x => x.State).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x)).ToList();
        var current = StateFilterBox.SelectedItem?.ToString() ?? "Todos";
        StateFilterBox.ItemsSource = states;
        StateFilterBox.SelectedItem = states.Contains(current) ? current : "Todos";
    }

    private void MergeOfficialNames()
    {
        foreach (var entry in _official)
            if (!_organizations.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) _organizations.Add(entry.Name);
        RefreshOrganizations(CurrentOrganization);
    }

    private void RefreshOrganizations(string selected)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            OrganizationBox.ItemsSource = null;
            OrganizationBox.ItemsSource = _organizations.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
            OrganizationBox.Text = selected;
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void RefreshOfficialView()
    {
        var q = MilitaryRankService.Normalize(OrganizationSearchBox?.Text);
        var state = StateFilterBox?.SelectedItem?.ToString() ?? "Todos";
        _visibleOfficial.Clear();
        foreach (var entry in _official)
        {
            if (!state.Equals("Todos", StringComparison.OrdinalIgnoreCase) && !entry.State.Equals(state, StringComparison.OrdinalIgnoreCase)) continue;
            var hay = MilitaryRankService.Normalize(entry.SearchText);
            if (!string.IsNullOrWhiteSpace(q) && !q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(term => hay.Contains(term, StringComparison.OrdinalIgnoreCase))) continue;
            _visibleOfficial.Add(entry);
        }
    }

    private void SaveCurrentImageMapping()
    {
        if (!string.IsNullOrWhiteSpace(_lastOrganization) && !string.IsNullOrWhiteSpace(LogoBox.Text))
            _images[_lastOrganization] = LogoBox.Text.Trim();
    }

    private void LoadOrganizationImage(string organization, string fallback = "")
    {
        var path = _images.GetValueOrDefault(organization);
        if (string.IsNullOrWhiteSpace(path))
            path = _official.FirstOrDefault(x => x.Name.Equals(organization, StringComparison.OrdinalIgnoreCase))?.CachedLogoPath;
        if (string.IsNullOrWhiteSpace(path)) path = fallback;
        LogoBox.Text = path ?? string.Empty;
        UpdateImagePreview(path);
    }

    private async Task ApplyOfficialEntryAsync(OrganizationCatalogEntry entry, bool resolveImage)
    {
        if (!_organizations.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) _organizations.Add(entry.Name);
        SaveCurrentImageMapping();
        _lastOrganization = entry.Name;
        RefreshOrganizations(entry.Name);
        OrganizationBox.Text = entry.Name;
        OfficialDetailTitle.Text = entry.Name;
        OfficialDetailText.Text = string.Join("\n", new[]
        {
            entry.LocationText,
            entry.Address,
            string.IsNullOrWhiteSpace(entry.District) ? string.Empty : "Bairro: " + entry.District,
            string.IsNullOrWhiteSpace(entry.ZipCode) ? string.Empty : "CEP: " + entry.ZipCode,
            string.IsNullOrWhiteSpace(entry.Email) ? string.Empty : "E-mail: " + entry.Email
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        LoadOrganizationImage(entry.Name);
        if (resolveImage && string.IsNullOrWhiteSpace(LogoBox.Text))
            await ResolveLogoAsync(entry);
    }

    private async Task ResolveLogoAsync(OrganizationCatalogEntry entry)
    {
        try
        {
            LogoStatusText.Text = "Procurando imagem institucional no site público da OM…";
            IsEnabled = false;
            var path = await _catalogService.ResolveAndCacheLogoAsync(entry);
            if (string.IsNullOrWhiteSpace(path))
            {
                LogoStatusText.Text = "Não foi encontrada imagem institucional automática. Escolha uma imagem local para esta OM.";
                return;
            }
            _images[entry.Name] = path;
            LogoBox.Text = path;
            UpdateImagePreview(path);
            LogoStatusText.Text = "Imagem oficial/localizada foi salva no cache do SIGFUR.";
        }
        catch (Exception ex)
        {
            LogoStatusText.Text = "Não foi possível buscar a imagem automaticamente: " + ex.Message;
        }
        finally { IsEnabled = true; }
    }

    private async void OrganizationBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _loading) return;
        SaveCurrentImageMapping();
        _lastOrganization = CurrentOrganization;
        LoadOrganizationImage(_lastOrganization);
        var entry = _official.FirstOrDefault(x => x.Name.Equals(_lastOrganization, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) await ApplyOfficialEntryAsync(entry, resolveImage: false);
    }

    private void OrganizationBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var current = CurrentOrganization;
        if (string.Equals(current, _lastOrganization, StringComparison.OrdinalIgnoreCase)) return;
        SaveCurrentImageMapping();
        _lastOrganization = current;
        if (!_organizations.Contains(current, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(current)) _organizations.Add(current);
        LoadOrganizationImage(current);
    }

    private void ChooseLogo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Imagens|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.ico|Todos os arquivos|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        LogoBox.Text = dialog.FileName;
        if (!string.IsNullOrWhiteSpace(CurrentOrganization)) _images[CurrentOrganization] = dialog.FileName;
        LogoStatusText.Text = "Imagem local vinculada à OM.";
    }

    private void LogoBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateImagePreview(LogoBox.Text);

    private void UpdateImagePreview(string? path)
    {
        if (ProfileImage is null) return;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ProfileImage.Source = null;
                ProfileFallback.Visibility = Visibility.Visible;
                return;
            }
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.DecodePixelWidth = 900;
            image.EndInit();
            image.Freeze();
            ProfileImage.Source = image;
            ProfileFallback.Visibility = Visibility.Collapsed;
        }
        catch
        {
            ProfileImage.Source = null;
            ProfileFallback.Visibility = Visibility.Visible;
        }
    }

    private async void RefreshOfficialCatalog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CatalogStatusText.Text = "Atualizando catálogo oficial…";
            IsEnabled = false;
            _official = (await _catalogService.RefreshFromOfficialDirectoryAsync()).ToList();
            PopulateStates();
            MergeOfficialNames();
            RefreshOfficialView();
            CatalogStatusText.Text = $"Catálogo oficial: {_official.Count:N0} OMs atualizadas";
        }
        catch (Exception ex)
        {
            CatalogStatusText.Text = "Falha na atualização";
            SigfurDialog.Show(this, "Não foi possível atualizar o catálogo oficial. O cache anterior foi preservado.\n\n" + ex.Message, "Organizações Militares", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsEnabled = true; }
    }

    private void OrganizationSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshOfficialView();
    private void StateFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) RefreshOfficialView(); }

    private void OfficialOrganizationGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OfficialOrganizationGrid.SelectedItem is not OrganizationCatalogEntry entry) return;
        OfficialDetailTitle.Text = entry.Name;
        OfficialDetailText.Text = string.Join("\n", new[] { entry.LocationText, entry.Address, entry.District, entry.ZipCode, entry.Email }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private async void OfficialOrganizationGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (OfficialOrganizationGrid.SelectedItem is OrganizationCatalogEntry entry) await ApplyOfficialEntryAsync(entry, resolveImage: true);
    }

    private async void UseSelectedOrganization_Click(object sender, RoutedEventArgs e)
    {
        if (OfficialOrganizationGrid.SelectedItem is OrganizationCatalogEntry entry) await ApplyOfficialEntryAsync(entry, resolveImage: true);
    }

    private async void ResolveOfficialLogo_Click(object sender, RoutedEventArgs e)
    {
        var entry = OfficialOrganizationGrid.SelectedItem as OrganizationCatalogEntry
                    ?? _official.FirstOrDefault(x => x.Name.Equals(CurrentOrganization, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            SigfurDialog.Show(this, "Selecione uma OM do catálogo oficial primeiro.", "Imagem da OM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await ResolveLogoAsync(entry);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var organization = CurrentOrganization;
        if (string.IsNullOrWhiteSpace(OperatorBox.Text) || string.IsNullOrWhiteSpace(organization))
        {
            SigfurDialog.Show(this, "Informe o nome do operador e a Organização Militar.", "Perfil", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!_organizations.Contains(organization, StringComparer.OrdinalIgnoreCase)) _organizations.Add(organization);
        if (!string.IsNullOrWhiteSpace(LogoBox.Text)) _images[organization] = LogoBox.Text.Trim();
        var logo = _images.GetValueOrDefault(organization) ?? LogoBox.Text.Trim();
        Profile = new UiProfile
        {
            Rank = RankBox.Text.Trim(),
            Operator = OperatorBox.Text.Trim(),
            Function = FunctionBox.Text.Trim(),
            Organization = organization,
            LogoPath = logo,
            CommanderName = string.Empty,
            CommanderRank = string.Empty,
            LegacyProjectRoot = Profile.LegacyProjectRoot,
            OrganizationCatalog = [.. _organizations.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)],
            OrganizationImages = new(_images, StringComparer.OrdinalIgnoreCase)
        };
        DialogResult = true;
    }
}
