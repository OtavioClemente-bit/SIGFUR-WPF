import fs from "node:fs/promises";
import { FileBlob, SpreadsheetFile } from "file:///C:/Users/tatal/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/@oai/artifact-tool/dist/artifact_tool.mjs";

const base = "C:/Users/tatal/OneDrive/Área de Trabalho/SIGFUR_WPF/src/SIGFUR.Wpf/outputs/sat_maior_20260807";
const workbook = await SpreadsheetFile.importXlsx(await FileBlob.load(`${base}/production_service_test.xlsx`));
const scan = await workbook.inspect({ kind: "match", searchTerm: "#REF!|#DIV/0!|#VALUE!|#NAME\\?|#N/A", options: { useRegex: true, maxResults: 100 }, summary: "production workbook error scan" });
await fs.writeFile(`${base}/production_error_scan.ndjson`, scan.ndjson ?? String(scan), "utf8");
for (const sheetName of ["Resumo SAT", "Relação SAT"]) {
  const preview = await workbook.render({ sheetName, autoCrop: "all", scale: 0.8, format: "png" });
  await fs.writeFile(`${base}/production_${sheetName === "Resumo SAT" ? "resumo" : "relacao"}.png`, new Uint8Array(await preview.arrayBuffer()));
}
console.log(scan.ndjson ?? String(scan));
