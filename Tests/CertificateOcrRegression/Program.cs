using System.Reflection;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

static void Check(bool condition, string scenario)
{
    if (!condition) throw new InvalidOperationException("Falha: " + scenario);
}

static CertificateOcrResult Parse(string text)
{
    var method = typeof(CertificateOcrService).GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)
                 ?? throw new MissingMethodException(typeof(CertificateOcrService).FullName, "Parse");
    return (CertificateOcrResult)(method.Invoke(null, [text, new MilitaryRecord()])
                                  ?? throw new InvalidOperationException("O parser não retornou resultado."));
}

var beloHorizonte = Parse("""
[IDENTIFICACAO_OCR_AMPLIADA]
Nome
ISAAC LUCAS MIRANDA DE AZEVEDO
Número do CPF
109.145.286-53
Matrícula
033118 01 55 2026 1 01311 102 0610918 32
[/IDENTIFICACAO_OCR_AMPLIADA]
[NASCIMENTO_OCR_AMPLIADO]
Vince e nove de agosto de dois mil e vinte e seis
Dia
29
Mês
08
Horário de nascimento
06h05min
Município de naturalidade
Belo Horizonte
Nome do(a) genitor(a)
PEDRO ARTUR RIBEIRO DE AZEVEDO
Município de nascimento
Belo Horizonte
2026
MG
[/NASCIMENTO_OCR_AMPLIADO]
[NASCIMENTO_FILIACAO_TRANSICAO_OCR]
Hospital Maternidade Sofia Feldman
I Masculino
[/NASCIMENTO_FILIACAO_TRANSICAO_OCR]
[GENITOR_SUPERIOR_VALOR_OCR]
belo nonzom
Município de nascimento
[/GENITOR_SUPERIOR_VALOR_OCR]
[GENITOR_INTERMEDIARIO_VALOR_OCR]
PÉBRO AKTUR RIBEIRO DE AZEVEDO
[/GENITOR_INTERMEDIARIO_VALOR_OCR]
[GENITOR_INFERIOR_VALOR_OCR]
LUIZA STEPHANIE RODRIGUES MIRANDA
[/GENITOR_INFERIOR_VALOR_OCR]
[DATA_REGISTRO_OCR_AMPLIADA]
Trina é um de agosto de dois mil e vinte e seis
[/DATA_REGISTRO_OCR_AMPLIADA]
[DNV_OCR_AMPLIADA]
DNV
30-99830073-1
[/DNV_OCR_AMPLIADA]
[CARTORIO_OCR_AMPLIADO]
CNS 033118
Ofício de Registro Civil de Pessoas Naturais
Belo Horizonte - MG
Nome da Oficial
José Augusto Silveira
Rua Aquiles Lobo, 535, A/B, Floresta
[/CARTORIO_OCR_AMPLIADO]
""");

Check(beloHorizonte.Keys["DATA_NASCIMENTO"] == "29/08/2026", "data de nascimento por extenso com ruído");
Check(beloHorizonte.Keys["DATA_CERTIDAO"] == "31/08/2026", "data de registro por extenso com ruído");
Check(beloHorizonte.Keys["FILIACAO_1"] == "PEDRO ARTUR RIBEIRO DE AZEVEDO", "primeiro genitor do layout de Belo Horizonte");
Check(beloHorizonte.Keys["FILIACAO_2"] == "LUIZA STEPHANIE RODRIGUES MIRANDA", "segundo genitor sem confundir município");
Check(beloHorizonte.Keys["SEXO_FILHO"] == "masculino", "sexo no recorte de transição");
Check(beloHorizonte.Keys["LOCAL_CERTIDAO"] == "Belo Horizonte - MG", "local da certidão de Belo Horizonte");

var betim = Parse("""
[IDENTIFICACAO_OCR_AMPLIADA]
Nomo
LIAM MIGUEL LARES DE SOUZA
Número do CPF
106.769.296-72
Matricula
055731 01 55 2026 1 00679 089 0268730 58
Data do nosclmonto
novo do agosto do dols mll o vinto o sols
Horário do nascimento
Município da naturalidade
Dia
09
MG
MOS
08
Saxo
masculino
2026
UF
MG
21:37 horas
Contagem
Local do nascimento
HOSPITAL PUBLICO REGIONAL DE
Município do nascimento
Betim
[/IDENTIFICACAO_OCR_AMPLIADA]
[NASCIMENTO_OCR_AMPLIADO]
Local do nascimento
HOSPITAL PUBLICO REGIONAL DE
BETIM OSVALDO REZENDE FRANCO
Avenida Edmela Mattos Lazzarotti,
3800 - Betim MG
Município do nascimento
Botim
MG
Saxo
masculino
[/NASCIMENTO_OCR_AMPLIADO]
[GENITOR_SUPERIOR_VALOR_OCR]
Nome do(a) gonitot(a)
BRENO HENRIQUE GOMES DE SOUZA
[/GENITOR_SUPERIOR_VALOR_OCR]
[GENITOR_INTERMEDIARIO_VALOR_OCR]
Nomo do(o) gonltor(n)
EVELYN OLIVEIRA LARES
[/GENITOR_INTERMEDIARIO_VALOR_OCR]
[GENITOR_INFERIOR_VALOR_OCR]
Data do registro
doz do agosto do dois mil o vinte o sols
[/GENITOR_INFERIOR_VALOR_OCR]
[DATA_REGISTRO_OCR_AMPLIADA]
assinou eletronicamente esta certidão em data 10/08/2026
[/DATA_REGISTRO_OCR_AMPLIADA]
[DNV_OCR_AMPLIADA]
DNV 30-97756664-3
[/DNV_OCR_AMPLIADA]
[CARTORIO_OCR_AMPLIADO]
CNS NO 055731
Oficial de Rogistro Civil das Pessoas Naturais
Botim - MG
Maria Assis Pinho Resende - Oficial
Avenida Presidente Kubitschek, no 315 - Centro CEP:
32600226 - Fone: (31) 35110826
[/CARTORIO_OCR_AMPLIADO]
""");

Check(betim.Keys["DATA_NASCIMENTO"] == "09/08/2026", "nascimento não pode receber a data do registro");
Check(betim.Keys["DATA_CERTIDAO"] == "10/08/2026", "registro do layout de Betim");
Check(betim.Keys["FILIACAO_1"] == "BRENO HENRIQUE GOMES DE SOUZA", "primeiro genitor do layout de Betim");
Check(betim.Keys["FILIACAO_2"] == "EVELYN OLIVEIRA LARES", "data por extenso não pode virar genitor");
Check(betim.Keys["MUNICIPIO_NATURALIDADE"] == "Contagem", "naturalidade após as caixas de dia/mês/ano");
Check(betim.Keys["MUNICIPIO_NASCIMENTO"] == "Betim", "município de nascimento com rótulo ruidoso");
Check(betim.Keys["LOCAL_CERTIDAO"] == "Betim - MG", "correção de Botim usando o município confirmado");
Check(betim.Keys["NOME_OFICIAL_CARTORIO"] == "Maria Assis Pinho Resende", "oficial informado na mesma linha");
Check(betim.Keys["ENDERECO_CARTORIO"].Contains("CEP: 32600-226", StringComparison.Ordinal), "CEP quebrado na linha seguinte");

Console.WriteLine("OCR de certidão: 15 verificações de regressão aprovadas.");
