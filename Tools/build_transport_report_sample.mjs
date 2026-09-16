import fs from "node:fs/promises";
import path from "node:path";
import { SpreadsheetFile, Workbook } from "file:///C:/Users/tatal/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/@oai/artifact-tool/dist/artifact_tool.mjs";

const inputPath = "C:/Users/tatal/Downloads/auxilio_transporte_JULHO.csv";
const outputDir = path.resolve("outputs/sat_maior_20260807");
const csvText = (await fs.readFile(inputPath, "utf8")).replace(/^\uFEFF/, "");
const sourceLines = csvText.split(/\r?\n/).filter(Boolean);
const sourceRows = sourceLines.slice(1).filter((line) => !line.startsWith("TOTAL LIQUIDO") && !/^;+$/u.test(line));
const parsed = sourceRows.map((line) => {
  const fields = line.split(";");
  const money = (value) => Number(String(value).replace("R$", "").trim().replace(/\./g, "").replace(",", ".")) || 0;
  return {
    rank: fields[0], name: fields[1], document: fields[2], receives: fields[3], range: fields[4],
    net: money(fields[5]), da: money(fields[6]),
  };
});

const rows = parsed.map((item, index) => ({
  ...item,
  vacation: index === 0 ? "Sim" : "Não",
  period: index === 0 ? "1º Período (10/07/2026 a 30/07/2026)" : "—",
  note: index === 0 ? "Pendente" : "Não se aplica",
  observation: index === 0
    ? "Férias na competência. SAT mensal integral como Despesa a Anular; falta enviar/salvar a nota complementar no SisBol."
    : (item.da > 0 ? "Despesa a Anular registrada para a competência." : "Sem Despesa a Anular na competência."),
}));

const workbook = Workbook.create();
const summary = workbook.worksheets.add("Resumo SAT");
const details = workbook.worksheets.add("Relação SAT");
summary.showGridLines = false;
details.showGridLines = false;

const navy = "#17365D";
const blue = "#1F4E78";
const lightBlue = "#D9EAF7";
const border = "#D5DFEA";
const muted = "#5B6573";

details.mergeCells("A1:K1");
details.mergeCells("A2:K2");
details.mergeCells("A3:K3");
details.getRange("A1").values = [["RELAÇÃO PARA CONFERÊNCIA — AUXÍLIO-TRANSPORTE"]];
details.getRange("A2").values = [["Competência 07/2026 • Controle da SAT Maior"]];
details.getRange("A3").values = [["Modelo profissional gerado a partir do relatório de julho • Férias e nota de DA auditadas"]];
details.getRange("A1:K2").format = { fill: navy, font: { color: "#FFFFFF", bold: true, size: 16 }, verticalAlignment: "center" };
details.getRange("A3:K3").format = { font: { color: muted, italic: true }, verticalAlignment: "center" };
details.getRange("A1:K1").format.rowHeight = 30;
details.getRange("A2:K2").format.rowHeight = 23;
details.getRange("A3:K3").format.rowHeight = 22;

const headers = [["P/G", "Nome completo", "PREC-CP", "Recebe AT", "Faixa de valor", "Valor líquido", "Férias", "Período de férias", "Nota DA do AT", "Despesa a anular", "Observação / fundamento"]];
details.getRange("A5:K5").values = headers;
const matrix = rows.map((r) => [r.rank, r.name, r.document, r.receives, r.range, r.net, r.vacation, r.period, r.note, r.da, r.observation]);
const firstData = 6;
const lastData = firstData + matrix.length - 1;
details.getRange(`A${firstData}:K${lastData}`).values = matrix;
details.getRange(`F${firstData}:F${lastData}`).format.numberFormat = '"R$" #,##0.00';
details.getRange(`J${firstData}:J${lastData}`).format.numberFormat = '"R$" #,##0.00';
details.getRange(`A5:K${lastData}`).format.borders = { preset: "all", style: "thin", color: border };
details.getRange(`B${firstData}:B${lastData}`).format.wrapText = true;
details.getRange(`H${firstData}:K${lastData}`).format.wrapText = true;
details.getRange(`A${firstData}:K${lastData}`).format.rowHeight = 36;
details.getRange(`D${firstData}:D${lastData}`).format.horizontalAlignment = "center";
details.getRange(`G${firstData}:I${lastData}`).format.horizontalAlignment = "center";
details.getRange(`F${firstData}:F${lastData}`).format.horizontalAlignment = "right";
details.getRange(`J${firstData}:J${lastData}`).format.horizontalAlignment = "right";
const table = details.tables.add(`A5:K${lastData}`, true, "RelacaoSatTable");
table.style = "TableStyleMedium2";
table.showFilterButton = true;
table.showBandedRows = true;
details.freezePanes.freezeRows(5);

details.getRange(`I${firstData}:I${lastData}`).conditionalFormats.add("containsText", {
  text: "Pendente", format: { fill: "#FCE4D6", font: { color: "#9C0006", bold: true } },
});
details.getRange(`I${firstData}:I${lastData}`).conditionalFormats.add("containsText", {
  text: "Gerada no SisBol", format: { fill: "#E2F0D9", font: { color: "#006100", bold: true } },
});
details.getRange(`G${firstData}:G${lastData}`).conditionalFormats.add("containsText", {
  text: "Sim", format: { fill: "#FFF2CC", font: { color: "#9C6500", bold: true } },
});

const totalRow = lastData + 1;
details.mergeCells(`A${totalRow}:E${totalRow}`);
details.getRange(`A${totalRow}`).values = [["TOTAIS"]];
details.getRange(`F${totalRow}`).formulas = [[`=SUM(F${firstData}:F${lastData})`]];
details.getRange(`J${totalRow}`).formulas = [[`=SUM(J${firstData}:J${lastData})`]];
details.getRange(`A${totalRow}:K${totalRow}`).format = { fill: navy, font: { color: "#FFFFFF", bold: true }, borders: { preset: "all", style: "thin", color: navy } };
details.getRange(`F${totalRow}`).format.numberFormat = '"R$" #,##0.00';
details.getRange(`J${totalRow}`).format.numberFormat = '"R$" #,##0.00';

const widths = [11, 42, 18, 13, 25, 17, 11, 32, 22, 19, 58];
for (let col = 0; col < widths.length; col++) {
  details.getRangeByIndexes(0, col, totalRow, 1).format.columnWidth = widths[col];
}

summary.mergeCells("A1:H1");
summary.mergeCells("A2:H2");
summary.mergeCells("A3:H3");
summary.getRange("A1").values = [["RELATÓRIO GERENCIAL DE AUXÍLIO-TRANSPORTE"]];
summary.getRange("A2").values = [["Competência 07/2026 • SAT Maior • Controle de férias e Despesa a Anular"]];
summary.getRange("A3").values = [["Painel executivo com conferência automática das pendências"]];
summary.getRange("A1:H2").format = { fill: navy, font: { color: "#FFFFFF", bold: true, size: 16 }, verticalAlignment: "center" };
summary.getRange("A3:H3").format = { font: { color: muted, italic: true } };
summary.getRange("A1:H1").format.rowHeight = 30;
summary.getRange("A2:H2").format.rowHeight = 23;

for (const range of ["A5:B5", "C5:D5", "E5:F5", "G5:H5", "A6:B6", "C6:D6", "E6:F6", "G6:H6", "A8:D8", "E8:H8", "A9:D9", "E9:H9"]) summary.mergeCells(range);
summary.getRange("A5").values = [["MILITARES"]];
summary.getRange("C5").values = [["RECEBEM AT"]];
summary.getRange("E5").values = [["EM FÉRIAS"]];
summary.getRange("G5").values = [["NOTAS PENDENTES"]];
summary.getRange("A6").formulas = [[`=COUNTA('Relação SAT'!B${firstData}:B${lastData})`]];
summary.getRange("C6").formulas = [[`=COUNTIF('Relação SAT'!D${firstData}:D${lastData},"Sim")`]];
summary.getRange("E6").formulas = [[`=COUNTIF('Relação SAT'!G${firstData}:G${lastData},"Sim")`]];
summary.getRange("G6").formulas = [[`=COUNTIF('Relação SAT'!I${firstData}:I${lastData},"Pendente")`]];
summary.getRange("A8").values = [["VALOR LÍQUIDO DO AT"]];
summary.getRange("E8").values = [["DESPESA A ANULAR"]];
summary.getRange("A9").formulas = [[`=SUM('Relação SAT'!F${firstData}:F${lastData})`]];
summary.getRange("E9").formulas = [[`=SUM('Relação SAT'!J${firstData}:J${lastData})`]];
summary.getRange("A9:H9").format.numberFormat = '"R$" #,##0.00';
summary.getRange("A5:H5").format = { fill: lightBlue, font: { color: navy, bold: true }, horizontalAlignment: "center", borders: { preset: "all", style: "thin", color: border } };
summary.getRange("A6:H6").format = { fill: lightBlue, font: { color: navy, bold: true, size: 16 }, horizontalAlignment: "center", borders: { preset: "all", style: "thin", color: border } };
summary.getRange("A8:H8").format = { fill: lightBlue, font: { color: navy, bold: true }, horizontalAlignment: "center", borders: { preset: "all", style: "thin", color: border } };
summary.getRange("A9:H9").format = { fill: lightBlue, font: { color: navy, bold: true, size: 16 }, horizontalAlignment: "center", borders: { preset: "all", style: "thin", color: border } };
summary.getRange("G6:H6").conditionalFormats.add("cellIs", { operator: "greaterThan", formula: 0, format: { fill: "#FCE4D6", font: { color: "#9C0006", bold: true } } });

summary.mergeCells("A11:H11");
summary.getRange("A11").values = [["PENDÊNCIAS PRIORITÁRIAS — NOTA DE DA DO AUXÍLIO-TRANSPORTE"]];
summary.getRange("A11:H11").format = { fill: blue, font: { color: "#FFFFFF", bold: true }, borders: { preset: "all", style: "thin", color: blue } };
summary.getRange("A12:E12").values = [["P/G", "Nome completo", "Período de férias", "Despesa a anular", "Situação da nota"]];
summary.getRange("A12:E12").format = { fill: blue, font: { color: "#FFFFFF", bold: true }, borders: { preset: "all", style: "thin", color: border }, horizontalAlignment: "center" };
const pendingRows = rows.filter((r) => r.note === "Pendente");
summary.getRange(`A13:E${12 + pendingRows.length}`).values = pendingRows.map((r) => [r.rank, r.name, r.period, r.da, r.note]);
summary.getRange(`A13:E${12 + pendingRows.length}`).format.borders = { preset: "all", style: "thin", color: border };
summary.getRange(`D13:D${12 + pendingRows.length}`).format.numberFormat = '"R$" #,##0.00';
summary.getRange(`E13:E${12 + pendingRows.length}`).conditionalFormats.add("containsText", { text: "Pendente", format: { fill: "#FCE4D6", font: { color: "#9C0006", bold: true } } });

const summaryWidths = [12, 38, 31, 20, 24, 5, 22, 5];
for (let col = 0; col < summaryWidths.length; col++) summary.getRangeByIndexes(0, col, 16, 1).format.columnWidth = summaryWidths[col];
summary.freezePanes.freezeRows(3);

await fs.mkdir(outputDir, { recursive: true });
const formulaInspect = await workbook.inspect({ kind: "formula", sheetId: "Resumo SAT", range: "A1:H15", maxChars: 5000, options: { maxResults: 30 } });
await fs.writeFile(path.join(outputDir, "formula_inspect.ndjson"), formulaInspect.ndjson ?? String(formulaInspect), "utf8");
const summaryPreview = await workbook.render({ sheetName: "Resumo SAT", autoCrop: "all", scale: 1, format: "png" });
await fs.writeFile(path.join(outputDir, "resumo_sat.png"), new Uint8Array(await summaryPreview.arrayBuffer()));
const detailsPreview = await workbook.render({ sheetName: "Relação SAT", range: `A1:K${totalRow}`, scale: 0.8, format: "png" });
await fs.writeFile(path.join(outputDir, "relacao_sat.png"), new Uint8Array(await detailsPreview.arrayBuffer()));
const output = await SpreadsheetFile.exportXlsx(workbook);
await output.save(path.join(outputDir, "SAT_Maior_Auxilio_Transporte_Julho_2026.xlsx"));

const errorInspect = await workbook.inspect({ kind: "match", searchTerm: "#REF!|#DIV/0!|#VALUE!|#NAME\\?|#N/A", options: { useRegex: true, maxResults: 100 }, summary: "final formula error scan" });
await fs.writeFile(path.join(outputDir, "error_scan.ndjson"), errorInspect.ndjson ?? String(errorInspect), "utf8");
console.log(JSON.stringify({ outputDir, rows: rows.length, total: rows.reduce((a, r) => a + r.net, 0), da: rows.reduce((a, r) => a + r.da, 0) }));
