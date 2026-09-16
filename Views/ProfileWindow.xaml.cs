using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private string _currentImageSourceName = string.Empty;
    private string _currentImageSourceUrl = string.Empty;
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
            try
            {
                CatalogStatusText.Text = "Baixando catálogo oficial pela primeira vez…";
                _official = (await _catalogService.RefreshFromOfficialDirectoryAsync()).ToList();
            }
            catch (Exception ex)
            {
                CatalogStatusText.Text = "Catálogo oficial ainda não baixado";
                PhotoCatalogStatusText.Text = "Conecte-se à internet e tente atualizar novamente.";
                RefreshOfficialView();
                await App.Log.WriteAsync("Falha ao baixar automaticamente o catálogo de OMs.", ex);
                return;
            }
        }
        CatalogStatusText.Text = $"Catálogo institucional: {_official.Count:N0} OMs";
        PopulateStates();
        MergeOfficialNames();
        RefreshOfficialView();
        UpdatePhotoCatalogStatus();
        LoadOrganizationImage(CurrentOrganization, Profile.LogoPath);
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
        UpdatePhotoCatalogStatus();
    }

    private void UpdatePhotoCatalogStatus()
    {
        if (PhotoCatalogStatusText is null) return;
        var withImage = _official.Count(x => x.HasCachedImage);
        PhotoCatalogStatusText.Text = _official.Count == 0
            ? "Imagens: catálogo ainda não carregado"
            : $"Imagens salvas: {withImage:N0} de {_official.Count:N0} • as demais podem ser buscadas quando forem utilizadas";
    }

    private void SaveCurrentImageMapping()
    {
        if (!string.IsNullOrWhiteSpace(_lastOrganization) && !string.IsNullOrWhiteSpace(LogoBox.Text))
            _images[_lastOrganization] = LogoBox.Text.Trim();
    }

    private void LoadOrganizationImage(string organization, string fallback = "")
    {
        var entry = _official.FirstOrDefault(x => x.Name.Equals(organization, StringComparison.OrdinalIgnoreCase));
        var path = _images.GetValueOrDefault(organization);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            path = entry?.CachedLogoPath;
        if (string.IsNullOrWhiteSpace(path)) path = fallback;
        if (entry is not null && string.Equals(path, entry.CachedLogoPath, StringComparison.OrdinalIgnoreCase))
        {
            _currentImageSourceName = entry.CachedLogoSourceName;
            _currentImageSourceUrl = entry.CachedLogoSourceUrl;
        }
        else
        {
            _currentImageSourceName = string.IsNullOrWhiteSpace(path) ? string.Empty : "Arquivo local";
            _currentImageSourceUrl = string.Empty;
        }
        LogoBox.Text = path ?? string.Empty;
        UpdateImagePreview(path);
        UpdateImageSourceCard();
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
            string.IsNullOrWhiteSpace(entry.Email) ? string.Empty : "E-mail: " + entry.Email,
            string.IsNullOrWhiteSpace(entry.InstagramUrl) ? string.Empty : "Instagram: " + entry.InstagramUrl,
            "Imagem: " + entry.ImageSourceText
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        LoadOrganizationImage(entry.Name);
        if (resolveImage && string.IsNullOrWhiteSpace(LogoBox.Text))
            await ResolveLogoAsync(entry);
    }

    private async Task ResolveLogoAsync(OrganizationCatalogEntry entry)
    {
        try
        {
            LogoStatusText.Text = "Pesquisando fotos e símbolos em páginas públicas, Instagram e sites do Exército…";
            SearchLogoButton.IsEnabled = false;
            SearchLogoButton.Content = "Pesquisando…";
            Mouse.OverrideCursor = Cursors.Wait;
            var candidates = await _catalogService.SearchImageCandidatesAsync(entry);
            Mouse.OverrideCursor = null;
            SearchLogoButton.IsEnabled = true;
            SearchLogoButton.Content = "Buscar fotos e símbolos";
            if (candidates.Count == 0)
            {
                LogoStatusText.Text = "Nenhuma imagem institucional foi encontrada nas fontes online para esta OM.";
                SigfurDialog.Show(this,
                    "Nenhuma imagem institucional confiável foi encontrada na internet para esta OM.\n\nTente atualizar o catálogo e pesquisar novamente. O arquivo local continua disponível somente no botão 'Escolher imagem local'.",
                    "Busca online concluída", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var picker = new OrganizationImagePickerWindow(entry.Name, candidates) { Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedCandidate is not { } selected)
            {
                LogoStatusText.Text = "Busca concluída sem alteração da imagem atual.";
                return;
            }

            IsEnabled = false;
            LogoStatusText.Text = "Salvando a imagem selecionada no cache local…";
            var resolution = await _catalogService.CacheImageCandidateAsync(entry, selected);
            if (resolution is null)
            {
                LogoStatusText.Text = "A imagem selecionada não pôde ser baixada. Escolha outra opção ou use um arquivo local.";
                return;
            }
            _images[entry.Name] = resolution.LocalPath;
            _currentImageSourceName = resolution.SourceName;
            _currentImageSourceUrl = resolution.SourceUrl;
            LogoBox.Text = resolution.LocalPath;
            UpdateImagePreview(resolution.LocalPath);
            UpdateImageSourceCard();
            RefreshOfficialView();
            LogoStatusText.Text = $"Imagem salva no SIGFUR • origem: {resolution.SourceName}.";
        }
        catch (Exception ex)
        {
            LogoStatusText.Text = "Não foi possível buscar a imagem automaticamente: " + ex.Message;
            SigfurDialog.Show(this, "A busca online não pôde ser concluída. Verifique a conexão e tente novamente.", "Imagem da OM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
            SearchLogoButton.IsEnabled = true;
            SearchLogoButton.Content = "Buscar fotos e símbolos";
            Mouse.OverrideCursor = null;
        }
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

    private void ChooseLogo_Click(object sender, RoutedEventArgs e) => ChooseLocalLogo();

    private bool ChooseLocalLogo()
    {
        var dialog = new OpenFileDialog { Filter = "Imagens|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.ico|Todos os arquivos|*.*" };
        if (dialog.ShowDialog(this) != true)
        {
            LogoStatusText.Text = "Nenhuma imagem foi escolhida.";
            return false;
        }
        LogoBox.Text = dialog.FileName;
        if (!string.IsNullOrWhiteSpace(CurrentOrganization)) _images[CurrentOrganization] = dialog.FileName;
        _currentImageSourceName = "Arquivo local";
        _currentImageSourceUrl = string.Empty;
        UpdateImageSourceCard();
        LogoStatusText.Text = "Imagem local vinculada à OM.";
        return true;
    }

    private void LogoBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateImagePreview(LogoBox.Text);

    private void UpdateImageSourceCard()
    {
        if (LogoSourceText is null) return;
        var hasImage = !string.IsNullOrWhiteSpace(LogoBox?.Text) && File.Exists(LogoBox.Text);
        LogoSourceText.Text = !hasImage
            ? "Imagem ainda não definida"
            : string.IsNullOrWhiteSpace(_currentImageSourceName) ? "Cache local do SIGFUR" : _currentImageSourceName;
        OpenImageSourceButton.Visibility = hasImage && !string.IsNullOrWhiteSpace(_currentImageSourceUrl)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OpenImageSource_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentImageSourceUrl)) return;
        try { Process.Start(new ProcessStartInfo(_currentImageSourceUrl) { UseShellExecute = true }); }
        catch (Exception ex) { SigfurDialog.Show(this, "Não foi possível abrir a origem.\n\n" + ex.Message, "Imagem da OM", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void RemoveLogo_Click(object sender, RoutedEventArgs e)
    {
        var organization = CurrentOrganization;
        if (string.IsNullOrWhiteSpace(organization)) return;
        _images.Remove(organization);
        var entry = _official.FirstOrDefault(x => x.Name.Equals(organization, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) await _catalogService.ClearCachedImageAsync(entry);
        _currentImageSourceName = string.Empty;
        _currentImageSourceUrl = string.Empty;
        LogoBox.Text = string.Empty;
        UpdateImagePreview(string.Empty);
        UpdateImageSourceCard();
        RefreshOfficialView();
        LogoStatusText.Text = "Imagem removida. Escolha ou pesquise uma nova antes de salvar o perfil.";
    }

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
            UpdatePhotoCatalogStatus();
        }
        catch (Exception ex)
        {
            CatalogStatusText.Text = "Falha na atualização";
            SigfurDialog.Show(this, "Não foi possível atualizar o catálogo oficial. O cache anterior foi preservado.\n\n" + ex.Message, "Organizações Militares", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsEnabled = true; }
    }

    private void OrganizationSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshOfficialView();
    private void ShowPoliceOrganizations_Click(object sender, RoutedEventArgs e)
    {
        StateFilterBox.SelectedItem = "Todos";
        OrganizationSearchBox.Text = "PE";
        OrganizationSearchBox.Focus();
        OrganizationSearchBox.SelectAll();
    }
    private void StateFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) RefreshOfficialView(); }

    private void OfficialOrganizationGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OfficialOrganizationGrid.SelectedItem is not OrganizationCatalogEntry entry) return;
        OfficialDetailTitle.Text = entry.Name;
        OfficialDetailText.Text = string.Join("\n", new[]
        {
            entry.LocationText,
            entry.Address,
            entry.District,
            entry.ZipCode,
            entry.Email,
            string.IsNullOrWhiteSpace(entry.InstagramUrl) ? string.Empty : "Instagram: " + entry.InstagramUrl,
            "Imagem: " + entry.ImageSourceText
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
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
            if (string.IsNullOrWhiteSpace(CurrentOrganization))
            {
                SigfurDialog.Show(this, "Escolha primeiro a Organização Militar.", "Imagem da OM", MessageBoxButton.OK, MessageBoxImage.Information);
                OrganizationBox.Focus();
                return;
            }
            entry = new OrganizationCatalogEntry { Name = CurrentOrganization };
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
        if (string.IsNullOrWhiteSpace(LogoBox.Text) || !File.Exists(LogoBox.Text))
        {
            SigfurDialog.Show(this, "A imagem institucional da Organização Militar é obrigatória.\n\nBusque uma foto ou símbolo na internet, ou escolha um arquivo local antes de salvar.",
                "Imagem obrigatória", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
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
