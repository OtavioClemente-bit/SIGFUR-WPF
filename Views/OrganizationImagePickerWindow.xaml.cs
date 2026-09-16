using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Views;

public partial class OrganizationImagePickerWindow : Window
{
    public OrganizationImagePickerWindow(string organization, IReadOnlyList<OrganizationImageCandidate> candidates)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        CandidateList.ItemsSource = candidates;
        CandidateList.SelectedIndex = candidates.Count > 0 ? 0 : -1;
        SubtitleText.Text = $"{organization} — confira a foto e a origem antes de aplicar ao perfil operacional.";
        CountText.Text = candidates.Count == 1 ? "1 opção" : $"{candidates.Count} opções";
    }

    public OrganizationImageCandidate? SelectedCandidate { get; private set; }

    private OrganizationImageCandidate? Current => CandidateList.SelectedItem as OrganizationImageCandidate;

    private void CandidateList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => UseCurrent();

    private void Use_Click(object sender, RoutedEventArgs e) => UseCurrent();

    private void UseCurrent()
    {
        SelectedCandidate = Current;
        if (SelectedCandidate is null)
        {
            SigfurDialog.Show(this, "Selecione uma imagem.", "Imagem da OM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void OpenSource_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { SourcePageUrl.Length: > 0 } candidate) return;
        try
        {
            Process.Start(new ProcessStartInfo(candidate.SourcePageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, "Não foi possível abrir a origem.\n\n" + ex.Message, "Imagem da OM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
