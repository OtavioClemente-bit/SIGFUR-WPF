using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Licensed;

public partial class LicensedTransferredEditorWindow : Window
{
    private readonly LicensedTransferredRepository _repository;
    private readonly LicensedTransferredRecord _record;
    public int SavedId { get; private set; }

    public LicensedTransferredEditorWindow(LicensedTransferredRepository repository, LicensedTransferredRecord record)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _repository = repository;
        _record = record.Clone();
        RankBox.ItemsSource = MilitaryRankService.AllRanks;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        TitleText.Text = _record.Id != 0 ? "Editar licenciado / transferido" : "Novo licenciado / transferido";
        RankBox.Text = _record.Rank; NameBox.Text = _record.Name; WarNameBox.Text = _record.WarName;
        CpfBox.Text = _record.FormattedCpf; PrecBox.Text = _record.FormattedPrecCp; IdtBox.Text = _record.MilitaryId;
        YearBox.Text = _record.FormationYear; VisibleBox.SelectedIndex = _record.IsVisible ? 0 : 1;
        PhoneBox.Text = _record.Phone; EmailBox.Text = _record.Email; EducationBox.Text = _record.Education;
        ZipBox.Text = _record.ZipCode; AddressBox.Text = _record.Address; BirthBox.Text = _record.BirthDate; EnlistmentBox.Text = _record.EnlistmentDate;
        BankBox.Text = _record.Bank; AgencyBox.Text = _record.Agency; AccountBox.Text = _record.Account; PhotoBox.Text = _record.PhotoPath;
        ReasonBox.Text = _record.Reason; DestinationBox.Text = _record.Destination;
        SetCombo(PreSchoolBox, _record.ReceivesPreSchool); PreSchoolValueBox.Text = _record.PreSchoolValue;
        SetCombo(TransportBox, _record.ReceivesTransportAid); TransportValueBox.Text = _record.TransportAidValue;
        SetCombo(PnrBox, _record.HasPnr); SetCombo(AlimonyBox, _record.Alimony); AlimonyValueBox.Text = _record.AlimonyValue;
        NameBox.Focus();
    }

    private static void SetCombo(ComboBox box, string value)
    {
        var yes = MilitaryRecord.IsYes(value);
        box.SelectedIndex = yes ? 1 : 0;
    }

    private static string ComboText(ComboBox box)
        => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? box.Text;

    private void ChoosePhoto_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Selecionar foto", Filter = "Imagens|*.jpg;*.jpeg;*.png;*.bmp;*.webp|Todos os arquivos|*.*" };
        if (dialog.ShowDialog(this) == true) PhotoBox.Text = dialog.FileName;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = NameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) { StatusText.Text = "Informe o nome completo."; NameBox.Focus(); return; }
            _record.Rank = MilitaryRankService.Canonicalize(RankBox.Text);
            _record.Name = name; _record.WarName = WarNameBox.Text.Trim();
            _record.Cpf = MilitaryFormatting.Digits(CpfBox.Text); _record.PrecCp = PrecBox.Text.Trim(); _record.MilitaryId = IdtBox.Text.Trim();
            _record.FormationYear = YearBox.Text.Trim(); _record.IsVisible = VisibleBox.SelectedIndex != 1;
            _record.Phone = PhoneBox.Text.Trim(); _record.Email = EmailBox.Text.Trim(); _record.Education = EducationBox.Text.Trim();
            _record.ZipCode = MilitaryFormatting.Digits(ZipBox.Text); _record.Address = AddressBox.Text.Trim();
            _record.BirthDate = MilitaryFormatting.NormalizeDateText(BirthBox.Text); _record.EnlistmentDate = MilitaryFormatting.NormalizeDateText(EnlistmentBox.Text);
            _record.Bank = BankBox.Text.Trim(); _record.Agency = AgencyBox.Text.Trim(); _record.Account = AccountBox.Text.Trim(); _record.PhotoPath = PhotoBox.Text.Trim();
            _record.Reason = ReasonBox.Text.Trim(); _record.Destination = DestinationBox.Text.Trim();
            _record.ReceivesPreSchool = ComboText(PreSchoolBox); _record.PreSchoolValue = PreSchoolValueBox.Text.Trim();
            _record.ReceivesTransportAid = ComboText(TransportBox); _record.TransportAidValue = TransportValueBox.Text.Trim();
            _record.HasPnr = ComboText(PnrBox); _record.Alimony = ComboText(AlimonyBox); _record.AlimonyValue = AlimonyValueBox.Text.Trim();
            StatusText.Text = "Salvando...";
            SavedId = await _repository.SaveAsync(_record);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            SigfurDialog.Show(this, ex.Message, "SIGFUR — Cadastro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
