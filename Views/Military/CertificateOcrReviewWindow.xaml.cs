using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Military;

public partial class CertificateOcrReviewWindow : Window
{
    private static readonly (string Key, string Label)[] PrimaryFields =
    [
        ("NOME_FILHO", "Nome da criança"), ("CPF_FILHO", "CPF da criança"),
        ("DATA_NASCIMENTO", "Data de nascimento"), ("MATRICULA_CERTIDAO", "Matrícula da certidão"),
        ("DATA_CERTIDAO", "Data da certidão/registro"), ("FILIACAO_1", "Filiação 1"),
        ("FILIACAO_2", "Filiação 2"), ("SEXO_FILHO", "Sexo"),
        ("CARTORIO", "Cartório"), ("LOCAL_CERTIDAO", "Local da certidão"),
        ("ENDERECO_CARTORIO", "Endereço do cartório")
    ];

    private readonly Dictionary<string, string> _baseValues;
    private static readonly Dictionary<string, string[]> AliasGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NOME_FILHO"] = ["NOME_FILHO", "NOME_FILHA", "NOME_CRIANCA", "NOME_DEPENDENTE"],
        ["CPF_FILHO"] = ["CPF_FILHO", "CPF_FILHA", "CPF_CRIANCA", "CPF_DEPENDENTE"],
        ["DATA_NASCIMENTO"] = ["DATA_NASCIMENTO", "NASCIMENTO"],
        ["MATRICULA_CERTIDAO"] = ["MATRICULA_CERTIDAO", "MATRICULA"],
        ["DATA_CERTIDAO"] = ["DATA_CERTIDAO", "DATA_REGISTRO"],
        ["FILIACAO_1"] = ["FILIACAO_1", "PAI"],
        ["FILIACAO_2"] = ["FILIACAO_2", "MAE"],
        ["SEXO_FILHO"] = ["SEXO_FILHO", "TIPO_FILHO", "SEU_SUA_FILHO"]
    };
    public ObservableCollection<CertificateKeyRow> Rows { get; } = [];
    public Dictionary<string, string> Values
    {
        get
        {
            var values = new Dictionary<string, string>(_baseValues, StringComparer.OrdinalIgnoreCase);
            foreach (var row in Rows)
            {
                if (AliasGroups.TryGetValue(row.Key, out var aliases))
                    foreach (var alias in aliases) values.Remove(alias);
                if (string.IsNullOrWhiteSpace(row.Value)) values.Remove(row.Key);
                else values[row.Key] = row.Value.Trim();
            }
            return values;
        }
    }

    public CertificateOcrReviewWindow(CertificateOcrResult result)
    {
        InitializeComponent();
        _baseValues = new Dictionary<string, string>(result.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var field in PrimaryFields)
            Rows.Add(new CertificateKeyRow { Key = field.Key, Label = field.Label, Value = result.Keys.GetValueOrDefault(field.Key, string.Empty) });
        KeysGrid.ItemsSource = Rows;
        RawTextBox.Text = result.RawText;
        HeaderSubtitle.Text = $"Leitura feita por {result.Engine}. Os dados só serão salvos após sua confirmação.";
        ScoreText.Text = $"LEITURA {result.ConfidenceScore}%";
        WarningsText.Text = result.Warnings.Count == 0
            ? "Leitura concluída. Mesmo assim, faça uma conferência rápida dos campos abaixo."
            : string.Join("  •  ", result.Warnings);
        App.UiState.Attach(this);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        KeysGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        KeysGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (string.IsNullOrWhiteSpace(Values.GetValueOrDefault("NOME_FILHO")) &&
            SigfurDialog.Show(this, "O nome da criança está vazio. Deseja salvar as demais chaves mesmo assim?", "Conferir certidão", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        DialogResult = true;
    }
}
