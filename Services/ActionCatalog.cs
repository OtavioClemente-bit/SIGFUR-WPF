using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public static class ActionCatalog
{
    public static List<ActionDefinition> Create(Dictionary<string, string>? hotkeys = null)
    {
        hotkeys ??= SettingsService.DefaultHotkeys();
        string H(string id) => hotkeys.TryGetValue(id, out var value) ? value : string.Empty;
        return
        [
            A("cadastro", "Cadastrar Militar", "🪖", "Cadastrar um novo militar", "Cadastros e Boletins", H("cadastro"), true),
            A("listar", "Listar Militares", "📋", "Listagem, carteira, edição, documentos, AT e contracheques salvos", "Cadastros e Boletins", H("listar"), true),
            A("plano_chamada", "Plano de Chamada", "📞", "Banco próprio importado da planilha; compara endereço e completa telefones ausentes", "Cadastros e Boletins", native: true),
            A("boletim", "Boletim", "🗂️", "Gerar, editar e enviar boletins ao SisBol", "Cadastros e Boletins", H("boletim"), true),
            A("boletim_resumo", "Resumo de Boletins", "🧠", "Ler PDFs, pesquisar militares e conferir achados", "Cadastros e Boletins", native: true),
            A("boletim_furriel", "Boletim Furriel", "📘", "Importar, indexar e pesquisar menções em aditamentos do furriel", "Cadastros e Boletins", native: true),
            A("boletins_externos", "Boletins de Fora", "🌐", "Pesquisar OM ou nome em Boletins Regionais e aditamentos de pagamento do CML", "Cadastros e Boletins", native: true),

            A("soldos", "Soldos", "💰", "Editar soldos e consultar a tabela oficial vigente", "Financeiro", H("soldos"), true),
            A("aux_transporte", "Auxílio-Transporte", "🚌", "Gerenciar auxílio-transporte", "Financeiro", H("aux_transporte")),
            A("grat_representacao", "Gratificação de Representação", "🎖️", "Cálculo de 2%, boletim, SisBol, planilha, DIEx e mapa", "Financeiro", H("grat_representacao"), true),
            A("ajuste_contas", "Ajuste de Contas", "🧾", "Cálculo, rubricas SIPPES, IR e boletim", "Financeiro", H("ajuste_contas"), true),
            A("pensao_judicial", "Pensão Judicial", "⚖️", "Cálculo, IRRF 2026, documento, boletim e histórico", "Financeiro", native: true),
            A("inconsistencia_bancaria", "Inconsistência Bancária", "🏦", "CPEX e lançamentos criticados do SINFOPPES", "Financeiro", H("inconsistencia_bancaria"), true),
            A("bizurometro_sped", "Bizurômetro SPED", "🧭", "Guia 4ª Cia PE: DIEx, anexos e despacho", "Financeiro", H("bizurometro_sped"), true),
            A("conferencia_pagamento", "Conferência de Pagamento", "✅", "Cruzar Aditamento do Furriel com contracheques do mês e conferir rubricas N/A/D", "Financeiro", native: true),

            A("lic_transf", "Licenciados/Transferidos", "🔁", "Carteiras, documentos e contracheques CPEX/SIPPES", "Gestão de Efetivo", H("lic_transf"), true),
            A("plano_ferias", "Plano de Férias", "🏖️", "Planejamento, períodos, financeiro, boletins e relatórios", "Gestão de Efetivo", H("plano_ferias"), true),
            A("exercicio_anterior", "Exercício Anterior (EA)", "📦", "IPCA-E, XLSM, documentos e CPEX Online", "Gestão de Efetivo", H("exercicio_anterior"), true),
            A("escala_sgt_dia", "Escala Sgt Dia", "🗓️", "Escala mensal, descanso de 48h, marcações e distribuição equilibrada", "Gestão de Efetivo", H("escala_sgt_dia"), true),

            A("phpm", "PHPM — Documentos", "📄", "Templates, preenchimento automático, lote e histórico de geração", "Documentos e Relatórios", H("phpm"), true),
            A("medidas_tomadas", "Medidas Tomadas", "📄", "Exame de Pagamento, Contracheque e providências", "Documentos e Relatórios", native: true),
            A("relacao_pessoal", "Relação Pessoal", "📑", "Organizar e gerar relação pessoal em Excel", "Documentos e Relatórios", H("relacao_pessoal"), true),
            A("legislacao", "Legislação", "📚", "Biblioteca offline com índice por página e resposta técnica com fontes", "Documentos e Relatórios", H("legislacao"), true),
            A("ferramentas_pdf", "Ferramentas PDF", "🧰", "Converter, comprimir, juntar e proteger PDFs", "Documentos e Relatórios", H("ferramentas_pdf"), true),
            A("fila_impressao", "Fila de Impressão", "🖨️", "Imprimir vários arquivos em lote", "Documentos e Relatórios", H("fila_impressao"), true),

            A("assistente", "Assistente SIGFUR", "✦", "Chat com API, dados locais, DIEx, anexos e impressão confirmada", "Produtividade", native: true),
            A("num_extenso", "Número por Extenso", "🔤", "Converter números em texto por extenso", "Produtividade", H("num_extenso"), true),
            A("faltas_atrasos", "Faltas & Atrasos", "⏱️", "Ocorrências, justificativas, medidas e relatório mensal", "Produtividade", H("faltas_atrasos"), true),
            A("consulta_rapida", "Consulta rápida (Carteira)", "🪪", "Buscar militar e abrir a carteira", "Produtividade", native: true),
            A("calculadora", "Calculadora", "🧮", "Abrir a calculadora do Windows", "Produtividade", H("calculadora"), true),

            A("abrir_dados", "Abrir pasta de dados", "📂", "Abrir AppData Local\\SIGFUR", "Sistema", H("abrir_dados"), true),
            A("gerenciar_atalhos", "Gerenciar Atalhos", "⌨️", "Editar atalhos do teclado", "Sistema", H("gerenciar_atalhos"), true),
            A("sair", "Sair", "⛔", "Fechar o SIGFUR", "Sistema", H("sair"), true)
        ];
    }

    private static ActionDefinition A(string id, string title, string icon, string description, string category, string hotkey = "", bool native = false)
        => new() { Id = id, Title = title, Icon = icon, Description = description, Category = category, HotKey = hotkey, IsNative = native };
}
