#!/usr/bin/env python3
"""Validação estrutural e de integridade do porte EA Python -> C# WPF.

Não substitui uma compilação do .NET nem os testes que dependem de Windows,
Microsoft Excel instalado e acesso à intranet do CPEx. O objetivo é detectar
regressões de estrutura, persistência, recursos e cobertura funcional antes da
compilação final.
"""
from __future__ import annotations

import json
import re
import sqlite3
import sys
import tempfile
import zipfile
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
RESULT_PATH = ROOT / "VALIDACAO_EA_CSHARP_5.0.23.json"

EXPECTED_PROCESS_COLUMNS = [
    "militar_id", "posto_grad", "nome_completo", "nome_guerra", "prec_cp", "idt", "cpf",
    "protocolo_geral", "secao", "assunto_num", "assunto_texto", "anexos_folhas", "destinatario",
    "objeto", "telefone", "motivo_pagamento", "eb_requerimento", "eb_info", "referente_a",
    "valor_requerido", "od_epoca_nome", "od_epoca_idt", "od_epoca_cpf", "cmt_companhia",
    "representante_nome", "representante_cpf", "representante_idt", "data_nascimento",
    "ea_indicativo", "tipo_exercicio_anterior", "possui_pensao_judiciaria",
    "pesquisa_ficha_cadastro", "pesquisa_ficha_financeira", "pesquisa_levantamento_siafi",
    "documento_remessa", "cpex_protocolo", "cpex_pagina_impressao", "cpex_protocolado_em",
    "cpex_status", "cpex_obs", "pago", "pago_em", "pago_obs", "situacao", "banco", "agencia",
    "conta", "num_processo", "ano_processo", "data_solicitacao_extenso", "om_nome", "rm", "ug",
    "codom", "od_nome_posto", "od_funcao", "chefe_pessoal_nome_posto", "chefe_pessoal_funcao",
    "fiscal_adm_nome_posto", "fiscal_adm_funcao", "cidade_estado", "data_requerimento", "bi_numero",
    "bi_data", "especie_divida", "periodo_inicio", "periodo_fim", "atualizado_ate",
    "doc_materializou", "boletim_averbou", "explicacao_nao_pagamento",
]

EXPECTED_PRESET_FIELDS = [
    "om_nome", "ug", "codom", "od_nome_posto", "od_funcao", "chefe_pessoal_nome_posto",
    "chefe_pessoal_funcao", "fiscal_adm_nome_posto", "fiscal_adm_funcao", "cidade_estado",
    "situacao", "protocolo_geral", "secao", "assunto_texto", "destinatario", "objeto",
    "referente_a", "od_epoca_nome", "od_epoca_idt", "od_epoca_cpf", "cmt_companhia",
    "ea_indicativo", "tipo_exercicio_anterior", "possui_pensao_judiciaria",
    "pesquisa_ficha_cadastro", "pesquisa_ficha_financeira", "pesquisa_levantamento_siafi",
    "documento_remessa",
]

EXPECTED_CPEX_FIELDS = {
    "cpf", "prec_cp", "codom", "sigla_om", "situacao", "indicativo", "nome", "posto_grad",
    "representante_nome", "representante_cpf", "representante_idt", "periodo_inicio", "periodo_fim",
    "qtd_meses", "tipo_exercicio_anterior", "data_requerimento", "averbacao_bi_adt",
    "averbacao_data", "documento_materializou", "objeto_justificativa",
    "possui_pensao_judiciaria", "pesquisa_ficha_cadastro", "pesquisa_ficha_financeira",
    "pesquisa_levantamento_siafi", "documento_remessa", "banco", "agencia", "conta",
    "operador_nome", "operador_cpf", "operador_email_om", "operador_celular",
    "valor_bruto_devido", "valor_bruto_devido_corrigido",
    "desconto_pensao_3_zeh", "desconto_pensao_3_zeh_corrigido",
    "desconto_pensao_zeb", "desconto_pensao_zeb_corrigido",
    "desconto_pensao_15_zec", "desconto_pensao_15_zec_corrigido",
    "desconto_fusex_zea", "desconto_fusex_zea_corrigido",
    "da_ex_ant_ded_gea", "da_ex_ant_ded_gea_corrigido",
    "da_ea_aj_con_n_d_geb", "da_ea_aj_con_n_d_geb_corrigido",
    "desconto_pnr_z13", "desconto_pnr_z13_corrigido",
    "desconto_pnr_z14", "desconto_pnr_z14_corrigido",
    "fusex_dep_zef", "fusex_dep_zef_corrigido",
    "valor_liquido_devido", "valor_liquido_devido_corrigido",
}

CRITICAL_SHEETS = {
    "Analista CPEX", "Passo a Passo", "Cálculo do Acumulado", "Informações",
    "Códigos Lançamento", "Atualização_Planilha", "Contracheque - F Financeira",
    "Lançar Valor Devido", "Solicitação", "Solicitação Verso", "CPEX",
    "Solicitação Recapitulação",
}

EXPECTED_FILES = [
    "Models/ExercisePreviousModels.cs",
    "Services/ExercisePreviousRepository.cs",
    "Services/ExercisePreviousAssetsService.cs",
    "Services/ExercisePreviousExcelService.cs",
    "Services/ExercisePreviousDocumentService.cs",
    "Services/CpexExerciseAutomationService.cs",
    "Services/ExercisePreviousProtocolService.cs",
    "Views/Finance/ExercisePreviousWindow.cs",
    "Views/Finance/ExercisePreviousDialogs.cs",
    "Resources/EA/EA_IPCAE_Template.xlsm",
    "Resources/EA/CAPA_TEMPLATE.docx",
    "Resources/EA/REQUERIMENTO_TEMPLATE.docx",
]


def read(rel: str) -> str:
    return (ROOT / rel).read_text(encoding="utf-8-sig")


def add_check(checks: list[dict], name: str, ok: bool, details=None) -> None:
    checks.append({"name": name, "ok": bool(ok), "details": details})


def strip_csharp_strings_comments(source: str) -> str:
    """Remove comentários e conteúdo de strings para balanceamento simples."""
    out: list[str] = []
    i = 0
    n = len(source)
    state = "code"
    while i < n:
        c = source[i]
        nxt = source[i + 1] if i + 1 < n else ""
        if state == "code":
            if c == "/" and nxt == "/":
                state = "line_comment"; out.extend("  "); i += 2; continue
            if c == "/" and nxt == "*":
                state = "block_comment"; out.extend("  "); i += 2; continue
            if c == '"':
                # @"..." and $@"..." are handled by detecting a nearby @.
                verbatim = i > 0 and source[i - 1] == "@"
                state = "verbatim" if verbatim else "string"
                out.append(" "); i += 1; continue
            if c == "'":
                state = "char"; out.append(" "); i += 1; continue
            out.append(c); i += 1; continue
        if state == "line_comment":
            if c == "\n": state = "code"; out.append("\n")
            else: out.append(" ")
            i += 1; continue
        if state == "block_comment":
            if c == "*" and nxt == "/": state = "code"; out.extend("  "); i += 2
            else: out.append("\n" if c == "\n" else " "); i += 1
            continue
        if state == "string":
            if c == "\\": out.extend("  "); i += 2; continue
            if c == '"': state = "code"
            out.append("\n" if c == "\n" else " "); i += 1; continue
        if state == "verbatim":
            if c == '"' and nxt == '"': out.extend("  "); i += 2; continue
            if c == '"': state = "code"
            out.append("\n" if c == "\n" else " "); i += 1; continue
        if state == "char":
            if c == "\\": out.extend("  "); i += 2; continue
            if c == "'": state = "code"
            out.append("\n" if c == "\n" else " "); i += 1
    return "".join(out)


def balanced(source: str) -> tuple[bool, dict]:
    clean = strip_csharp_strings_comments(source)
    pairs = {"(": ")", "[": "]", "{": "}"}
    closing = {v: k for k, v in pairs.items()}
    stack: list[tuple[str, int]] = []
    for idx, c in enumerate(clean):
        if c in pairs: stack.append((c, idx))
        elif c in closing:
            if not stack or stack[-1][0] != closing[c]:
                return False, {"unexpected": c, "offset": idx}
            stack.pop()
    return (not stack), {"unclosed": stack[-5:]}


def workbook_sheet_names(path: Path) -> list[str]:
    with zipfile.ZipFile(path) as z:
        root = ET.fromstring(z.read("xl/workbook.xml"))
        ns = {"m": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
        return [el.attrib["name"] for el in root.findall("m:sheets/m:sheet", ns)]


def docx_placeholders(path: Path) -> set[str]:
    pat = re.compile(r"\{\{\s*([A-Z0-9_]+)\s*\}\}")
    values: set[str] = set()
    with zipfile.ZipFile(path) as z:
        for name in z.namelist():
            if name.startswith("word/") and name.endswith(".xml"):
                raw = z.read(name).decode("utf-8", errors="ignore")
                # Word can split placeholders across runs; strip XML tags for coverage.
                text = re.sub(r"<[^>]+>", "", raw)
                values.update(pat.findall(text))
    return values


def sqlite_compatibility_test() -> dict:
    with tempfile.TemporaryDirectory(prefix="sigfur_ea_validate_") as td:
        db = Path(td) / "militares.db"
        con = sqlite3.connect(db)
        con.execute("CREATE TABLE ea_processos (id INTEGER PRIMARY KEY, nome_completo TEXT, periodo_inicio TEXT, periodo_fim TEXT, atualizado_ate TEXT)")
        con.execute("INSERT INTO ea_processos VALUES (1, 'MILITAR TESTE', '2024-01-01', '2024-12-31', '2025-01')")
        con.execute("CREATE TABLE ea_codigos (id INTEGER PRIMARY KEY, processo_id INTEGER, ordem INTEGER, codigo_desc TEXT, tipo TEXT)")
        con.execute("INSERT INTO ea_codigos(processo_id,ordem,codigo_desc,tipo) VALUES (1,1,'A','Receita'),(1,1,'B','Despesa')")
        current = {r[1] for r in con.execute("PRAGMA table_info(ea_processos)")}
        for col in EXPECTED_PROCESS_COLUMNS:
            if col not in current:
                typ = "INTEGER" if col in {"militar_id", "pago", "ano_processo"} else "TEXT"
                con.execute(f'ALTER TABLE ea_processos ADD COLUMN "{col}" {typ}')
        con.commit()
        row = con.execute("SELECT nome_completo, periodo_inicio, periodo_fim, atualizado_ate FROM ea_processos WHERE id=1").fetchone()
        duplicate_orders = con.execute("SELECT COUNT(*) FROM ea_codigos WHERE processo_id=1 AND ordem=1").fetchone()[0]
        cols_after = {r[1] for r in con.execute("PRAGMA table_info(ea_processos)")}
        con.close()
        return {
            "legacy_row_preserved": row == ("MILITAR TESTE", "2024-01-01", "2024-12-31", "2025-01"),
            "all_columns_addable": set(EXPECTED_PROCESS_COLUMNS).issubset(cols_after),
            "duplicate_legacy_code_orders_allowed": duplicate_orders == 2,
        }


def main() -> int:
    checks: list[dict] = []

    missing_files = [p for p in EXPECTED_FILES if not (ROOT / p).is_file()]
    add_check(checks, "Arquivos obrigatórios", not missing_files, {"missing": missing_files, "expected_count": len(EXPECTED_FILES)})

    csproj = read("SIGFUR.Wpf.csproj")
    try:
        ET.fromstring(csproj)
        xml_ok = True
    except Exception as exc:
        xml_ok = False
        xml_error = str(exc)
    add_check(checks, "XML do projeto válido", xml_ok, None if xml_ok else xml_error)
    add_check(checks, "Versão 5.0.23", "<Version>5.0.23</Version>" in csproj)
    add_check(checks, "WPF .NET 10 Windows", "<TargetFramework>net10.0-windows</TargetFramework>" in csproj and "<UseWPF>true</UseWPF>" in csproj)
    packages = dict(re.findall(r'<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"', csproj))
    add_check(checks, "Pacotes C# necessários", all(k in packages for k in ("Microsoft.Data.Sqlite", "Selenium.WebDriver", "Selenium.Support")), packages)
    resources = re.findall(r'(?:Update|Include)="(Resources\\EA\\[^"]+)"', csproj)
    add_check(checks, "Recursos EA copiados na saída", len(set(resources)) >= 3, sorted(set(resources)))

    xaml_errors = {}
    for path in ROOT.rglob("*.xaml"):
        try: ET.parse(path)
        except Exception as exc: xaml_errors[str(path.relative_to(ROOT))] = str(exc)
    add_check(checks, "XAML bem-formado", not xaml_errors, xaml_errors)

    repo = read("Services/ExercisePreviousRepository.cs")
    map_pairs = re.findall(r'nameof\(ExercisePreviousProcess\.([A-Za-z0-9_]+)\)\s*,\s*"([a-z0-9_]+)"', repo)
    mapped_props = [p for p, _ in map_pairs]
    mapped_cols = [c for _, c in map_pairs]
    add_check(checks, "71 campos persistidos", len(map_pairs) == 71, {"count": len(map_pairs)})
    add_check(checks, "Colunas Python preservadas", set(mapped_cols) == set(EXPECTED_PROCESS_COLUMNS), {
        "missing": sorted(set(EXPECTED_PROCESS_COLUMNS) - set(mapped_cols)),
        "extra": sorted(set(mapped_cols) - set(EXPECTED_PROCESS_COLUMNS)),
    })
    add_check(checks, "Sem campos persistidos duplicados", len(mapped_cols) == len(set(mapped_cols)) and len(mapped_props) == len(set(mapped_props)), {
        "duplicate_columns": [k for k, v in Counter(mapped_cols).items() if v > 1],
        "duplicate_properties": [k for k, v in Counter(mapped_props).items() if v > 1],
    })

    model = read("Models/ExercisePreviousModels.cs")
    model_props = set(re.findall(r'public\s+(?:[A-Za-z0-9_?.<>\[\],]+\s+)+([A-Za-z_][A-Za-z0-9_]*)\s*\{', model))
    add_check(checks, "Propriedades do mapa existem no modelo", set(mapped_props).issubset(model_props), {
        "missing": sorted(set(mapped_props) - model_props)
    })

    ui = read("Views/Finance/ExercisePreviousWindow.cs") + "\n" + read("Views/Finance/ExercisePreviousDialogs.cs")
    preset_literals = set(re.findall(r'"([a-z][a-z0-9_]+)"', ui))
    add_check(checks, "28 presets editáveis do Python", set(EXPECTED_PRESET_FIELDS).issubset(preset_literals), {
        "missing": sorted(set(EXPECTED_PRESET_FIELDS) - preset_literals),
        "extra_supported": sorted({"banco"} & preset_literals),
    })

    ea_cs_paths = [ROOT / p for p in EXPECTED_FILES if p.endswith(".cs")]
    balance_errors = {}
    marker_errors = {}
    for path in ea_cs_paths:
        source = path.read_text(encoding="utf-8-sig")
        ok, detail = balanced(source)
        if not ok: balance_errors[str(path.relative_to(ROOT))] = detail
        bad = [m for m in ("<<<<<<<", ">>>>>>>", "NotImplementedException", "TODO_PORTAR_EA") if m in source]
        if bad: marker_errors[str(path.relative_to(ROOT))] = bad
    add_check(checks, "Delimitadores C# balanceados", not balance_errors, balance_errors)
    add_check(checks, "Sem marcadores de código incompleto/conflito", not marker_errors, marker_errors)

    workbook = ROOT / "Resources/EA/EA_IPCAE_Template.xlsm"
    workbook_details = {}
    workbook_ok = False
    if workbook.exists():
        try:
            with zipfile.ZipFile(workbook) as z:
                names = set(z.namelist())
                has_vba = "xl/vbaProject.bin" in names
                entry_count = len(names)
            sheets = workbook_sheet_names(workbook)
            workbook_details = {
                "zip_entries": entry_count,
                "has_vbaProject_bin": has_vba,
                "sheet_count": len(sheets),
                "missing_critical_sheets": sorted(CRITICAL_SHEETS - set(sheets)),
                "year_sheets_present": sorted([s for s in sheets if re.fullmatch(r"\d{2}", s)])
            }
            workbook_ok = has_vba and not workbook_details["missing_critical_sheets"] and len(sheets) >= 90
        except Exception as exc:
            workbook_details = {"error": str(exc)}
    add_check(checks, "XLSM original íntegro com VBA", workbook_ok, workbook_details)

    doc_service = read("Services/ExercisePreviousDocumentService.cs")
    document_keys = set(re.findall(r'\["([A-Z0-9_]+)"\]\s*=', doc_service))
    doc_details = {}
    docs_ok = True
    for filename in ("CAPA_TEMPLATE.docx", "REQUERIMENTO_TEMPLATE.docx"):
        path = ROOT / "Resources/EA" / filename
        try:
            placeholders = docx_placeholders(path)
            missing = sorted(placeholders - document_keys)
            doc_details[filename] = {"placeholder_count": len(placeholders), "missing_mapping": missing}
            docs_ok &= not missing
        except Exception as exc:
            docs_ok = False
            doc_details[filename] = {"error": str(exc)}
    add_check(checks, "Marcadores dos DOCX atendidos", docs_ok, doc_details)

    cpex = read("Services/CpexExerciseAutomationService.cs")
    cpex_literals = set(re.findall(r'"([A-Za-z][A-Za-z0-9_]+)"', cpex))
    cpex_missing = sorted(EXPECTED_CPEX_FIELDS - cpex_literals)
    review_guards = [
        "LeaveBrowserRunning = true" in cpex,
        "nenhum envio/protocolo foi confirmado" in cpex.lower(),
        "conferência e protocolo manual" in cpex.lower(),
    ]
    add_check(checks, "Campos essenciais do CPEX", not cpex_missing, {"missing": cpex_missing})
    add_check(checks, "CPEX para antes do protocolo", all(review_guards), {"guards": review_guards})
    inativo_rule = 'n.Contains("inativo")'
    ativo_rule = 'n.Contains("ativo")'
    ten_cel_rule = 'n.Contains("ten cel")'
    cel_rule = 'Word(n,"cel")'
    add_check(checks, "Correção INATIVO antes de ATIVO", cpex.find(inativo_rule) >= 0 and cpex.find(ativo_rule) >= 0 and cpex.find(inativo_rule) < cpex.find(ativo_rule), {
        "inativo_rule_index": cpex.find(inativo_rule), "ativo_rule_index": cpex.find(ativo_rule)
    })
    add_check(checks, "Correção TENENTE-CORONEL antes de CORONEL", cpex.find(ten_cel_rule) >= 0 and cpex.find(cel_rule) >= 0 and cpex.find(ten_cel_rule) < cpex.find(cel_rule), {
        "ten_cel_rule_index": cpex.find(ten_cel_rule), "cel_rule_index": cpex.find(cel_rule)
    })

    excel = read("Services/ExercisePreviousExcelService.cs")
    excel_guards = {
        "abre Excel via COM": "Excel.Application" in excel,
        "calcula antes de importar": "CalculateFull" in excel,
        "gera sempre de modelo limpo": "File.Copy" in excel,
        "exporta PDF": "ExportAsFixedFormat" in excel,
        "imprime": "PrintOut" in excel,
        "limpa matriz": bool(re.search(r"=\s*0(?:m|d)?\s*;", excel)),
    }
    add_check(checks, "Proteções de preenchimento XLSM", all(excel_guards.values()), excel_guards)
    build_regressions = {
        "sem out var em leitura dynamic do Excel": "TryDouble(factorRaw, out var" not in excel and "TryDouble(sheet.Cells" not in excel,
        "sem argumento nomeado após dynamic": "received: true" not in excel and "received: false" not in excel,
        "tipos explícitos na importação IPCA": all(token in excel for token in ("object? monthYearRaw", "string competence", "double factor", "double parsedPercentage")),
        "sem IWebElement.Rect": ".Rect" not in cpex,
        "posição Selenium por Location": "element.Location" in cpex,
    }
    add_check(checks, "Regressões de build 5.0.21 corrigidas", all(build_regressions.values()), build_regressions)

    schema = sqlite_compatibility_test()
    add_check(checks, "Compatibilidade de migração SQLite", all(schema.values()), schema)

    integration = read("MainWindow.xaml.cs") + "\n" + read("Services/ActionCatalog.cs")
    add_check(checks, "EA integrado à janela principal", "ExercisePreviousWindow" in integration and "Exercício Anterior" in integration)

    passed = sum(1 for c in checks if c["ok"])
    failed = len(checks) - passed
    result = {
        "project": "SIGFUR.Wpf",
        "version": "5.0.23",
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
        "root": str(ROOT),
        "status": "PASS" if failed == 0 else "FAIL",
        "summary": {"checks": len(checks), "passed": passed, "failed": failed},
        "build": {
            "executed": False,
            "reason": "Ambiente de validação sem SDK .NET/compilador C# e sem acesso de rede para instalação.",
            "requires_target_validation": [
                "dotnet restore/build/publish no Windows",
                "abertura e salvamento real pelo Microsoft Excel desktop",
                "preenchimento do CPEX na intranet com captcha e revisão humana",
            ],
        },
        "checks": checks,
    }
    RESULT_PATH.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result["summary"], ensure_ascii=False))
    if failed:
        for c in checks:
            if not c["ok"]:
                print(f"FALHOU: {c['name']} -> {c['details']}")
    print(f"Relatório: {RESULT_PATH}")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
