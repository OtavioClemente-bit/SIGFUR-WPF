# -*- coding: utf-8 -*-
"""Ponte persistente entre a janela principal WPF e os módulos Python do SIGFUR.

Mantém um único interpretador Python e um único root Tk oculto para preservar:
- sessão compartilhada do SISBOL;
- módulos importados e caches;
- banco e AppData atuais;
- abertura das telas ainda não migradas durante a transição código por código.
"""
from __future__ import annotations

import argparse
import importlib
import importlib.util as importlib_util
import inspect
import json
import os
import queue
import re
import sqlite3
import subprocess
import sys
import threading
import traceback
from collections import Counter
from datetime import date, datetime
from pathlib import Path
from typing import Any

_PROTOCOL_OUT = sys.stdout
_PROTOCOL_LOCK = threading.Lock()
_AUDIT_JOBS: dict[str, dict] = {}
_AUDIT_JOBS_LOCK = threading.RLock()


def _args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--server", action="store_true")
    parser.add_argument("--project-root", default="")
    parser.add_argument("--data-dir", default="")
    parser.add_argument("--log-file", default="")
    return parser.parse_args()


ARGS = _args()
PROJECT_ROOT = os.path.abspath(ARGS.project_root or os.getcwd())
DATA_DIR = os.path.abspath(ARGS.data_dir or os.path.join(os.environ.get("LOCALAPPDATA", os.getcwd()), "SIGFUR"))
os.makedirs(DATA_DIR, exist_ok=True)
os.environ["ESCALA_DATA_DIR"] = DATA_DIR
DATABASE_FILE = os.path.abspath(os.environ.get("MILITARES_DB") or os.path.join(DATA_DIR, "militares.db"))
os.environ["MILITARES_DB"] = DATABASE_FILE
if PROJECT_ROOT and PROJECT_ROOT not in sys.path:
    sys.path.insert(0, PROJECT_ROOT)

_log_path = ARGS.log_file or os.path.join(DATA_DIR, "logs", "wpf_python_bridge.log")
os.makedirs(os.path.dirname(_log_path), exist_ok=True)
_LOG = open(_log_path, "a", encoding="utf-8", buffering=1)
sys.stdout = _LOG
sys.stderr = _LOG


def log(message: str):
    try:
        print(f"[{datetime.now():%Y-%m-%d %H:%M:%S}] {message}", file=_LOG, flush=True)
    except Exception:
        pass


def respond(req_id: str, ok: bool, result: Any = None, error: str = ""):
    payload = {"id": req_id, "ok": bool(ok)}
    if result is not None:
        payload["result"] = result
    if error:
        payload["error"] = error
    line = json.dumps(payload, ensure_ascii=False, default=_json_default)
    with _PROTOCOL_LOCK:
        _PROTOCOL_OUT.write(line + "\n")
        _PROTOCOL_OUT.flush()


def _json_default(value):
    if isinstance(value, (datetime, date)):
        return value.isoformat()
    try:
        return dict(value)
    except Exception:
        return str(value)


try:
    import tkinter as tk
    ROOT = tk.Tk()
    ROOT.withdraw()
    ROOT.title("SIGFUR — Compatibilidade Python")
except Exception as exc:
    log("Falha ao criar root Tk: " + repr(exc))
    ROOT = None

REQUESTS: queue.Queue[dict] = queue.Queue()
SHUTTING_DOWN = False
OPEN_WINDOWS: dict[str, Any] = {}
OPENING_ACTIONS: set[str] = set()


ACTION_MAP: dict[str, tuple[tuple[str, ...], tuple[str, ...]]] = {
    "cadastro": (("interface.cadastro_militar", "cadastro_militar"), ("abrir_cadastro",)),
    "listar": (("interface.listar_militares", "listar_militares"), ("abrir_listagem",)),
    "plano_chamada": (("interface.plano_chamada", "plano_chamada"), ("abrir_plano_chamada",)),
    "boletim": (("interface.boletim", "boletim"), ("abrir_boletim",)),
    "boletim_resumo": (("interface.boletim_inteligente", "boletim_inteligente"), ("abrir_boletim_inteligente",)),
    "boletim_furriel": (("interface.boletim_furriel", "boletim_furriel"), ("abrir_boletim_furriel",)),
    "lembretes": (("interface.lembretes", "lembretes"), ("abrir_lembretes",)),
    "soldos": (("interface.soldos", "soldos"), ("abrir_soldos",)),
    "aux_transporte": (("interface.auxilio_transporte", "auxilio_transporte"), ("abrir_auxilio_transporte",)),
    "grat_representacao": (("interface.gratificacao", "gratificacao"), ("abrir_gratificacao_representacao",)),
    "ajuste_contas": (("interface.ajuste_contas", "ajuste_contas"), ("abrir_ajuste_contas",)),
    "lic_transf": (("interface.licenciados_transferidos", "licenciados_transferidos"), ("abrir_licenciados_transferidos",)),
    "plano_ferias": (("interface.plano_ferias", "plano_ferias"), ("abrir_plano_ferias",)),
    "exercicio_anterior": (("interface.exercicio_anterior", "exercicio_anterior"), ("abrir_exercicio_anterior",)),
    "escala_sgt_dia": (("interface.escala_sgt_dia", "escala_sgt_dia"), ("abrir_escala_sgt_dia",)),
    "pensao_judicial": (("interface.pensao_judicial", "pensao_judicial"), ("abrir_pensao_judicial",)),
    "inconsistencia_bancaria": (("interface.inconsistencia_bancaria", "inconsistencia_bancaria"), ("abrir_inconsistencia_bancaria",)),
    "bizurometro_sped": (("interface.bizurometro_sped", "bizurometro_sped"), ("abrir_bizurometro_sped",)),
    "legislacao": (("interface.legislacao", "legislacao"), ("abrir_legislacao",)),
    "ferramentas_pdf": (("interface.ferramentas_pdf", "ferramentas_pdf"), ("abrir_ferramentas_pdf",)),
    "fila_impressao": (("interface.fila_impressao", "fila_impressao"), ("abrir_fila_impressao",)),
    "phpm": (("interface.phpm", "phpm"), ("abrir_phpm",)),
    "relacao_pessoal": (("relatorios.relacao_pessoal", "relacao_pessoal"), ("gerar_relacao_pessoal",)),
    "medidas_tomadas": (("interface.medidas_tomadas", "medidas_tomadas"), ("abrir_medidas_tomadas",)),
    "faltas_atrasos": (("interface.faltas_atrasos", "faltas_atrasos"), ("abrir_faltas_atrasos",)),
}


def import_first(names: tuple[str, ...]):
    errors = []
    for name in names:
        try:
            return importlib.import_module(name)
        except Exception as exc:
            errors.append(f"{name}: {exc}")
    raise ImportError(" | ".join(errors))


def callable_first(module, names: tuple[str, ...]):
    for name in names:
        fn = getattr(module, name, None)
        if callable(fn):
            return fn
    raise AttributeError(f"Nenhuma função encontrada: {', '.join(names)}")


def invoke_ui(fn, action_id: str):
    if ROOT is None:
        return fn()
    if action_id == "medidas_tomadas":
        db_path = _db_path()
        for kwargs in (
            {"db_path": db_path, "om_padrao": _ui_config().get("om") or "4ª Cia PE", "funcao_assinatura_padrao": "Respondendo pelo Cmdo da 4ª Cia PE"},
            {"db_path": db_path},
            {},
        ):
            try:
                return fn(ROOT, **kwargs)
            except TypeError:
                continue
        return fn(ROOT)
    if action_id == "legislacao":
        try:
            return fn(ROOT, base_dir=os.path.join(DATA_DIR, "legislacao"))
        except TypeError:
            pass
    try:
        sig = inspect.signature(fn)
        positional = [p for p in sig.parameters.values() if p.kind in (p.POSITIONAL_ONLY, p.POSITIONAL_OR_KEYWORD)]
        if positional:
            return fn(ROOT)
        return fn()
    except (ValueError, TypeError):
        try:
            return fn(ROOT)
        except TypeError:
            return fn()


def bring_children_front():
    if ROOT is None:
        return
    try:
        for child in ROOT.winfo_children():
            if isinstance(child, tk.Toplevel) and child.winfo_exists():
                try:
                    child.deiconify()
                    child.lift()
                    child.attributes("-topmost", True)
                    child.after(180, lambda w=child: w.attributes("-topmost", False) if w.winfo_exists() else None)
                except Exception:
                    pass
    except Exception:
        pass


def _window_exists(window) -> bool:
    try:
        return bool(window is not None and window.winfo_exists())
    except Exception:
        return False


def _focus_window(window) -> bool:
    if not _window_exists(window):
        return False
    try:
        window.deiconify()
        window.lift()
        window.focus_force()
        window.attributes("-topmost", True)
        window.after(180, lambda w=window: w.attributes("-topmost", False) if _window_exists(w) else None)
        return True
    except Exception:
        return False


def _remember_action_window(action_id: str, before: set, result=None):
    candidate = result if _window_exists(result) else None
    if candidate is None and ROOT is not None:
        try:
            new_children = [w for w in ROOT.winfo_children() if w not in before and isinstance(w, tk.Toplevel) and _window_exists(w)]
            if new_children:
                candidate = new_children[-1]
        except Exception:
            candidate = None
    if candidate is not None:
        OPEN_WINDOWS[action_id] = candidate
        try:
            candidate.bind("<Destroy>", lambda _e, key=action_id, w=candidate: OPEN_WINDOWS.pop(key, None) if OPEN_WINDOWS.get(key) is w else None, add="+")
        except Exception:
            pass


def _run_ui_safely(label: str, callback):
    try:
        callback()
    except Exception:
        log(f"Falha abrindo {label}:\n{traceback.format_exc()}")
    finally:
        OPENING_ACTIONS.discard(label)


def open_action(action_id: str):
    """Valida o módulo e agenda a abertura na fila Tk, preservando janela única."""
    current = OPEN_WINDOWS.get(action_id)
    if _focus_window(current):
        return {"focused": True, "action_id": action_id}
    if action_id in OPENING_ACTIONS:
        bring_children_front()
        return {"already_opening": True, "action_id": action_id}
    if action_id not in ACTION_MAP:
        raise KeyError(f"Ação sem ponte Python: {action_id}")
    modules, functions = ACTION_MAP[action_id]
    module = import_first(modules)
    fn = callable_first(module, functions)
    OPENING_ACTIONS.add(action_id)

    def _open():
        before = set(ROOT.winfo_children()) if ROOT is not None else set()
        result = invoke_ui(fn, action_id)
        _remember_action_window(action_id, before, result)
        if ROOT is not None:
            ROOT.after(120, bring_children_front)

    if ROOT is None:
        _run_ui_safely(action_id, _open)
    else:
        ROOT.after_idle(lambda: _run_ui_safely(action_id, _open))
    return {"scheduled": True, "action_id": action_id}


def open_military_wallet(military_id: int):
    module = import_first(("interface.listar_militares", "listar_militares"))
    candidates = (
        "abrir_carteira_por_id",
        "abrir_carteira_militar_por_id",
        "abrir_carteira_full_by_id",
        "abrir_carteira_full_por_id",
    )
    fn = callable_first(module, candidates)

    def _open():
        attempts = [
            lambda: fn(ROOT, int(military_id)),
            lambda: fn(int(military_id), parent=ROOT),
            lambda: fn(int(military_id)),
        ]
        last = None
        for attempt in attempts:
            try:
                attempt()
                if ROOT is not None:
                    ROOT.after(120, bring_children_front)
                return
            except TypeError as exc:
                last = exc
        raise RuntimeError(str(last or "Não foi possível chamar a carteira por ID."))

    if ROOT is None:
        _open()
    else:
        ROOT.after_idle(lambda: _run_ui_safely(f"carteira {military_id}", _open))
    return {"scheduled": True, "military_id": int(military_id)}



def _sqlite_military_count(path: str) -> int:
    """Retorna a quantidade da tabela militares; -1 indica banco inválido/sem tabela."""
    try:
        if not path or not os.path.isfile(path):
            return -1
        with sqlite3.connect(f"file:{Path(path).as_posix()}?mode=ro", uri=True, timeout=5) as con:
            row = con.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='militares'").fetchone()
            if not row:
                return -1
            return int(con.execute("SELECT COUNT(*) FROM militares").fetchone()[0])
    except Exception:
        return -1


def _database_candidates() -> list[str]:
    candidates: list[str] = []

    def add(value):
        try:
            if not value:
                return
            path = os.path.abspath(os.path.expandvars(os.path.expanduser(str(value))))
            if os.path.isdir(path):
                path = os.path.join(path, "militares.db")
            if path not in candidates:
                candidates.append(path)
        except Exception:
            pass

    add(os.environ.get("MILITARES_DB"))
    add(DATABASE_FILE)
    add(os.path.join(DATA_DIR, "militares.db"))

    local = os.environ.get("LOCALAPPDATA")
    roaming = os.environ.get("APPDATA")
    if local:
        add(os.path.join(local, "SIGFUR", "militares.db"))
    if roaming:
        add(os.path.join(roaming, "SIGFUR", "militares.db"))

    add(os.path.join(PROJECT_ROOT, "militares.db"))
    add(os.path.join(PROJECT_ROOT, "database", "militares.db"))
    add(os.path.join(PROJECT_ROOT, "dados", "militares.db"))

    try:
        root = Path(PROJECT_ROOT)
        for parent in [root, *list(root.parents)[:4]]:
            add(parent / "militares.db")
            add(parent / "SIGFUR" / "militares.db")
    except Exception:
        pass

    return candidates


def _copy_database_consistently(source: str, target: str) -> bool:
    """Copia via API de backup do SQLite, inclusive quando o banco usa WAL."""
    try:
        if not source or not target or os.path.abspath(source) == os.path.abspath(target):
            return False
        os.makedirs(os.path.dirname(target), exist_ok=True)
        temp = target + ".wpf_migrating"
        try:
            if os.path.exists(temp):
                os.remove(temp)
        except Exception:
            pass
        with sqlite3.connect(source, timeout=15) as src, sqlite3.connect(temp, timeout=15) as dst:
            src.backup(dst)
        os.replace(temp, target)
        return True
    except Exception:
        log("Falha ao consolidar banco no AppData Local:\n" + traceback.format_exc())
        try:
            if os.path.exists(target + ".wpf_migrating"):
                os.remove(target + ".wpf_migrating")
        except Exception:
            pass
        return False


def _discover_database_file(*, consolidate: bool = True) -> str:
    """Localiza o banco real com dados e, quando seguro, consolida-o no AppData Local."""
    global DATABASE_FILE
    target = os.path.abspath(os.path.join(DATA_DIR, "militares.db"))
    evaluated = []
    for candidate in _database_candidates():
        count = _sqlite_military_count(candidate)
        if count < 0:
            continue
        try:
            size = os.path.getsize(candidate)
            modified = os.path.getmtime(candidate)
        except Exception:
            size = 0
            modified = 0
        evaluated.append((count, size, modified, os.path.abspath(candidate)))

    if evaluated:
        # Prioridade principal: banco com mais militares. Em empate, maior e mais recente.
        evaluated.sort(key=lambda item: (item[0], item[1], item[2]), reverse=True)
        best_count, _size, _modified, best = evaluated[0]
        target_count = _sqlite_military_count(target)
        if consolidate and best_count > 0 and os.path.abspath(best) != target and target_count <= 0:
            if _copy_database_consistently(best, target):
                best = target
                log(f"Banco com {best_count} militar(es) consolidado em: {target}")
        DATABASE_FILE = os.path.abspath(best)
    else:
        DATABASE_FILE = target

    os.environ["ESCALA_DATA_DIR"] = DATA_DIR
    os.environ["MILITARES_DB"] = DATABASE_FILE
    return DATABASE_FILE


def _configure_database_module(module, path: str):
    try:
        set_path = getattr(module, "set_db_path", None)
        if callable(set_path):
            set_path(path)
        elif hasattr(module, "DB_PATH"):
            setattr(module, "DB_PATH", path)
    except Exception:
        log("Não foi possível configurar o caminho no módulo database.db:\n" + traceback.format_exc())
    return module


def _query_military_rows_direct(path: str):
    try:
        if not path or not os.path.isfile(path):
            return []
        con = sqlite3.connect(path, timeout=10)
        con.row_factory = sqlite3.Row
        try:
            exists = con.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='militares'").fetchone()
            if not exists:
                return []
            return list(con.execute("SELECT * FROM militares").fetchall())
        finally:
            con.close()
    except Exception:
        log("Falha na leitura direta da tabela militares:\n" + traceback.format_exc())
        return []

def init_database():
    path = _discover_database_file(consolidate=True)
    module_available = False
    module_error = ""
    try:
        module = _configure_database_module(import_first(("database.db", "db")), path)
        module_available = True
        fn = getattr(module, "init", None)
        if callable(fn):
            fn()
        # O init pode criar/migrar o banco; confira novamente o caminho efetivo.
        path = _db_path(module)
    except Exception as exc:
        # O Dashboard não deve depender da importação do projeto Python.
        module_error = f"{type(exc).__name__}: {exc}"
        log("Módulo database.db indisponível na inicialização; usando SQLite direto.\n" + traceback.format_exc())
    return {
        "initialized": True,
        "database_path": path,
        "military_count": max(0, _sqlite_military_count(path)),
        "module_available": module_available,
        "module_error": module_error,
    }


def _db_module():
    path = _discover_database_file(consolidate=True)
    module = import_first(("database.db", "db"))
    return _configure_database_module(module, path)


def _db_path(module=None) -> str:
    discovered = _discover_database_file(consolidate=True)
    try:
        if module is not None:
            _configure_database_module(module, discovered)
            fn = getattr(module, "get_db_path", None)
            if callable(fn):
                path = fn()
                if path:
                    return str(path)
            for attr in ("DB_PATH", "DATABASE_PATH", "db_path"):
                path = getattr(module, attr, None)
                if path:
                    return str(path)
    except Exception:
        pass
    return discovered


def _buscar_militares():
    path = _discover_database_file(consolidate=True)
    direct_rows = _query_military_rows_direct(path)
    module_rows = []
    try:
        module = _configure_database_module(import_first(("database.db", "db")), path)
        for name in ("buscar_todos", "listar_militares", "militares_listar"):
            fn = getattr(module, name, None)
            if callable(fn):
                module_rows = list(fn() or [])
                break
    except Exception:
        log("Consulta pelo módulo Python indisponível; usando SQLite direto.\n" + traceback.format_exc())

    # Se o módulo apontou para um banco vazio/errado, prevalece a leitura direta do banco detectado.
    return direct_rows if len(direct_rows) > len(module_rows) else module_rows

def row_to_dict(row) -> dict:
    if isinstance(row, dict):
        return dict(row)
    try:
        if hasattr(row, "keys"):
            return {str(k): row[k] for k in row.keys()}
    except Exception:
        pass
    try:
        return dict(row)
    except Exception:
        return {}


def row_get(row, *keys, default=""):
    d = row_to_dict(row)
    lower = {str(k).lower(): v for k, v in d.items()}
    for key in keys:
        if isinstance(key, int):
            try:
                value = row[key]
                if value not in (None, ""):
                    return value
            except Exception:
                continue
        else:
            if key in d and d[key] not in (None, ""):
                return d[key]
            value = lower.get(str(key).lower())
            if value not in (None, ""):
                return value
    return default


def norm(value: Any) -> str:
    import unicodedata
    text = unicodedata.normalize("NFKD", str(value or ""))
    text = "".join(ch for ch in text if not unicodedata.combining(ch))
    return re.sub(r"\s+", " ", text).strip().lower()


def canon_rank(value: str) -> str:
    s = norm(value)
    rules = (
        (("general", "gen ", "gen"), "Gen"), (("tenente coronel", "ten cel", "tc"), "Ten Cel"),
        (("coronel", "cel"), "Cel"), (("major", "maj"), "Maj"), (("capitao", "cap"), "Cap"),
        (("primeiro tenente", "1 ten", "1º ten", "1o ten"), "1º Ten"), (("segundo tenente", "2 ten", "2º ten", "2o ten"), "2º Ten"),
        (("aspirante", "asp of", "asp"), "Asp Of"), (("subtenente", "sub ten", "st"), "Sub Ten"),
        (("primeiro sargento", "1 sgt", "1º sgt", "1o sgt"), "1º Sgt"), (("segundo sargento", "2 sgt", "2º sgt", "2o sgt"), "2º Sgt"),
        (("terceiro sargento", "3 sgt", "3º sgt", "3o sgt"), "3º Sgt"),
        (("cabo", "cb"), "Cb Ef Profl"), (("soldado", "sd"), "Sd Ef Profl"),
    )
    for aliases, label in rules:
        if any(s == alias or s.startswith(alias + " ") for alias in aliases):
            return label
    return str(value or "Outros").strip() or "Outros"


RANK_ORDER = ["Gen", "Cel", "Ten Cel", "Maj", "Cap", "1º Ten", "2º Ten", "Asp Of", "Sub Ten", "1º Sgt", "2º Sgt", "3º Sgt", "Cb Ef Profl", "Sd Ef Profl", "Sd Ef Vrv", "Outros"]


def bool_value(value) -> bool | None:
    if value in (None, ""):
        return None
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return bool(value)
    s = norm(value)
    if s in {"sim", "s", "yes", "true", "1", "recebe", "ativo"} or s.startswith("sim") or "recebe" in s:
        if "nao recebe" not in s:
            return True
    if s in {"nao", "n", "no", "false", "0", "sem", "inativo"} or s.startswith("nao"):
        return False
    return None


def money_float(value) -> float:
    try:
        if isinstance(value, (int, float)):
            return float(value)
        s = str(value or "").replace("R$", "").replace(" ", "")
        if "," in s:
            s = s.replace(".", "").replace(",", ".")
        s = re.sub(r"[^0-9.\-]", "", s)
        return float(s or 0)
    except Exception:
        return 0.0


def receives_transport(row) -> bool:
    raw = row_get(row, "recebe_aux_transporte", "RecebeAuxTransporte", "recebe_aux", "RecebeAux", "aux_transporte_recebe", "tem_aux_transporte", "recebe_at", "RecebeAT", 18, default=None)
    decided = bool_value(raw)
    if decided is not None:
        return decided
    value = row_get(row, "valor_aux_transporte", "aux_transporte_valor", "aux_valor", "ValorAux", "ValorLiq", "TotalLiq", "total_liq", "aux_transporte", 19, default=0)
    return money_float(value) > 0


def military_item(row) -> dict:
    return {
        "id": _to_int(row_get(row, "id", "ID", "militar_id", 0, default=0)),
        "rank": canon_rank(str(row_get(row, "posto", "Posto", "posto_grad", "posto_graduacao", 1, default=""))),
        "name": str(row_get(row, "nome", "Nome", "nome_completo", 2, default="—") or "—"),
        "war_name": str(row_get(row, "nome_guerra", "NomeGuerra", "guerra", "NG", "ng", 3, default="—") or "—"),
        "cpf": str(row_get(row, "cpf", "CPF", 4, default="") or ""),
        "formation_year": str(row_get(row, "ano", "ano_formacao", "turma", default="—") or "—"),
    }


def _to_int(value, default=0):
    try:
        return int(float(str(value).strip()))
    except Exception:
        return default


def parse_date_any(value):
    s = str(value or "").strip()
    if not s:
        return None
    for fmt in ("%Y-%m-%d", "%d/%m/%Y", "%d-%m-%Y", "%d.%m.%Y", "%d/%m/%y", "%d/%m"):
        try:
            return datetime.strptime(s[:10], fmt)
        except Exception:
            pass
    digits = re.sub(r"\D+", "", s)
    for fmt in ("%d%m%Y", "%Y%m%d", "%d%m"):
        try:
            return datetime.strptime(digits, fmt)
        except Exception:
            pass
    return None


def load_json(path, default):
    try:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        return data if data is not None else default
    except Exception:
        return default


def _ui_config():
    data = load_json(os.path.join(DATA_DIR, "ui_config.json"), {})
    return data if isinstance(data, dict) else {}


def reminders_snapshot() -> tuple[list[dict], dict]:
    raw = load_json(os.path.join(DATA_DIR, "lembretes.json"), [])
    items = raw if isinstance(raw, list) else raw.get("items", []) if isinstance(raw, dict) else []
    today = date.today()
    out = []
    counts = {"total": len(items), "overdue": 0, "today": 0, "upcoming": 0}
    for index, item in enumerate(items):
        if not isinstance(item, dict):
            continue
        completed = bool(item.get("concluido") or item.get("completed"))
        dt = parse_date_any(item.get("data") or item.get("date"))
        if completed:
            status = "Concluído"
        elif dt is None:
            status = "Sem data"
        elif dt.date() < today:
            status = "Atrasado"; counts["overdue"] += 1
        elif dt.date() == today:
            status = "Hoje"; counts["today"] += 1
        else:
            status = "Pendente"
            if 0 < (dt.date() - today).days <= 2:
                counts["upcoming"] += 1
        out.append({
            "id": str(item.get("id") or index), "title": str(item.get("titulo") or item.get("title") or ""),
            "date": dt.strftime("%d/%m/%Y") if dt else "—", "status": status,
            "priority": str(item.get("prioridade") or item.get("priority") or "Normal"), "completed": completed,
        })
    order = {"Atrasado": 0, "Hoje": 1, "Pendente": 2, "Sem data": 3, "Concluído": 4}
    out.sort(key=lambda x: (order.get(x["status"], 9), x["date"], norm(x["title"])))
    return out[:100], counts


def birthdays_snapshot(rows) -> list[dict]:
    today = date.today()
    conference = load_json(os.path.join(DATA_DIR, "aniversariantes_conferencia.json"), {})
    month_key = today.strftime("%Y-%m")
    bucket = conference.get(month_key, {}) if isinstance(conference, dict) else {}
    out = []
    for row in rows:
        raw = row_get(row, "data_nascimento", "nascimento", "dt_nascimento", "data_nasc", default="")
        dt = parse_date_any(raw)
        if not dt or dt.month != today.month:
            continue
        mid = _to_int(row_get(row, "id", "militar_id", 0, default=0))
        age = max(0, today.year - dt.year) if dt.year > 1900 else 0
        checked = False
        val = bucket.get(str(mid)) if isinstance(bucket, dict) and mid else None
        if isinstance(val, dict): checked = bool(val.get("conferido"))
        else: checked = bool(val)
        out.append({
            "military_id": mid, "day": f"{dt.day:02d}", "rank": canon_rank(str(row_get(row, "posto", 1, default=""))),
            "war_name": str(row_get(row, "nome_guerra", "guerra", 3, default="") or ""),
            "name": str(row_get(row, "nome", 2, default="") or ""), "age": age,
            "is_today": dt.day == today.day, "confirmed": checked,
        })
    out.sort(key=lambda x: (_to_int(x["day"]), RANK_ORDER.index(x["rank"]) if x["rank"] in RANK_ORDER else 999, norm(x["name"])))
    return out


def rank_snapshot(rows) -> list[dict]:
    grouped: dict[str, list[dict]] = {}
    for row in rows:
        item = military_item(row)
        grouped.setdefault(item["rank"], []).append(item)
    out = []
    for rank, items in grouped.items():
        years = Counter(str(x["formation_year"]) for x in items if str(x["formation_year"]).strip() not in ("", "—", "None"))
        out.append({
            "rank": rank, "count": len(items),
            "years": [{"year": year, "count": count} for year, count in sorted(years.items(), key=lambda kv: (_to_int(kv[0], 9999), kv[0]))],
        })
    out.sort(key=lambda x: (RANK_ORDER.index(x["rank"]) if x["rank"] in RANK_ORDER else 999, norm(x["rank"])))
    return out


def bulletin_snapshot() -> list[dict]:
    out = []
    bi = load_json(os.path.join(DATA_DIR, "boletins_salvos_index.json"), {})
    items = bi if isinstance(bi, list) else bi.get("items", []) if isinstance(bi, dict) else []
    for item in items:
        if not isinstance(item, dict): continue
        path = str(item.get("caminho_pdf") or item.get("path") or "")
        out.append({
            "type": "BI", "number": str(item.get("numero_bi") or item.get("numero") or "—"),
            "date": _format_date(item.get("data_bi") or item.get("data") or item.get("data_iso")),
            "file": str(item.get("nome_arquivo") or item.get("nome_arquivo_original") or os.path.basename(path) or "—"), "path": path,
        })
    furriel = load_json(os.path.join(DATA_DIR, "boletim_furriel", "indice_furriel.json"), {})
    for item in furriel.get("files", []) if isinstance(furriel, dict) else []:
        if not isinstance(item, dict): continue
        path = str(item.get("stored_path") or item.get("signed_path") or "")
        out.append({
            "type": "ADT", "number": str(item.get("boletim") or "—"), "date": _format_date(item.get("data")),
            "file": str(item.get("original_name") or item.get("arquivo") or os.path.basename(path) or "—"), "path": path,
        })
    out.sort(key=lambda x: parse_date_any(x["date"]) or datetime(1900, 1, 1), reverse=True)
    return out[:80]


def _format_date(value):
    dt = parse_date_any(value)
    return dt.strftime("%d/%m/%Y") if dt else str(value or "—")


def third_business_day(year: int, month: int) -> date:
    count = 0
    for day in range(1, 32):
        try: current = date(year, month, day)
        except ValueError: break
        if current.weekday() < 5:
            count += 1
            if count == 3: return current
    return date(year, month, 3)


def corrida_config() -> tuple[int, int, date]:
    settings = load_json(os.path.join(DATA_DIR, "app_settings.json"), {})
    cfg = settings.get("corrida_pagamento", {}) if isinstance(settings, dict) else {}
    today = date.today()
    year, month = today.year, today.month
    match = re.match(r"^(20\d{2})-(\d{1,2})$", str(cfg.get("competencia") or ""))
    if match and 1 <= int(match.group(2)) <= 12:
        year, month = int(match.group(1)), int(match.group(2))
    second = parse_date_any(cfg.get("segunda"))
    return year, month, second.date() if second else third_business_day(year, month)


def financial_alerts_snapshot() -> list[dict]:
    today = date.today()
    year, month, deadline = corrida_config()
    month_names = ["JAN", "FEV", "MAR", "ABR", "MAI", "JUN", "JUL", "AGO", "SET", "OUT", "NOV", "DEZ"]
    next_month = month + 1; next_year = year
    if next_month == 13: next_month = 1; next_year += 1
    competence = f"{month_names[month-1]}/{str(year)[-2:]}"
    vacation = f"{month_names[next_month-1]}/{str(next_year)[-2:]}"
    days = (deadline - today).days
    status = "ATRASADO" if days < 0 else "HOJE" if days == 0 else "URGENTE" if days <= 3 else "PRAZO"
    automatic = [
        ("ferias", f"Férias {vacation} — {status}", f"Pagar/conferir militares que entram de férias em {vacation}. Lançar na competência {competence}; prazo na 2ª corrida ({deadline:%d/%m/%Y})."),
        ("da_at", f"DA Aux Transporte — {status}", f"Conferir Despesa a Anular do Auxílio-Transporte da competência {competence} até a 2ª corrida ({deadline:%d/%m/%Y})."),
        ("inconsistencia", f"Inconsistência bancária — {status}", f"Conferir inconsistências bancárias e regularizar antes da 2ª corrida ({deadline:%d/%m/%Y})."),
        ("ajuste", f"Ajuste de contas — {status}", f"Verificar ajustes de contas pendentes da competência {competence} até a 2ª corrida ({deadline:%d/%m/%Y})."),
        ("bloqueios", f"Bloqueios de pagamento — {status}", f"Conferir bloqueios e desbloqueios de pagamento até a 2ª corrida ({deadline:%d/%m/%Y})."),
    ]
    out = [{"id": key, "deadline": deadline.strftime("%d/%m/%Y"), "type": typ, "detail": detail, "status": status, "manual": False} for key, typ, detail in automatic]
    raw = load_json(os.path.join(DATA_DIR, "pagamento_lembretes_dashboard.json"), [])
    items = raw.get("items", []) if isinstance(raw, dict) else raw if isinstance(raw, list) else []
    for index, item in enumerate(items):
        if not isinstance(item, dict): continue
        if norm(item.get("status")) in {"concluido", "ok", "feito"}: continue
        dt = parse_date_any(item.get("prazo"))
        item_deadline = dt.date() if dt else None
        d = (item_deadline - today).days if item_deadline else 999
        item_status = "ATRASADO" if d < 0 else "HOJE" if d == 0 else "URGENTE" if d <= 3 else "MANUAL"
        out.append({
            "id": str(item.get("id") or f"manual_{index}"), "deadline": item_deadline.strftime("%d/%m/%Y") if item_deadline else "—",
            "type": str(item.get("tipo") or item.get("titulo") or "Lembrete manual"),
            "detail": str(item.get("detalhe") or item.get("descricao") or ""), "status": item_status, "manual": True,
        })
    priority = {"ATRASADO": 0, "HOJE": 1, "URGENTE": 2, "PRAZO": 3, "MANUAL": 4}
    out.sort(key=lambda x: (priority.get(x["status"], 9), parse_date_any(x["deadline"]) or datetime(2999, 1, 1)))
    return out[:100]


def licensed_count(db_path: str) -> int:
    db_path = db_path or _discover_database_file(consolidate=True)
    if db_path and os.path.exists(db_path):
        try:
            with sqlite3.connect(db_path, timeout=10) as con:
                row = con.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='lt_militares'").fetchone()
                if row:
                    cols = {r[1] for r in con.execute("PRAGMA table_info(lt_militares)").fetchall()}
                    if "visivel" in cols:
                        return int(con.execute("SELECT COUNT(*) FROM lt_militares WHERE visivel IS NULL OR visivel=1").fetchone()[0])
                    return int(con.execute("SELECT COUNT(*) FROM lt_militares").fetchone()[0])
        except Exception:
            log("Falha na contagem direta de licenciados/transferidos:\n" + traceback.format_exc())
    try:
        module = _configure_database_module(import_first(("database.db", "db")), db_path)
        for name in ("lt_buscar_todos", "buscar_licenciados_transferidos", "buscar_todos_licenciados_transferidos", "listar_licenciados_transferidos"):
            fn = getattr(module, name, None)
            if callable(fn):
                return len(list(fn() or []))
    except Exception:
        pass
    return 0

def file_size_text(path: str) -> str:
    try:
        size = os.path.getsize(path)
        for unit in ("B", "KB", "MB", "GB"):
            if size < 1024 or unit == "GB":
                return f"{size:.1f} {unit}" if unit != "B" else f"{size} B"
            size /= 1024
    except Exception:
        return "—"
    return "—"


def backup_info() -> tuple[str, int]:
    directory = os.path.join(DATA_DIR, "backups")
    try:
        files = sorted(Path(directory).glob("*.zip"), key=lambda p: p.stat().st_mtime, reverse=True)
        return (datetime.fromtimestamp(files[0].stat().st_mtime).strftime("%d/%m/%Y %H:%M") if files else "—", len(files))
    except Exception:
        return "—", 0


def dashboard_snapshot() -> dict:
    rows = _buscar_militares()
    military = [military_item(row) for row in rows]
    missing = [military_item(row) for row in rows if not receives_transport(row)]
    missing.sort(key=lambda x: (RANK_ORDER.index(x["rank"]) if x["rank"] in RANK_ORDER else 999, norm(x["name"])))
    reminders, reminder_counts = reminders_snapshot()
    db_path = _db_path()
    last_backup, backup_count = backup_info()
    try:
        version_module = importlib.import_module("version")
        version = str(getattr(version_module, "APP_VERSION_DISPLAY", None) or f"v{getattr(version_module, 'APP_VERSION', '5.0.18')}")
    except Exception:
        version = "v5.0.18"
    return {
        "active_military_count": len(rows), "licensed_transferred_count": licensed_count(db_path),
        "reminder_total": reminder_counts["total"], "reminder_overdue": reminder_counts["overdue"],
        "reminder_today": reminder_counts["today"], "reminder_upcoming": reminder_counts["upcoming"],
        "database_path": db_path or "—", "database_size": file_size_text(db_path),
        "database_status": f"{len(rows)} militar(es) lido(s) diretamente do SQLite" if os.path.exists(db_path) else "Banco não encontrado",
        "database_candidates": [{"path": p, "count": _sqlite_military_count(p)} for p in _database_candidates() if os.path.exists(p)],
        "last_backup": last_backup, "backup_count": backup_count, "version": version,
        "reminders": reminders, "military": military, "missing_transport_aid": missing,
        "bulletins": bulletin_snapshot(), "financial_alerts": financial_alerts_snapshot(),
        "birthdays": birthdays_snapshot(rows), "rank_summary": rank_snapshot(rows),
    }


def _contracheque_module():
    # Usa primeiro a versão enviada junto com o WPF. Assim a Central de
    # Contracheques não depende de uma cópia antiga existente no projeto Python.
    module_name = "sigfur_wpf_contracheque_manager"
    cached = sys.modules.get(module_name)
    if cached is not None:
        return cached
    local_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "contracheque_manager.py")
    if os.path.isfile(local_path):
        spec = importlib_util.spec_from_file_location(module_name, local_path)
        if spec is None or spec.loader is None:
            raise ImportError(f"Não foi possível carregar {local_path}")
        module = importlib_util.module_from_spec(spec)
        sys.modules[module_name] = module
        spec.loader.exec_module(module)
        return module
    return import_first(("contracheque_manager", "interface.contracheque_manager"))


def _contracheque_mref(module, data: dict):
    cls = getattr(module, "MilitarRef")
    return cls(
        pg=str(data.get("pg") or ""),
        nome=str(data.get("nome") or ""),
        cpf=str(data.get("cpf") or ""),
        idt=str(data.get("idt") or ""),
        prec=str(data.get("prec") or ""),
        id_militar=data.get("id_militar") or "",
        nome_guerra=str(data.get("nome_guerra") or ""),
        banco=str(data.get("banco") or ""),
        agencia=str(data.get("agencia") or ""),
        conta=str(data.get("conta") or ""),
    )


def _schedule_contracheque_window(action_id: str, callback):
    current = OPEN_WINDOWS.get(action_id)
    if _focus_window(current):
        return {"focused": True, "action_id": action_id}
    if action_id in OPENING_ACTIONS:
        bring_children_front()
        return {"already_opening": True, "action_id": action_id}
    OPENING_ACTIONS.add(action_id)

    def _open():
        before = set(ROOT.winfo_children()) if ROOT is not None else set()
        result = callback()
        _remember_action_window(action_id, before, result)
        if ROOT is not None:
            ROOT.after(120, bring_children_front)

    if ROOT is None:
        _run_ui_safely(action_id, _open)
    else:
        ROOT.after_idle(lambda: _run_ui_safely(action_id, _open))
    return {"scheduled": True, "action_id": action_id}


def open_paystub_manager(args: dict):
    module = _contracheque_module()
    fn = getattr(module, "abrir_janela_contracheque")
    mref = _contracheque_mref(module, dict(args.get("military") or {}))
    return _schedule_contracheque_window(
        f"paystub_manager_{mref.id_militar or mref.cpf}",
        lambda: fn(ROOT, mref, modal=False),
    )


def open_paystub_batch(args: dict):
    module = _contracheque_module()
    fn = getattr(module, "abrir_janela_lote_contracheques")
    refs = [_contracheque_mref(module, dict(item or {})) for item in (args.get("military") or [])]
    return _schedule_contracheque_window("paystub_batch", lambda: fn(ROOT, refs))


def open_paystub_audit(args: dict):
    module = _contracheque_module()
    fn = getattr(module, "abrir_janela_auditoria_contracheques")
    refs = [_contracheque_mref(module, dict(item or {})) for item in (args.get("military") or [])]
    return _schedule_contracheque_window("paystub_audit", lambda: fn(ROOT, refs))


def _find_tk_button_by_text(widget, words: tuple[str, ...]):
    try:
        children = list(widget.winfo_children())
    except Exception:
        children = []
    for child in children:
        try:
            text = str(child.cget("text") or "").lower()
            if all(word in text for word in words) and callable(getattr(child, "invoke", None)):
                return child
        except Exception:
            pass
        found = _find_tk_button_by_text(child, words)
        if found is not None:
            return found
    return None


def open_external_paystub():
    """Abre diretamente o fluxo SIPPES/SIAPPES de pessoas fora da relação.

    A rotina original está dentro da janela Listar Militares; por compatibilidade,
    abrimos a listagem Python, acionamos o botão correto e ocultamos a listagem.
    """
    module = import_first(("interface.listar_militares", "listar_militares"))
    fn = callable_first(module, ("abrir_listagem",))

    def _open():
        before = set(ROOT.winfo_children()) if ROOT is not None else set()
        result = invoke_ui(fn, "listar")
        list_window = result if _window_exists(result) else None
        if list_window is None and ROOT is not None:
            try:
                created = [w for w in ROOT.winfo_children() if w not in before and isinstance(w, tk.Toplevel) and _window_exists(w)]
                list_window = created[-1] if created else None
            except Exception:
                list_window = None

        def _invoke(attempt=0):
            root = list_window or ROOT
            button = _find_tk_button_by_text(root, ("contracheque", "pessoas", "fora")) if root is not None else None
            if button is not None:
                try:
                    button.invoke()
                    if _window_exists(list_window):
                        list_window.withdraw()
                    bring_children_front()
                    return
                except Exception:
                    log("Falha acionando pessoas fora:\n" + traceback.format_exc())
            if ROOT is not None and attempt < 20:
                ROOT.after(180, lambda: _invoke(attempt + 1))
            else:
                log("Botão 'Contracheque pessoas fora' não localizado na listagem Python.")

        if ROOT is not None:
            ROOT.after(350, _invoke)
        else:
            _invoke()

    return _schedule_contracheque_window("external_paystub", _open)



def _cpex_paystub_module():
    module_name = "sigfur_wpf_cpex_paystub_automation"
    cached = sys.modules.get(module_name)
    if cached is not None:
        return cached
    local_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "cpex_paystub_automation.py")
    if not os.path.isfile(local_path):
        raise FileNotFoundError(f"Automação CPEx não encontrada: {local_path}")
    spec = importlib_util.spec_from_file_location(module_name, local_path)
    if spec is None or spec.loader is None:
        raise ImportError(f"Não foi possível carregar {local_path}")
    module = importlib_util.module_from_spec(spec)
    sys.modules[module_name] = module
    spec.loader.exec_module(module)
    return module


def sippes_credentials_status():
    module = _contracheque_module()
    creds = dict(getattr(module, "_sippes_load_credentials")() or {})
    return {
        "usuario": str(creds.get("usuario") or ""),
        "has_password": bool(creds.get("senha")),
        "storage": "DPAPI do usuário do Windows" if os.name == "nt" else "proteção local",
    }


def sippes_save_credentials(args: dict):
    usuario = str(args.get("usuario") or "").strip()
    senha = str(args.get("senha") or "")
    if not usuario or not senha:
        raise ValueError("Informe usuário/CPF e senha.")
    module = _contracheque_module()
    getattr(module, "_sippes_save_credentials")(usuario, senha)
    return {"saved": True, "usuario": usuario}


def sippes_clear_credentials():
    module = _contracheque_module()
    getattr(module, "_sippes_clear_credentials")()
    return {"cleared": True}


def _cpex_credentials_path() -> Path:
    return Path(DATA_DIR) / "cpex_area_ua_credenciais.dat"


def _cpex_obfuscation_key() -> bytes:
    import hashlib
    seed = "|".join([
        os.environ.get("USERNAME", ""), os.environ.get("USERDOMAIN", ""),
        os.environ.get("COMPUTERNAME", ""), str(Path.home()), "SIGFUR_CPEX_AREA_UA_V1",
    ])
    return hashlib.sha256(seed.encode("utf-8", errors="ignore")).digest()


def _cpex_load_credentials() -> dict:
    import base64
    path = _cpex_credentials_path()
    if not path.exists():
        return {}
    try:
        module = _contracheque_module()
        data = json.loads(path.read_text(encoding="utf-8"))
        raw = base64.b64decode(data.get("payload") or "")
        if str(data.get("modo") or "").lower() == "dpapi":
            plain = getattr(module, "_dpapi_unprotect")(raw)
            if plain is None:
                return {}
        else:
            plain = getattr(module, "_xor_bytes")(raw, _cpex_obfuscation_key())
        payload = json.loads(plain.decode("utf-8", errors="ignore"))
        return {"usuario": str(payload.get("usuario") or "").strip(), "senha": str(payload.get("senha") or "")}
    except Exception:
        log("Falha lendo credencial da Área Exclusiva:\n" + traceback.format_exc())
        return {}


def cpex_credentials_status():
    creds = _cpex_load_credentials()
    return {
        "usuario": str(creds.get("usuario") or ""),
        "has_password": bool(creds.get("senha")),
        "storage": "DPAPI do usuário do Windows" if os.name == "nt" else "proteção local",
    }


def cpex_save_credentials(args: dict):
    import base64
    usuario = str(args.get("usuario") or "").strip()
    senha = str(args.get("senha") or "")
    if not usuario or not senha:
        raise ValueError("Informe usuário/CPF e senha da Área Exclusiva da UA.")
    module = _contracheque_module()
    payload = json.dumps({"usuario": usuario, "senha": senha}, ensure_ascii=False).encode("utf-8")
    protected = getattr(module, "_dpapi_protect")(payload)
    if protected is not None:
        data = {"modo": "dpapi", "payload": base64.b64encode(protected).decode("ascii")}
    else:
        encoded = getattr(module, "_xor_bytes")(payload, _cpex_obfuscation_key())
        data = {"modo": "ofuscado", "payload": base64.b64encode(encoded).decode("ascii")}
    path = _cpex_credentials_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    return {"saved": True, "usuario": usuario}


def cpex_clear_credentials():
    try:
        _cpex_credentials_path().unlink(missing_ok=True)
    except Exception:
        pass
    return {"cleared": True}


def _selenium_status() -> dict:
    try:
        import selenium
        return {
            "selenium_ok": True,
            "selenium_version": str(getattr(selenium, "__version__", "instalado")),
            "selenium_error": "",
            "python_executable": sys.executable,
        }
    except Exception as exc:
        return {
            "selenium_ok": False,
            "selenium_version": "",
            "selenium_error": str(exc),
            "python_executable": sys.executable,
        }


def _pdf_reader_status() -> dict:
    for module_name in ("fitz", "pypdf", "PyPDF2"):
        try:
            module = importlib.import_module(module_name)
            return {"pdf_reader_ok": True, "pdf_reader": module_name, "pdf_reader_error": ""}
        except Exception:
            pass
    return {"pdf_reader_ok": False, "pdf_reader": "", "pdf_reader_error": "PyMuPDF/pypdf não instalado"}


def ensure_paystub_dependencies(args: dict):
    """Instala Selenium e leitores de PDF no MESMO Python da ponte WPF.

    Isso evita instalações em outro interpretador e garante tanto a automação
    web quanto a auditoria nativa dos PDFs de contracheque.
    """
    status = {**_selenium_status(), **_pdf_reader_status()}
    force = bool(args.get("force", False))
    install = bool(args.get("install", False))
    if status["selenium_ok"] and status["pdf_reader_ok"] and not force:
        return {**status, "installed": False, "message": f"Automação pronta: Selenium {status['selenium_version']} e leitor PDF {status['pdf_reader']}."}
    if not install:
        return {**status, "installed": False, "message": "Selenium não está instalado neste Python."}

    commands = [
        [sys.executable, "-m", "ensurepip", "--upgrade"],
        [sys.executable, "-m", "pip", "install", "--disable-pip-version-check", "--upgrade", "pip"],
        [sys.executable, "-m", "pip", "install", "--disable-pip-version-check", "--upgrade", "selenium", "pypdf", "pymupdf"],
    ]
    outputs = []
    for index, command in enumerate(commands):
        try:
            cp = subprocess.run(
                command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, encoding="utf-8", errors="replace", timeout=600,
            )
            outputs.append((cp.stdout or "").strip())
            # ensurepip pode falhar quando o pip já existe; isso não impede as próximas etapas.
            if cp.returncode != 0 and index > 0:
                raise RuntimeError((cp.stdout or "").strip() or f"Comando retornou código {cp.returncode}.")
        except subprocess.TimeoutExpired as exc:
            raise RuntimeError("A preparação da automação excedeu 10 minutos. Verifique a internet e tente novamente.") from exc

    importlib.invalidate_caches()
    # Remove eventual import quebrado/parcial antes de testar novamente.
    sys.modules.pop("selenium", None)
    status = {**_selenium_status(), **_pdf_reader_status()}
    if not status["selenium_ok"] or not status["pdf_reader_ok"]:
        detalhe = "\n".join(x for x in outputs[-2:] if x)[-5000:]
        raise RuntimeError(
            "O pip terminou, mas a automação ainda não pôde ser carregada neste Python.\n\n"
            f"Python: {sys.executable}\n{status['selenium_error']}\n\n{detalhe}"
        )
    return {
        **status,
        "installed": True,
        "message": f"Automação preparada: Selenium {status['selenium_version']} e leitor PDF {status['pdf_reader']}.",
        "details": "\n".join(x for x in outputs[-2:] if x)[-5000:],
    }


def paystub_automation_status():
    status = {**_selenium_status(), **_pdf_reader_status()}
    creds = sippes_credentials_status()
    cpex_creds = cpex_credentials_status()
    return {
        **status,
        "credentials_saved": bool(creds.get("has_password")),
        "usuario": creds.get("usuario", ""),
        "cpex_credentials_saved": bool(cpex_creds.get("has_password")),
        "cpex_usuario": cpex_creds.get("usuario", ""),
        "message": (
            f"Selenium {status['selenium_version']} disponível no Python do SIGFUR."
            if status["selenium_ok"] else
            "Selenium não está instalado no Python usado pelo SIGFUR."
        ),
    }


_SIPPES_SESSION_READY_AT = 0.0
_SIPPES_SESSION_LAST_USED_AT = 0.0


def _sippes_session_is_ready(module, driver) -> bool:
    """Confere a sessão já aberta sem refazer login ou recriar o navegador."""
    global _SIPPES_SESSION_READY_AT, _SIPPES_SESSION_LAST_USED_AT
    try:
        if not getattr(module, "_selenium_driver_alive")(driver):
            return False
        ready = bool(getattr(module, "_selenium_tem_funcao_selecionar_favorecido")(driver))
        if ready:
            now = time.time()
            if not _SIPPES_SESSION_READY_AT:
                _SIPPES_SESSION_READY_AT = now
            _SIPPES_SESSION_LAST_USED_AT = now
        return ready
    except Exception:
        return False


def _ensure_sippes_session(module, driver, label: str = "SIPPES") -> bool:
    """Reutiliza a sessão enquanto válida e só renova quando o site expirar."""
    global _SIPPES_SESSION_READY_AT, _SIPPES_SESSION_LAST_USED_AT
    if _sippes_session_is_ready(module, driver):
        log(f"[{label}] Sessão já pronta; reutilizando o mesmo navegador oculto.")
        return True
    log(f"[{label}] Sessão ausente ou expirada; tentando renovar o login automaticamente.")
    ready = bool(getattr(module, "_preparar_fluxo_autenticado_sippes")(
        driver, status_cb=lambda text: log(f"[{label}] " + str(text)), tentar_login=True,
    ))
    if ready and _sippes_session_is_ready(module, driver):
        now = time.time()
        _SIPPES_SESSION_READY_AT = now
        _SIPPES_SESSION_LAST_USED_AT = now
        return True
    return False


def _sippes_driver_and_module(*, visible: bool = False):
    module = _contracheque_module()
    os.environ["SIGFUR_SIPPES_HEADLESS"] = "0" if visible else "1"
    temp = Path(getattr(module, "_default_temp")())
    temp.mkdir(parents=True, exist_ok=True)
    driver, reused = getattr(module, "_get_reusable_sippes_driver")(temp)
    if visible:
        getattr(module, "_selenium_restore_window")(driver)
    return module, driver, reused, temp


def _close_sippes_driver(module=None):
    global _SIPPES_SESSION_READY_AT, _SIPPES_SESSION_LAST_USED_AT
    _SIPPES_SESSION_READY_AT = 0.0
    _SIPPES_SESSION_LAST_USED_AT = 0.0
    module = module or _contracheque_module()
    close = getattr(module, "_close_reusable_sippes_driver", None)
    if callable(close):
        close()
        return
    driver = getattr(module, "_SIPPES_DRIVER", None)
    if driver is not None:
        try:
            driver.quit()
        except Exception:
            pass
        try:
            setattr(module, "_SIPPES_DRIVER", None)
        except Exception:
            pass


def sippes_open_login():
    module, driver, reused, _temp = _sippes_driver_and_module(visible=True)
    login_url = str(getattr(module, "_sippes_tela_consultar_url")())
    try:
        if _sippes_session_is_ready(module, driver):
            return {"opened": True, "ready": True, "reused": reused, "message": "Sessão já estava pronta e continua reutilizável."}
    except Exception:
        pass
    driver.get(login_url)
    return {
        "opened": True,
        "ready": False,
        "reused": reused,
        "message": "Navegador aberto. Faça o login e depois clique em Confirmar sessão.",
    }


def sippes_confirm_session():
    # A preparação padrão usa um único navegador oculto e o mantém vivo enquanto
    # a ponte do SIGFUR estiver aberta. Novos downloads não repetem o login.
    module, driver, reused, _temp = _sippes_driver_and_module(visible=False)
    with getattr(module, "_SIPPES_ACTION_LOCK"):
        ready = _ensure_sippes_session(module, driver, "SIPPES")
    if not ready:
        raise RuntimeError(
            "A sessão do SIPPES não chegou à tela de seleção de favorecido. "
            "Confira a credencial salva; quando o site expirar, o SIGFUR tentará renovar automaticamente."
        )
    return {
        "ready": True, "reused": reused,
        "message": (
            "Sessão SIPPES pronta e mantida no mesmo navegador oculto. "
            "Ela será reutilizada até expirar; depois o SIGFUR tentará renovar o login automaticamente."
        )
    }


def _sippes_output_path(module, root: Path, mref, year: int, month: int) -> Path:
    folder = Path(getattr(module, "_militar_folder")(root, mref))
    folder.mkdir(parents=True, exist_ok=True)
    return folder / str(getattr(module, "_format_filename_contracheque")(year, month))


def _sippes_code(module, args: dict, year: int, month: int) -> str:
    code = re.sub(r"\D+", "", str(args.get("sheet_code") or ""))
    if code:
        return code
    return str(getattr(module, "_codigo_folha_sippes")(year, month))


def sippes_download_one(args: dict):
    module = _contracheque_module()
    mref = _contracheque_mref(module, dict(args.get("military") or {}))
    year = int(args.get("year") or datetime.now().year)
    month = int(args.get("month") or datetime.now().month)
    if month < 1 or month > 12:
        raise ValueError("Mês inválido.")
    code = _sippes_code(module, args, year, month)
    root = Path(str(args.get("output_directory") or getattr(module, "_default_root")())).expanduser().resolve()
    root.mkdir(parents=True, exist_ok=True)
    dst = _sippes_output_path(module, root, mref, year, month)
    overwrite = bool(args.get("overwrite", True))
    if dst.exists() and not overwrite:
        return {"saved": [], "skipped": [str(dst)], "errors": [], "path": str(dst)}

    with getattr(module, "_SIPPES_ACTION_LOCK"):
        module, driver, _reused, temp = _sippes_driver_and_module(visible=False)
        downloads = Path(getattr(module, "_downloads_fallback")())
        # O próprio fluxo individual confere a sessão atual e só renova se o site
        # tiver expirado. Assim, preparar uma vez não custa outro login por militar.
        getattr(module, "_download_one_contracheque_sippes")(
            driver, mref, code, [temp, downloads], dst,
            status_cb=lambda text: log(f"[SIPPES {mref.nome}] {text}"),
        )
    return {"saved": [str(dst)], "skipped": [], "errors": [], "path": str(dst)}


def sippes_download_batch_native(args: dict):
    module = _contracheque_module()
    refs = [_contracheque_mref(module, dict(item or {})) for item in (args.get("military") or [])]
    if not refs:
        raise ValueError("Nenhum militar foi informado para o lote.")
    year = int(args.get("year") or datetime.now().year)
    month = int(args.get("month") or datetime.now().month)
    if month < 1 or month > 12:
        raise ValueError("Mês inválido.")
    code = _sippes_code(module, args, year, month)
    root = Path(str(args.get("output_directory") or getattr(module, "_default_root")())).expanduser().resolve()
    root.mkdir(parents=True, exist_ok=True)
    skip_existing = bool(args.get("skip_existing", True))
    saved, skipped, errors = [], [], []

    with getattr(module, "_SIPPES_ACTION_LOCK"):
        module, driver, _reused, temp = _sippes_driver_and_module(visible=False)
        downloads = Path(getattr(module, "_downloads_fallback")())
        try:
            ready = _ensure_sippes_session(module, driver, "SIPPES LOTE")
            if not ready:
                raise RuntimeError("Não foi possível preparar ou renovar a sessão do SIPPES com a credencial salva.")
            getattr(module, "_selenium_minimize_window")(driver)
            total = len(refs)
            for index, mref in enumerate(refs, start=1):
                dst = _sippes_output_path(module, root, mref, year, month)
                if skip_existing and dst.exists():
                    skipped.append(str(dst))
                    log(f"[SIPPES LOTE] {index}/{total} pulado: {mref.nome}")
                    continue
                try:
                    log(f"[SIPPES LOTE] {index}/{total}: {mref.nome}")
                    getattr(module, "_download_one_contracheque_sippes")(
                        driver, mref, code, [temp, downloads], dst,
                        status_cb=lambda text, name=mref.nome: log(f"[SIPPES {name}] {text}"),
                    )
                    saved.append(str(dst))
                except Exception as exc:
                    errors.append(f"{mref.pg} {mref.nome}: {exc}".strip())
        finally:
            try:
                getattr(module, "_selenium_minimize_window")(driver)
            except Exception:
                pass
    return {"saved": saved, "skipped": skipped, "errors": errors, "folder": str(root)}


def _unique_batch_folder(base: str, system: str, year: int, month: int, processing: str, payroll_type: str) -> str:
    root = os.path.abspath(str(base or os.path.join(DATA_DIR, "contracheques", "pessoas_fora")))
    os.makedirs(root, exist_ok=True)
    parts = ["Lote Contracheques", system, f"{year}-{month:02d}"]
    if system == "SIPPES":
        parts.extend([processing, payroll_type])
    parts.append(datetime.now().strftime("%d-%m-%Y %Hh%Mm%Ss"))
    name = " - ".join(re.sub(r'[\\/:*?"<>|]+', "_", str(x)).strip() for x in parts if str(x).strip())
    folder = os.path.join(root, name)
    suffix = 2
    original = folder
    while os.path.exists(folder):
        folder = f"{original} ({suffix})"
        suffix += 1
    os.makedirs(folder, exist_ok=True)
    return folder


def cpex_download_external(args: dict):
    system = str(args.get("system") or "SIPPES").upper()
    people = [dict(item or {}) for item in (args.get("people") or [])]
    year = int(args.get("year") or datetime.now().year)
    month = int(args.get("month") or datetime.now().month)
    processing = str(args.get("processing") or "Definitivo")
    payroll_type = str(args.get("payroll_type") or "Normal")
    browser = str(args.get("browser") or "Edge")
    usuario = str(args.get("usuario") or "").strip()
    senha = str(args.get("senha") or "")
    if not usuario or not senha:
        creds = _cpex_load_credentials()
        usuario = usuario or str(creds.get("usuario") or "")
        senha = senha or str(creds.get("senha") or "")
    if not usuario or not senha:
        raise RuntimeError("Salve o usuário/CPF e a senha da Área Exclusiva da UA antes de iniciar pessoas de fora.")
    # Atualiza a credencial separada quando o operador informou os campos na tela.
    if str(args.get("usuario") or "").strip() and str(args.get("senha") or ""):
        cpex_save_credentials({"usuario": usuario, "senha": senha})
    folder = _unique_batch_folder(str(args.get("output_directory") or ""), system, year, month, processing, payroll_type)
    module = _cpex_paystub_module()
    result = module.download_batch(
        system, people, folder, month, year, usuario, senha,
        processing, payroll_type, browser,
        progress=lambda text: log("[CPEX] " + str(text)),
    )
    return result

def _audit_public_row(row: dict) -> dict:
    """Remove objetos Python internos antes de enviar a linha ao WPF."""
    result = {}
    for key, value in dict(row or {}).items():
        if str(key).startswith("_"):
            continue
        if isinstance(value, Path):
            result[key] = str(value)
        elif isinstance(value, (str, int, float, bool)) or value is None:
            result[key] = value
        elif isinstance(value, (list, tuple)):
            result[key] = [str(x) for x in value]
        elif isinstance(value, dict):
            result[key] = {
                str(k): ([str(x) for x in v] if isinstance(v, (list, tuple)) else str(v))
                for k, v in value.items()
            }
        else:
            result[key] = str(value)
    return result


def paystub_audit_start(args: dict):
    import uuid
    rows_data = [dict(item or {}) for item in (args.get("military") or [])]
    if not rows_data:
        raise ValueError("Nenhum militar foi informado para a auditoria.")
    year = int(args.get("year") or datetime.now().year)
    month = int(args.get("month") or datetime.now().month)
    if month < 1 or month > 12:
        raise ValueError("Mês inválido.")
    module = _contracheque_module()
    root = Path(str(args.get("output_directory") or getattr(module, "_default_root")())).expanduser().resolve()
    refs = [_contracheque_mref(module, item) for item in rows_data]
    job_id = uuid.uuid4().hex
    stop_event = threading.Event()
    job = {
        "job_id": job_id, "state": "running", "done": False, "cancelled": False,
        "current": 0, "total": len(refs), "message": "Preparando auditoria…",
        "rows": [], "logs": [], "lidos": 0, "faltando": 0, "falhas": 0,
        "year": year, "month": month, "folder": str(root), "_stop": stop_event,
    }
    with _AUDIT_JOBS_LOCK:
        _AUDIT_JOBS[job_id] = job

    def append_log(text: str):
        with _AUDIT_JOBS_LOCK:
            job["logs"].append(str(text))
            job["logs"] = job["logs"][-300:]

    def worker():
        try:
            name_pdf = str(getattr(module, "_format_filename_contracheque")(year, month))
            for index, (mref, source_data) in enumerate(zip(refs, rows_data), start=1):
                if stop_event.is_set():
                    with _AUDIT_JOBS_LOCK:
                        job["cancelled"] = True
                        job["state"] = "cancelled"
                        job["message"] = "Auditoria interrompida pelo usuário."
                    break
                display = f"{mref.pg} {mref.nome}".strip()
                with _AUDIT_JOBS_LOCK:
                    job["message"] = f"Conferindo {index}/{len(refs)}: {display}"
                pdf_path = Path(getattr(module, "_militar_folder")(root, mref)) / name_pdf
                base_row = {
                    "militar": display, "pg": mref.pg, "nome": mref.nome,
                    "nome_guerra": getattr(mref, "nome_guerra", ""), "cpf": mref.cpf,
                    "idt": mref.idt, "prec": mref.prec, "id_militar": getattr(mref, "id_militar", ""),
                    "banco_db": getattr(mref, "banco", ""), "agencia_db": getattr(mref, "agencia", ""),
                    "conta_db": getattr(mref, "conta", ""), "pdf_path": str(pdf_path),
                }
                try:
                    if not pdf_path.exists():
                        row = {**base_row, "pdf": "NÃO ENCONTRADO", "pdf_ok": False,
                               "situacao": "Contracheque não salvo para este mês"}
                        with _AUDIT_JOBS_LOCK:
                            job["faltando"] += 1
                    else:
                        text = getattr(module, "_extract_pdf_text")(pdf_path)
                        data = dict(getattr(module, "_analisar_texto_contracheque")(text, mref) or {})
                        # No WPF o valor cadastrado de AT já vem do SQLite nativo. Usa-o
                        # como referência quando o módulo Python não consegue importar db.py.
                        raw_expected = str(source_data.get("valor_aux_transporte") or "").strip()
                        expected = None
                        if raw_expected:
                            try:
                                clean = raw_expected.replace("R$", "").replace(" ", "")
                                if "," in clean:
                                    clean = clean.replace(".", "").replace(",", ".")
                                expected = float(clean)
                            except Exception:
                                expected = None
                        receives = str(source_data.get("recebe_aux_transporte") or "").strip().lower() in ("sim", "s", "1", "yes", "true")
                        if expected is not None and (receives or expected > 0):
                            aux_pdf = float(data.get("aux_pdf") or 0.0)
                            diff = aux_pdf - expected
                            if aux_pdf > 0:
                                aux_status = "OK" if abs(diff) <= 1.0 else "DIVERGENTE"
                            else:
                                aux_status = "NÃO RECEBEU"
                            if float(data.get("aux_dr") or 0.0) > 0:
                                aux_status += " + DESC DR"
                            data["aux_db"] = expected
                            data["aux_diff"] = diff
                            data["aux_status"] = aux_status
                            situation = str(data.get("situacao") or "")
                            alert = "Aux transporte diferente" if "DIVERGENTE" in aux_status else (
                                "Aux transporte previsto no cadastro, mas não localizado" if "NÃO RECEBEU" in aux_status else ""
                            )
                            if alert and alert not in situation:
                                data["situacao"] = "; ".join(x for x in (situation if situation != "Sem achado relevante" else "", alert) if x)
                        row = {**base_row, "pdf": pdf_path.name, "pdf_ok": True, **data}
                        with _AUDIT_JOBS_LOCK:
                            job["lidos"] += 1
                        if str(row.get("situacao") or "") not in ("", "Sem achado relevante"):
                            append_log(f"⚠ {display}: {row.get('situacao')}")
                except Exception as exc:
                    row = {**base_row, "pdf": pdf_path.name if pdf_path.exists() else "NÃO ENCONTRADO",
                           "pdf_ok": pdf_path.exists(), "situacao": f"Erro ao ler contracheque: {exc}"}
                    with _AUDIT_JOBS_LOCK:
                        job["falhas"] += 1
                    append_log(f"❌ {display}: {exc}")
                public = _audit_public_row(row)
                with _AUDIT_JOBS_LOCK:
                    job["rows"].append(public)
                    job["current"] = index
            with _AUDIT_JOBS_LOCK:
                if job["state"] == "running":
                    job["state"] = "completed"
                    job["message"] = (
                        f"Auditoria finalizada. Lidos: {job['lidos']} | "
                        f"Sem contracheque: {job['faltando']} | Falhas: {job['falhas']}"
                    )
        except Exception as exc:
            with _AUDIT_JOBS_LOCK:
                job["state"] = "failed"
                job["message"] = f"Falha na auditoria: {exc}"
                job["error"] = f"{type(exc).__name__}: {exc}"
            append_log(traceback.format_exc())
        finally:
            with _AUDIT_JOBS_LOCK:
                job["done"] = True

    threading.Thread(target=worker, daemon=True, name=f"paystub-audit-{job_id[:8]}").start()
    return {"job_id": job_id, "total": len(refs), "message": "Auditoria iniciada."}


def paystub_audit_status(args: dict):
    job_id = str(args.get("job_id") or "")
    with _AUDIT_JOBS_LOCK:
        job = _AUDIT_JOBS.get(job_id)
        if not job:
            raise KeyError("Auditoria não encontrada ou já encerrada.")
        return {key: value for key, value in job.items() if not str(key).startswith("_")}


def paystub_audit_cancel(args: dict):
    job_id = str(args.get("job_id") or "")
    with _AUDIT_JOBS_LOCK:
        job = _AUDIT_JOBS.get(job_id)
        if not job:
            return {"cancelled": False}
        stop = job.get("_stop")
        if isinstance(stop, threading.Event):
            stop.set()
        job["message"] = "Parada solicitada…"
        return {"cancelled": True}


def sisbol_module():
    return import_first(("sigfur_sisbol_controlado", "interface.sigfur_sisbol_controlado"))


def sisbol_state():
    try:
        state = sisbol_module().estado_sessao()
        return dict(state or {})
    except Exception:
        return {"pronto": False, "vivo": False, "navegador": ""}


def sisbol_prepare():
    module = sisbol_module()
    fn = getattr(module, "preparar_interativo")
    return {"prepared": bool(fn(ROOT))}


def sisbol_send_adjustment_accounts(args: dict):
    text = str(args.get("text") or "").strip()
    if not text:
        raise ValueError("O texto do boletim está vazio.")

    subject = str(args.get("subject") or "Ajuste de Contas").strip() or "Ajuste de Contas"
    specific_code = str(args.get("specific_code") or "").strip()
    war_names = [str(value or "").strip() for value in (args.get("war_names") or [])]
    war_names = sorted({value for value in war_names if value}, key=len, reverse=True)

    module = import_first(("interface.ajuste_contas", "ajuste_contas"))
    send = getattr(module, "_preencher_sisbol_boletim", None)
    if not callable(send):
        raise AttributeError("A integração do Ajuste de Contas com o SisBol não foi encontrada no módulo Python.")
    if ROOT is None:
        raise RuntimeError("A janela de compatibilidade do SIGFUR não está disponível.")

    editor = tk.Text(ROOT)
    editor.insert("1.0", text)
    try:
        editor.tag_configure("ng_bold")
        for war_name in war_names:
            start = "1.0"
            while True:
                index = editor.search(war_name, start, stopindex="end", nocase=True)
                if not index:
                    break
                end = f"{index}+{len(war_name)}c"
                editor.tag_add("ng_bold", index, end)
                start = end

        send(
            editor,
            subject,
            parent=ROOT,
            codigo_especifico=specific_code,
            parte_bi="3ª Parte",
            secao_bi="OUTROS ASSUNTOS",
            assunto_geral_codigo="1077",
            assunto_geral_nome="PAGAMENTO PESSOAL",
        )
        return {"processed": True, "subject": subject, "specific_code": specific_code}
    finally:
        try:
            editor.destroy()
        except Exception:
            pass


def start_updater():
    def worker():
        try:
            module = importlib.import_module("atualizador")
            fn = getattr(module, "verificar_atualizacao")
            fn(automatico=True, modo_silencioso=True)
        except Exception:
            log("Atualizador: " + traceback.format_exc())
    threading.Thread(target=worker, daemon=True).start()
    return {"started": True}


def shutdown():
    global SHUTTING_DOWN
    SHUTTING_DOWN = True
    try:
        _close_sippes_driver()
    except Exception:
        pass
    try:
        with _AUDIT_JOBS_LOCK:
            for job in _AUDIT_JOBS.values():
                stop = job.get("_stop")
                if isinstance(stop, threading.Event):
                    stop.set()
    except Exception:
        pass
    try:
        module = sisbol_module()
        close = getattr(module, "fechar", None)
        if callable(close):
            try: close(rapido=True)
            except TypeError: close()
    except Exception:
        pass
    if ROOT is not None:
        ROOT.after(50, ROOT.destroy)
    return {"shutdown": True}


COMMANDS = {
    "ping": lambda args: {"pong": True, "project_root": PROJECT_ROOT, "data_dir": DATA_DIR},
    "init_database": lambda args: init_database(),
    "dashboard_snapshot": lambda args: dashboard_snapshot(),
    "open_action": lambda args: open_action(str(args.get("action_id") or "")),
    "open_military_wallet": lambda args: open_military_wallet(_to_int(args.get("military_id"))),
    "open_paystub_manager": lambda args: open_paystub_manager(args),
    "open_paystub_batch": lambda args: open_paystub_batch(args),
    "open_paystub_audit": lambda args: open_paystub_audit(args),
    "open_external_paystub": lambda args: open_external_paystub(),
    "sippes_credentials_status": lambda args: sippes_credentials_status(),
    "sippes_save_credentials": lambda args: sippes_save_credentials(args),
    "sippes_clear_credentials": lambda args: sippes_clear_credentials(),
    "cpex_credentials_status": lambda args: cpex_credentials_status(),
    "cpex_save_credentials": lambda args: cpex_save_credentials(args),
    "cpex_clear_credentials": lambda args: cpex_clear_credentials(),
    "paystub_automation_status": lambda args: paystub_automation_status(),
    "ensure_paystub_dependencies": lambda args: ensure_paystub_dependencies(args),
    "sippes_open_login": lambda args: sippes_open_login(),
    "sippes_confirm_session": lambda args: sippes_confirm_session(),
    "sippes_download_one": lambda args: sippes_download_one(args),
    "sippes_download_batch_native": lambda args: sippes_download_batch_native(args),
    "cpex_download_external": lambda args: cpex_download_external(args),
    "paystub_audit_start": lambda args: paystub_audit_start(args),
    "paystub_audit_status": lambda args: paystub_audit_status(args),
    "paystub_audit_cancel": lambda args: paystub_audit_cancel(args),
    "sisbol_state": lambda args: sisbol_state(),
    "sisbol_prepare": lambda args: sisbol_prepare(),
    "sisbol_send_adjustment_accounts": lambda args: sisbol_send_adjustment_accounts(args),
    "start_updater": lambda args: start_updater(),
    "shutdown": lambda args: shutdown(),
}


ASYNC_COMMANDS = {
    "ensure_paystub_dependencies", "sippes_open_login", "sippes_confirm_session", "sippes_download_one",
    "sippes_download_batch_native", "cpex_download_external", "paystub_audit_start",
}


def _execute_request(req_id: str, command: str, args: dict):
    try:
        fn = COMMANDS.get(command)
        if fn is None:
            raise KeyError(f"Comando desconhecido: {command}")
        result = fn(args)
        respond(req_id, True, result=result)
    except Exception as exc:
        log(f"Falha no comando {command}:\n{traceback.format_exc()}")
        respond(req_id, False, error=f"{type(exc).__name__}: {exc}")


def handle_request(request: dict):
    req_id = str(request.get("id") or "")
    command = str(request.get("command") or "")
    args = request.get("args") if isinstance(request.get("args"), dict) else {}
    if command in ASYNC_COMMANDS:
        threading.Thread(target=_execute_request, args=(req_id, command, args), daemon=True).start()
        return
    _execute_request(req_id, command, args)


def reader_loop():
    for line in sys.stdin:
        if SHUTTING_DOWN:
            break
        try:
            clean_line = line.lstrip("\ufeff").strip()
            if not clean_line:
                continue
            request = json.loads(clean_line)
            if isinstance(request, dict):
                REQUESTS.put(request)
        except Exception:
            log("JSON inválido recebido: " + line[:500])


def poll_requests():
    if ROOT is None:
        return
    try:
        for _ in range(12):
            try:
                request = REQUESTS.get_nowait()
            except queue.Empty:
                break
            handle_request(request)
    finally:
        if not SHUTTING_DOWN:
            ROOT.after(30, poll_requests)


def main():
    if not ARGS.server:
        print(json.dumps(dashboard_snapshot(), ensure_ascii=False, default=_json_default), file=_PROTOCOL_OUT)
        return
    if ROOT is None:
        respond("startup", False, error="Tkinter não pôde ser inicializado.")
        return
    threading.Thread(target=reader_loop, daemon=True).start()
    ROOT.after(30, poll_requests)
    log(f"Ponte iniciada. project_root={PROJECT_ROOT} data_dir={DATA_DIR}")
    ROOT.mainloop()
    log("Ponte encerrada.")


if __name__ == "__main__":
    main()
