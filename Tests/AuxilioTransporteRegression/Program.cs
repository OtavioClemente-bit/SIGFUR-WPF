using System.Collections;
using System.Reflection;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

static void Check(bool condition, string scenario)
{
    if (!condition) throw new InvalidOperationException("Falha: " + scenario);
}

var assembly = typeof(MilitaryRecord).Assembly;
var windowType = assembly.GetType("SIGFUR.Wpf.ViewModels.AuxilioTransporteWindow", throwOnError: true)!;
var flags = BindingFlags.NonPublic | BindingFlags.Static;

var isUpdate = windowType.GetMethod("IsTransportUpdateAction", flags)
               ?? throw new MissingMethodException(windowType.FullName, "IsTransportUpdateAction");
Check((bool)isUpdate.Invoke(null, ["Auxílio-Transporte - Atualização de Valores"])!,
    "o modelo de atualização deve ser reconhecido");
Check(!(bool)isUpdate.Invoke(null, ["Auxílio-Transporte - Implantação"])!,
    "implantação não pode cair na regra de atualização");
var actionField = windowType.GetField("BulletinActions", flags)
                  ?? throw new MissingFieldException(windowType.FullName, "BulletinActions");
var actions = ((IEnumerable)actionField.GetValue(null)!).Cast<object>().Select(value => value?.ToString()).ToList();
Check(actions.Count(value => string.Equals(value, "Auxílio-Transporte - Atualização de Valores", StringComparison.Ordinal)) == 1,
    "a atualização de valores deve aparecer uma única vez na lista de modelos");

var calculationType = assembly.GetType("SIGFUR.Wpf.ViewModels.AtCalculation", throwOnError: true)!;
var calculation = Activator.CreateInstance(calculationType, [22, 5000m, 12.50m, 275m, 220m, 55m])
                  ?? throw new InvalidOperationException("Não foi possível criar o cálculo de teste.");
var buildLines = windowType.GetMethod("BuildRequiredTransportLines", flags)
                 ?? throw new MissingMethodException(windowType.FullName, "BuildRequiredTransportLines");
var result = buildLines.Invoke(null,
    ["Auxílio-Transporte - Atualização de Valores", calculation, "08/2026", 0m]) as IEnumerable
             ?? throw new InvalidOperationException("A atualização não retornou as linhas obrigatórias.");
var lines = result.Cast<object>().Select(value => value?.ToString() ?? string.Empty).ToList();

Check(lines.Count == 4, "atualização individual deve gerar exatamente valor diário, dias, mês e ano");
Check(lines[0] == "Valor diário: R$ 12,50", "valor diário da atualização");
Check(lines[1] == "Quantidade de dias: 22", "quantidade de dias da atualização");
Check(lines[2] == "Mês de referência: AGOSTO", "mês da atualização");
Check(lines[3] == "Ano de referência: 2026", "ano da atualização");
Check(!lines.Any(line => line.Contains("R$ 275,00", StringComparison.Ordinal)
                         || line.Contains("R$ 220,00", StringComparison.Ordinal)
                         || line.Contains("R$ 55,00", StringComparison.Ordinal)),
    "o modelo não deve misturar bruto mensal, cota-parte ou líquido com os campos do SIPPES");

var safeAction = windowType.GetMethod("RunModuleActionAsync", BindingFlags.NonPublic | BindingFlags.Instance);
Check(safeAction is not null, "ações assíncronas do módulo devem possuir contenção local de exceções");

var contextMenuType = typeof(GlobalContextMenuService);
var spellCheckField = contextMenuType.GetField("NativeSpellCheckAvailable", BindingFlags.NonPublic | BindingFlags.Static);
Check(spellCheckField?.GetValue(null) is true,
    "a publicação completa deve pré-carregar a dependência do corretor nativo antes de habilitá-lo");

Console.WriteLine("Auxílio-Transporte: 11 verificações de regressão aprovadas.");
