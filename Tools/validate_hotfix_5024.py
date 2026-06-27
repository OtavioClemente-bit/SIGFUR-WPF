from __future__ import annotations

import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
checks: list[dict[str, object]] = []

def add(name: str, ok: bool, detail: str = "") -> None:
    checks.append({"name": name, "ok": bool(ok), "detail": detail})

adjust = (ROOT / "Views/Finance/AdjustmentAccountsWindow.xaml.cs").read_text(encoding="utf-8")
ea = (ROOT / "Views/Finance/ExercisePreviousWindow.cs").read_text(encoding="utf-8")
csproj = (ROOT / "SIGFUR.Wpf.csproj").read_text(encoding="utf-8")

add("Versão 5.0.24", "<Version>5.0.24</Version>" in csproj)
add("Mensagem do boletim usa escape", "'{_settings.Rank}'.\\n\\nO boletim" in adjust)
add("Mensagem de substituição EA usa escape", "{codeName}.\\n\\nDeseja substituir" in ea)
add("Mensagem de edição EA usa escape", "{edited.CodeOrder:00}.\\n\\nEdite o lançamento" in ea)
add("Mensagem de duplicação EA usa escape", "{dialog.Entry.CodeOrder:00}.\\n\\nEscolha outra competência" in ea)

# Os quatro literais que causaram CS1039/CS1010 não podem voltar a ocupar várias linhas físicas.
for rel in [
    "Views/Finance/AdjustmentAccountsWindow.xaml.cs",
    "Views/Finance/ExercisePreviousWindow.cs",
]:
    text = (ROOT / rel).read_text(encoding="utf-8")
    odd_lines = []
    raw = False
    for number, line in enumerate(text.splitlines(), 1):
        triple = line.count('"""')
        if triple % 2:
            raw = not raw
            continue
        if raw:
            continue
        # Conta aspas não escapadas. Linhas com literal C# comum devem terminar com quantidade par.
        count = 0
        i = 0
        while i < len(line):
            if line[i] == "\\":
                i += 2
                continue
            if line[i] == '"':
                count += 1
            i += 1
        if count % 2:
            odd_lines.append(number)
    add(f"Literais fechados em {rel}", not odd_lines, f"linhas suspeitas: {odd_lines}")

# Todo XAML precisa continuar sendo XML válido.
xaml_errors = []
for path in ROOT.rglob("*.xaml"):
    try:
        ET.parse(path)
    except Exception as exc:  # pragma: no cover - diagnóstico local
        xaml_errors.append(f"{path.relative_to(ROOT)}: {exc}")
add("Todos os XAML são XML válidos", not xaml_errors, "; ".join(xaml_errors))

# Eventos dos dois XAML alterados devem existir no code-behind.
event_names = {
    "Click", "SelectionChanged", "TextChanged", "PreviewKeyDown", "MouseDoubleClick",
    "CellEditEnding", "Loaded", "Closing", "KeyDown"
}
missing_handlers = []
for xaml_rel, code_rel in [
    ("Views/Finance/AdjustmentAccountsWindow.xaml", "Views/Finance/AdjustmentAccountsWindow.xaml.cs"),
    ("Views/Finance/AdjustmentBulletinWindow.xaml", "Views/Finance/AdjustmentBulletinWindow.xaml.cs"),
]:
    tree = ET.parse(ROOT / xaml_rel)
    code = (ROOT / code_rel).read_text(encoding="utf-8")
    for element in tree.iter():
        for attr, value in element.attrib.items():
            event = attr.split("}")[-1]
            if event in event_names and re.fullmatch(r"[A-Za-z_]\w*", value):
                if not re.search(rf"\b{re.escape(value)}\s*\(", code):
                    missing_handlers.append(f"{xaml_rel}: {value}")
add("Eventos XAML possuem handlers", not missing_handlers, "; ".join(missing_handlers))

add("Script de publicação 5.0.24", (ROOT / "COMPILAR_E_PUBLICAR_5.0.24.bat").exists())
add("Editor profissional de lançamentos permanece", "class ExercisePreviousEntryEditorWindow" in (ROOT / "Views/Finance/ExercisePreviousDialogs.cs").read_text(encoding="utf-8"))
add("Filtro por posto no boletim permanece", "Apenas militares desse mesmo posto/graduação" in (ROOT / "Views/Finance/AdjustmentBulletinWindow.xaml.cs").read_text(encoding="utf-8"))

result = {
    "version": "5.0.24",
    "checks": len(checks),
    "passed": sum(1 for item in checks if item["ok"]),
    "failed": sum(1 for item in checks if not item["ok"]),
    "items": checks,
}
out = ROOT / "VALIDACAO_HOTFIX_5.0.24.json"
out.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps(result, ensure_ascii=False, indent=2))
raise SystemExit(1 if result["failed"] else 0)
