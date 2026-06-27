using System.Windows;
using System.Windows.Controls;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Military;

namespace SIGFUR.Wpf.Views.Bulletin;

public partial class BulletinTemplateEditorWindow : Window
{
    private readonly BulletinTemplate _bulletinTemplate;

    public BulletinTemplateEditorWindow(BulletinTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        _bulletinTemplate = template;
        InitializeComponent();
        NameBox.Text = template.Name;
        TemplateTextBox.Text = template.Text;
        App.UiState.Attach(this);
    }

    private void InsertAtCaret(string text)
    {
        var index = TemplateTextBox.CaretIndex;
        TemplateTextBox.Text = TemplateTextBox.Text.Insert(index, text);
        TemplateTextBox.CaretIndex = index + text.Length;
        TemplateTextBox.Focus();
    }

    private string? Ask(string title, string prompt)
    {
        var dialog = new TextPromptWindow(title, prompt) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Value?.Trim() : null;
    }

    private static string KeyName(string value) => value.Trim().ToUpperInvariant().Replace(' ', '_');

    private void InsertAutoKey_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        AddSection(menu, "MILITAR", ["POSTO_ABREV", "NOME", "NOME_GUERRA", "CPF", "PREC_CP", "IDT", "EMAIL", "TELEFONE", "ESCOLARIDADE", "ENDERECO", "CEP", "BANCO", "AGENCIA", "CONTA", "TEMPO_SERVICO", "MILITAR_FORMATADO"]);
        AddSection(menu, "DATA E MÊS AUTOMÁTICOS", ["DATA_HOJE", "DATA_ATUAL", "DATA_HOJE_BR", "DATA_HOJE_ABREV", "DATA_HOJE_EXTENSO", "DIA_ATUAL", "MES_ATUAL", "MES_ATUAL_NOME", "MES_ATUAL_ABREV", "MES_ATUAL_NOME_ANO", "MES_ATUAL_ABREV_ANO", "MES_ATUAL_NUMERO", "ANO_ATUAL"]);
        AddSection(menu, "CERTIDÃO DE NASCIMENTO", ["NOME_FILHO", "CPF_FILHO", "DATA_NASCIMENTO", "MATRICULA_CERTIDAO", "DATA_CERTIDAO", "FILIACAO_1", "FILIACAO_2", "PAI", "MAE", "SEXO_FILHO", "TIPO_FILHO", "SEU_SUA_FILHO", "CARTORIO", "LOCAL_CERTIDAO", "ENDERECO_CARTORIO", "MES_IMPLANTACAO", "MES_TERMINO_PRE_ESCOLAR"]);
        AddSection(menu, "BI / ADT", ["BI_REFERENCIA", "NUM_BI", "DATA_PUBLICACAO_BI", "DATA_PUBLICACAO_BI_ABREV", "ADT_REFERENCIA", "NUM_ADT", "DATA_ADT"]);
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private void AddSection(ContextMenu menu, string title, IEnumerable<string> keys)
    {
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = title, IsEnabled = false, FontWeight = FontWeights.Bold });
        foreach (var key in keys)
        {
            var (description, example) = AutomaticKeyHelp(key);
            var item = new MenuItem
            {
                Header = key,
                ToolTip = $"{description}\n\nMarcador: [[{key}]]\nExemplo de resultado: {example}"
            };
            ToolTipService.SetInitialShowDelay(item, 180);
            ToolTipService.SetShowDuration(item, 30000);
            item.Click += (_, _) => InsertAtCaret($"[[{key}]]");
            menu.Items.Add(item);
        }
    }

    private static (string Description, string Example) AutomaticKeyHelp(string key)
    {
        var today = DateTime.Today;
        var dateBr = today.ToString("dd/MM/yyyy");
        var monthNames = new[] { "janeiro", "fevereiro", "março", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro" };
        var monthAbbreviations = new[] { "JAN", "FEV", "MAR", "ABR", "MAI", "JUN", "JUL", "AGO", "SET", "OUT", "NOV", "DEZ" };
        var monthName = monthNames[today.Month - 1];
        var monthAbbreviation = monthAbbreviations[today.Month - 1];
        return key switch
        {
            "POSTO_ABREV" => ("Posto ou graduação abreviado do militar selecionado.", "3º Sgt"),
            "NOME" => ("Nome completo do militar selecionado.", "OTÁVIO CLEMENTE DA SILVA"),
            "NOME_GUERRA" => ("Nome de guerra do militar selecionado.", "CLEMENTE"),
            "CPF" => ("CPF formatado do militar selecionado.", "123.456.789-00"),
            "PREC_CP" => ("Prec-CP cadastrado do militar.", "123456789"),
            "IDT" => ("Número de identidade militar.", "0123456789"),
            "EMAIL" => ("E-mail salvo no cadastro/carteira.", "militar@exemplo.com"),
            "TELEFONE" => ("Telefone salvo no cadastro/carteira.", "(31) 99999-0000"),
            "ESCOLARIDADE" => ("Escolaridade registrada para o militar.", "Ensino superior completo"),
            "ENDERECO" => ("Endereço completo cadastrado.", "Rua Exemplo, 100 — Belo Horizonte/MG"),
            "CEP" => ("CEP cadastrado.", "30100-000"),
            "BANCO" => ("Banco cadastrado para pagamento.", "Banco do Brasil"),
            "AGENCIA" => ("Agência bancária cadastrada.", "1234-5"),
            "CONTA" => ("Conta bancária cadastrada.", "12345-6"),
            "TEMPO_SERVICO" => ("Tempo de serviço calculado pelo cadastro e pelos intervalos registrados.", "7a, 02m e 14d"),
            "MILITAR_FORMATADO" => ("Posto/graduação e nome completo prontos para texto de boletim.", "3º Sgt OTÁVIO CLEMENTE DA SILVA"),
            "DATA_HOJE" or "DATA_ATUAL" or "DATA_HOJE_BR" => ("Data atual no formato brasileiro.", dateBr),
            "DATA_HOJE_ABREV" => ("Data atual no padrão militar abreviado.", $"{today:dd} {monthAbbreviation} {today:yy}"),
            "DATA_HOJE_EXTENSO" => ("Data atual escrita por extenso.", $"{today.Day:00} de {monthName} de {today:yyyy}"),
            "DIA_ATUAL" => ("Dia atual com dois algarismos.", today.ToString("dd")),
            "MES_ATUAL" or "MES_ATUAL_NUMERO" => ("Mês e ano atuais em números.", today.ToString("MM/yyyy")),
            "MES_ATUAL_NOME" => ("Nome completo do mês atual.", char.ToUpper(monthName[0]) + monthName[1..]),
            "MES_ATUAL_ABREV" => ("Mês atual abreviado no padrão militar.", monthAbbreviation),
            "MES_ATUAL_NOME_ANO" => ("Nome do mês atual acompanhado do ano.", $"{char.ToUpper(monthName[0]) + monthName[1..]} {today:yyyy}"),
            "MES_ATUAL_ABREV_ANO" => ("Mês abreviado acompanhado do ano.", $"{monthAbbreviation} {today:yyyy}"),
            "ANO_ATUAL" => ("Ano atual com quatro algarismos.", today.ToString("yyyy")),
            "NOME_FILHO" => ("Nome do dependente lido e conferido na certidão de nascimento.", "HELENA CLEMENTE DA SILVA"),
            "CPF_FILHO" => ("CPF do dependente identificado na certidão.", "123.456.789-00"),
            "DATA_NASCIMENTO" => ("Data de nascimento identificada na certidão.", "15/03/2023"),
            "MATRICULA_CERTIDAO" => ("Matrícula da certidão de nascimento.", "000000 01 55 2023 1 00001 001 0000000 00"),
            "DATA_CERTIDAO" => ("Data de emissão/registro identificada na certidão.", "17/03/2023"),
            "FILIACAO_1" or "PAI" => ("Primeira filiação identificada na certidão.", "OTÁVIO CLEMENTE DA SILVA"),
            "FILIACAO_2" or "MAE" => ("Segunda filiação identificada na certidão.", "MARIA DE EXEMPLO"),
            "SEXO_FILHO" => ("Sexo identificado na certidão.", "Feminino"),
            "TIPO_FILHO" => ("Forma adequada para filho ou filha.", "filha"),
            "SEU_SUA_FILHO" => ("Pronome adequado conforme o sexo do dependente.", "sua filha"),
            "CARTORIO" => ("Nome do cartório identificado na certidão.", "Cartório de Registro Civil"),
            "LOCAL_CERTIDAO" => ("Município/UF do registro.", "Belo Horizonte/MG"),
            "ENDERECO_CARTORIO" => ("Endereço do cartório, quando encontrado.", "Rua Exemplo, 50 — Centro"),
            "MES_IMPLANTACAO" => ("Competência sugerida para implantação do benefício.", "ABR 2026"),
            "MES_TERMINO_PRE_ESCOLAR" => ("Competência calculada para término do auxílio pré-escolar.", "MAR 2029"),
            "BI_REFERENCIA" => ("Referência completa do BI escolhido nas chaves do SIGFUR.", "BI Nr 81, de 04 ABR 26, da 4ª Cia PE"),
            "NUM_BI" => ("Número do Boletim Interno escolhido.", "81"),
            "DATA_PUBLICACAO_BI" => ("Data do BI escolhido em formato brasileiro.", "04/04/2026"),
            "DATA_PUBLICACAO_BI_ABREV" => ("Data do BI escolhido no padrão militar abreviado.", "04 ABR 26"),
            "ADT_REFERENCIA" => ("Referência completa do Aditamento do Furriel escolhido.", "Adt Furr Nr 15, de 06 ABR 26, da 4ª Cia PE"),
            "NUM_ADT" => ("Número do Aditamento do Furriel escolhido.", "15"),
            "DATA_ADT" => ("Data do Aditamento do Furriel escolhido.", "06/04/2026"),
            _ => ("Chave automática preenchida pelo SIGFUR quando os dados estiverem disponíveis.", "preenchimento automático")
        };
    }

    private void InsertTextField_Click(object sender, RoutedEventArgs e)
    {
        var key = Ask("Campo de texto", "Nome da chave (ex.: NUMERO_PROCESSO):");
        if (!string.IsNullOrWhiteSpace(key)) InsertAtCaret($"[[{KeyName(key)}]]");
    }

    private void InsertListField_Click(object sender, RoutedEventArgs e)
    {
        var key = Ask("Campo de lista", "Nome da chave:");
        if (string.IsNullOrWhiteSpace(key)) return;
        var options = Ask("Campo de lista", "Opções separadas por |:");
        if (!string.IsNullOrWhiteSpace(options)) InsertAtCaret($"[[{KeyName(key)}:LISTA={options}]]");
    }

    private void InsertDateField_Click(object sender, RoutedEventArgs e)
    {
        var key = Ask("Campo de data", "Nome da chave (ex.: DATA_DOCUMENTO):");
        if (string.IsNullOrWhiteSpace(key)) return;
        ShowFormatMenu(sender, KeyName(key), "DATA",
        [
            ("22/04/2026", "BR"),
            ("22 ABR 26", "ABREV"),
            ("22 de abril de 2026", "EXTENSO"),
            ("2026-04-22", "ISO")
        ]);
    }

    private void InsertMonthField_Click(object sender, RoutedEventArgs e)
    {
        var key = Ask("Campo de mês", "Nome da chave (ex.: MES_REFERENCIA):");
        if (string.IsNullOrWhiteSpace(key)) return;
        ShowFormatMenu(sender, KeyName(key), "MES",
        [
            ("Abril", "MES"), ("ABR", "ABREV"), ("Abril 2026", "MES_ANO"),
            ("ABR 2026", "ABREV_ANO"), ("04/2026", "NUMERO"),
            ("2026", "ANO"), ("30 dias", "DIAS"),
            ("30 dias do mês de abril", "DIAS_MES"), ("30 dias do mês de ABR", "DIAS_ABREV"),
            ("30 dias do mês de abril de 2026", "DIAS_MES_ANO"),
            ("30 dias do mês de ABR 2026", "DIAS_ABREV_ANO")
        ]);
    }

    private void InsertMoneyField_Click(object sender, RoutedEventArgs e)
    {
        var key = Ask("Campo de valor", "Nome da chave (ex.: VALOR_TOTAL):");
        if (string.IsNullOrWhiteSpace(key)) return;
        ShowFormatMenu(sender, KeyName(key), "VALOR",
        [
            ("R$ 1.234,56", "NUMERO"),
            ("mil duzentos e trinta e quatro reais...", "EXTENSO"),
            ("R$ 1.234,56 (mil duzentos...)", "AMBOS"),
            ("EXTENSO EM MAIÚSCULO", "EXTENSO_MAIUSCULO"),
            ("R$ 1.234,56 (EXTENSO EM MAIÚSCULO)", "AMBOS_MAIUSCULO")
        ]);
    }

    private void ShowFormatMenu(object sender, string key, string type, IEnumerable<(string Label, string Format)> formats)
    {
        var menu = new ContextMenu();
        foreach (var format in formats)
        {
            var item = new MenuItem { Header = format.Label };
            item.Click += (_, _) => InsertAtCaret($"[[{key}:{type}={format.Format}]]");
            menu.Items.Add(item);
        }
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private void InsertItemBlock_Click(object sender, RoutedEventArgs e)
        => InsertAtCaret("[[ITEM]]\n[[POSTO_ABREV]] [[NOME]]\nPrec-CP [[PREC_CP]] CPF [[CPF]]\n[[/ITEM]]");

    private void Undo_Click(object sender, RoutedEventArgs e) { if (TemplateTextBox.CanUndo) TemplateTextBox.Undo(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { if (TemplateTextBox.CanRedo) TemplateTextBox.Redo(); }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            SigfurDialog.Show(this, "Informe o nome do modelo.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _bulletinTemplate.Name = BulletinService.NormalizeTemplateName(NameBox.Text);
        _bulletinTemplate.Text = TemplateTextBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
