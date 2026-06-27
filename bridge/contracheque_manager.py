from __future__ import annotations

import os
import re
import shutil
import subprocess
import threading
import time
import unicodedata
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from tkinter import Toplevel, Frame, Label, Button, Listbox, Scrollbar, StringVar, BooleanVar, Menu, Text, ttk, messagebox


@dataclass(frozen=True)
class MilitarRef:
    pg: str
    nome: str
    cpf: str
    idt: str = ""
    prec: str = ""
    id_militar: str | int = ""
    nome_guerra: str = ""
    banco: str = ""
    agencia: str = ""
    conta: str = ""


_PT_MONTHS = {
    1: "JANEIRO", 2: "FEVEREIRO", 3: "MARÇO", 4: "ABRIL",
    5: "MAIO", 6: "JUNHO", 7: "JULHO", 8: "AGOSTO",
    9: "SETEMBRO", 10: "OUTUBRO", 11: "NOVEMBRO", 12: "DEZEMBRO",
}
_MONTH_TO_NUM = {v: k for k, v in _PT_MONTHS.items()}
_MONTH_NAMES = [v for k, v in sorted(_PT_MONTHS.items(), key=lambda x: x[0])]


def _strip_accents(s: str) -> str:
    s = unicodedata.normalize("NFD", s or "")
    return "".join(ch for ch in s if unicodedata.category(ch) != "Mn")


def _safe_slug(s: str) -> str:
    s = _strip_accents(s or "").strip()
    s = re.sub(r"\s+", "_", s)
    s = re.sub(r"[^A-Za-z0-9_.-]+", "", s)
    return s or "MILITAR"


def _clean_filename_text(s: str) -> str:
    s = (s or "").strip()
    s = re.sub(r'[<>:"/\\|?*]+', " ", s)
    s = re.sub(r"\s+", " ", s).strip()
    return s or "MILITAR"


def _cpf_digits(cpf: str) -> str:
    return re.sub(r"\D+", "", cpf or "")


def _only_digits(value: str) -> str:
    return re.sub(r"\D+", "", value or "")


def _idt_digits(idt: str) -> str:
    return _only_digits(idt)


def _prec_digits(prec: str) -> str:
    return _only_digits(prec)


def _codigo_folha_sippes(year: int, month: int) -> int:
    """Código observado: ABR/2026 = 4178, variando 20 por mês."""
    try:
        y = int(year)
        m = int(month)
    except Exception:
        now = datetime.now()
        y, m = now.year, now.month
    delta_meses = (y - 2026) * 12 + (m - 4)
    return 4178 + (delta_meses * 20)


def _sippes_consulta_url() -> str:
    # Tela que carrega a função selecionarFavorecido(...).
    # Se a sessão do SIPPES estiver ativa, este link abre direto.
    # Se a sessão expirou, o usuário loga uma vez e o programa volta para este link.
    return _sippes_url()


def _sippes_tela_consultar_url() -> str:
    return "https://sippes.eb.mil.br/consultarContracheque.do?metodo=exibirTelaConsultar&limparSessao=true"


def _sigfur_data_dir() -> Path:
    base = os.environ.get("ESCALA_DATA_DIR") or os.path.join(os.environ.get("LOCALAPPDATA", os.getcwd()), "SIGFUR")
    p = Path(base)
    p.mkdir(parents=True, exist_ok=True)
    return p


def _sippes_credentials_path() -> Path:
    return _sigfur_data_dir() / "sippes_credenciais.dat"


def _sippes_obfuscation_key() -> bytes:
    import hashlib
    seed = "|".join([os.environ.get("USERNAME", ""), os.environ.get("USERDOMAIN", ""), os.environ.get("COMPUTERNAME", ""), str(Path.home()), "SIGFUR_SIPPES_CRED_V2"])
    return hashlib.sha256(seed.encode("utf-8", errors="ignore")).digest()


def _xor_bytes(data: bytes, key: bytes) -> bytes:
    return bytes(b ^ key[i % len(key)] for i, b in enumerate(data)) if key else data


def _dpapi_protect(data: bytes) -> bytes | None:
    if os.name != "nt":
        return None
    try:
        import ctypes
        from ctypes import wintypes
        class DATA_BLOB(ctypes.Structure):
            _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_byte))]
        in_buf = ctypes.create_string_buffer(data)
        in_blob = DATA_BLOB(len(data), ctypes.cast(in_buf, ctypes.POINTER(ctypes.c_byte)))
        out_blob = DATA_BLOB()
        ok = ctypes.windll.crypt32.CryptProtectData(ctypes.byref(in_blob), "SIGFUR_SIPPES".encode("utf-16le"), None, None, None, 0, ctypes.byref(out_blob))
        if not ok:
            return None
        try:
            return ctypes.string_at(out_blob.pbData, out_blob.cbData)
        finally:
            ctypes.windll.kernel32.LocalFree(out_blob.pbData)
    except Exception:
        return None


def _dpapi_unprotect(data: bytes) -> bytes | None:
    if os.name != "nt":
        return None
    try:
        import ctypes
        from ctypes import wintypes
        class DATA_BLOB(ctypes.Structure):
            _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_byte))]
        in_buf = ctypes.create_string_buffer(data)
        in_blob = DATA_BLOB(len(data), ctypes.cast(in_buf, ctypes.POINTER(ctypes.c_byte)))
        out_blob = DATA_BLOB()
        ok = ctypes.windll.crypt32.CryptUnprotectData(ctypes.byref(in_blob), None, None, None, None, 0, ctypes.byref(out_blob))
        if not ok:
            return None
        try:
            return ctypes.string_at(out_blob.pbData, out_blob.cbData)
        finally:
            ctypes.windll.kernel32.LocalFree(out_blob.pbData)
    except Exception:
        return None


def _sippes_save_credentials(usuario: str, senha: str) -> None:
    import base64, json
    payload = json.dumps({"usuario": (usuario or "").strip(), "senha": senha or ""}, ensure_ascii=False).encode("utf-8")
    protected = _dpapi_protect(payload)
    if protected is not None:
        data = {"modo": "dpapi", "payload": base64.b64encode(protected).decode("ascii")}
    else:
        data = {"modo": "ofuscado", "payload": base64.b64encode(_xor_bytes(payload, _sippes_obfuscation_key())).decode("ascii")}
    _sippes_credentials_path().write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")


def _sippes_load_credentials() -> dict:
    import base64, json
    path = _sippes_credentials_path()
    if not path.exists():
        return {}
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        raw = base64.b64decode(data.get("payload") or "")
        if str(data.get("modo") or "").lower() == "dpapi":
            plain = _dpapi_unprotect(raw)
            if plain is None:
                return {}
        else:
            plain = _xor_bytes(raw, _sippes_obfuscation_key())
        payload = json.loads(plain.decode("utf-8", errors="ignore"))
        return {"usuario": str(payload.get("usuario") or "").strip(), "senha": str(payload.get("senha") or "")}
    except Exception:
        return {}


def _sippes_clear_credentials() -> None:
    try:
        _sippes_credentials_path().unlink(missing_ok=True)
    except Exception:
        pass


def abrir_janela_credenciais_sippes(parent=None) -> None:
    """Janela profissional para salvar/alterar o login automático do SIPPES.

    A senha não fica em texto puro: no Windows é protegida via DPAPI do usuário atual.
    A janela usa tamanho explícito para não cortar botões em monitores com escala diferente.
    """
    creds = _sippes_load_credentials()
    win = Toplevel(parent) if parent is not None else Toplevel()
    win.title("Credenciais SIPPES")
    win.configure(bg="#f7fafc")
    win.resizable(True, False)
    try:
        win.transient(parent)
    except Exception:
        pass
    try:
        win.minsize(640, 390)
        win.geometry("720x430")
    except Exception:
        pass

    header = Frame(win, bg="#0d47a1")
    header.pack(fill="x")
    Label(header, text="Credenciais SIPPES", bg="#0d47a1", fg="white",
          font=("Segoe UI", 13, "bold"), padx=14, pady=10).pack(side="left")
    Label(header, text="login automático protegido", bg="#0d47a1", fg="#bbdefb",
          font=("Segoe UI", 9, "bold"), padx=8).pack(side="left")

    footer = Frame(win, bg="#f7fafc")
    footer.pack(side="bottom", fill="x", padx=14, pady=(6, 14))

    outer = Frame(win, bg="#ffffff", highlightbackground="#d9e2ec", highlightthickness=1)
    outer.pack(side="top", fill="both", expand=True, padx=14, pady=14)

    Label(
        outer,
        text="Salve o usuário e a senha para o SIGFUR entrar no SIPPES, abrir as telas corretas e baixar o contracheque automaticamente.",
        bg="#ffffff", fg="#37474f", font=("Segoe UI", 9), wraplength=660, justify="left",
        padx=12, pady=10,
    ).pack(anchor="w", fill="x")

    form = Frame(outer, bg="#ffffff")
    form.pack(fill="x", padx=12, pady=(4, 0))
    form.grid_columnconfigure(1, weight=1)

    Label(form, text="Usuário/SIPPES:", bg="#ffffff", fg="#263238",
          font=("Segoe UI", 9, "bold")).grid(row=0, column=0, sticky="w", pady=7)
    usuario_var = StringVar(value=creds.get("usuario", ""))
    ent_user = ttk.Entry(form, textvariable=usuario_var, width=46)
    ent_user.grid(row=0, column=1, sticky="ew", padx=(10, 0), pady=7)

    Label(form, text="Senha:", bg="#ffffff", fg="#263238",
          font=("Segoe UI", 9, "bold")).grid(row=1, column=0, sticky="w", pady=7)
    senha_var = StringVar(value=creds.get("senha", ""))
    ent_pass = ttk.Entry(form, textvariable=senha_var, width=46, show="•")
    ent_pass.grid(row=1, column=1, sticky="ew", padx=(10, 0), pady=7)

    opts = Frame(outer, bg="#ffffff")
    opts.pack(fill="x", padx=12, pady=(8, 0))
    mostrar_var = BooleanVar(value=False)
    def _toggle_pass():
        try:
            ent_pass.configure(show="" if mostrar_var.get() else "•")
        except Exception:
            pass
    ttk.Checkbutton(opts, text="Mostrar senha", variable=mostrar_var, command=_toggle_pass).pack(side="left")

    info = StringVar(value="Credencial salva." if creds.get("senha") else "Nenhuma senha salva ainda.")
    Label(outer, textvariable=info, bg="#ffffff", fg="#607d8b",
          font=("Segoe UI", 8, "italic"), padx=12, pady=8).pack(anchor="w", fill="x")

    aviso = (
        "Segurança: no Windows, o SIGFUR tenta proteger a senha com a proteção do próprio usuário do sistema. "
        "Use esta opção apenas em computador confiável."
    )
    Label(outer, text=aviso, bg="#fff8e1", fg="#795548",
          font=("Segoe UI", 8), wraplength=660, justify="left",
          padx=10, pady=8).pack(fill="x", padx=12, pady=(4, 10))

    def _salvar():
        usuario = (usuario_var.get() or "").strip()
        senha = senha_var.get() or ""
        if not usuario:
            messagebox.showwarning("Credenciais SIPPES", "Informe o usuário/login do SIPPES.", parent=win)
            ent_user.focus_set()
            return
        if not senha:
            messagebox.showwarning("Credenciais SIPPES", "Informe a senha do SIPPES.", parent=win)
            ent_pass.focus_set()
            return
        _sippes_save_credentials(usuario, senha)
        info.set("Credencial salva com proteção local ✅")
        try:
            _toast(win, "Credencial SIPPES salva ✅", 1500)
        except Exception:
            pass

    def _remover():
        if messagebox.askyesno("Credenciais SIPPES", "Remover a credencial salva do SIPPES?", parent=win):
            _sippes_clear_credentials()
            senha_var.set("")
            info.set("Credencial removida.")

    def _testar():
        _salvar()
        info.set("Tentando preparar/login automático no SIPPES…")
        try:
            preparar_sessao_sippes(win, status_cb=lambda texto: info.set(str(texto)), minimizar=True)
        except Exception as exc:
            messagebox.showerror("Credenciais SIPPES", f"Não consegui iniciar o teste de login.\n\n{exc}", parent=win)

    Button(footer, text="Salvar", command=_salvar, bg="#1e88e5", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=14, pady=8).pack(side="left")
    Button(footer, text="Salvar e testar login", command=_testar, bg="#2e7d32", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=14, pady=8).pack(side="left", padx=8)
    Button(footer, text="Remover", command=_remover, bg="#c62828", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=14, pady=8).pack(side="left")
    Button(footer, text="Fechar", command=win.destroy, bg="#607d8b", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=14, pady=8).pack(side="right")

    try:
        win.update_idletasks()
        if parent is not None:
            sw = max(900, int(parent.winfo_screenwidth()))
            sh = max(620, int(parent.winfo_screenheight()))
            w = min(max(640, win.winfo_width()), sw - 60)
            h = min(max(390, win.winfo_height()), sh - 90)
            px, py = parent.winfo_rootx(), parent.winfo_rooty()
            pw, ph = max(1, parent.winfo_width()), max(1, parent.winfo_height())
            x = px + (pw - w) // 2
            y = py + (ph - h) // 3
            x = max(10, min(x, sw - w - 10))
            y = max(10, min(y, sh - h - 50))
            win.geometry(f"{int(w)}x{int(h)}+{int(x)}+{int(y)}")
        else:
            sw, sh = win.winfo_screenwidth(), win.winfo_screenheight()
            w, h = 720, 430
            win.geometry(f"{w}x{h}+{max(10,(sw-w)//2)}+{max(10,(sh-h)//3)}")
    except Exception:
        pass

    ent_user.focus_set()


# =========================
#   SIPPES — navegador persistente/reutilizável
# =========================
# Mantém UM navegador controlado pelo Selenium enquanto o SIGFUR estiver aberto.
# Assim, depois que o usuário faz o login/preparo manual uma vez, o botão automático
# reutiliza a mesma janela em vez de abrir um navegador novo a cada download.
_SIPPES_DRIVER = None
_SIPPES_DRIVER_HEADLESS = None
_SIPPES_DRIVER_LOCK = threading.RLock()


def _selenium_driver_alive(driver) -> bool:
    try:
        driver.execute_script("return 1;")
        return True
    except Exception:
        return False

def _selenium_switch_to_sippes_window(driver) -> bool:
    """Garante que o Selenium esteja olhando para a aba/janela correta do SIPPES.

    Em algumas telas antigas do SIPPES, uma chamada JavaScript pode trocar o foco
    visual do navegador, mas o Selenium continua preso no handle anterior. Aí a
    tela está correta para o usuário, porém o código procura o botão no documento
    errado. Esta função varre as janelas abertas e escolhe a que contém SIPPES.
    """
    try:
        handles = list(driver.window_handles)
    except Exception:
        handles = []

    if not handles:
        return False

    best = None
    for h in handles:
        try:
            driver.switch_to.window(h)
            url = (driver.current_url or "").lower()
            title = (driver.title or "").lower()
            if (
                "sippes" in url
                or "consultarcontracheque" in url
                or "contracheque" in url
                or "sippes" in title
                or "contracheque" in title
            ):
                best = h
        except Exception:
            continue

    if best:
        try:
            driver.switch_to.window(best)
            try:
                driver.switch_to.default_content()
            except Exception:
                pass
            return True
        except Exception:
            return False
    return False


def _sippes_headless_requested() -> bool:
    value = str(os.environ.get("SIGFUR_SIPPES_HEADLESS", "1") or "1").strip().lower()
    return value not in ("0", "false", "nao", "não", "off", "visible", "visivel", "visível")


def _close_reusable_sippes_driver() -> None:
    """Fecha e remove o driver persistente, inclusive ao trocar visível/oculto."""
    global _SIPPES_DRIVER, _SIPPES_DRIVER_HEADLESS
    with _SIPPES_DRIVER_LOCK:
        driver = _SIPPES_DRIVER
        _SIPPES_DRIVER = None
        _SIPPES_DRIVER_HEADLESS = None
        if driver is not None:
            try:
                driver.quit()
            except Exception:
                pass


def _get_reusable_sippes_driver(download_dir: Path):
    """Retorna o navegador do SIPPES no modo solicitado pelo WPF.

    A preparação manual usa uma janela visível. Downloads individuais e em lote
    usam outro driver headless, sem janela, reaproveitando o perfil persistente.
    """
    global _SIPPES_DRIVER, _SIPPES_DRIVER_HEADLESS
    requested_headless = _sippes_headless_requested()
    with _SIPPES_DRIVER_LOCK:
        alive = _SIPPES_DRIVER is not None and _selenium_driver_alive(_SIPPES_DRIVER)
        if alive and _SIPPES_DRIVER_HEADLESS == requested_headless:
            return _SIPPES_DRIVER, True
        if alive and _SIPPES_DRIVER_HEADLESS != requested_headless:
            try:
                _SIPPES_DRIVER.quit()
            except Exception:
                pass
            _SIPPES_DRIVER = None
        _SIPPES_DRIVER = _start_selenium_driver(download_dir, headless=requested_headless)
        _SIPPES_DRIVER_HEADLESS = requested_headless
        if not requested_headless:
            try:
                _selenium_minimize_window(_SIPPES_DRIVER)
            except Exception:
                pass
        return _SIPPES_DRIVER, False


def _selenium_restore_window(driver) -> None:
    try:
        driver.maximize_window()
    except Exception:
        try:
            driver.set_window_position(40, 40)
            driver.set_window_size(1200, 800)
        except Exception:
            pass


def _selenium_minimize_window(driver) -> None:
    try:
        driver.minimize_window()
    except Exception:
        pass


def _start_selenium_driver(download_dir: Path, headless: bool | None = None):
    """Abre um navegador dedicado e reutilizável para o SIPPES.

    Edge é a primeira opção porque fica mais estável minimizado no Windows e o
    Selenium Manager baixa/seleciona o driver compatível automaticamente.
    Chrome e Firefox permanecem como fallback.
    """
    try:
        from selenium import webdriver  # type: ignore
    except Exception as exc:
        raise RuntimeError(
            "A automação precisa do Selenium instalado.\n\n"
            "Execute PREPARAR_AUTOMACAO.bat uma vez ou instale com:\n"
            "python -m pip install --upgrade selenium"
        ) from exc

    if headless is None:
        headless = _sippes_headless_requested()
    download_dir.mkdir(parents=True, exist_ok=True)
    profile_root = Path.home() / "Documents" / "SIGFUR" / "SIPPES_WebDriver"
    profile_root.mkdir(parents=True, exist_ok=True)
    errors: list[str] = []
    common_prefs = {
        "download.default_directory": str(download_dir.resolve()),
        "download.prompt_for_download": False,
        "download.directory_upgrade": True,
        "plugins.always_open_pdf_externally": True,
        "safebrowsing.enabled": True,
    }

    try:
        from selenium.webdriver.edge.options import Options as EdgeOptions  # type: ignore
        options = EdgeOptions()
        if headless:
            options.add_argument("--headless=new")
            options.add_argument("--window-size=1500,1000")
        else:
            options.add_argument("--start-minimized")
        options.add_argument("--disable-gpu")
        options.add_argument("--disable-notifications")
        options.add_argument("--disable-popup-blocking")
        options.add_argument("--no-first-run")
        options.add_argument("--no-default-browser-check")
        options.add_argument(f"--user-data-dir={profile_root / 'edge_profile'}")
        options.add_experimental_option("prefs", common_prefs)
        return webdriver.Edge(options=options)
    except Exception as exc:
        errors.append(f"Edge: {exc}")

    try:
        from selenium.webdriver.chrome.options import Options as ChromeOptions  # type: ignore
        options = ChromeOptions()
        if headless:
            options.add_argument("--headless=new")
            options.add_argument("--window-size=1500,1000")
        else:
            options.add_argument("--start-minimized")
        options.add_argument("--disable-gpu")
        options.add_argument("--disable-notifications")
        options.add_argument("--disable-popup-blocking")
        options.add_argument("--no-first-run")
        options.add_argument("--no-default-browser-check")
        options.add_argument(f"--user-data-dir={profile_root / 'chrome_profile'}")
        options.add_experimental_option("prefs", common_prefs)
        return webdriver.Chrome(options=options)
    except Exception as exc:
        errors.append(f"Chrome: {exc}")

    try:
        from selenium.webdriver.firefox.options import Options as FirefoxOptions  # type: ignore
        options = FirefoxOptions()
        if headless:
            options.add_argument("-headless")
        firefox_profile = profile_root / "firefox_profile"
        firefox_profile.mkdir(parents=True, exist_ok=True)
        options.add_argument("-profile")
        options.add_argument(str(firefox_profile))
        options.set_preference("browser.download.folderList", 2)
        options.set_preference("browser.download.dir", str(download_dir.resolve()))
        options.set_preference("browser.download.useDownloadDir", True)
        options.set_preference("browser.download.manager.showWhenStarting", False)
        options.set_preference("browser.helperApps.neverAsk.saveToDisk", "application/pdf,application/octet-stream,application/x-pdf,binary/octet-stream")
        options.set_preference("pdfjs.disabled", True)
        options.set_preference("browser.download.alwaysOpenPanel", False)
        return webdriver.Firefox(options=options)
    except Exception as exc:
        errors.append(f"Firefox: {exc}")

    raise RuntimeError(
        "Não consegui abrir Edge, Chrome nem Firefox pelo Selenium.\n\n"
        "Verifique se pelo menos um desses navegadores está instalado e execute PREPARAR_AUTOMACAO.bat.\n\n"
        + "\n".join(errors[-3:])
    )

def _selenium_run_in_contexts(driver, callback, max_depth: int = 4) -> bool:
    """Executa uma ação no documento atual e, se precisar, dentro de frames/iframes.

    O SIPPES é antigo e algumas telas podem ficar dentro de frame. Quando isso
    acontece, o botão está visível na tela, mas document.querySelectorAll(...) no
    documento principal não enxerga o botão. Por isso a automação procura também
    nos frames.
    """
    try:
        driver.switch_to.default_content()
    except Exception:
        pass

    def _visit(depth: int) -> bool:
        try:
            if callback():
                return True
        except Exception:
            pass

        if depth >= max_depth:
            return False

        try:
            frames = driver.find_elements("css selector", "iframe, frame")
        except Exception:
            frames = []

        for fr in frames:
            try:
                driver.switch_to.frame(fr)
                if _visit(depth + 1):
                    return True
            except Exception:
                pass
            finally:
                try:
                    driver.switch_to.parent_frame()
                except Exception:
                    try:
                        driver.switch_to.default_content()
                    except Exception:
                        pass
        return False

    return _visit(0)


_PESQUISAR_DETECT_JS = r"""
const norm = (s) => {
    try { return (s || '').toString().normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase().trim(); }
    catch(e) { return (s || '').toString().toLowerCase().trim(); }
};

// Procura em TODOS os elementos, não só input/button.
// O SIPPES é antigo e em algumas telas o "botão" pode estar renderizado por tag fora do padrão.
const els = Array.from(document.querySelectorAll('*'));
for (const el of els) {
    const txt = norm([
        el.value, el.innerText, el.textContent, el.name, el.id, el.alt, el.title,
        el.getAttribute && el.getAttribute('aria-label'),
        el.getAttribute && el.getAttribute('onclick'),
        el.outerHTML
    ].join(' '));
    if (txt.includes('pesquisar')) return true;
}

const body = norm(document.body ? (document.body.innerText || document.body.textContent || '') : '');
if (body.includes('consultar contracheque')) return true;
if (document.forms && document.forms.length) return true;
return false;
"""


_PESQUISAR_CLICK_JS = r"""
const norm = (s) => {
    try { return (s || '').toString().normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase().trim(); }
    catch(e) { return (s || '').toString().toLowerCase().trim(); }
};
const visible = (el) => {
    try {
        const st = window.getComputedStyle(el);
        const r = el.getBoundingClientRect();
        return st.display !== 'none' && st.visibility !== 'hidden' && r.width >= 0 && r.height >= 0;
    } catch(e) { return true; }
};
const fireMouse = (el) => {
    try { el.removeAttribute('disabled'); } catch(e) {}
    try { el.scrollIntoView({block:'center', inline:'center'}); } catch(e) {}
    try { el.focus(); } catch(e) {}
    try { el.dispatchEvent(new MouseEvent('mouseover', {bubbles:true, cancelable:true, view:window})); } catch(e) {}
    try { el.dispatchEvent(new MouseEvent('mousedown', {bubbles:true, cancelable:true, view:window})); } catch(e) {}
    try { el.dispatchEvent(new MouseEvent('mouseup', {bubbles:true, cancelable:true, view:window})); } catch(e) {}
    try { el.click(); return 'click'; } catch(e) {}
    return '';
};

// 0) Caminho mais confiável no SIPPES: o botão chama pesquisarContracheque().
// HTML observado:
// <input id="botao" type="button" name="btnPesquisar" value="Pesquisar" onclick="pesquisarContracheque()" ...>
try {
    if (typeof pesquisarContracheque === 'function') {
        pesquisarContracheque();
        return 'function:pesquisarContracheque';
    }
} catch(e) {}

// 0.1) Procura exata pelo ID/name real do botão do SIPPES.
for (const sel of ['#botao', 'input#botao', 'input[name="btnPesquisar"]', 'input[title*="Pesquisar"]', 'input[onclick*="pesquisarContracheque"]']) {
    try {
        const el = document.querySelector(sel);
        if (el) {
            const clicked = fireMouse(el);
            if (clicked) return clicked + ':' + sel;
        }
    } catch(e) {}
}

// 1) XPath direto para value/texto Pesquisar.
try {
    const xp = "//*[translate(normalize-space(@value),'ABCDEFGHIJKLMNOPQRSTUVWXYZÁÀÃÂÉÊÍÓÔÕÚÇ','abcdefghijklmnopqrstuvwxyzaaaaeeiooouc')='pesquisar' or contains(translate(normalize-space(string(.)),'ABCDEFGHIJKLMNOPQRSTUVWXYZÁÀÃÂÉÊÍÓÔÕÚÇ','abcdefghijklmnopqrstuvwxyzaaaaeeiooouc'),'pesquisar')]";
    const r = document.evaluate(xp, document, null, XPathResult.ORDERED_NODE_SNAPSHOT_TYPE, null);
    for (let i = 0; i < r.snapshotLength; i++) {
        const el = r.snapshotItem(i);
        if (el && norm([el.value, el.innerText, el.textContent, el.outerHTML].join(' ')).includes('pesquisar')) {
            const clicked = fireMouse(el);
            if (clicked) return clicked + ':xpath';
        }
    }
} catch(e) {}

// 2) Procura em TODOS os elementos e prioriza os menores elementos com texto/value Pesquisar.
let scored = [];
const all = Array.from(document.querySelectorAll('*'));
for (const el of all) {
    const key = norm([
        el.value, el.innerText, el.textContent, el.name, el.id, el.alt, el.title,
        el.getAttribute && el.getAttribute('aria-label'),
        el.getAttribute && el.getAttribute('onclick')
    ].join(' '));
    if (!key || key.includes('limpar')) continue;
    let score = 0;
    if (key === 'pesquisar') score += 200;
    if ((norm(el.value) === 'pesquisar') || (norm(el.innerText) === 'pesquisar') || (norm(el.textContent) === 'pesquisar')) score += 160;
    if (key.includes('pesquisar')) score += 100;
    if (['INPUT','BUTTON','A','IMG'].includes(el.tagName)) score += 50;
    if (visible(el)) score += 10;
    try {
        const r = el.getBoundingClientRect();
        if (r.width <= 250 && r.height <= 80) score += 20;
    } catch(e) {}
    if (score > 0) scored.push({el, score});
}
scored.sort((a, b) => b.score - a.score);
for (const item of scored) {
    const clicked = fireMouse(item.el);
    if (clicked) return clicked + ':scored';
}

// 3) Funções comuns de sistemas antigos.
for (const fn of ['pesquisar', 'Pesquisar', 'consultar', 'Consultar', 'consultarContracheque', 'enviar', 'submitForm']) {
    try {
        if (typeof window[fn] === 'function') {
            window[fn]();
            return 'function:' + fn;
        }
    } catch(e) {}
}

// 4) Fallback pesado: submete o formulário mais provável.
const forms = Array.from(document.forms || []);
for (const form of forms) {
    const formText = norm([form.name, form.id, form.action, form.innerText, form.textContent, form.outerHTML].join(' '));
    if (formText.includes('contracheque') || formText.includes('favorecido') || formText.includes('cpf') || formText.includes('prec') || formText.includes('idt')) {
        try {
            if (form.requestSubmit) { form.requestSubmit(); return 'requestSubmit'; }
        } catch(e) {}
        try {
            HTMLFormElement.prototype.submit.call(form);
            return 'submit';
        } catch(e) {}
        try {
            form.submit();
            return 'submit2';
        } catch(e) {}
    }
}

// 5) Último recurso: primeiro formulário da página.
try {
    if (document.forms && document.forms[0]) {
        if (document.forms[0].requestSubmit) { document.forms[0].requestSubmit(); return 'requestSubmit0'; }
        HTMLFormElement.prototype.submit.call(document.forms[0]);
        return 'submit0';
    }
} catch(e) {}
return '';
"""

def _selenium_has_button_pesquisar(driver) -> bool:
    _selenium_switch_to_sippes_window(driver)
    return _selenium_run_in_contexts(
        driver,
        lambda: bool(driver.execute_script(_PESQUISAR_DETECT_JS)),
        max_depth=5,
    )


def _selenium_executar_pesquisar_contracheque(driver) -> bool:
    """Executa diretamente a função do botão Pesquisar do SIPPES.

    Pelo HTML real da tela, o botão é:
      id="botao", name="btnPesquisar", onclick="pesquisarContracheque()"

    Então o caminho mais confiável é chamar pesquisarContracheque() em vez de
    depender do Selenium encontrar/clicar visualmente no botão.
    """
    _selenium_switch_to_sippes_window(driver)
    js = """
    try {
        if (typeof pesquisarContracheque === 'function') {
            pesquisarContracheque();
            return true;
        }
    } catch(e) {}
    try {
        if (window.parent && typeof window.parent.pesquisarContracheque === 'function') {
            window.parent.pesquisarContracheque();
            return true;
        }
    } catch(e) {}
    try {
        if (window.top && typeof window.top.pesquisarContracheque === 'function') {
            window.top.pesquisarContracheque();
            return true;
        }
    } catch(e) {}
    return false;
    """
    return _selenium_run_in_contexts(
        driver,
        lambda: bool(driver.execute_script(js)),
        max_depth=5,
    )


def _selenium_click_pesquisar(driver) -> bool:
    _selenium_switch_to_sippes_window(driver)
    return _selenium_run_in_contexts(
        driver,
        lambda: bool(driver.execute_script(_PESQUISAR_CLICK_JS)),
        max_depth=5,
    )

def _selenium_tem_funcao_selecionar_favorecido(driver) -> bool:
    try:
        return bool(driver.execute_script("return (typeof selecionarFavorecido === 'function');"))
    except Exception:
        return False


def _selenium_tem_funcao_visualizar_contracheque(driver) -> bool:
    try:
        return bool(driver.execute_script("return (typeof visualizarContracheque === 'function');"))
    except Exception:
        return False


def _selenium_preencher_favorecido(driver, idt: str, cpf: str, nome: str, prec: str) -> bool:
    """Preenche o favorecido usando o mesmo comando que funcionou no console.

    Ordem correta observada no SIPPES:
      selecionarFavorecido(IDENTIDADE_MILITAR, CPF, NOME_SEM_ACENTO, PREC_CP, '1')
    """
    try:
        nome_sem_acento = _strip_accents(nome or "").upper().strip()
        return bool(driver.execute_script(
            """
            const idt  = arguments[0] || '';
            const cpf  = arguments[1] || '';
            const nome = arguments[2] || '';
            const prec = arguments[3] || '';

            if (typeof selecionarFavorecido === 'function') {
                selecionarFavorecido(idt, cpf, nome, prec, '1');
                return true;
            }

            // Plano B: tenta preencher os campos se, por algum motivo,
            // a função selecionarFavorecido não estiver carregada.
            const norm = (s) => (s || '').toString().toLowerCase();
            const fire = (el) => {
                try { el.dispatchEvent(new Event('input', {bubbles:true})); } catch(e) {}
                try { el.dispatchEvent(new Event('change', {bubbles:true})); } catch(e) {}
                try { el.dispatchEvent(new Event('blur', {bubbles:true})); } catch(e) {}
            };
            const inputs = Array.from(document.querySelectorAll('input')).filter(el => {
                const t = norm(el.type);
                return !t || ['text','search','tel','number','hidden'].includes(t);
            });

            let touched = false;
            for (const el of inputs) {
                const key = norm([el.name, el.id, el.className, el.placeholder, el.title].join(' '));
                if (key.includes('cpf')) { el.value = cpf; fire(el); touched = true; }
                else if (key.includes('prec')) { el.value = prec; fire(el); touched = true; }
                else if (key.includes('idt') || key.includes('cadastro') || key.includes('identificacao')) { el.value = idt; fire(el); touched = true; }
                else if (key.includes('nome')) { el.value = nome; fire(el); touched = true; }
            }
            if (!touched && inputs.length) {
                inputs[0].value = idt || cpf;
                fire(inputs[0]);
                touched = true;
            }
            return touched;
            """,
            idt, cpf, nome_sem_acento, prec
        ))
    except Exception:
        return False


def _selenium_executar_visualizar_contracheque(driver, idt: str, prec: str, codigo_folha: str) -> None:
    """Executa a mesma chamada que funcionou no console do SIPPES.

    Ordem correta observada no sistema:
      visualizarContracheque(IDENTIDADE_MILITAR, PREC_CP, CODIGO_FOLHA, '')

    O CPF continua sendo usado antes, apenas para preencher/pesquisar o favorecido
    e liberar a tela. Na hora de baixar, a chamada usa IDT + PREC/CP.
    """
    driver.execute_script(
        """
        const idt = arguments[0];
        const prec = arguments[1];
        const cod = arguments[2];
        if (typeof visualizarContracheque !== 'function') {
            throw new Error('A função visualizarContracheque ainda não está disponível. Clique em Pesquisar ou aguarde a página carregar.');
        }
        visualizarContracheque(idt, prec, cod, '');
        """,
        idt, prec, codigo_folha
    )



def _selenium_wait_for(condition, timeout: float, interval: float = 0.4) -> bool:
    limite = time.time() + float(timeout or 0)
    while time.time() < limite:
        try:
            if condition():
                return True
        except Exception:
            pass
        time.sleep(interval)
    return False


def _selenium_is_login_page(driver) -> bool:
    try:
        return bool(driver.execute_script('''
        const txt = (document.body ? document.body.innerText : '').toLowerCase();
        const hasPass = !!document.querySelector('input[type="password"]');
        const hasLoginButton = !!document.querySelector('input[name="botaoLogin"], input[value="OK"], button[name="botaoLogin"]');
        const hasLoginFn = (typeof logar === 'function');
        return !!(hasPass || hasLoginButton || hasLoginFn || (txt.includes('senha') && txt.includes('sippes')));
        '''))
    except Exception:
        return False


def _selenium_login_automatico_sippes(driver, status_cb=None) -> bool:
    creds = _sippes_load_credentials()
    usuario = (creds.get("usuario") or "").strip()
    senha = creds.get("senha") or ""
    if not senha:
        return False
    def _status(msg: str):
        if status_cb:
            try: status_cb(msg)
            except Exception: pass
    _status("Tela de login detectada. Preenchendo credencial salva…")
    try:
        ok = bool(driver.execute_script('''
        const usuario = arguments[0] || '';
        const senha = arguments[1] || '';
        const norm = (s) => (s || '').toString().toLowerCase();
        const visible = (el) => { try { const r = el.getBoundingClientRect(); const st = window.getComputedStyle(el); return r.width > 0 && r.height > 0 && st.visibility !== 'hidden' && st.display !== 'none'; } catch(e) { return true; } };
        const fire = (el) => { try { el.dispatchEvent(new Event('input', {bubbles:true})); } catch(e) {} try { el.dispatchEvent(new Event('change', {bubbles:true})); } catch(e) {} try { el.dispatchEvent(new Event('blur', {bubbles:true})); } catch(e) {} };
        const inputs = Array.from(document.querySelectorAll('input'));
        const pass = inputs.find(el => norm(el.type) === 'password' && !el.disabled);
        if (pass) { pass.focus(); pass.value = senha; fire(pass); }
        const userCandidates = inputs.filter(el => { const type = norm(el.type || 'text'); if (el.disabled || type === 'password' || type === 'hidden' || type === 'button' || type === 'submit') return false; if (!['text','search','tel','number','email',''].includes(type)) return false; if (!visible(el)) return false; return true; });
        let user = null;
        for (const el of userCandidates) { const key = norm([el.name, el.id, el.className, el.placeholder, el.title].join(' ')); if (key.includes('usuario') || key.includes('login') || key.includes('user') || key.includes('nome') || key.includes('idt') || key.includes('ident')) { user = el; break; } }
        if (!user && userCandidates.length === 1) user = userCandidates[0];
        if (user && usuario) { user.focus(); user.value = usuario; fire(user); }
        try { if (typeof logar === 'function') { logar(); try { if (typeof loading === 'function') loading(); } catch(e) {} return true; } } catch(e) {}
        const btn = document.querySelector('input[name="botaoLogin"], input[title*="login" i], input[value="OK"], input#botao, button[name="botaoLogin"]');
        if (btn) { try { btn.focus(); } catch(e) {} try { btn.click(); return true; } catch(e) {} }
        return !!pass;
        ''', usuario, senha))
        if ok:
            _status("Login enviado. Aguardando liberação da sessão…")
            time.sleep(1.0)
        return ok
    except Exception:
        return False


def _preparar_fluxo_autenticado_sippes(driver, status_cb=None, *, tentar_login: bool = True) -> bool:
    def _status(msg: str):
        if status_cb:
            try: status_cb(msg)
            except Exception: pass
    try:
        try: _selenium_restore_window(driver)
        except Exception: pass
        _status("Abrindo tela base do SIPPES…")
        driver.get(_sippes_tela_consultar_url())
        time.sleep(1.2)
        if tentar_login and _selenium_is_login_page(driver):
            if not _selenium_login_automatico_sippes(driver, status_cb=_status):
                _status("Login automático indisponível: credencial não configurada ou tela não aceita preenchimento.")
                return False
            _selenium_wait_for(lambda: not _selenium_is_login_page(driver), 18, 0.5)
        _status("Abrindo tela de consulta do contracheque…")
        driver.get(_sippes_tela_consultar_url())
        time.sleep(1.0)
        if tentar_login and _selenium_is_login_page(driver):
            if not _selenium_login_automatico_sippes(driver, status_cb=_status): return False
            _selenium_wait_for(lambda: not _selenium_is_login_page(driver), 18, 0.5)
            driver.get(_sippes_tela_consultar_url()); time.sleep(1.0)
        _status("Abrindo seleção de favorecido…")
        driver.get(_sippes_consulta_url())
        ok = _selenium_wait_for(lambda: _selenium_tem_funcao_selecionar_favorecido(driver), 25, 0.5)
        if not ok and tentar_login and _selenium_is_login_page(driver):
            if _selenium_login_automatico_sippes(driver, status_cb=_status):
                _selenium_wait_for(lambda: not _selenium_is_login_page(driver), 18, 0.5)
                driver.get(_sippes_tela_consultar_url()); time.sleep(1.0); driver.get(_sippes_consulta_url())
                ok = _selenium_wait_for(lambda: _selenium_tem_funcao_selecionar_favorecido(driver), 25, 0.5)
        return bool(ok)
    except Exception as exc:
        _status(f"Falha no fluxo automático do SIPPES: {exc}")
        return False

def _default_root() -> Path:
    return Path.home() / "Documents" / "SIGFUR" / "Contracheques"


def _default_temp() -> Path:
    return Path.home() / "Downloads" / "SIGFUR_TEMP"


def _downloads_fallback() -> Path:
    return Path.home() / "Downloads"


def _militar_folder(root: Path, mref: MilitarRef) -> Path:
    return root / f"{_safe_slug(mref.pg)}_{_safe_slug(mref.nome)}"


def _open_file(path: Path) -> None:
    try:
        os.startfile(str(path))  # type: ignore[attr-defined]
    except Exception:
        subprocess.Popen(["cmd", "/c", "start", "", str(path)], shell=False)


def _open_folder(path: Path) -> None:
    try:
        os.startfile(str(path))  # type: ignore[attr-defined]
    except Exception:
        subprocess.Popen(["explorer", str(path)], shell=False)


def _open_firefox(url: str) -> None:
    try:
        subprocess.Popen(["firefox", url], shell=False)
    except Exception:
        try:
            os.startfile(url)  # type: ignore[attr-defined]
        except Exception:
            subprocess.Popen(["cmd", "/c", "start", "", url], shell=False)


def _sippes_url() -> str:
    return (
        "https://sippes.eb.mil.br/consultarContracheque.do?metodo=exibirTelaSelecionarFavorecidoCC&paginaDestino=consultarRelatorio&acaoPai=consultarContracheque&formularioPai=formularioConsultarContracheque&camposDestino=identificacaoFavorecido-cpfFavorecido-nomeFavorecido-precCpFavorecido&abrangenciaOm=true"
    )


def _ficha_financeira_url() -> str:
    return "https://cpex-intranet.eb.mil.br/area_ua_cpex/fichas/index.html"


def _toast(parent, text: str, ms: int = 1300):
    try:
        t = Toplevel(parent)
        t.overrideredirect(True)
        t.attributes("-topmost", True)
        t.configure(bg="#1e88e5")
        Label(
            t, text=text, bg="#1e88e5", fg="white",
            font=("Segoe UI", 9, "bold"), padx=14, pady=8
        ).pack()
        t.update_idletasks()
        x = parent.winfo_rootx() + parent.winfo_width() - t.winfo_width() - 18
        y = parent.winfo_rooty() + 18
        t.geometry(f"+{x}+{y}")
        t.after(ms, t.destroy)
    except Exception:
        pass


def _looks_like_pdf(p: Path) -> bool:
    try:
        if p.stat().st_size < 2048:
            return False
        with open(p, "rb") as f:
            head = f.read(5)
        return head == b"%PDF-"
    except Exception:
        return False


def _wait_file_stable(p: Path, total: float = 2.6, checks: int = 7) -> bool:
    try:
        last = -1
        stable_hits = 0
        for _ in range(checks):
            if not p.exists():
                return False
            size = p.stat().st_size
            if size == last and size > 0:
                stable_hits += 1
            else:
                stable_hits = 0
            last = size
            time.sleep(total / checks)
        if stable_hits < 2:
            return False
        return _looks_like_pdf(p)
    except Exception:
        return False


def _format_filename_contracheque(year: int, month: int) -> str:
    return f"{_PT_MONTHS.get(month, str(month))} - {year}.pdf"


def _format_filename_ficha(year: int, nome: str) -> str:
    nome_limpo = _clean_filename_text(nome)
    return f"FICHA FINANCEIRA - {year} - {nome_limpo}.pdf"


def _extract_year_from_name(name: str) -> int | None:
    m = re.search(r"(20\d{2})", name)
    return int(m.group(1)) if m else None


def _extract_month_from_name(name: str) -> int | None:
    up = _strip_accents(name).upper()
    for n, nm in _PT_MONTHS.items():
        if _strip_accents(nm).upper() in up:
            return n
    m = re.search(r"(20\d{2})\D(0[1-9]|1[0-2])", up)
    if m:
        return int(m.group(2))
    return None


class _DownloadWatcher:
    def __init__(self, dirs: list[Path], status_cb=None):
        self.dirs = dirs
        self.status_cb = status_cb
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

    def start(self, on_pdf_ready) -> None:
        self._stop.clear()
        self._thread = threading.Thread(target=self._run, args=(on_pdf_ready,), daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()

    def _set_status(self, s: str):
        if self.status_cb:
            try:
                self.status_cb(s)
            except Exception:
                pass

    def _run(self, on_pdf_ready) -> None:
        baseline = set()
        for d in self.dirs:
            try:
                d.mkdir(parents=True, exist_ok=True)
                for p in d.glob("*"):
                    baseline.add(str(p))
            except Exception:
                pass

        self._set_status("Aguardando o PDF baixar…")

        while not self._stop.is_set():
            for d in self.dirs:
                try:
                    pdfs = list(d.glob("*.pdf"))
                    pdfs.sort(key=lambda p: -p.stat().st_mtime)
                    for p in pdfs:
                        if str(p) in baseline:
                            continue
                        part1 = p.with_suffix(p.suffix + ".part")
                        part2 = p.with_suffix(".part")
                        crdownload = p.with_suffix(p.suffix + ".crdownload")
                        if part1.exists() or part2.exists() or crdownload.exists():
                            continue

                        self._set_status(f"Validando: {p.name}…")
                        if not _wait_file_stable(p):
                            continue

                        on_pdf_ready(p)
                        return
                except Exception:
                    pass
            time.sleep(0.6)

        self._set_status("Monitoramento cancelado.")


# =========================
#   JANELA — posição/tamanho persistentes
# =========================
def _sigfur_data_dir() -> Path:
    base = os.environ.get("ESCALA_DATA_DIR") or os.environ.get("LOCALAPPDATA")
    if base:
        p = Path(base)
        if p.name.lower() != "sigfur":
            p = p / "SIGFUR"
    else:
        p = Path.home() / "AppData" / "Local" / "SIGFUR"
    try:
        p.mkdir(parents=True, exist_ok=True)
    except Exception:
        pass
    return p




def _get_root_owner(widget):
    """Retorna a raiz Tk mais alta possível.

    Usado para janelas longas (lote/auditoria), para que continuem abertas mesmo
    se a janela Listar Militares for fechada.
    """
    try:
        cur = widget
        last = widget
        while cur is not None:
            last = cur
            cur = getattr(cur, "master", None)
        return last or widget
    except Exception:
        return widget

def _window_pos_file() -> Path:
    return _sigfur_data_dir() / "janelas_lt.json"


def _load_window_positions() -> dict:
    try:
        import json
        p = _window_pos_file()
        if p.exists():
            with open(p, "r", encoding="utf-8") as f:
                data = json.load(f)
            return data if isinstance(data, dict) else {}
    except Exception:
        pass
    return {}


def _save_window_positions(data: dict) -> None:
    try:
        import json
        p = _window_pos_file()
        p.parent.mkdir(parents=True, exist_ok=True)
        with open(p, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
    except Exception:
        pass


def _screen_signature(win) -> dict:
    """Assinatura do monitor atual para não restaurar posição de outro PC/monitor."""
    try:
        return {
            "w": int(win.winfo_screenwidth()),
            "h": int(win.winfo_screenheight()),
            "vroot_w": int(getattr(win, "winfo_vrootwidth", lambda: win.winfo_screenwidth())()),
            "vroot_h": int(getattr(win, "winfo_vrootheight", lambda: win.winfo_screenheight())()),
        }
    except Exception:
        return {}


def _same_screen(saved: object, current: dict) -> bool:
    if not isinstance(saved, dict) or not current:
        return False
    try:
        return (
            int(saved.get("w", 0)) == int(current.get("w", 0))
            and int(saved.get("h", 0)) == int(current.get("h", 0))
            and int(saved.get("vroot_w", 0)) == int(current.get("vroot_w", 0))
            and int(saved.get("vroot_h", 0)) == int(current.get("vroot_h", 0))
        )
    except Exception:
        return False


def _parse_geometry(geometry: str):
    try:
        m = re.search(r"(\d+)x(\d+)([+-]\d+)?([+-]\d+)?", str(geometry or ""))
        if not m:
            return None
        return int(m.group(1)), int(m.group(2)), int(m.group(3) or 0), int(m.group(4) or 0)
    except Exception:
        return None


def _fit_geometry(win, geometry: str | None = None, *, width: int = 1100, height: int = 760, center: bool = False) -> str:
    try:
        sw0 = int(win.winfo_screenwidth())
        sh0 = int(win.winfo_screenheight())
        vw = int(getattr(win, "winfo_vrootwidth", lambda: sw0)())
        vh = int(getattr(win, "winfo_vrootheight", lambda: sh0)())
        sw = max(900, sw0, vw)
        sh = max(620, sh0, vh)
    except Exception:
        sw, sh = 1366, 768

    margin = 28
    max_w = max(520, sw - margin * 2)
    max_h = max(360, sh - margin * 2 - 20)

    geometry_text = str(geometry or "")
    parsed = _parse_geometry(geometry_text)
    has_explicit_position = bool(re.search(r"\d+x\d+[+-]\d+[+-]\d+", geometry_text))
    if parsed:
        w, h, x, y = parsed
        if not has_explicit_position:
            center = True
    else:
        w, h = int(width or 1100), int(height or 760)
        x, y = (sw - w) // 2, (sh - h) // 2

    too_small = w < 520 or h < 360
    w = max(520, min(int(w), max_w))
    h = max(360, min(int(h), max_h))

    off_screen = (x > sw - 120 or y > sh - 80 or x + 160 < 0 or y + 80 < 0)
    if center or too_small or off_screen:
        x = (sw - w) // 2
        y = (sh - h) // 2

    x = max(10, min(int(x), max(10, sw - w - 10)))
    y = max(10, min(int(y), max(10, sh - h - 40)))
    return f"{w}x{h}+{x}+{y}"


def _save_window_geometry(win, key: str) -> None:
    try:
        if not win.winfo_exists():
            return
        state = str(win.state() or "normal").lower()
        if state not in ("normal", "zoomed"):
            state = "normal"
        geom = str(win.geometry())
        parsed = _parse_geometry(geom)
        if not parsed:
            return
        w, h, _x, _y = parsed
        if w < 520 or h < 360:
            return
        data = _load_window_positions()
        data[key] = {
            "geometry": _fit_geometry(win, geom),
            "state": state,
            "screen": _screen_signature(win),
            "layout_version": 2,
        }
        _save_window_positions(data)
    except Exception:
        pass


def _apply_persistent_window(win, key: str, *, default_geometry: str = "1100x760", parent=None) -> None:
    data = _load_window_positions()
    item = data.get(key) or data.get("contracheque")
    restored = False
    state = "normal"
    if isinstance(item, dict):
        # Ignora posições legadas/gravadas em outro computador ou monitor.
        # Assim, no primeiro uso em outro PC, a janela abre centralizada no SIGFUR
        # e depois salva a posição nova corretamente.
        if int(item.get("layout_version", 0) or 0) >= 2 and _same_screen(item.get("screen"), _screen_signature(win)):
            geom = str(item.get("geometry") or "").strip()
            state = str(item.get("state") or "normal").strip().lower()
        else:
            geom = ""
    else:
        geom = ""
    if geom:
        try:
            win.geometry(_fit_geometry(win, geom))
            restored = True
        except Exception:
            restored = False

    if not restored:
        try:
            if parent is not None:
                win.update_idletasks()
                parent.update_idletasks()
                parsed = _parse_geometry(default_geometry) or (1100, 760, 0, 0)
                w, h = parsed[0], parsed[1]
                try:
                    px, py = parent.winfo_rootx(), parent.winfo_rooty()
                    pw, ph = parent.winfo_width(), parent.winfo_height()
                    x = px + max(0, (pw - w) // 2)
                    y = py + max(0, (ph - h) // 2)
                    win.geometry(_fit_geometry(win, f"{w}x{h}+{x}+{y}"))
                except Exception:
                    win.geometry(_fit_geometry(win, default_geometry, center=True))
            else:
                win.geometry(_fit_geometry(win, default_geometry, center=True))
        except Exception:
            pass

    if state == "zoomed":
        try:
            win.after_idle(lambda: win.state("zoomed"))
        except Exception:
            pass

    job = {"id": None}

    def _schedule(_evt=None):
        try:
            if job["id"] is not None:
                win.after_cancel(job["id"])
        except Exception:
            pass
        try:
            job["id"] = win.after(450, lambda: _save_window_geometry(win, key))
        except Exception:
            pass

    try:
        win.bind("<Configure>", _schedule, add="+")
        win.bind("<FocusOut>", lambda _e=None: _save_window_geometry(win, key), add="+")
        win.bind("<Unmap>", lambda _e=None: _save_window_geometry(win, key), add="+")
        win.bind("<Destroy>", lambda _e=None: _save_window_geometry(win, key), add="+")
        win.after(800, lambda: _save_window_geometry(win, key))
    except Exception:
        pass





def preparar_sessao_sippes(parent=None, status_cb=None, *, minimizar: bool = True) -> None:
    """Abre/reutiliza o navegador persistente do SIPPES e prepara a sessão.

    Pode ser chamada pela janela individual, pelo lote ou diretamente pela
    janela principal. A ação roda em thread separada e mantém o navegador aberto
    para os próximos downloads.
    """
    owner = _get_root_owner(parent) if parent is not None else None
    temp = _default_temp()
    temp.mkdir(parents=True, exist_ok=True)

    def _call_status(text: str):
        if status_cb:
            try:
                if owner is not None and hasattr(owner, "after"):
                    owner.after(0, lambda t=text: status_cb(t))
                else:
                    status_cb(text)
            except Exception:
                pass

    def _show_error(title: str, msg: str):
        def _do():
            try:
                messagebox.showerror(title, msg, parent=parent if parent is not None else owner)
            except Exception:
                try:
                    messagebox.showerror(title, msg)
                except Exception:
                    pass
        try:
            if owner is not None and hasattr(owner, "after"):
                owner.after(0, _do)
            else:
                _do()
        except Exception:
            pass

    def _ask_ready() -> bool:
        ev = threading.Event()
        res = {"ok": False}

        def _ask():
            try:
                res["ok"] = messagebox.askokcancel(
                    "Preparar sessão SIPPES",
                    "O navegador do SIPPES será mantido aberto para os próximos downloads.\n\n"
                    "Faça o login normalmente. Se o link interno ainda der erro na primeira vez,\n"
                    "faça o caminho manual no SIPPES uma vez até a tela de contracheque.\n\n"
                    "Quando estiver pronto, clique em OK aqui.\n\n"
                    "Importante: não feche esse navegador; ele será minimizado e reutilizado.",
                    parent=parent if parent is not None else owner,
                )
            except Exception:
                res["ok"] = False
            finally:
                ev.set()

        try:
            if owner is not None and hasattr(owner, "after"):
                owner.after(0, _ask)
            else:
                _ask()
            ev.wait()
        except Exception:
            return False
        return bool(res["ok"])

    def _wait_for(condition, timeout: float, interval: float = 0.5) -> bool:
        limite = time.time() + timeout
        while time.time() < limite:
            try:
                if condition():
                    return True
            except Exception:
                pass
            time.sleep(interval)
        return False

    def _worker():
        try:
            with _SIPPES_ACTION_LOCK:
                driver, reused_driver = _get_reusable_sippes_driver(temp)
                _selenium_restore_window(driver)
                _call_status("Navegador do SIPPES já estava aberto. Conferindo sessão…" if reused_driver else "Navegador do SIPPES aberto. Faça o login/preparo se necessário…")

                auto_ok = _preparar_fluxo_autenticado_sippes(driver, status_cb=lambda s: _call_status(s), tentar_login=True)

                if not auto_ok:
                    _call_status("Aguardando você preparar/logar no SIPPES manualmente…")
                    if not _ask_ready():
                        _call_status("Preparo da sessão cancelado.")
                        return
                    _call_status("Aplicando fluxo de links após o login/preparo manual…")
                    driver.get(_sippes_tela_consultar_url())
                    time.sleep(1.0)
                    driver.get(_sippes_consulta_url())

                if _wait_for(lambda: _selenium_tem_funcao_selecionar_favorecido(driver), 40):
                    if minimizar:
                        _selenium_minimize_window(driver)
                    _call_status("Sessão SIPPES pronta. Navegador mantido aberto/minimizado para reutilizar.")
                    try:
                        if parent is not None:
                            parent.after(0, lambda: _toast(parent, "SIPPES pronto ✅", 1500))
                    except Exception:
                        pass
                else:
                    _call_status("Sessão aberta, mas não localizei a tela interna. Deixe o navegador aberto e tente novamente.")
                    _show_error(
                        "SIPPES",
                        "Não localizei a função selecionarFavorecido após o preparo.\n\n"
                        "Faça o caminho manual no navegador aberto uma vez, não feche a janela, e tente novamente."
                    )
        except Exception as e:
            _call_status("Falha ao preparar a sessão do SIPPES.")
            _show_error("SIPPES", f"Não consegui preparar o navegador persistente.\n\nDetalhe:\n{e}")

    threading.Thread(target=_worker, daemon=True).start()


# =========================
#   LOTE — Contracheques do mês em segundo plano
# =========================
# Observação importante:
# - O lote roda em uma thread separada para não travar a janela principal do SIGFUR.
# - Toda atualização de Tkinter é feita via win.after(...), porque Tkinter não é thread-safe.
# - O Selenium usa o mesmo navegador persistente do SIPPES enquanto o SIGFUR estiver aberto.
_SIPPES_ACTION_LOCK = threading.RLock()


def _wait_new_pdf_ready(dirs: list[Path], baseline: set[str], started_at: float, stop_event: threading.Event | None = None,
                        timeout: float = 90.0, status_cb=None) -> Path | None:
    """Espera aparecer um PDF novo/recente em uma das pastas monitoradas."""
    deadline = time.time() + max(5.0, float(timeout or 90.0))
    stop_event = stop_event or threading.Event()

    while time.time() < deadline and not stop_event.is_set():
        candidates: list[Path] = []
        for d in dirs:
            try:
                d.mkdir(parents=True, exist_ok=True)
                for p in d.glob("*.pdf"):
                    try:
                        # Novo em relação ao baseline OU modificado depois do início do download.
                        if str(p) not in baseline or p.stat().st_mtime >= started_at - 1.0:
                            part1 = p.with_suffix(p.suffix + ".part")
                            part2 = p.with_suffix(".part")
                            crdownload = p.with_suffix(p.suffix + ".crdownload")
                            if part1.exists() or part2.exists() or crdownload.exists():
                                continue
                            candidates.append(p)
                    except Exception:
                        continue
            except Exception:
                continue

        candidates.sort(key=lambda x: x.stat().st_mtime if x.exists() else 0, reverse=True)
        for p in candidates:
            try:
                if status_cb:
                    status_cb(f"Validando PDF baixado: {p.name}…")
                if _wait_file_stable(p, total=2.4, checks=6):
                    return p
            except Exception:
                continue
        time.sleep(0.7)
    return None


def _save_downloaded_pdf_to_militar(pdf_path: Path, dst: Path, overwrite: bool = True, remove_original: bool = True) -> None:
    dst.parent.mkdir(parents=True, exist_ok=True)
    if overwrite and dst.exists():
        try:
            dst.unlink()
        except Exception:
            try:
                os.remove(str(dst))
            except Exception:
                pass
    shutil.copy2(str(pdf_path), str(dst))
    if not _looks_like_pdf(dst):
        try:
            dst.unlink(missing_ok=True)
        except Exception:
            try:
                os.remove(str(dst))
            except Exception:
                pass
        raise RuntimeError("O arquivo baixado não parece ser um PDF válido.")
    if remove_original:
        try:
            pdf_path.unlink()
        except Exception:
            pass


def _download_one_contracheque_sippes(driver, mref: MilitarRef, codigo_folha: str,
                                      dirs_monitoradas: list[Path], dst: Path,
                                      stop_event: threading.Event | None = None,
                                      status_cb=None) -> None:
    """Baixa um contracheque pelo SIPPES e salva no destino individual do militar."""
    cpf = _cpf_digits(mref.cpf)
    idt = _idt_digits(mref.idt)
    prec = _prec_digits(mref.prec)
    codigo = _only_digits(str(codigo_folha or ""))

    if len(cpf) != 11:
        raise ValueError("CPF vazio/inválido")
    if not idt:
        raise ValueError("IDT vazia")
    if not prec:
        raise ValueError("PREC/CP vazio")
    if not codigo:
        raise ValueError("Código da folha vazio")

    stop_event = stop_event or threading.Event()

    def _status(msg: str):
        if status_cb:
            try:
                status_cb(msg)
            except Exception:
                pass

    baseline: set[str] = set()
    for d in dirs_monitoradas:
        try:
            d.mkdir(parents=True, exist_ok=True)
            for p in d.glob("*"):
                baseline.add(str(p))
        except Exception:
            pass

    started_at = time.time()

    _status("Abrindo tela do SIPPES…")
    driver.get(_sippes_consulta_url())

    limite = time.time() + 14
    while time.time() < limite and not stop_event.is_set():
        try:
            if _selenium_tem_funcao_selecionar_favorecido(driver):
                break
        except Exception:
            pass
        time.sleep(0.5)

    if not _selenium_tem_funcao_selecionar_favorecido(driver):
        _status("Sessão não pronta. Tentando login automático e fluxo correto do SIPPES…")
        if not _preparar_fluxo_autenticado_sippes(driver, status_cb=_status, tentar_login=True):
            raise RuntimeError("Sessão do SIPPES não está pronta. Configure as credenciais ou clique em Preparar SIPPES/login e tente novamente.")

    _status("Selecionando favorecido…")
    if not _selenium_preencher_favorecido(driver, idt, cpf, mref.nome, prec):
        raise RuntimeError("Falha ao executar selecionarFavorecido")

    time.sleep(1.4)
    _selenium_switch_to_sippes_window(driver)

    _status("Acionando Pesquisar…")
    pesquisou = False
    limite = time.time() + 8
    while time.time() < limite and not stop_event.is_set():
        try:
            if _selenium_executar_pesquisar_contracheque(driver) or _selenium_click_pesquisar(driver):
                pesquisou = True
                break
        except Exception:
            pass
        time.sleep(0.8)

    if pesquisou:
        _status("Pesquisar acionado. Solicitando PDF…")
        time.sleep(1.6)
    else:
        _status("Pesquisar automático não confirmou. Tentando solicitar PDF direto…")
        time.sleep(0.8)

    try:
        _selenium_executar_visualizar_contracheque(driver, idt, prec, codigo)
    except Exception as exc:
        raise RuntimeError(f"Não consegui solicitar o contracheque: {exc}")

    _status("Aguardando download terminar…")
    pdf = _wait_new_pdf_ready(dirs_monitoradas, baseline, started_at, stop_event=stop_event, timeout=95, status_cb=status_cb)
    if stop_event.is_set():
        raise RuntimeError("Cancelado pelo usuário")
    if not pdf:
        raise TimeoutError("O PDF não apareceu na pasta de downloads dentro do tempo limite")

    _status("Salvando na pasta individual do militar…")
    _save_downloaded_pdf_to_militar(pdf, dst, overwrite=True, remove_original=True)


def abrir_janela_lote_contracheques(parent, militares: list[MilitarRef] | tuple[MilitarRef, ...], *, on_open_edit=None, on_open_carteira=None) -> None:
    """Janela não modal para baixar contracheques em lote, em segundo plano.

    A janela é criada como filha da raiz do SIGFUR, não da listagem. Assim o
    usuário pode fechar a janela Listar Militares e o lote continua rodando.
    """
    owner = parent if parent is not None else _get_root_owner(parent)
    root = _default_root()
    temp = _default_temp()
    downloads = _downloads_fallback()
    root.mkdir(parents=True, exist_ok=True)
    temp.mkdir(parents=True, exist_ok=True)

    militares = list(militares or [])
    if not militares:
        messagebox.showwarning("Contracheques em lote", "Nenhum militar encontrado para baixar.", parent=parent)
        return

    win = Toplevel(owner)
    win.title("Baixar contracheques do mês — lote")
    win.configure(bg="white")
    # Não usa transient/grab_set(): a lista continua livre e não minimiza junto.
    try:
        win.minsize(760, 520)
    except Exception:
        pass
    try:
        _apply_persistent_window(win, "lote_contracheques", default_geometry="920x680", parent=owner)
    except Exception:
        win.geometry("920x680")

    stop_event = threading.Event()
    running = {"value": False}
    results: list[tuple[str, str, str]] = []  # status, militar, detalhe

    header = Frame(win, bg="#0d47a1")
    header.pack(fill="x")
    Label(header, text="Baixar contracheques do mês", bg="#0d47a1", fg="white",
          font=("Segoe UI", 12, "bold")).pack(side="left", padx=12, pady=10)
    Label(header, text=f"{len(militares)} militar(es) na fila", bg="#0d47a1", fg="#bbdefb",
          font=("Segoe UI", 9, "bold")).pack(side="left", padx=8)

    top = Frame(win, bg="white")
    top.pack(fill="x", padx=12, pady=(10, 6))

    now = datetime.now()
    Label(top, text="Ano:", bg="white", fg="#263238", font=("Segoe UI", 9, "bold")).grid(row=0, column=0, sticky="w")
    year_var = StringVar(value=str(now.year))
    years = [str(y) for y in range(now.year + 1, now.year - 7, -1)]
    cb_year = ttk.Combobox(top, textvariable=year_var, values=years, width=8, state="readonly")
    cb_year.grid(row=0, column=1, sticky="w", padx=(6, 14))

    Label(top, text="Mês:", bg="white", fg="#263238", font=("Segoe UI", 9, "bold")).grid(row=0, column=2, sticky="w")
    month_var = StringVar(value=_PT_MONTHS[now.month])
    cb_month = ttk.Combobox(top, textvariable=month_var, values=_MONTH_NAMES, width=14, state="readonly")
    cb_month.grid(row=0, column=3, sticky="w", padx=(6, 14))

    Label(top, text="Código folha:", bg="white", fg="#263238", font=("Segoe UI", 9, "bold")).grid(row=0, column=4, sticky="w")
    codigo_var = StringVar(value=str(_codigo_folha_sippes(now.year, now.month)))
    ent_codigo = ttk.Entry(top, textvariable=codigo_var, width=10)
    ent_codigo.grid(row=0, column=5, sticky="w", padx=(6, 14))

    pular_existentes_var = StringVar(value="1")
    abrir_pdf_var = StringVar(value="0")
    ttk.Checkbutton(top, text="Pular quem já tem PDF salvo", variable=pular_existentes_var, onvalue="1", offvalue="0").grid(row=1, column=0, columnspan=3, sticky="w", pady=(8, 0))
    ttk.Checkbutton(top, text="Abrir contracheques após salvar (não recomendado no lote)", variable=abrir_pdf_var, onvalue="1", offvalue="0").grid(row=1, column=3, columnspan=4, sticky="w", pady=(8, 0))

    def _update_codigo(_=None):
        try:
            y = int(year_var.get())
            m = _MONTH_TO_NUM.get(month_var.get(), now.month)
            codigo_var.set(str(_codigo_folha_sippes(y, m)))
        except Exception:
            pass

    cb_year.bind("<<ComboboxSelected>>", _update_codigo)
    cb_month.bind("<<ComboboxSelected>>", _update_codigo)

    status_var = StringVar(value="Pronto. Você pode usar o SIGFUR normalmente enquanto o lote roda.")
    atual_var = StringVar(value="")
    Label(win, textvariable=status_var, bg="white", fg="#455a64", font=("Segoe UI", 9, "italic")).pack(anchor="w", padx=12, pady=(4, 2))
    Label(win, textvariable=atual_var, bg="white", fg="#263238", font=("Segoe UI", 9, "bold")).pack(anchor="w", padx=12, pady=(0, 6))

    progress = ttk.Progressbar(win, orient="horizontal", mode="determinate", maximum=max(1, len(militares)))
    progress.pack(fill="x", padx=12, pady=(0, 6))

    body = Frame(win, bg="white")
    body.pack(fill="both", expand=True, padx=12, pady=(0, 10))
    log = Listbox(body, font=("Consolas", 9))
    sb = Scrollbar(body, command=log.yview)
    log.configure(yscrollcommand=sb.set)
    log.pack(side="left", fill="both", expand=True)
    sb.pack(side="right", fill="y")

    footer = Frame(win, bg="white")
    footer.pack(fill="x", padx=12, pady=(0, 12))

    def _ui(fn):
        try:
            win.after(0, fn)
        except Exception:
            pass

    def _log(msg: str):
        def _do():
            try:
                log.insert("end", msg)
                log.yview_moveto(1)
            except Exception:
                pass
        _ui(_do)

    def _set_status(msg: str):
        _ui(lambda: status_var.set(msg))

    def _set_atual(msg: str):
        _ui(lambda: atual_var.set(msg))

    def _set_progress(value: int):
        def _do():
            try:
                progress.configure(value=value)
            except Exception:
                pass
        _ui(_do)

    def _open_report_folder():
        _open_folder(root)

    def _cancel():
        if running.get("value"):
            stop_event.set()
            _set_status("Parada solicitada. O SIGFUR vai concluir/encerrar o item atual com segurança…")
        else:
            try:
                win.destroy()
            except Exception:
                pass

    def _start():
        if running.get("value"):
            return
        codigo = _only_digits(codigo_var.get())
        if not codigo:
            messagebox.showwarning("Contracheques em lote", "Informe o código da folha do SIPPES.", parent=win)
            return
        try:
            year = int(year_var.get())
            month = _MONTH_TO_NUM.get(month_var.get(), now.month)
        except Exception:
            messagebox.showwarning("Contracheques em lote", "Ano/mês inválidos.", parent=win)
            return

        stop_event.clear()
        running["value"] = True
        results.clear()
        log.delete(0, "end")
        btn_start.configure(state="disabled")
        btn_cancel.configure(text="Parar após o atual")
        _set_progress(0)
        _set_status("Lote iniciado em segundo plano. Você pode continuar usando o SIGFUR.")

        def _worker():
            ok = skip = fail = invalid = 0
            try:
                with _SIPPES_ACTION_LOCK:
                    driver, reused = _get_reusable_sippes_driver(temp)
                    _set_status("Usando navegador persistente do SIPPES…" if reused else "Abrindo navegador persistente do SIPPES…")

                    if not _selenium_tem_funcao_selecionar_favorecido(driver):
                        driver.get(_sippes_consulta_url())
                        limite = time.time() + 12
                        while time.time() < limite and not stop_event.is_set():
                            try:
                                if _selenium_tem_funcao_selecionar_favorecido(driver):
                                    break
                            except Exception:
                                pass
                            time.sleep(0.5)

                    if not _selenium_tem_funcao_selecionar_favorecido(driver):
                        _set_status("Sessão não pronta. Tentando login automático pelo SIPPES…")
                        _log("Sessão SIPPES não pronta. Tentando login automático e links internos…")
                        _preparar_fluxo_autenticado_sippes(driver, status_cb=lambda s: _set_status(s), tentar_login=True)

                    if not _selenium_tem_funcao_selecionar_favorecido(driver):
                        _set_status("Sessão do SIPPES não está pronta. Configure as credenciais ou use Preparar SIPPES.")
                        _log("❌ Sessão SIPPES não pronta. Configure as credenciais/login e rode o lote novamente.")
                        return

                    for idx, mref in enumerate(militares, start=1):
                        if stop_event.is_set():
                            _log("⏹ Lote interrompido pelo usuário.")
                            break

                        nome_linha = f"{mref.pg} {mref.nome}".strip() or "MILITAR"
                        _set_progress(idx - 1)
                        _set_atual(f"{idx}/{len(militares)} — {nome_linha}")

                        dst = _militar_folder(root, mref) / _format_filename_contracheque(year, month)
                        if dst.exists() and pular_existentes_var.get() == "1":
                            skip += 1
                            results.append(("JÁ EXISTIA", nome_linha, str(dst)))
                            _log(f"↷ {idx:03d}/{len(militares):03d} JÁ EXISTIA — {nome_linha}")
                            _set_progress(idx)
                            continue

                        try:
                            if len(_cpf_digits(mref.cpf)) != 11 or not _idt_digits(mref.idt) or not _prec_digits(mref.prec):
                                raise ValueError("dados incompletos: CPF/IDT/PREC")

                            _log(f"… {idx:03d}/{len(militares):03d} Baixando — {nome_linha}")
                            _download_one_contracheque_sippes(
                                driver,
                                mref,
                                codigo,
                                [temp, downloads],
                                dst,
                                stop_event=stop_event,
                                status_cb=lambda msg, n=nome_linha: _set_status(f"{n}: {msg}"),
                            )
                            ok += 1
                            results.append(("OK", nome_linha, str(dst)))
                            _log(f"✅ {idx:03d}/{len(militares):03d} SALVO — {nome_linha} -> {dst.name}")
                            if abrir_pdf_var.get() == "1":
                                try:
                                    _open_file(dst)
                                except Exception:
                                    pass
                        except ValueError as e:
                            invalid += 1
                            results.append(("DADOS", nome_linha, str(e)))
                            _log(f"⚠ {idx:03d}/{len(militares):03d} DADOS INCOMPLETOS — {nome_linha}: {e}")
                        except Exception as e:
                            fail += 1
                            results.append(("FALHA", nome_linha, str(e)))
                            _log(f"❌ {idx:03d}/{len(militares):03d} FALHA — {nome_linha}: {e}")

                        _set_progress(idx)
                        try:
                            _selenium_minimize_window(driver)
                        except Exception:
                            pass
                        time.sleep(0.8)

                    _set_status(f"Finalizado. Baixados: {ok} | Já existiam: {skip} | Dados incompletos: {invalid} | Falhas: {fail}")
                    _set_atual("Concluído.")
            finally:
                running["value"] = False
                def _done_ui():
                    try:
                        btn_start.configure(state="normal")
                        btn_cancel.configure(text="Fechar")
                    except Exception:
                        pass
                _ui(_done_ui)

        threading.Thread(target=_worker, daemon=True).start()

    def _preparar_sippes_lote():
        _set_status("Preparando SIPPES em segundo plano…")
        preparar_sessao_sippes(win, status_cb=lambda texto: _set_status(texto), minimizar=True)

    Button(footer, text="Preparar SIPPES", command=_preparar_sippes_lote,
           bg="#00695c", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left")
    Button(footer, text="Credenciais SIPPES", command=lambda: abrir_janela_credenciais_sippes(win),
           bg="#455A64", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left", padx=(8, 0))

    btn_start = Button(footer, text="Iniciar lote em segundo plano", command=_start,
                       bg="#1e88e5", fg="white", bd=0, cursor="hand2",
                       font=("Segoe UI", 9, "bold"), padx=12, pady=7)
    btn_start.pack(side="left", padx=8)
    Button(footer, text="Abrir pasta raiz", command=_open_report_folder,
           bg="#607d8b", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left", padx=8)
    Button(footer, text="Auditar salvos", command=lambda: abrir_janela_auditoria_contracheques(win, militares, on_open_edit=on_open_edit, on_open_carteira=on_open_carteira),
           bg="#00695c", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left")
    btn_cancel = Button(footer, text="Fechar", command=_cancel,
                        bg="#c62828", fg="white", bd=0, cursor="hand2",
                        font=("Segoe UI", 9, "bold"), padx=12, pady=7)
    btn_cancel.pack(side="right")

    # Menu profissional de clique direito na janela de lote.
    # Evita ficar caçando botões enquanto o lote está rodando.
    def _lote_copy_text(texto: str):
        try:
            win.clipboard_clear()
            win.clipboard_append(str(texto or ""))
            _toast(win, "Copiado ✅", 1200)
        except Exception:
            pass

    def _lote_selected_line() -> str:
        try:
            sel = log.curselection()
            if not sel:
                return ""
            return str(log.get(sel[0]) or "")
        except Exception:
            return ""

    def _lote_copy_selected():
        linha = _lote_selected_line()
        if linha:
            _lote_copy_text(linha)

    def _lote_copy_all():
        try:
            linhas = [str(log.get(i) or "") for i in range(log.size())]
            _lote_copy_text("\n".join(linhas))
        except Exception:
            pass

    def _lote_clear_log():
        try:
            log.delete(0, "end")
        except Exception:
            pass

    def _lote_context_menu(event):
        try:
            idx = log.nearest(event.y)
            if idx >= 0:
                log.selection_clear(0, "end")
                log.selection_set(idx)
                log.activate(idx)
        except Exception:
            pass

        menu = Menu(win, tearoff=False)
        menu.add_command(label="Preparar SIPPES", command=_preparar_sippes_lote)
        menu.add_command(label="Iniciar lote em segundo plano", command=_start, state=("disabled" if running.get("value") else "normal"))
        menu.add_command(label=("Parar após o atual" if running.get("value") else "Fechar janela"), command=_cancel)
        menu.add_separator()
        menu.add_command(label="Auditar salvos deste mês", command=lambda: abrir_janela_auditoria_contracheques(win, militares, on_open_edit=on_open_edit, on_open_carteira=on_open_carteira))
        menu.add_command(label="Abrir pasta raiz dos contracheques", command=_open_report_folder)
        menu.add_separator()
        menu.add_command(label="Copiar linha selecionada", command=_lote_copy_selected, state=("normal" if _lote_selected_line() else "disabled"))
        menu.add_command(label="Copiar log completo", command=_lote_copy_all, state=("normal" if log.size() else "disabled"))
        menu.add_command(label="Limpar log", command=_lote_clear_log, state=("normal" if log.size() else "disabled"))
        try:
            menu.tk_popup(event.x_root, event.y_root)
        finally:
            try:
                menu.grab_release()
            except Exception:
                pass
        return "break"

    for _w in (log, body, win):
        try:
            _w.bind("<Button-3>", _lote_context_menu, add="+")
            _w.bind("<Button-2>", _lote_context_menu, add="+")
        except Exception:
            pass

    def _on_close():
        if running.get("value"):
            if messagebox.askyesno(
                "Lote em andamento",
                "O lote ainda está rodando em segundo plano.\n\nDeseja pedir parada após o item atual?",
                parent=win,
            ):
                stop_event.set()
            return
        try:
            win.destroy()
        except Exception:
            pass

    win.protocol("WM_DELETE_WINDOW", _on_close)


# =========================
#   AUDITORIA — Leitura de contracheques salvos
# =========================
def _parse_money_br(value: str) -> float | None:
    try:
        s = str(value or "").strip()
        if not s:
            return None
        s = s.replace("R$", "").replace(" ", "")
        neg = s.startswith("-")
        s = s.lstrip("+-")
        if "," in s and "." in s:
            if s.rfind(",") > s.rfind("."):
                s = s.replace(".", "").replace(",", ".")
            else:
                s = s.replace(",", "")
        elif "," in s:
            s = s.replace(".", "").replace(",", ".")
        v = float(s)
        return -v if neg else v
    except Exception:
        return None


def _fmt_money_br(v) -> str:
    try:
        if v is None:
            return "—"
        return (f"R$ {float(v):,.2f}".replace(",", "X").replace(".", ",").replace("X", "."))
    except Exception:
        return "—"


def _norm_text(s: str) -> str:
    return _strip_accents(str(s or "")).upper()


def _extract_pdf_text(path: Path) -> str:
    """Extrai texto do PDF usando bibliotecas disponíveis no computador."""
    errors = []
    try:
        import fitz  # PyMuPDF  # type: ignore
        doc = fitz.open(str(path))
        parts = []
        for page in doc:
            parts.append(page.get_text("text") or "")
        try:
            doc.close()
        except Exception:
            pass
        txt = "\n".join(parts).strip()
        if txt:
            return txt
    except Exception as e:
        errors.append(f"PyMuPDF: {e}")

    try:
        from pypdf import PdfReader  # type: ignore
        reader = PdfReader(str(path))
        parts = []
        for page in reader.pages:
            try:
                parts.append(page.extract_text() or "")
            except Exception:
                pass
        txt = "\n".join(parts).strip()
        if txt:
            return txt
    except Exception as e:
        errors.append(f"pypdf: {e}")

    try:
        from PyPDF2 import PdfReader  # type: ignore
        reader = PdfReader(str(path))
        parts = []
        for page in reader.pages:
            try:
                parts.append(page.extract_text() or "")
            except Exception:
                pass
        txt = "\n".join(parts).strip()
        if txt:
            return txt
    except Exception as e:
        errors.append(f"PyPDF2: {e}")

    raise RuntimeError(
        "Não consegui ler o texto do contracheque. Instale uma destas bibliotecas: pymupdf ou pypdf. "
        "Exemplo: pip install pymupdf\n" + " | ".join(errors[-2:])
    )


_MONEY_RE = re.compile(r"(?<!\d)(?:R\$\s*)?[-+]?\d{1,3}(?:\.\d{3})*,\d{2}|(?<!\d)(?:R\$\s*)?[-+]?\d+,\d{2}(?!\d)")


def _money_values_from_line(line: str) -> list[float]:
    vals = []
    for m in _MONEY_RE.finditer(str(line or "")):
        v = _parse_money_br(m.group(0))
        if v is not None:
            vals.append(v)
    return vals


def _rubrica_valor_da_linha(line: str) -> float | None:
    """Retorna o valor financeiro principal da rubrica.

    Nos contracheques do CPEx a linha vem, em regra:
        RUBRICA DESCRIÇÃO VALOR % R/D IR PARC

    Portanto o valor correto é o PRIMEIRO valor monetário da linha. O segundo
    valor costuma ser o percentual, por isso não pode ser usado para auditoria.
    """
    vals = _money_values_from_line(line)
    if not vals:
        return None
    return abs(float(vals[0]))


def _iter_rubrica_lines(text: str):
    """Itera linhas de rubricas do contracheque, preservando o código.

    O PDF do CPEx pode sair de duas formas na extração:
      1) tudo em uma linha: "NR0001 SOLDO 5.209,00 100,00 R +";
      2) tabela quebrada em várias linhas:
            NR0001
            SOLDO
            5.209,00 100,00
            R
            +
    Esta função junta os blocos para a auditoria enxergar a rubrica completa.
    """
    raw_lines = [" ".join(str(x or "").split()).strip() for x in str(text or "").splitlines()]
    lines = [x for x in raw_lines if x]
    stop_labels = {
        "DATA IMP/PRACA", "DATA IMP/PRAÇA", "DEP IR:", "ISENTO IR:", "RECEITA:", "DESPESA:",
        "LIQUIDO:", "LÍQUIDO:", "BANCO:", "AGENCIA:", "AGÊNCIA:", "C/C:", "IDT MARGEM:",
        "SITUACAO:", "SITUAÇÃO:", "2º VIA", "2 VIA"
    }
    i = 0
    while i < len(lines):
        line = lines[i]
        n = _norm_text(line)
        m = re.match(r"^([A-Z]{2}\d{4})\b", n)
        if not m:
            i += 1
            continue

        code = m.group(1)
        parts = [line]
        j = i + 1
        while j < len(lines):
            nxt = lines[j]
            nn = _norm_text(nxt)
            if re.match(r"^[A-Z]{2}\d{4}\b", nn):
                break
            if any(nn.startswith(lbl) for lbl in stop_labels):
                break
            parts.append(nxt)
            j += 1

        joined = " ".join(parts)
        yield joined, _norm_text(joined), code
        i = j

def _sum_by_keywords(text: str, keywords: list[str], *, require_any: list[str] | None = None, exclude: list[str] | None = None) -> tuple[float, list[str]]:
    """Soma rubricas por palavra-chave usando o VALOR, não o percentual.

    Esta função agora só considera linhas de rubrica (NR/ND/DR/etc.). Isso evita
    falso positivo em cabeçalho, rodapé e totais.
    """
    total = 0.0
    matched = []
    kws = [_norm_text(k) for k in keywords]
    req = [_norm_text(k) for k in (require_any or [])]
    exc = [_norm_text(k) for k in (exclude or [])]
    for line, n, _code in _iter_rubrica_lines(text):
        if not any(k in n for k in kws):
            continue
        if req and not any(k in n for k in req):
            continue
        if exc and any(k in n for k in exc):
            continue
        v = _rubrica_valor_da_linha(line)
        if v is None:
            continue
        total += abs(float(v))
        matched.append(line)
    return total, matched


def _sum_rubricas(text: str, *, keywords: list[str] | None = None, codes: set[str] | None = None,
                  code_prefixes: tuple[str, ...] | None = None, exclude: list[str] | None = None) -> tuple[float, list[str]]:
    """Soma rubricas com filtro por código, prefixo e/ou palavras.

    Exemplos:
      - FUSEx 3% real: code ND0001
      - Auxílio-transporte normal: code NR0095
      - Diferenças/atrasados: prefixo DR ou descrição com ATRAS/DIFERENÇA/AR
    """
    total = 0.0
    matched: list[str] = []
    kws = [_norm_text(k) for k in (keywords or [])]
    exc = [_norm_text(k) for k in (exclude or [])]
    codes_norm = {str(c or "").upper().strip() for c in (codes or set())}
    prefixes = tuple(str(p or "").upper().strip() for p in (code_prefixes or tuple()) if str(p or "").strip())

    for line, n, code in _iter_rubrica_lines(text):
        if codes_norm and code not in codes_norm:
            continue
        if prefixes and not any(code.startswith(px) for px in prefixes):
            continue
        if kws and not any(k in n for k in kws):
            continue
        if exc and any(k in n for k in exc):
            continue
        v = _rubrica_valor_da_linha(line)
        if v is None:
            continue
        total += abs(float(v))
        matched.append(line)
    return total, matched


def _find_summary_money(text: str, label: str) -> float | None:
    """Lê totais do rodapé como RECEITA, DESPESA e LÍQUIDO."""
    lab = _norm_text(label)
    linhas = str(text or "").splitlines()
    for idx, raw in enumerate(linhas):
        line = " ".join(str(raw or "").split()).strip()
        n = _norm_text(line)
        bloco = line
        if lab in n:
            for extra in range(1, 3):
                if idx + extra < len(linhas):
                    bloco += " " + " ".join(str(linhas[idx + extra] or "").split())
            vals = _money_values_from_line(bloco)
            if vals:
                return abs(float(vals[0]))
    return None




def _extract_label_value(text: str, label: str) -> str:
    """Extrai valor logo após um rótulo do rodapé do contracheque."""
    linhas = [" ".join(str(x or "").split()).strip() for x in str(text or "").splitlines()]
    lab_norm = _norm_text(label).replace(":", "").strip()
    stop_words = (
        "IDENTIFICADOR", "PREC/CP", "NOME", "CPF", "CATEGORIA", "FOLHA", "P/G", "RUBRICA",
        "DATA IMP", "DEP IR", "ISENTO IR", "RECEITA", "DESPESA", "LIQUIDO", "LÍQUIDO",
        "BANCO", "AGENCIA", "AGÊNCIA", "C/C", "CONTA", "IDT MARGEM", "SITUACAO", "SITUAÇÃO", "2º VIA", "2 VIA"
    )
    for i, line in enumerate(linhas):
        if not line:
            continue
        n = _norm_text(line).replace(":", "").strip()
        if not n.startswith(lab_norm):
            continue
        val = ""
        if ":" in line:
            val = line.split(":", 1)[1].strip()
        else:
            val = line[len(label):].strip() if len(line) > len(label) else ""
        if not val or _norm_text(val).replace(":", "").strip() in stop_words:
            for j in range(i + 1, min(i + 4, len(linhas))):
                nxt = linhas[j]
                if not nxt:
                    continue
                nn = _norm_text(nxt).replace(":", "").strip()
                if any(nn.startswith(sw) for sw in stop_words if sw != lab_norm):
                    if lab_norm in ("C/C", "CONTA") and "IDT MARGEM" in nn:
                        val = nxt
                    break
                val = nxt
                break
        val = re.split(r"\b(?:IDT\s+MARGEM|SITUA[ÇC][AÃ]O|BANCO|AG[ÊE]NCIA|C/C|RECEITA|DESPESA|L[IÍ]QUIDO)\b\s*:?,?", val, flags=re.IGNORECASE)[0].strip()
        return val
    return ""


def _extract_dados_bancarios_pdf(text: str) -> dict:
    banco = _extract_label_value(text, "BANCO")
    agencia = _extract_label_value(text, "AGÊNCIA") or _extract_label_value(text, "AGENCIA")
    conta = _extract_label_value(text, "C/C") or _extract_label_value(text, "CONTA")
    situacao = _extract_label_value(text, "SITUAÇÃO") or _extract_label_value(text, "SITUACAO")
    return {
        "banco_pdf": banco.strip(),
        "agencia_pdf": agencia.strip(),
        "conta_pdf": conta.strip(),
        "situacao_pagamento": situacao.strip() or "—",
    }


def _bank_code(value: str) -> str:
    s = str(value or "").strip()
    m = re.search(r"(\d{3})", s)
    if m:
        return m.group(1)
    n = _norm_text(s)
    mapa = {
        "BANCO DO BRASIL": "001",
        "BRASIL S.A": "001",
        "ITAU": "341",
        "ITAÚ": "341",
        "SANTANDER": "033",
        "BRADESCO": "237",
        "CAIXA": "104",
        "CEF": "104",
    }
    for k, v in mapa.items():
        if _norm_text(k) in n:
            return v
    d = _only_digits(s)
    return d.zfill(3) if d and len(d) <= 3 else d


def _norm_num_for_compare(value: str) -> str:
    d = _only_digits(str(value or ""))
    return d.lstrip("0") or ("0" if d else "")


def _db_bank_info_from_ref(mref: MilitarRef) -> dict:
    """Obtém banco/agência/conta salvos no cadastro."""
    out = {
        "banco_db": str(getattr(mref, "banco", "") or "").strip(),
        "agencia_db": str(getattr(mref, "agencia", "") or "").strip(),
        "conta_db": str(getattr(mref, "conta", "") or "").strip(),
    }
    if all(out.values()):
        return out
    mid = str(getattr(mref, "id_militar", "") or "").strip()
    if not mid:
        return out
    try:
        import sqlite3, importlib
        dbp = None
        for modname in ("database.db", "db"):
            try:
                mod = importlib.import_module(modname)
                if hasattr(mod, "get_db_path"):
                    dbp = mod.get_db_path(); break
                if hasattr(mod, "DB_PATH"):
                    dbp = getattr(mod, "DB_PATH"); break
            except Exception:
                continue
        if not dbp:
            return out
        with sqlite3.connect(dbp) as con:
            con.row_factory = sqlite3.Row
            row = con.execute("SELECT * FROM militares WHERE id = ?", (mid,)).fetchone()
        if not row:
            return out
        rowd = {k: row[k] for k in row.keys()}
        def pick(*names):
            for name in names:
                if name in rowd and rowd[name] not in (None, ""):
                    return str(rowd[name]).strip()
            return ""
        out["banco_db"] = out["banco_db"] or pick("banco", "bank")
        out["agencia_db"] = out["agencia_db"] or pick("agencia", "agência", "ag")
        out["conta_db"] = out["conta_db"] or pick("conta", "cc", "conta_corrente", "c_c")
    except Exception:
        pass
    return out


def _comparar_dados_bancarios(text: str, mref: MilitarRef) -> dict:
    pdf = _extract_dados_bancarios_pdf(text)
    db = _db_bank_info_from_ref(mref)
    b_pdf = _bank_code(pdf.get("banco_pdf", "")); b_db = _bank_code(db.get("banco_db", ""))
    ag_pdf = _norm_num_for_compare(pdf.get("agencia_pdf", "")); ag_db = _norm_num_for_compare(db.get("agencia_db", ""))
    cc_pdf = _norm_num_for_compare(pdf.get("conta_pdf", "")); cc_db = _norm_num_for_compare(db.get("conta_db", ""))
    diffs = []
    if b_pdf and b_db and b_pdf != b_db: diffs.append("banco")
    if ag_pdf and ag_db and ag_pdf != ag_db: diffs.append("agência")
    if cc_pdf and cc_db and cc_pdf != cc_db: diffs.append("conta")
    if diffs:
        status = "DIVERGENTE: " + ", ".join(diffs)
    elif any((b_pdf, ag_pdf, cc_pdf)) and any((b_db, ag_db, cc_db)):
        status = "OK"
    elif any((b_pdf, ag_pdf, cc_pdf)):
        status = "Contracheque sem comparação BD"
    else:
        status = "Não localizado"
    situacao_pgto = (pdf.get("situacao_pagamento") or "—").strip()
    situacao_norm = _norm_text(situacao_pgto)
    situacao_diferente = bool(situacao_pgto and situacao_pgto != "—" and situacao_norm not in ("NORMAL", "SITUACAO NORMAL"))
    return {**pdf, **db, "banco_status": status, "banco_divergente": bool(diffs), "situacao_pagamento": situacao_pgto, "situacao_diferente": situacao_diferente}

def _expected_aux_transporte(mref: MilitarRef) -> float | None:
    """Calcula estimativa do auxílio-transporte líquido pelo que está salvo no banco.

    Fórmula usada no SIGFUR/SAT: (soma tarifas ida/volta x 22 dias) - cota 6% do soldo proporcional a 22/30.
    """
    try:
        mid = int(str(getattr(mref, "id_militar", "") or "0"))
        if not mid:
            return None
    except Exception:
        return None

    try:
        try:
            from database.db import aux_tarifas_listar, obter_soldo_por_posto  # type: ignore
        except Exception:
            from db import aux_tarifas_listar, obter_soldo_por_posto  # type: ignore
    except Exception:
        return None

    try:
        tarifas = list(aux_tarifas_listar(mid) or [])
    except Exception:
        tarifas = []
    vals = []
    for t in tarifas:
        v = _parse_money_br(str(t))
        if v and v > 0:
            vals.append(v)
    if not vals:
        return None
    try:
        soldo = _parse_money_br(str(obter_soldo_por_posto(mref.pg) or "")) or 0.0
    except Exception:
        soldo = 0.0
    dias = 22.0
    bruto = sum(vals) * 2.0 * dias
    cota = soldo * 0.06 * (dias / 30.0) if soldo else 0.0
    return max(0.0, bruto - cota)


def _analisar_texto_contracheque(text: str, mref: MilitarRef) -> dict:
    """Audita as rubricas do contracheque.

    Ajustado a partir dos PDFs reais enviados:
      - FUSEx 3% é a rubrica ND0001, separada de DESCONTO DEPENDENTE - FUSEX e DESPESA MÉDICA - FUSEX.
      - Auxílio-transporte recebido no mês é NR0095. DR0095 é desconto e não conta como recebido.
      - Salário-família usa NR0018 e cada R$ 0,16 representa 1 dependente financeiro.
      - O valor financeiro da rubrica é o primeiro valor monetário da linha; o segundo costuma ser percentual.
    """
    # Auxílio-transporte: separa o que foi RECEBIDO no mês do que foi DESCONTADO.
    # Regra confirmada: toda rubrica DR é desconto/ajuste negativo.
    # Portanto, DR0095 não conta como auxílio recebido no contracheque; fica em campo separado.
    aux_nr, aux_nr_linhas = _sum_rubricas(
        text,
        keywords=["AUX TRANSP", "AUXILIO TRANSP", "AUXÍLIO TRANSP", "TRANSPORTE"],
        codes={"NR0095"},
    )
    aux_dr, aux_dr_linhas = _sum_rubricas(
        text,
        keywords=["AUX TRANSP", "AUXILIO TRANSP", "AUXÍLIO TRANSP", "TRANSPORTE"],
        code_prefixes=("DR",),
    )
    # AR fica separado também; não entra como auxílio normal do mês.
    aux_ar, aux_ar_linhas = _sum_rubricas(
        text,
        keywords=["AUX TRANSP", "AUXILIO TRANSP", "AUXÍLIO TRANSP", "TRANSPORTE"],
        code_prefixes=("AR",),
    )
    # fallback somente quando não houver NR/DR/AR identificado. Isso evita somar DR0095 como recebimento.
    aux_total_kw, aux_kw_linhas = _sum_by_keywords(
        text,
        ["AUX TRANSP", "AUXILIO TRANSP", "AUXÍLIO TRANSP", "TRANSPORTE"],
        exclude=["BASE", "COMPROVANTE"],
    )
    aux = aux_nr
    aux_linhas = aux_nr_linhas
    if aux <= 0 and aux_dr <= 0 and aux_ar <= 0 and aux_total_kw > 0:
        aux = aux_total_kw
        aux_linhas = aux_kw_linhas

    # FUSEx principal: não misturar dependente nem despesa médica.
    fusex, fusex_linhas = _sum_rubricas(text, codes={"ND0001"}, keywords=["FUSEX"])
    if fusex <= 0:
        fusex, fusex_linhas = _sum_by_keywords(
            text,
            ["FUSEX 3%"],
            exclude=["DEPENDENTE", "DESPESA MEDICA", "DESPESA MÉDICA"],
        )

    dep_fusex, dep_fusex_linhas = _sum_rubricas(text, codes={"ND0011"}, keywords=["FUSEX"])
    if dep_fusex <= 0:
        dep_fusex, dep_fusex_linhas = _sum_by_keywords(text, ["DESCONTO DEPENDENTE", "DEPENDENTE - FUSEX"])

    med_fusex, med_fusex_linhas = _sum_rubricas(text, codes={"ND0013"}, keywords=["FUSEX"])
    if med_fusex <= 0:
        med_fusex, med_fusex_linhas = _sum_by_keywords(text, ["DESPESA MEDICA", "DESPESA MÉDICA"])

    sal_fam, sal_fam_linhas = _sum_rubricas(text, codes={"NR0018"}, keywords=["SALARIO FAMILIA", "SALÁRIO FAMÍLIA"])
    if sal_fam <= 0:
        sal_fam, sal_fam_linhas = _sum_by_keywords(text, ["SALARIO FAMILIA", "SALÁRIO FAMÍLIA", "SAL FAMILIA"])

    pre_escolar, pre_escolar_linhas = _sum_rubricas(text, codes={"NR0077"}, keywords=["PRE-ESCOLAR", "PRÉ-ESCOLAR", "ASSISTENCIA PRE"])
    if pre_escolar <= 0:
        pre_escolar, pre_escolar_linhas = _sum_by_keywords(text, ["PRE-ESCOLAR", "PRÉ-ESCOLAR", "ASSISTENCIA PRE", "ASSISTÊNCIA PRÉ"])

    ferias, ferias_linhas = _sum_by_keywords(text, ["FERIAS", "FÉRIAS"])
    aux_alim, aux_alim_linhas = _sum_by_keywords(text, ["AUX ALIMENT", "AUXILIO ALIMENT", "AUXÍLIO ALIMENT", "ALIMENTACAO", "ALIMENTAÇÃO"])

    # Diferenças, atrasados, exercício anterior e rubricas DR/AR.
    dr_ar, dr_ar_linhas = _sum_rubricas(text, code_prefixes=("DR", "AR"))
    atras_kw, atras_kw_linhas = _sum_by_keywords(text, ["ATRAS", "EXERCICIO ANTERIOR", "EXERCÍCIO ANTERIOR", "DIFERENCA", "DIFERENÇA", " AR ", "AR-"])
    atrasados = max(dr_ar, atras_kw)
    atrasados_linhas = (dr_ar_linhas + atras_kw_linhas)[:10]

    pensao_militar, pensao_militar_linhas = _sum_by_keywords(text, ["PENSAO MILITAR", "PENSÃO MILITAR"])
    pensao_alimenticia, pensao_alimenticia_linhas = _sum_by_keywords(text, ["PENSAO ALIMENTICIA", "PENSÃO ALIMENTÍCIA", "PENSAO ALIMENT"])
    pensao = pensao_militar + pensao_alimenticia

    irrf, irrf_linhas = _sum_by_keywords(text, ["IRRF", "IMPOSTO DE RENDA"])
    hab, hab_linhas = _sum_by_keywords(text, ["ADICIONAL DE HABILITACAO", "ADICIONAL DE HABILITAÇÃO", "ADIC HABILIT", "HABILITACAO", "HABILITAÇÃO"])
    ad_disp, ad_disp_linhas = _sum_by_keywords(text, ["AD C DISP MIL", "ADICIONAL DE COMPENSACAO", "ADICIONAL DE COMPENSAÇÃO"])
    pnr, pnr_linhas = _sum_by_keywords(text, ["PNR", "OCUPACAO PNR", "OCUPAÇÃO PNR", "OCUPA PNR"])
    emprestimos, emprestimos_linhas = _sum_by_keywords(text, ["FHE", "FIN IMOB", "CEF EMPREST", "SABEMI", "SEGUROS", "CSSE", "PEC"])

    receita = _find_summary_money(text, "RECEITA")
    despesa = _find_summary_money(text, "DESPESA")
    liquido = _find_summary_money(text, "LÍQUIDO") or _find_summary_money(text, "LIQUIDO")
    banco_info = _comparar_dados_bancarios(text, mref)

    expected_aux = _expected_aux_transporte(mref)
    diff_aux = None
    aux_status = "—"
    # Comparação com o banco usa APENAS o auxílio normal recebido no mês (NR0095).
    # DR0095 é desconto; se houver só DR, o filtro "sem auxílio no contracheque" deve mostrar o militar.
    aux_para_comparar = aux
    if expected_aux is not None:
        diff_aux = aux_para_comparar - expected_aux
        if aux_para_comparar > 0:
            aux_status = "OK" if abs(diff_aux) <= 1.00 else "DIVERGENTE"
        else:
            aux_status = "NÃO RECEBEU"
        if aux_dr > 0:
            aux_status = f"{aux_status} + DESC DR"
    elif aux > 0:
        aux_status = "RECEBE NO CONTRACHEQUE"
    elif aux_dr > 0:
        aux_status = "SÓ DESCONTO DR"
    elif expected_aux is not None and aux <= 0:
        aux_status = "NÃO RECEBEU"

    dependentes = 0
    if sal_fam > 0:
        dependentes = int(round(sal_fam / 0.16)) if sal_fam else 0

    alertas = []
    if "DIVERGENTE" in aux_status:
        alertas.append("Aux transporte diferente")
    if "NÃO RECEBEU" in aux_status:
        alertas.append("Aux transporte previsto no banco, mas não localizado")
    if aux_dr > 0:
        alertas.append(f"Desconto de aux transporte (DR) {_fmt_money_br(aux_dr)}")
    if aux_ar > 0:
        alertas.append(f"Aux transporte AR {_fmt_money_br(aux_ar)}")
    if sal_fam > 0:
        alertas.append(f"Salário-família ({dependentes} dep.)")
    if pre_escolar > 0:
        alertas.append("Assistência pré-escolar")
    if ferias > 0:
        alertas.append("Recebeu férias")
    if aux_alim > 0:
        alertas.append("Recebeu aux alimentação")
    if atrasados > 0:
        alertas.append("DR/AR/atrasados/diferença")
    if fusex <= 0:
        alertas.append("Sem FUSEx 3% localizado")
    if pensao_alimenticia > 0:
        alertas.append("Pensão alimentícia")
    if pnr > 0:
        alertas.append("PNR")
    if emprestimos > 0:
        alertas.append("Empréstimo/seguro/FHE")
    if banco_info.get("banco_divergente"):
        alertas.append("Banco/agência/conta diferente do cadastro")
    if banco_info.get("situacao_diferente"):
        alertas.append(f"Situação de pagamento: {banco_info.get('situacao_pagamento')}")

    return {
        "aux_pdf": aux,
        "aux_normal": aux_nr,
        "aux_dr": aux_dr,
        "aux_ar": aux_ar,
        "aux_db": expected_aux,
        "aux_diff": diff_aux,
        "aux_status": aux_status,
        "fusex": fusex,
        "dep_fusex": dep_fusex,
        "med_fusex": med_fusex,
        "salario_familia": sal_fam,
        "dependentes": dependentes,
        "pre_escolar": pre_escolar,
        "ferias": ferias,
        "aux_alimentacao": aux_alim,
        "atrasados": atrasados,
        "pensao": pensao,
        "pensao_militar": pensao_militar,
        "pensao_alimenticia": pensao_alimenticia,
        "irrf": irrf,
        "adicional_habilitacao": hab,
        "ad_c_disp_mil": ad_disp,
        "pnr": pnr,
        "emprestimos": emprestimos,
        "receita": receita,
        "despesa": despesa,
        "liquido": liquido,
        "banco_pdf": banco_info.get("banco_pdf", ""),
        "agencia_pdf": banco_info.get("agencia_pdf", ""),
        "conta_pdf": banco_info.get("conta_pdf", ""),
        "banco_db": banco_info.get("banco_db", ""),
        "agencia_db": banco_info.get("agencia_db", ""),
        "conta_db": banco_info.get("conta_db", ""),
        "banco_status": banco_info.get("banco_status", ""),
        "banco_divergente": banco_info.get("banco_divergente", False),
        "situacao_pagamento": banco_info.get("situacao_pagamento", "—"),
        "situacao_diferente": banco_info.get("situacao_diferente", False),
        "situacao": "; ".join(alertas) if alertas else "Sem achado relevante",
        "linhas": {
            "aux_transporte": aux_linhas[:8],
            "aux_dr": aux_dr_linhas[:8],
            "aux_ar": aux_ar_linhas[:8],
            "fusex": fusex_linhas[:5],
            "dep_fusex": dep_fusex_linhas[:5],
            "med_fusex": med_fusex_linhas[:5],
            "salario_familia": sal_fam_linhas[:5],
            "pre_escolar": pre_escolar_linhas[:5],
            "ferias": ferias_linhas[:5],
            "aux_alimentacao": aux_alim_linhas[:5],
            "atrasados": atrasados_linhas[:8],
            "pensao_militar": pensao_militar_linhas[:5],
            "pensao_alimenticia": pensao_alimenticia_linhas[:5],
            "irrf": irrf_linhas[:5],
            "habilitacao": hab_linhas[:5],
            "ad_c_disp_mil": ad_disp_linhas[:5],
            "pnr": pnr_linhas[:5],
            "emprestimos": emprestimos_linhas[:5],
        }
    }

def abrir_janela_auditoria_contracheques(parent, militares: list[MilitarRef] | tuple[MilitarRef, ...], *, on_open_edit=None, on_open_carteira=None, on_open_contracheque=None) -> None:
    """Audita contracheques já salvos individualmente por militar."""
    owner = parent if parent is not None else _get_root_owner(parent)
    root = _default_root()
    militares = list(militares or [])
    if not militares:
        messagebox.showwarning("Auditoria de contracheques", "Nenhum militar encontrado para auditar.", parent=parent)
        return

    win = Toplevel(owner)
    win.title("Auditoria de contracheques")
    win.configure(bg="white")
    # Não usa transient/grab_set(): a lista permanece livre durante a auditoria.
    try:
        _apply_persistent_window(win, "auditoria_contracheques", default_geometry="1500x900", parent=owner)
    except Exception:
        win.geometry("1500x900")
    try:
        win.minsize(1080, 680)
    except Exception:
        pass

    now = datetime.now()
    rows_cache: list[dict] = []
    visible_rows: list[dict] = []
    tree_row_map: dict[str, dict] = {}
    sort_state = {"col": "pg", "reverse": False}
    stop_event = threading.Event()
    running = {"value": False}

    def _pg_abrev(pg: str) -> str:
        raw = str(pg or "").strip()
        norm = _norm_text(raw).replace(".", " ").replace("-", " ")
        norm = " ".join(norm.split())
        mp = {
            "1O SARGENTO": "1º Sgt", "1 SARGENTO": "1º Sgt", "1O SGT": "1º Sgt", "1 SGT": "1º Sgt", "1SGT": "1º Sgt",
            "2O SARGENTO": "2º Sgt", "2 SARGENTO": "2º Sgt", "2O SGT": "2º Sgt", "2 SGT": "2º Sgt", "2SGT": "2º Sgt",
            "3O SARGENTO": "3º Sgt", "3 SARGENTO": "3º Sgt", "3O SGT": "3º Sgt", "3 SGT": "3º Sgt", "3SGT": "3º Sgt",
            "SUBTENENTE": "S Ten", "SUB TEN": "S Ten", "S TEN": "S Ten", "ST": "S Ten",
            "CAPITAO": "Cap", "CAP": "Cap",
            "1O TENENTE": "1º Ten", "1 TENENTE": "1º Ten", "1O TEN": "1º Ten", "1 TEN": "1º Ten",
            "2O TENENTE": "2º Ten", "2 TENENTE": "2º Ten", "2O TEN": "2º Ten", "2 TEN": "2º Ten",
            "TENENTE CORONEL": "Ten Cel", "TEN CEL": "Ten Cel", "MAJOR": "Maj", "CORONEL": "Cel", "CEL": "Cel",
            "CABO EFETIVO PROFISSIONAL": "Cb", "CB EF PROFL": "Cb", "CABO": "Cb", "CB": "Cb",
            "SOLDADO EFETIVO PROFISSIONAL": "Sd", "SOLDADO EFETIVO VARIAVEL": "Sd EV", "SD EF PROFL": "Sd", "SD EF VRV": "Sd EV", "SD EV": "Sd EV", "SOLDADO": "Sd", "SD": "Sd",
        }
        if norm in mp:
            return mp[norm]
        if "SUB" in norm and "TEN" in norm: return "S Ten"
        if "SARGENTO" in norm or "SGT" in norm:
            if norm.startswith("1"): return "1º Sgt"
            if norm.startswith("2"): return "2º Sgt"
            if norm.startswith("3"): return "3º Sgt"
            return "Sgt"
        if "TENENTE" in norm or norm.endswith(" TEN"):
            if norm.startswith("1"): return "1º Ten"
            if norm.startswith("2"): return "2º Ten"
            return "Ten"
        if "CABO" in norm or norm.startswith("CB"): return "Cb"
        if "SOLDADO" in norm or norm.startswith("SD"): return "Sd"
        return raw[:12]

    def _unicode_bold_local(txt: str) -> str:
        # Evita caracteres matemáticos que aparecem como quadrados em algumas
        # instalações do Windows/Tk. A tela continua legível com texto normal.
        return str(txt or "")


    def _nome_destacado(nome: str, nome_guerra: str) -> str:
        """Destaca o nome de guerra dentro do nome completo, sem estrela."""
        nome_base = str(nome or "").strip().upper()
        ng_raw = str(nome_guerra or "").strip().upper()
        if not nome_base:
            return ""
        if not ng_raw:
            return nome_base

        nome_norm = _strip_accents(nome_base).upper()
        ng_norm = _strip_accents(ng_raw).upper()

        idx_map: list[int] = []
        for i, ch in enumerate(nome_base):
            folded = _strip_accents(ch).upper() or ch
            for _ in folded:
                idx_map.append(i)

        palavras = []
        for m in re.finditer(r"\S+", nome_base):
            trecho = nome_base[m.start():m.end()]
            palavras.append((m.start(), m.end(), trecho, _strip_accents(trecho).upper()))

        ranges: list[tuple[int, int]] = []
        usados: set[int] = set()

        def add_range(b: int, e: int) -> bool:
            if b < 0 or e <= b:
                return False
            pos = set(range(b, e))
            if usados & pos:
                return False
            usados.update(pos)
            ranges.append((b, e))
            return True

        # 1) Tenta casar o nome de guerra completo dentro do nome.
        idx = nome_norm.find(ng_norm) if ng_norm else -1
        if idx >= 0 and idx < len(idx_map):
            fim_norm = idx + len(ng_norm) - 1
            if fim_norm < len(idx_map):
                add_range(idx_map[idx], idx_map[fim_norm] + 1)

        # 2) Tenta por tokens para casos como "D TAVARES".
        if not ranges:
            tokens = [t for t in ng_norm.split() if t]
            for tok in tokens:
                if len(tok) > 1:
                    idx = nome_norm.find(tok)
                    if idx >= 0 and idx < len(idx_map):
                        fim_norm = idx + len(tok) - 1
                        if fim_norm < len(idx_map):
                            add_range(idx_map[idx], idx_map[fim_norm] + 1)
            for tok in tokens:
                if len(tok) == 1:
                    for wb, _we, _wtxt, wnorm in palavras:
                        if wnorm.startswith(tok):
                            add_range(wb, wb + 1)
                            break

        if not ranges:
            # Não achou dentro do nome: mostra no final destacado, sem usar estrela.
            return f"{nome_base} ({_unicode_bold_local(ng_raw)})"

        ranges.sort()
        partes: list[str] = []
        cur = 0
        for b, e in ranges:
            if cur < b:
                partes.append(nome_base[cur:b])
            partes.append(_unicode_bold_local(nome_base[b:e]))
            cur = e
        if cur < len(nome_base):
            partes.append(nome_base[cur:])
        return "".join(partes)

    def _achado_resumo(row: dict) -> str:
        if not _row_pdf_ok(row):
            return "SEM CONTRACHEQUE"
        itens = []
        if bool(row.get("banco_divergente")) or "DIVERG" in _norm_text(row.get("banco_status")):
            itens.append("Banco")
        if bool(row.get("situacao_diferente")):
            itens.append("Sit. pgto")
        if "DIVERG" in _norm_text(row.get("aux_status")) or "NAO RECEBEU" in _norm_text(row.get("aux_status")) or "NÃO RECEBEU" in _norm_text(row.get("aux_status")):
            itens.append("Aux")
        if _row_has(row, "aux_dr"):
            itens.append("Desc. Aux DR")
        elif _row_has(row, "atrasados"):
            itens.append("DR/AR")
        if not _row_has(row, "fusex"):
            itens.append("Sem FUSEx")
        if _row_has(row, "salario_familia"):
            itens.append(f"Sal Fam {row.get('dependentes', 0)}dep")
        if _row_has(row, "pre_escolar"):
            itens.append("Pré")
        if _row_has(row, "ferias"):
            itens.append("Férias")
        if _row_has(row, "aux_alimentacao"):
            itens.append("Alim")
        if _row_has(row, "pensao_alimenticia"):
            itens.append("Pens. alim")
        if _row_has(row, "pnr"):
            itens.append("PNR")
        if _row_has(row, "emprestimos"):
            itens.append("Empr/FHE")
        return ", ".join(itens[:5]) + ("…" if len(itens) > 5 else "") if itens else "OK"

    filtro_opcoes = [
        "Todos",
        "Somente com achado",
        "Divergências / verificar",
        "Sem contracheque",
        "Com FUSEx 3%",
        "Sem FUSEx 3%",
        "Com Aux Transporte no contracheque",
        "Sem Aux Transporte no contracheque",
        "Aux Transporte divergente",
        "Aux Transporte OK",
        "Com desconto DR de Aux Transporte",
        "Com Salário Família",
        "Sem Salário Família",
        "Com Pré-escolar",
        "Sem Pré-escolar",
        "Com Férias",
        "Sem Férias",
        "Com Aux Alimentação",
        "Sem Aux Alimentação",
        "Com DR/AR/Atrasados",
        "Sem DR/AR/Atrasados",
        "Com Pensão",
        "Com Pensão Alimentícia",
        "Com IRRF",
        "Com Adicional Habilitação",
        "Com PNR",
        "Com Empréstimo/Seguro/FHE",
        "Com Desconto Dependente FUSEx",
        "Com Despesa Médica FUSEx",
        "Banco/Conta divergente",
        "Banco/Conta OK",
        "Situação diferente de Normal",
        "Pagamento suspenso",
        "Situação Normal",
    ]

    def _row_money(row: dict, key: str) -> float:
        try:
            return float(row.get(key) or 0)
        except Exception:
            return 0.0

    def _row_has(row: dict, key: str) -> bool:
        return abs(_row_money(row, key)) > 0.004

    def _row_pdf_ok(row: dict) -> bool:
        try:
            p = str(row.get("pdf_path") or "")
            return bool(p) and Path(p).exists()
        except Exception:
            return False

    def _matches_filter(row: dict) -> bool:
        filtro = (filtro_var.get() or "Todos").strip()
        busca = _norm_text(search_var.get())
        try:
            pg_filtro = (pg_filter_var.get() or "Todos").strip()
            if pg_filtro and pg_filtro != "Todos" and _pg_abrev(row.get("pg", "")) != pg_filtro:
                return False
        except Exception:
            pass
        if busca:
            blob = _norm_text(" ".join(str(row.get(k, "")) for k in ("militar", "situacao", "pdf", "aux_status", "banco_status", "situacao_pagamento", "banco_pdf", "banco_db", "agencia_pdf", "agencia_db", "conta_pdf", "conta_db")))
            if busca not in blob:
                return False

        pdf_ok = _row_pdf_ok(row)
        situacao = _norm_text(row.get("situacao"))
        aux_status = _norm_text(row.get("aux_status"))

        if filtro == "Todos":
            return True
        if filtro == "Somente com achado":
            return _achado_resumo(row) not in ("", "OK")
        if filtro == "Divergências / verificar":
            return ("DIVERG" in aux_status) or ("DIFERENTE" in situacao) or ("SEM FUSEX" in situacao) or (not pdf_ok)
        if filtro == "Sem contracheque":
            return not pdf_ok
        if filtro in ("Com FUSEx", "Com FUSEx 3%"):
            return _row_has(row, "fusex")
        if filtro in ("Sem FUSEx", "Sem FUSEx 3%"):
            return pdf_ok and not _row_has(row, "fusex")
        if filtro in ("Com Aux Transporte no contracheque", "Com Aux Transporte no PDF"):
            return _row_has(row, "aux_pdf")
        if filtro in ("Sem Aux Transporte no contracheque", "Sem Aux Transporte no PDF"):
            return pdf_ok and not _row_has(row, "aux_pdf")
        if filtro == "Aux Transporte divergente":
            return "DIVERG" in aux_status or "NAO RECEBEU" in aux_status or "NÃO RECEBEU" in aux_status
        if filtro == "Aux Transporte OK":
            return aux_status.startswith("OK")
        if filtro in ("Com desconto DR de Aux Transporte", "Com Aux Transporte DR/AR"):
            return _row_has(row, "aux_dr")
        if filtro == "Com Salário Família":
            return _row_has(row, "salario_familia")
        if filtro == "Sem Salário Família":
            return pdf_ok and not _row_has(row, "salario_familia")
        if filtro == "Com Pré-escolar":
            return _row_has(row, "pre_escolar")
        if filtro == "Sem Pré-escolar":
            return pdf_ok and not _row_has(row, "pre_escolar")
        if filtro == "Com Férias":
            return _row_has(row, "ferias")
        if filtro == "Sem Férias":
            return pdf_ok and not _row_has(row, "ferias")
        if filtro == "Com Aux Alimentação":
            return _row_has(row, "aux_alimentacao")
        if filtro == "Sem Aux Alimentação":
            return pdf_ok and not _row_has(row, "aux_alimentacao")
        if filtro in ("Com AR/Atrasados", "Com DR/AR/Atrasados"):
            return _row_has(row, "atrasados")
        if filtro in ("Sem AR/Atrasados", "Sem DR/AR/Atrasados"):
            return pdf_ok and not _row_has(row, "atrasados")
        if filtro == "Com Pensão":
            return _row_has(row, "pensao")
        if filtro == "Com Pensão Alimentícia":
            return _row_has(row, "pensao_alimenticia")
        if filtro == "Com IRRF":
            return _row_has(row, "irrf")
        if filtro == "Com Adicional Habilitação":
            return _row_has(row, "adicional_habilitacao")
        if filtro == "Com PNR":
            return _row_has(row, "pnr")
        if filtro == "Com Empréstimo/Seguro/FHE":
            return _row_has(row, "emprestimos")
        if filtro == "Com Desconto Dependente FUSEx":
            return _row_has(row, "dep_fusex")
        if filtro == "Com Despesa Médica FUSEx":
            return _row_has(row, "med_fusex")
        if filtro == "Banco/Conta divergente":
            return bool(row.get("banco_divergente")) or "DIVERG" in _norm_text(row.get("banco_status"))
        if filtro == "Banco/Conta OK":
            return _norm_text(row.get("banco_status")) == "OK"
        if filtro == "Situação diferente de Normal":
            return bool(row.get("situacao_diferente"))
        if filtro == "Pagamento suspenso":
            return "SUSPENS" in _norm_text(row.get("situacao_pagamento"))
        if filtro == "Situação Normal":
            return _norm_text(row.get("situacao_pagamento")) == "NORMAL"
        return True

    header = Frame(win, bg="#0d47a1")
    header.pack(fill="x")
    Label(header, text="Auditoria de Contracheques", bg="#0d47a1", fg="white", font=("Segoe UI", 12, "bold")).pack(side="left", padx=12, pady=10)
    Label(header, text="Duplo clique abre contracheque • botão direito mostra opções e filtros rápidos", bg="#0d47a1", fg="#bbdefb", font=("Segoe UI", 9, "bold")).pack(side="left", padx=8)

    top = Frame(win, bg="white")
    top.pack(fill="x", padx=12, pady=(10, 4))
    Label(top, text="Ano:", bg="white", fg="#263238", font=("Segoe UI", 9, "bold")).pack(side="left")
    year_var = StringVar(value=str(now.year))
    ttk.Combobox(top, textvariable=year_var, values=[str(y) for y in range(now.year + 1, now.year - 7, -1)], width=8, state="readonly").pack(side="left", padx=(6, 14))
    Label(top, text="Mês:", bg="white", fg="#263238", font=("Segoe UI", 9, "bold")).pack(side="left")
    month_var = StringVar(value=_PT_MONTHS[now.month])
    ttk.Combobox(top, textvariable=month_var, values=_MONTH_NAMES, width=14, state="readonly").pack(side="left", padx=(6, 14))

    filter_bar = Frame(win, bg="white")
    filter_bar.pack(fill="x", padx=12, pady=(0, 6))
    Label(filter_bar, text="Filtro:", bg="white", fg="#263238", font=("Segoe UI", 9, "bold")).pack(side="left")
    filtro_var = StringVar(value="Todos")
    cb_filtro = ttk.Combobox(filter_bar, textvariable=filtro_var, values=filtro_opcoes, width=28, state="readonly")
    cb_filtro.pack(side="left", padx=(6, 10))
    Label(filter_bar, text="P/G:", bg="white", fg="#263238", font=("Segoe UI", 9, "bold")).pack(side="left")
    pg_filter_var = StringVar(value="Todos")
    cb_pg = ttk.Combobox(filter_bar, textvariable=pg_filter_var, values=["Todos"], width=10, state="readonly")
    cb_pg.pack(side="left", padx=(6, 10))
    Label(filter_bar, text="Buscar:", bg="white", fg="#263238", font=("Segoe UI", 9, "bold")).pack(side="left")
    search_var = StringVar(value="")
    ent_busca = ttk.Entry(filter_bar, textvariable=search_var, width=24)
    ent_busca.pack(side="left", padx=(6, 8))
    count_var = StringVar(value="0 resultado(s)")
    Label(filter_bar, textvariable=count_var, bg="white", fg="#00695c", font=("Segoe UI", 9, "bold")).pack(side="left", padx=(8, 0))

    status_var = StringVar(value="Pronto. Clique no cabeçalho da tabela para ordenar. Use Colunas para mostrar só o necessário e botão direito para ações rápidas.")
    Label(win, textvariable=status_var, bg="white", fg="#455a64", font=("Segoe UI", 9, "italic")).pack(anchor="w", padx=12, pady=(0, 6))

    progress = ttk.Progressbar(win, orient="horizontal", mode="determinate", maximum=max(1, len(militares)))
    progress.pack(fill="x", padx=12, pady=(0, 8))

    cols = ("pg", "nome", "pdf", "banco_status", "sit_pgto", "aux_pdf", "aux_db", "aux_status", "fusex", "sal_fam", "dep", "pre", "ferias", "alim", "drar", "situacao")
    headings = {
        "pg": "P/G", "nome": "Nome", "pdf": "Contracheque", "banco_status": "Banco/Conta", "sit_pgto": "Sit. Pgto",
        "aux_pdf": "Aux Contrach.", "aux_db": "Aux BD", "aux_status": "Aux",
        "fusex": "FUSEx", "sal_fam": "Sal Família", "dep": "Dep.", "pre": "Pré",
        "ferias": "Férias", "alim": "Alim.", "drar": "DR/AR", "situacao": "Resumo"
    }
    widths = {"pg": 64, "nome": 360, "pdf": 105, "banco_status": 132, "sit_pgto": 98, "aux_pdf": 82, "aux_db": 82, "aux_status": 118, "fusex": 76,
              "sal_fam": 86, "dep": 48, "pre": 74, "ferias": 74, "alim": 74, "drar": 74, "situacao": 260}
    default_visible = {"pg", "nome", "banco_status", "sit_pgto", "aux_status", "fusex", "sal_fam", "dep", "situacao"}
    col_vars = {c: StringVar(value=("1" if c in default_visible else "0")) for c in cols}

    def _visible_cols():
        out = [c for c in cols if col_vars[c].get() == "1"]
        if "pg" not in out:
            out.insert(0, "pg")
        if "nome" not in out:
            out.insert(1 if out and out[0] == "pg" else 0, "nome")
        return tuple(out)

    # Área redimensionável: tabela em cima e log/observações embaixo.
    # O usuário pode arrastar a divisória para aumentar a parte de baixo em monitores menores.
    paned = ttk.Panedwindow(win, orient="vertical")
    paned.pack(fill="both", expand=True, padx=12, pady=(0, 8))

    tree_wrap = Frame(paned, bg="white")
    try:
        tree_wrap.grid_rowconfigure(0, weight=1)
        tree_wrap.grid_columnconfigure(0, weight=1)
    except Exception:
        pass
    try:
        style = ttk.Style(win)
        style.configure("Auditoria.Treeview", rowheight=27, font=("Segoe UI", 9))
        style.configure("Auditoria.Treeview.Heading", font=("Segoe UI", 9, "bold"))
    except Exception:
        pass
    tv = ttk.Treeview(tree_wrap, columns=cols, show="headings", displaycolumns=_visible_cols(), style="Auditoria.Treeview")
    for c in cols:
        tv.heading(c, text=headings[c], command=lambda col=c: _sort_by_col(col))
        tv.column(c, width=widths.get(c, 90), anchor=("center" if c in ("pg", "dep") else "w"), stretch=(c in ("nome", "situacao")))
    ysb = Scrollbar(tree_wrap, orient="vertical", command=tv.yview)
    xsb = Scrollbar(tree_wrap, orient="horizontal", command=tv.xview)
    tv.configure(yscrollcommand=ysb.set, xscrollcommand=xsb.set)
    tv.grid(row=0, column=0, sticky="nsew")
    ysb.grid(row=0, column=1, sticky="ns")
    xsb.grid(row=1, column=0, sticky="ew")
    try:
        tv.tag_configure("ok", background="#e8f5e9")
        tv.tag_configure("warn", background="#fff8e1")
        tv.tag_configure("bad", background="#ffebee")
        tv.tag_configure("missing", background="#eceff1")
    except Exception:
        pass

    log_wrap = Frame(paned, bg="white", height=120)
    try:
        log_wrap.grid_rowconfigure(0, weight=1)
        log_wrap.grid_columnconfigure(0, weight=1)
    except Exception:
        pass
    log = Listbox(log_wrap, font=("Consolas", 9), height=6)
    log_sb = Scrollbar(log_wrap, orient="vertical", command=log.yview)
    log_xsb = Scrollbar(log_wrap, orient="horizontal", command=log.xview)
    log.configure(yscrollcommand=log_sb.set, xscrollcommand=log_xsb.set)
    log.grid(row=0, column=0, sticky="nsew")
    log_sb.grid(row=0, column=1, sticky="ns")
    log_xsb.grid(row=1, column=0, sticky="ew")

    def _add_pane_resizable():
        try:
            paned.add(tree_wrap, weight=5)
            paned.add(log_wrap, weight=1)
        except Exception:
            try:
                paned.add(tree_wrap)
                paned.add(log_wrap)
            except Exception:
                pass
        def _place_sash():
            try:
                # deixa o log visível, mas a tabela continua predominante
                h = max(680, win.winfo_height())
                paned.sashpos(0, max(420, h - 245))
            except Exception:
                pass
        try:
            win.after(350, _place_sash)
        except Exception:
            pass

    _add_pane_resizable()

    footer = Frame(win, bg="white")
    footer.pack(fill="x", padx=12, pady=(0, 4))
    footer2 = Frame(win, bg="white")
    footer2.pack(fill="x", padx=12, pady=(0, 8))

    def _ui(fn):
        try:
            win.after(0, fn)
        except Exception:
            pass

    def _set_status(text: str):
        _ui(lambda: status_var.set(text))

    def _log(text: str):
        def _do():
            try:
                log.insert("end", text)
                log.yview_moveto(1)
            except Exception:
                pass
        _ui(_do)

    def _row_value_by_col(row: dict, col: str) -> str:
        if col == "pg": return _pg_abrev(row.get("pg", ""))
        if col == "nome": return _nome_destacado(row.get("nome", row.get("militar", "")), row.get("nome_guerra", ""))
        if col == "pdf": return str(row.get("pdf", ""))
        if col == "banco_status": return str(row.get("banco_status", ""))
        if col == "sit_pgto": return str(row.get("situacao_pagamento", ""))
        if col == "aux_pdf": return _fmt_money_br(row.get("aux_pdf"))
        if col == "aux_db": return _fmt_money_br(row.get("aux_db"))
        if col == "aux_status": return str(row.get("aux_status", ""))
        if col == "fusex": return _fmt_money_br(row.get("fusex"))
        if col == "sal_fam": return _fmt_money_br(row.get("salario_familia"))
        if col == "dep": return str(row.get("dependentes", 0) or 0)
        if col == "pre": return _fmt_money_br(row.get("pre_escolar"))
        if col == "ferias": return _fmt_money_br(row.get("ferias"))
        if col == "alim": return _fmt_money_br(row.get("aux_alimentacao"))
        if col == "drar": return _fmt_money_br(row.get("atrasados"))
        if col == "situacao": return _achado_resumo(row)
        return str(row.get(col, ""))

    def _row_values(row: dict):
        return tuple(_row_value_by_col(row, c) for c in cols)

    def _row_tag(row: dict) -> str:
        if not _row_pdf_ok(row): return "missing"
        if bool(row.get("banco_divergente")) or bool(row.get("situacao_diferente")) or "DIVERG" in _norm_text(row.get("aux_status")):
            return "bad"
        if _achado_resumo(row) not in ("OK", ""):
            return "warn"
        return "ok"

    def _pg_sort_rank(pg: str) -> int:
        ordem = ["Gen", "Cel", "Ten Cel", "Maj", "Cap", "1º Ten", "2º Ten", "Asp", "S Ten", "1º Sgt", "2º Sgt", "3º Sgt", "Cb", "Sd", "Sd EV"]
        abv = _pg_abrev(pg)
        try:
            return ordem.index(abv)
        except Exception:
            return 999

    def _sort_key_value(row: dict, col: str):
        try:
            if col == "pg":
                return (_pg_sort_rank(row.get("pg", "")), _norm_text(row.get("nome", row.get("militar", ""))))
            if col == "nome":
                return _norm_text(row.get("nome", row.get("militar", "")))
            if col == "pdf":
                return _norm_text(row.get("pdf", ""))
            if col == "banco_status":
                return (0 if (bool(row.get("banco_divergente")) or "DIVERG" in _norm_text(row.get("banco_status"))) else 1, _norm_text(row.get("banco_status", "")))
            if col == "sit_pgto":
                return (0 if bool(row.get("situacao_diferente")) else 1, _norm_text(row.get("situacao_pagamento", "")))
            if col == "aux_status":
                st = _norm_text(row.get("aux_status", ""))
                prioridade = 0 if ("DIVERG" in st or "NAO RECEBEU" in st or "NÃO RECEBEU" in st) else (1 if st else 2)
                return (prioridade, st)
            numeric_map = {
                "aux_pdf": "aux_pdf", "aux_db": "aux_db", "fusex": "fusex", "sal_fam": "salario_familia",
                "dep": "dependentes", "pre": "pre_escolar", "ferias": "ferias", "alim": "aux_alimentacao", "drar": "atrasados",
            }
            if col in numeric_map:
                try:
                    return float(row.get(numeric_map[col]) or 0)
                except Exception:
                    return 0.0
            if col == "situacao":
                resumo = _achado_resumo(row)
                prioridade = 0 if resumo not in ("OK", "") else 1
                return (prioridade, _norm_text(resumo))
            return _norm_text(_row_value_by_col(row, col))
        except Exception:
            return ""

    def _refresh_heading_arrows():
        try:
            col_ativo = sort_state.get("col") or ""
            rev = bool(sort_state.get("reverse"))
            for c in cols:
                seta = ""
                if c == col_ativo:
                    seta = " ▼" if rev else " ▲"
                tv.heading(c, text=f"{headings[c]}{seta}", command=lambda col=c: _sort_by_col(col))
        except Exception:
            pass

    def _sort_by_col(col: str):
        try:
            if sort_state.get("col") == col:
                sort_state["reverse"] = not bool(sort_state.get("reverse"))
            else:
                sort_state["col"] = col
                sort_state["reverse"] = False
            _refresh_heading_arrows()
            _render_rows()
            try:
                status_var.set(f"Ordenado por {headings.get(col, col)} {'decrescente' if sort_state.get('reverse') else 'crescente'}.")
            except Exception:
                pass
        except Exception:
            pass

    def _sorted_rows(rows: list[dict]) -> list[dict]:
        try:
            col = sort_state.get("col") or "pg"
            reverse = bool(sort_state.get("reverse"))
            return sorted(list(rows), key=lambda r: _sort_key_value(r, col), reverse=reverse)
        except Exception:
            return list(rows)

    _refresh_heading_arrows()

    def _refresh_pg_filter_options():
        try:
            atual = pg_filter_var.get() or "Todos"
            vals = sorted({_pg_abrev(r.get("pg", "")) for r in rows_cache if _pg_abrev(r.get("pg", ""))})
            valores = ["Todos"] + vals
            cb_pg.configure(values=valores)
            if atual not in valores:
                pg_filter_var.set("Todos")
        except Exception:
            pass

    def _render_rows():
        def _do():
            try:
                tree_row_map.clear()
                tv.delete(*tv.get_children())
                visible_rows.clear()
                try:
                    tv.configure(displaycolumns=_visible_cols())
                except Exception:
                    pass
                _refresh_pg_filter_options()
                matched = [row for row in rows_cache if _matches_filter(row)]
                matched = _sorted_rows(matched)
                for row in matched:
                    iid = tv.insert("", "end", values=_row_values(row), tags=(_row_tag(row),))
                    tree_row_map[iid] = row
                    visible_rows.append(row)
                try:
                    _refresh_heading_arrows()
                except Exception:
                    pass
                count_var.set(f"{len(visible_rows)} de {len(rows_cache)} resultado(s)")
            except Exception:
                pass
        _ui(_do)

    def _append_row(row: dict):
        rows_cache.append(row)
        _render_rows()

    def _selected_row() -> dict | None:
        try:
            sel = tv.selection()
            if not sel:
                return None
            return tree_row_map.get(sel[0])
        except Exception:
            return None

    def _abrir_pdf_selecionado(_evt=None):
        row = _selected_row()
        if not row:
            return "break"
        p = Path(str(row.get("pdf_path") or ""))
        if not p.exists():
            messagebox.showwarning("Abrir contracheque", "PDF não encontrado para esta linha.", parent=win)
            return "break"
        try:
            _open_file(p)
        except Exception as e:
            messagebox.showerror("Abrir contracheque", f"Não consegui abrir o PDF.\n\n{e}", parent=win)
        return "break"

    def _open_contracheque_selected():
        """Abre o gerenciador individual de contracheques do militar selecionado.

        Abre sem grab/modal para a Auditoria continuar visível e utilizável.
        Mostra todos os PDFs já salvos daquele militar e mantém as ações de baixar/abrir/excluir.
        """
        row = _selected_row()
        if not row:
            return
        mref = row.get("_mref") or row
        try:
            # Permite que o chamador use uma rotina própria, mas o padrão abre direto por aqui.
            if callable(on_open_contracheque):
                on_open_contracheque(mref)
            else:
                try:
                    win.grab_release()
                except Exception:
                    pass
                abrir_janela_contracheque(win, mref, modal=False)
            try:
                win.after(150, _manter_auditoria_visivel)
                win.after(650, _manter_auditoria_visivel)
            except Exception:
                pass
        except Exception as e:
            messagebox.showerror("Contracheques", f"Não consegui abrir o gerenciador de contracheques.\n\n{e}", parent=win)

    def _manter_auditoria_visivel():
        # Ao abrir Editar/Carteira pelo Listar Militares, algumas janelas podem roubar foco.
        # Mantém a auditoria aberta/visível sem forçar ela por cima da janela recém-aberta.
        try:
            if win.winfo_exists() and str(win.state()).lower() == "iconic":
                win.deiconify()
        except Exception:
            pass

    def _open_edit_selected():
        row = _selected_row()
        if not row or not callable(on_open_edit):
            return
        try:
            on_open_edit(row.get("_mref") or row)
            try:
                win.after(150, _manter_auditoria_visivel)
                win.after(650, _manter_auditoria_visivel)
            except Exception:
                pass
        except Exception as e:
            messagebox.showerror("Editar militar", f"Não consegui abrir a edição.\n\n{e}", parent=win)

    def _open_carteira_selected():
        row = _selected_row()
        if not row or not callable(on_open_carteira):
            return
        try:
            on_open_carteira(row.get("_mref") or row)
            try:
                win.after(150, _manter_auditoria_visivel)
                win.after(650, _manter_auditoria_visivel)
            except Exception:
                pass
        except Exception as e:
            messagebox.showerror("Carteira", f"Não consegui abrir a carteira.\n\n{e}", parent=win)

    def _limpar_filtro():
        filtro_var.set("Todos")
        try:
            pg_filter_var.set("Todos")
        except Exception:
            pass
        search_var.set("")
        _render_rows()

    def _aplicar_colunas():
        try:
            tv.configure(displaycolumns=_visible_cols())
        except Exception:
            pass
        _render_rows()

    def _janela_colunas():
        dlg = Toplevel(win)
        dlg.title("Colunas visíveis")
        dlg.configure(bg="white")
        try:
            dlg.transient(win)
            dlg.geometry("440x520+%d+%d" % (win.winfo_rootx()+80, win.winfo_rooty()+70))
        except Exception:
            pass
        Label(dlg, text="Colunas visíveis", bg="white", fg="#0d47a1", font=("Segoe UI", 12, "bold")).pack(anchor="w", padx=14, pady=(14, 4))
        Label(dlg, text="A lista Word e o CSV filtrado usam exatamente estas colunas.", bg="white", fg="#607d8b", font=("Segoe UI", 9)).pack(anchor="w", padx=14, pady=(0, 10))
        body_cols = Frame(dlg, bg="white")
        body_cols.pack(fill="both", expand=True, padx=14, pady=(0, 10))
        for i, col in enumerate(cols):
            state = "disabled" if col in ("pg", "nome") else "normal"
            ttk.Checkbutton(body_cols, text=headings[col], variable=col_vars[col], onvalue="1", offvalue="0", state=state, command=_aplicar_colunas).grid(row=i//2, column=i%2, sticky="w", padx=8, pady=4)
        btns_cols = Frame(dlg, bg="white")
        btns_cols.pack(fill="x", padx=14, pady=(0, 14))
        def _essencial():
            for c in cols:
                col_vars[c].set("1" if c in default_visible else "0")
            _aplicar_colunas()
        def _todos():
            for c in cols:
                col_vars[c].set("1")
            _aplicar_colunas()
        Button(btns_cols, text="Essencial", command=_essencial, bg="#607d8b", fg="white", bd=0, padx=10, pady=6).pack(side="left")
        Button(btns_cols, text="Mostrar tudo", command=_todos, bg="#1e88e5", fg="white", bd=0, padx=10, pady=6).pack(side="left", padx=8)
        Button(btns_cols, text="Fechar", command=dlg.destroy, bg="#c62828", fg="white", bd=0, padx=10, pady=6).pack(side="right")

    def _export_rows_csv(rows: list[dict], *, filtrado: bool):
        if not rows:
            messagebox.showinfo("Auditoria", "Nenhum resultado para exportar.", parent=win)
            return
        try:
            from tkinter import filedialog
            import csv
            year = int(year_var.get())
            month = _MONTH_TO_NUM.get(month_var.get(), now.month)
            sufixo = "filtrado" if filtrado else "completo"
            path = filedialog.asksaveasfilename(
                parent=win,
                title="Salvar relatório de auditoria",
                defaultextension=".csv",
                initialfile=f"auditoria_contracheques_{sufixo}_{year}_{month:02d}.csv",
                filetypes=[("CSV", "*.csv"), ("Todos", "*.*")],
            )
            if not path:
                return
            csv_cols = list(_visible_cols()) if filtrado else [
                "pg", "nome", "pdf", "banco_status", "sit_pgto", "aux_pdf", "aux_db", "aux_status", "aux_dr",
                "fusex", "dep_fusex", "med_fusex", "sal_fam", "dep", "pre", "ferias", "alim", "drar",
                "pensao", "irrf", "pnr", "situacao"
            ]
            for obrig in ("pg", "nome"):
                if obrig not in csv_cols:
                    csv_cols.insert(0, obrig)
            with open(path, "w", encoding="utf-8-sig", newline="") as f:
                wr = csv.writer(f, delimiter=";")
                wr.writerow(["Auditoria de Contracheques"])
                wr.writerow(["Mês/Ano", f"{_PT_MONTHS.get(month, month)} / {year}", "Filtro", filtro_var.get(), "P/G", pg_filter_var.get(), "Gerado em", datetime.now().strftime("%d/%m/%Y %H:%M")])
                wr.writerow([])
                wr.writerow([headings.get(c, c) for c in csv_cols])
                for r in rows:
                    wr.writerow([_row_value_by_col(r, c) for c in csv_cols])
            _toast(win, "Relatório CSV salvo ✅", 1600)
            _open_file(Path(path))
        except Exception as e:
            messagebox.showerror("Auditoria", f"Não consegui exportar o relatório.\n\n{e}", parent=win)

    def _docx_escape(s: str) -> str:
        return (str(s or "")
                .replace("&", "&amp;")
                .replace("<", "&lt;")
                .replace(">", "&gt;"))

    def _docx_run(texto: str, *, bold: bool = False) -> str:
        pr = "<w:rPr><w:b/></w:rPr>" if bold else ""
        return f'<w:r>{pr}<w:t xml:space="preserve">{_docx_escape(texto)}</w:t></w:r>'

    def _nome_runs_docx(pg: str, nome: str, nome_guerra: str) -> str:
        nome_up = str(nome or "").strip().upper()
        ng = str(nome_guerra or "").strip().upper()
        prefix = (_pg_abrev(pg) + " ") if str(pg or "").strip() else ""
        if not nome_up:
            return _docx_run(prefix.strip())
        if not ng:
            return _docx_run(prefix + nome_up)

        nome_norm = _norm_text(nome_up)
        ng_norm = _norm_text(ng)
        idx = nome_norm.find(ng_norm)
        if idx < 0:
            # tenta por tokens do nome de guerra
            parts = [_docx_run(prefix)]
            palavras_ng = {p for p in ng_norm.split() if p}
            for i, palavra in enumerate(nome_up.split()):
                if i:
                    parts.append(_docx_run(" "))
                parts.append(_docx_run(palavra, bold=_norm_text(palavra) in palavras_ng))
            return "".join(parts)
        before = nome_up[:idx]
        mid = nome_up[idx:idx + len(ng)]
        after = nome_up[idx + len(ng):]
        return _docx_run(prefix + before) + _docx_run(mid, bold=True) + _docx_run(after)

    def _docx_p(runs: str, *, style: str = "") -> str:
        ppr = f"<w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>" if style else ""
        return f"<w:p>{ppr}{runs}</w:p>"

    def _gerar_docx_lista(rows: list[dict]):
        if not rows:
            messagebox.showinfo("Lista filtrada", "Nenhum militar no filtro atual para gerar lista.", parent=win)
            return
        try:
            from tkinter import filedialog
            import zipfile
            year = int(year_var.get())
            month = _MONTH_TO_NUM.get(month_var.get(), now.month)
            filtro = filtro_var.get() or "Todos"
            safe_filtro = re.sub(r"[^A-Za-z0-9_-]+", "_", _strip_accents(filtro)).strip("_") or "filtro"
            path = filedialog.asksaveasfilename(
                parent=win,
                title="Gerar lista filtrada",
                defaultextension=".docx",
                initialfile=f"lista_auditoria_{year}_{month:02d}_{safe_filtro}.docx",
                filetypes=[("Word", "*.docx"), ("Todos", "*.*")],
            )
            if not path:
                return

            body = []
            body.append(_docx_p(_docx_run("LISTA DE AUDITORIA DE CONTRACHEQUES", bold=True), style="Title"))
            body.append(_docx_p(_docx_run(f"Mês/Ano: {_PT_MONTHS.get(month, month)} / {year}", bold=True)))
            body.append(_docx_p(_docx_run(f"Filtro: {filtro} | P/G: {pg_filter_var.get()} | Gerado em: {datetime.now().strftime('%d/%m/%Y %H:%M')}", bold=True)))
            body.append(_docx_p(_docx_run(f"Total: {len(rows)} militar(es)")))
            body.append(_docx_p(_docx_run("")))
            doc_cols = [c for c in _visible_cols() if c not in ("pg", "nome")]
            for i, row in enumerate(rows, start=1):
                nome_runs = _docx_run(f"{i}. ", bold=True) + _nome_runs_docx(row.get("pg", ""), row.get("nome", row.get("militar", "")), row.get("nome_guerra", ""))
                body.append(_docx_p(nome_runs))
                resumo = []
                for c in doc_cols:
                    val = _row_value_by_col(row, c)
                    if val not in ("", "R$ 0,00", "0", "—", None):
                        resumo.append(f"{headings.get(c, c)}: {val}")
                if resumo:
                    body.append(_docx_p(_docx_run("   " + " | ".join(resumo))))
                body.append(_docx_p(_docx_run("")))

            document_xml = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:body>
    %s
    <w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1134" w:right="1134" w:bottom="1134" w:left="1134"/></w:sectPr>
  </w:body>
</w:document>""" % "\n".join(body)
            content_types = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
  <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
</Types>"""
            rels = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
</Relationships>"""
            styles = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:rPr><w:b/><w:sz w:val="32"/></w:rPr></w:style>
</w:styles>"""
            with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
                z.writestr("[Content_Types].xml", content_types)
                z.writestr("_rels/.rels", rels)
                z.writestr("word/document.xml", document_xml)
                z.writestr("word/styles.xml", styles)
            _toast(win, "Lista DOCX gerada ✅", 1600)
            _open_file(Path(path))
        except Exception as e:
            messagebox.showerror("Lista filtrada", f"Não consegui gerar a lista.\n\n{e}", parent=win)

    def _conferir():
        if running.get("value"):
            return
        try:
            year = int(year_var.get())
            month = _MONTH_TO_NUM.get(month_var.get(), now.month)
        except Exception:
            messagebox.showwarning("Auditoria", "Ano/mês inválidos.", parent=win)
            return

        stop_event.clear()
        running["value"] = True
        rows_cache.clear()
        visible_rows.clear()
        tree_row_map.clear()
        tv.delete(*tv.get_children())
        log.delete(0, "end")
        count_var.set("0 resultado(s)")
        progress.configure(value=0, maximum=max(1, len(militares)))
        btn_conferir.configure(state="disabled")
        btn_parar.configure(text="Parar")
        nome_pdf = _format_filename_contracheque(year, month)
        _set_status(f"Conferindo {nome_pdf} em {len(militares)} militar(es)…")

        def _worker():
            lidos = faltando = falha = 0
            for idx, mref in enumerate(militares, start=1):
                if stop_event.is_set():
                    _log("⏹ Auditoria interrompida pelo usuário.")
                    break
                nome = f"{mref.pg} {mref.nome}".strip()
                pdf_path = _militar_folder(root, mref) / nome_pdf
                _ui(lambda v=idx-1: progress.configure(value=v))
                base_row = {
                    "_mref": mref,
                    "militar": nome,
                    "pg": mref.pg,
                    "nome": mref.nome,
                    "nome_guerra": getattr(mref, "nome_guerra", ""),
                    "cpf": mref.cpf,
                    "idt": mref.idt,
                    "prec": mref.prec,
                    "id_militar": getattr(mref, "id_militar", ""),
                    "banco_db": getattr(mref, "banco", ""),
                    "agencia_db": getattr(mref, "agencia", ""),
                    "conta_db": getattr(mref, "conta", ""),
                    "pdf_path": str(pdf_path),
                }
                if not pdf_path.exists():
                    faltando += 1
                    row = {**base_row, "pdf": "NÃO ENCONTRADO", "situacao": "Contracheque não salvo para este mês"}
                    _append_row(row)
                    _ui(lambda v=idx: progress.configure(value=v))
                    continue
                try:
                    text = _extract_pdf_text(pdf_path)
                    dados = _analisar_texto_contracheque(text, mref)
                    row = {**base_row, "pdf": pdf_path.name, **dados}
                    _append_row(row)
                    lidos += 1
                    if _achado_resumo(row) not in ("OK", ""):
                        _log(f"⚠ {nome}: {_achado_resumo(row)}")
                except Exception as e:
                    falha += 1
                    row = {**base_row, "pdf": pdf_path.name, "situacao": f"Erro ao ler contracheque: {e}"}
                    _append_row(row)
                    _log(f"❌ {nome}: {e}")
                _ui(lambda v=idx: progress.configure(value=v))
            _set_status(f"Auditoria finalizada. Lidos: {lidos} | Sem contracheque: {faltando} | Falhas: {falha}")
            running["value"] = False
            _ui(lambda: btn_conferir.configure(state="normal"))
            _ui(lambda: btn_parar.configure(text="Fechar"))
            _render_rows()

        threading.Thread(target=_worker, daemon=True).start()

    def _parar_ou_fechar():
        if running.get("value"):
            stop_event.set()
            _set_status("Parada solicitada…")
        else:
            try:
                win.destroy()
            except Exception:
                pass

    # Menu profissional de clique direito na auditoria.
    def _aud_set_filter(nome: str):
        try:
            filtro_var.set(nome)
            search_var.set("")
            _render_rows()
        except Exception:
            pass

    def _aud_open_folder_selected():
        row = _selected_row()
        if not row:
            return
        try:
            p = Path(str(row.get("pdf_path") or ""))
            alvo = p.parent if p.parent.exists() else root
            _open_folder(alvo)
        except Exception:
            pass

    def _aud_copy_text(texto: str):
        try:
            win.clipboard_clear()
            win.clipboard_append(str(texto or ""))
            _toast(win, "Copiado ✅", 1200)
        except Exception:
            pass

    def _aud_copy_row_summary():
        row = _selected_row()
        if not row:
            return
        linhas = [
            f"Militar: {row.get('militar','')}",
            f"PDF: {row.get('pdf','')}",
            f"Aux Transporte no contracheque: {_fmt_money_br(row.get('aux_pdf'))}",
            f"Aux Transporte Banco: {_fmt_money_br(row.get('aux_db'))}",
            f"Aux Transporte Status: {row.get('aux_status','')}",
            f"Banco/Conta: {row.get('banco_status','')}",
            f"  PDF: banco {row.get('banco_pdf','')} ag {row.get('agencia_pdf','')} cc {row.get('conta_pdf','')}",
            f"  Cadastro: banco {row.get('banco_db','')} ag {row.get('agencia_db','')} cc {row.get('conta_db','')}",
            f"Situação pagamento: {row.get('situacao_pagamento','')}",
            f"FUSEx 3%: {_fmt_money_br(row.get('fusex'))}",
            f"Salário-família: {_fmt_money_br(row.get('salario_familia'))} ({row.get('dependentes',0)} dep.)",
            f"Pré-escolar: {_fmt_money_br(row.get('pre_escolar'))}",
            f"DR/AR/Atrasados: {_fmt_money_br(row.get('atrasados'))}",
            f"Pensão alimentícia: {_fmt_money_br(row.get('pensao_alimenticia'))}",
            f"Situação: {row.get('situacao','')}",
        ]
        _aud_copy_text("\n".join(linhas))

    def _aud_details_text(row: dict) -> str:
        linhas = []
        linhas.append(f"MILITAR: {_pg_abrev(row.get('pg',''))} {row.get('nome','')}")
        if row.get("nome_guerra"):
            linhas.append(f"NOME DE GUERRA: {row.get('nome_guerra')}")
        linhas.append(f"PDF: {row.get('pdf','')}")
        linhas.append(f"RESUMO: {_achado_resumo(row)}")
        linhas.append(f"ACHADOS COMPLETOS: {row.get('situacao','')}")
        linhas.append("")
        linhas.append("DADOS BANCÁRIOS")
        linhas.append(f"  PDF: banco {row.get('banco_pdf','')} | agência {row.get('agencia_pdf','')} | conta {row.get('conta_pdf','')}")
        linhas.append(f"  Cadastro: banco {row.get('banco_db','')} | agência {row.get('agencia_db','')} | conta {row.get('conta_db','')}")
        linhas.append(f"  Conferência: {row.get('banco_status','')}")
        linhas.append(f"  Situação de pagamento: {row.get('situacao_pagamento','')}")
        linhas.append("")
        linhas.append("VALORES PRINCIPAIS")
        linhas.append(f"  Aux Transporte no contracheque: {_fmt_money_br(row.get('aux_pdf'))}")
        linhas.append(f"    Normal (NR): {_fmt_money_br(row.get('aux_normal'))}")
        linhas.append(f"    Desconto DR: {_fmt_money_br(row.get('aux_dr'))}")
        if row.get('aux_ar'):
            linhas.append(f"    AR: {_fmt_money_br(row.get('aux_ar'))}")
        linhas.append(f"    Banco: {_fmt_money_br(row.get('aux_db'))}")
        linhas.append(f"    Diferença: {_fmt_money_br(row.get('aux_diff'))}")
        linhas.append(f"  FUSEx 3%: {_fmt_money_br(row.get('fusex'))}")
        linhas.append(f"  Desconto dependente FUSEx: {_fmt_money_br(row.get('dep_fusex'))}")
        linhas.append(f"  Despesa médica FUSEx: {_fmt_money_br(row.get('med_fusex'))}")
        linhas.append(f"  Salário-família: {_fmt_money_br(row.get('salario_familia'))} ({row.get('dependentes', 0)} dep.)")
        linhas.append(f"  Pré-escolar: {_fmt_money_br(row.get('pre_escolar'))}")
        linhas.append(f"  Férias: {_fmt_money_br(row.get('ferias'))}")
        linhas.append(f"  Aux Alimentação: {_fmt_money_br(row.get('aux_alimentacao'))}")
        linhas.append(f"  Pensão militar: {_fmt_money_br(row.get('pensao_militar'))}")
        linhas.append(f"  Pensão alimentícia: {_fmt_money_br(row.get('pensao_alimenticia'))}")
        linhas.append(f"  IRRF: {_fmt_money_br(row.get('irrf'))}")
        linhas.append(f"  Adicional habilitação: {_fmt_money_br(row.get('adicional_habilitacao'))}")
        linhas.append(f"  AD C DISP MIL: {_fmt_money_br(row.get('ad_c_disp_mil'))}")
        linhas.append(f"  PNR: {_fmt_money_br(row.get('pnr'))}")
        linhas.append(f"  Empréstimo/Seguro/FHE: {_fmt_money_br(row.get('emprestimos'))}")
        linhas.append("")
        linhas.append("LINHAS ENCONTRADAS NO PDF")
        linhas_dict = row.get("linhas") or {}
        if isinstance(linhas_dict, dict):
            for chave, vals in linhas_dict.items():
                if not vals:
                    continue
                linhas.append(f"\n[{chave}]")
                for v in vals:
                    linhas.append(f"  - {v}")
        return "\n".join(linhas)

    def _aud_show_details():
        row = _selected_row()
        if not row:
            return
        dlg = Toplevel(win)
        dlg.title("Detalhes da auditoria")
        dlg.configure(bg="white")
        try:
            dlg.geometry("780x620+%d+%d" % (win.winfo_rootx()+80, win.winfo_rooty()+60))
            dlg.transient(win)
        except Exception:
            pass
        Label(dlg, text=f"Detalhes — {_pg_abrev(row.get('pg',''))} {row.get('nome','')}", bg="#0d47a1", fg="white", font=("Segoe UI", 11, "bold"), padx=12, pady=9).pack(fill="x")
        frame = Frame(dlg, bg="white")
        frame.pack(fill="both", expand=True, padx=12, pady=12)
        txt = Text(frame, wrap="word", font=("Consolas", 10), bg="#fafafa", fg="#102a43", relief="solid", bd=1)
        sb = Scrollbar(frame, orient="vertical", command=txt.yview)
        txt.configure(yscrollcommand=sb.set)
        txt.pack(side="left", fill="both", expand=True)
        sb.pack(side="right", fill="y")
        conteudo = _aud_details_text(row)
        txt.insert("1.0", conteudo)
        txt.configure(state="disabled")
        btns = Frame(dlg, bg="white")
        btns.pack(fill="x", padx=12, pady=(0,12))
        Button(btns, text="Abrir contracheque", command=_abrir_pdf_selecionado, bg="#455A64", fg="white", bd=0, padx=12, pady=7).pack(side="left")
        Button(btns, text="Contracheques do militar", command=_open_contracheque_selected, bg="#0d47a1", fg="white", bd=0, padx=12, pady=7).pack(side="left", padx=8)
        Button(btns, text="Copiar detalhes", command=lambda: _aud_copy_text(conteudo), bg="#1e88e5", fg="white", bd=0, padx=12, pady=7).pack(side="left", padx=8)
        Button(btns, text="Fechar", command=dlg.destroy, bg="#c62828", fg="white", bd=0, padx=12, pady=7).pack(side="right")

    def _aud_context_menu(event):
        try:
            iid = tv.identify_row(event.y)
            if iid:
                tv.selection_set(iid)
                tv.focus(iid)
        except Exception:
            pass

        tem_linha = _selected_row() is not None
        menu = Menu(win, tearoff=False)
        menu.add_command(label="Abrir contracheque", command=_abrir_pdf_selecionado, state=("normal" if tem_linha else "disabled"))
        menu.add_command(label="Abrir gerenciador de contracheques", command=_open_contracheque_selected, state=("normal" if tem_linha else "disabled"))
        menu.add_command(label="Ver achados/detalhes", command=_aud_show_details, state=("normal" if tem_linha else "disabled"))
        menu.add_command(label="Abrir pasta deste militar", command=_aud_open_folder_selected, state=("normal" if tem_linha else "disabled"))
        menu.add_separator()
        menu.add_command(label="Editar militar", command=_open_edit_selected, state=("normal" if tem_linha and callable(on_open_edit) else "disabled"))
        menu.add_command(label="Abrir carteira", command=_open_carteira_selected, state=("normal" if tem_linha and callable(on_open_carteira) else "disabled"))
        menu.add_separator()
        menu.add_command(label="Copiar resumo da linha", command=_aud_copy_row_summary, state=("normal" if tem_linha else "disabled"))
        menu.add_separator()
        sub = Menu(menu, tearoff=False)
        for label in (
            "Divergências / verificar",
            "Sem contracheque",
            "Sem FUSEx 3%",
            "Sem Aux Transporte no contracheque",
            "Aux Transporte divergente",
            "Com desconto DR de Aux Transporte",
            "Com Salário Família",
            "Com Pré-escolar",
            "Com Pensão Alimentícia",
            "Com DR/AR/Atrasados",
            "Com PNR",
            "Com Empréstimo/Seguro/FHE",
            "Banco/Conta divergente",
            "Situação diferente de Normal",
            "Pagamento suspenso",
        ):
            sub.add_command(label=label, command=lambda x=label: _aud_set_filter(x))
        menu.add_cascade(label="Filtros rápidos", menu=sub)
        menu.add_command(label="Colunas visíveis…", command=_janela_colunas)
        menu.add_command(label="Limpar filtros", command=_limpar_filtro)
        menu.add_separator()
        menu.add_command(label="Gerar lista Word do filtro atual", command=lambda: _gerar_docx_lista(list(visible_rows)), state=("normal" if visible_rows else "disabled"))
        menu.add_command(label="Exportar CSV filtrado", command=lambda: _export_rows_csv(list(visible_rows), filtrado=True), state=("normal" if visible_rows else "disabled"))
        menu.add_command(label="Exportar CSV completo", command=lambda: _export_rows_csv(list(rows_cache), filtrado=False), state=("normal" if rows_cache else "disabled"))
        menu.add_separator()
        menu.add_command(label="Conferir novamente", command=_conferir, state=("disabled" if running.get("value") else "normal"))
        try:
            menu.tk_popup(event.x_root, event.y_root)
        finally:
            try:
                menu.grab_release()
            except Exception:
                pass
        return "break"

    cb_filtro.bind("<<ComboboxSelected>>", lambda _=None: _render_rows())
    cb_pg.bind("<<ComboboxSelected>>", lambda _=None: _render_rows())
    search_var.trace_add("write", lambda *_a: _render_rows())
    tv.bind("<Double-Button-1>", _abrir_pdf_selecionado)
    tv.bind("<Return>", _abrir_pdf_selecionado)
    for _w in (tv, tree_wrap):
        try:
            _w.bind("<Button-3>", _aud_context_menu, add="+")
            _w.bind("<Button-2>", _aud_context_menu, add="+")
        except Exception:
            pass

    btn_conferir = Button(footer, text="Conferir contracheques salvos", command=_conferir,
                          bg="#1e88e5", fg="white", bd=0, cursor="hand2", font=("Segoe UI", 9, "bold"), padx=12, pady=7)
    btn_conferir.pack(side="left")
    Button(footer, text="Abrir contracheque", command=_abrir_pdf_selecionado,
           bg="#455A64", fg="white", bd=0, cursor="hand2", font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left", padx=8)
    Button(footer, text="Contracheques", command=_open_contracheque_selected,
           bg="#0d47a1", fg="white", bd=0, cursor="hand2", font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left")
    Button(footer, text="Ver achados", command=_aud_show_details,
           bg="#00695c", fg="white", bd=0, cursor="hand2", font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left", padx=8)
    Button(footer, text="Colunas", command=_janela_colunas,
           bg="#6a1b9a", fg="white", bd=0, cursor="hand2", font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left", padx=8)
    btn_parar = Button(footer, text="Fechar", command=_parar_ou_fechar,
                       bg="#c62828", fg="white", bd=0, cursor="hand2", font=("Segoe UI", 9, "bold"), padx=12, pady=7)
    btn_parar.pack(side="right")

    Button(footer2, text="Editar selecionado", command=_open_edit_selected,
           bg="#ef6c00", fg="white", bd=0, cursor="hand2", font=("Segoe UI", 9, "bold"), padx=12, pady=7, state=("normal" if callable(on_open_edit) else "disabled")).pack(side="left")
    Button(footer2, text="Carteira", command=_open_carteira_selected,
           bg="#00897b", fg="white", bd=0, cursor="hand2", font=("Segoe UI", 9, "bold"), padx=12, pady=7, state=("normal" if callable(on_open_carteira) else "disabled")).pack(side="left", padx=8)
    Button(footer2, text="Lista Word filtrada", command=lambda: _gerar_docx_lista(list(visible_rows)),
           bg="#6a1b9a", fg="white", bd=0, cursor="hand2", font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left")
    Button(footer2, text="CSV filtrado", command=lambda: _export_rows_csv(list(visible_rows), filtrado=True),
           bg="#2e7d32", fg="white", bd=0, cursor="hand2", font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left", padx=8)
    Button(footer2, text="CSV completo", command=lambda: _export_rows_csv(list(rows_cache), filtrado=False),
           bg="#2e7d32", fg="white", bd=0, cursor="hand2", font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left")
    Button(footer2, text="Limpar filtros", command=_limpar_filtro,
           bg="#607d8b", fg="white", bd=0, cursor="hand2", font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left", padx=8)
    Button(footer2, text="Pasta raiz", command=lambda: _open_folder(root),
           bg="#607d8b", fg="white", bd=0, cursor="hand2", font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left")

    win.protocol("WM_DELETE_WINDOW", _parar_ou_fechar)

def abrir_janela_contracheque(parent, mref: MilitarRef, *, modal: bool = True) -> None:
    root = _default_root()
    temp = _default_temp()
    downloads = _downloads_fallback()
    folder = _militar_folder(root, mref)
    folder.mkdir(parents=True, exist_ok=True)
    temp.mkdir(parents=True, exist_ok=True)

    win = Toplevel(parent)
    win.title(f"Contracheque — {mref.pg} {mref.nome}")
    win.configure(bg="white")
    _apply_persistent_window(win, "contracheque_geometry", default_geometry="1100x760", parent=parent)
    if modal:
        try:
            win.transient(parent)
        except Exception:
            pass
        try:
            win.grab_set()
        except Exception:
            pass
    else:
        # Modo usado pela Auditoria: abre o gerenciador sem prender o foco
        # nem minimizar/esconder a janela de auditoria.
        try:
            win.transient(parent)
        except Exception:
            pass
        try:
            win.grab_release()
        except Exception:
            pass
    try:
        win.minsize(760, 540)
    except Exception:
        pass

    watchers: list[_DownloadWatcher] = []

    header = Frame(win, bg="#0d47a1")
    header.pack(fill="x")
    Label(header, text="Contracheques e Ficha Financeira", bg="#0d47a1", fg="white",
          font=("Segoe UI", 12, "bold")).pack(side="left", padx=12, pady=10)
    Label(header, text=f"{mref.pg} {mref.nome}", bg="#0d47a1", fg="#bbdefb",
          font=("Segoe UI", 9, "bold")).pack(side="left", padx=8)

    info = Frame(win, bg="white")
    info.pack(fill="x", padx=12, pady=(10, 6))
    Label(info, text=f"CPF: {_cpf_digits(mref.cpf)}", bg="white", fg="#263238",
          font=("Segoe UI", 9)).pack(anchor="w")
    Label(info, text=f"Pasta: {folder}", bg="white", fg="#607d8b",
          font=("Segoe UI", 8)).pack(anchor="w", pady=(2, 0))

    notebook = ttk.Notebook(win)
    notebook.pack(fill="both", expand=True, padx=12, pady=(0, 12))

    tab_contra = Frame(notebook, bg="white")
    tab_ficha = Frame(notebook, bg="white")
    notebook.add(tab_contra, text="Contracheque")
    notebook.add(tab_ficha, text="Ficha Financeira")

    now = datetime.now()

    def _all_pdfs() -> list[Path]:
        return sorted(folder.glob("*.pdf"), key=lambda p: p.stat().st_mtime, reverse=True) if folder.exists() else []

    # ---------------- CONTRACHEQUE ----------------
    status_contra = StringVar(value="Pronto.")
    Label(tab_contra, textvariable=status_contra, bg="white", fg="#455a64",
          font=("Segoe UI", 8, "italic")).pack(anchor="w", padx=12, pady=(10, 6))

    row1 = Frame(tab_contra, bg="white")
    row1.pack(fill="x", padx=12, pady=(0, 6))
    Label(row1, text="Ano:", bg="white", fg="#263238",
          font=("Segoe UI", 9, "bold")).pack(side="left")

    year_var = StringVar(value=str(now.year))
    years = [str(y) for y in range(now.year, now.year - 6, -1)]
    cb_year = ttk.Combobox(row1, textvariable=year_var, values=years, width=8, state="readonly")
    cb_year.pack(side="left", padx=8)

    row2 = Frame(tab_contra, bg="white")
    row2.pack(fill="x", padx=12, pady=(0, 8))
    Label(row2, text="Mês:", bg="white", fg="#263238",
          font=("Segoe UI", 9, "bold")).pack(side="left")

    month_var = StringVar(value=_PT_MONTHS[now.month])
    cb_month = ttk.Combobox(row2, textvariable=month_var, values=_MONTH_NAMES, width=14, state="readonly")
    cb_month.pack(side="left", padx=8)

    Label(row2, text="Código folha:", bg="white", fg="#263238",
          font=("Segoe UI", 9, "bold")).pack(side="left", padx=(14, 4))
    codigo_folha_var = StringVar(value=str(_codigo_folha_sippes(now.year, now.month)))
    ent_codigo_folha = ttk.Entry(row2, textvariable=codigo_folha_var, width=8)
    ent_codigo_folha.pack(side="left")

    warn_var = StringVar(value="")
    Label(row2, textvariable=warn_var, bg="white", fg="#c62828",
          font=("Segoe UI", 9, "bold")).pack(side="left", padx=(10, 0))

    body = Frame(tab_contra, bg="white")
    body.pack(fill="both", expand=True, padx=12, pady=(0, 10))

    lb = Listbox(body, font=("Consolas", 10))
    sb = Scrollbar(body, command=lb.yview)
    lb.configure(yscrollcommand=sb.set)
    lb.pack(side="left", fill="both", expand=True)
    sb.pack(side="right", fill="y")

    def _dst_contra() -> Path:
        try:
            y = int(year_var.get())
        except Exception:
            y = now.year
        m_name = month_var.get()
        m = _MONTH_TO_NUM.get(m_name, now.month)
        return folder / _format_filename_contracheque(y, m)

    def _update_codigo_folha():
        try:
            y = int(year_var.get())
        except Exception:
            y = now.year
        m = _MONTH_TO_NUM.get(month_var.get(), now.month)
        codigo_folha_var.set(str(_codigo_folha_sippes(y, m)))

    def _update_warn_contra():
        dst = _dst_contra()
        warn_var.set("Já existe (vai sobrescrever)" if dst.exists() else "")

    def refresh_list_contra():
        lb.delete(0, "end")
        try:
            y = int(year_var.get())
        except Exception:
            y = now.year
        for p in _all_pdfs():
            py = _extract_year_from_name(p.name)
            if py == y and not p.name.upper().startswith("FICHA FINANCEIRA - "):
                lb.insert("end", p.name)
        _update_warn_contra()

    def _on_periodo_contra_change(_=None):
        _update_codigo_folha()
        refresh_list_contra()

    cb_year.bind("<<ComboboxSelected>>", _on_periodo_contra_change)
    cb_month.bind("<<ComboboxSelected>>", _on_periodo_contra_change)

    watcher_contra = _DownloadWatcher([temp, downloads], status_cb=lambda s: status_contra.set(s))
    watchers.append(watcher_contra)

    def _salvar_pdf_contra_baixado(pdf_path: Path, dst: Path, overwrite: bool) -> None:
        try:
            if overwrite and dst.exists():
                try:
                    dst.unlink()
                except Exception:
                    try:
                        os.remove(str(dst))
                    except Exception:
                        pass

            shutil.copy2(str(pdf_path), str(dst))
            if not _looks_like_pdf(dst):
                try:
                    dst.unlink(missing_ok=True)
                except Exception:
                    try:
                        os.remove(str(dst))
                    except Exception:
                        pass
                status_contra.set("PDF ainda não ficou pronto… aguardando…")
                return

            try:
                pdf_path.unlink()
            except Exception:
                pass

            refresh_list_contra()
            status_contra.set(f"Salvo: {dst.name}")
            _toast(win, f"Salvo e aberto: {dst.name}", 1900)
            try:
                win.after(250, lambda p=dst: _open_file(p))
            except Exception:
                try:
                    _open_file(dst)
                except Exception:
                    pass
        except Exception as e:
            status_contra.set("Erro ao salvar.")
            messagebox.showerror("Erro", f"Falha ao salvar contracheque.\n\n{e}", parent=win)

    def _validar_destino_contra() -> tuple[Path, bool] | None:
        dst = _dst_contra()
        overwrite = False
        if dst.exists():
            overwrite = messagebox.askyesno(
                "Já existe",
                f"Já existe um contracheque salvo para:\n\n{dst.name}\n\nDeseja salvar por cima?",
                parent=win,
            )
            if not overwrite:
                _toast(win, "Cancelado (já existia)", 1500)
                return None
        return dst, overwrite

    def _preparar_sessao_sippes():
        status_contra.set("Preparando navegador persistente do SIPPES…")
        _toast(win, "Preparando SIPPES…", 1300)

        def _set_status(text: str):
            try:
                win.after(0, lambda: status_contra.set(text))
            except Exception:
                pass

        def _show_error(title: str, msg: str):
            try:
                win.after(0, lambda: messagebox.showerror(title, msg, parent=win))
            except Exception:
                pass

        def _ask_ready() -> bool:
            ev = threading.Event()
            res = {"ok": False}

            def _ask():
                try:
                    res["ok"] = messagebox.askokcancel(
                        "Preparar sessão SIPPES",
                        "O navegador do SIPPES será mantido aberto para os próximos downloads.\n\n"
                        "Faça o login normalmente. Se o link interno ainda der erro na primeira vez,\n"
                        "faça o caminho manual no SIPPES uma vez até a tela de contracheque.\n\n"
                        "Quando estiver pronto, clique em OK aqui.\n\n"
                        "Importante: não feche esse navegador; ele será minimizado e reutilizado.",
                        parent=win,
                    )
                except Exception:
                    res["ok"] = False
                finally:
                    ev.set()

            try:
                win.after(0, _ask)
                ev.wait()
            except Exception:
                return False
            return bool(res["ok"])

        def _wait_for(condition, timeout: float, interval: float = 0.5) -> bool:
            limite = time.time() + timeout
            while time.time() < limite:
                try:
                    if condition():
                        return True
                except Exception:
                    pass
                time.sleep(interval)
            return False

        def _worker():
            try:
                driver, reused_driver = _get_reusable_sippes_driver(temp)
                _selenium_restore_window(driver)
                if reused_driver:
                    _set_status("Navegador do SIPPES já estava aberto. Conferindo sessão…")
                else:
                    _set_status("Navegador do SIPPES aberto. Faça o login/preparo uma vez se necessário…")

                auto_ok = _preparar_fluxo_autenticado_sippes(driver, status_cb=lambda s: _set_status(s), tentar_login=True)

                if not auto_ok:
                    _set_status("Aguardando você preparar/logar no SIPPES manualmente…")
                    if not _ask_ready():
                        _set_status("Preparo da sessão cancelado.")
                        return
                    _set_status("Aplicando os links corretos após o login/preparo…")
                    driver.get(_sippes_tela_consultar_url())
                    time.sleep(1.0)
                    driver.get(_sippes_consulta_url())

                if _wait_for(lambda: _selenium_tem_funcao_selecionar_favorecido(driver), 40):
                    _selenium_minimize_window(driver)
                    _set_status("Sessão SIPPES pronta. Navegador mantido aberto/minimizado para reutilizar.")
                    _toast(win, "SIPPES pronto ✅", 1500)
                else:
                    _set_status("Sessão aberta, mas não localizei a tela interna. Deixe o navegador aberto e tente automatizar.")
                    _show_error(
                        "SIPPES",
                        "Não localizei a função selecionarFavorecido após o preparo.\n\n"
                        "Faça o caminho manual no navegador aberto uma vez, não feche a janela, e tente novamente."
                    )
            except Exception as e:
                _set_status("Falha ao preparar a sessão do SIPPES.")
                _show_error("SIPPES", f"Não consegui preparar o navegador persistente.\n\nDetalhe:\n{e}")

        threading.Thread(target=_worker, daemon=True).start()


    def _baixar_contra_automatico():
        cpf = _cpf_digits(mref.cpf)
        idt = _idt_digits(mref.idt)
        prec = _prec_digits(mref.prec)
        codigo = _only_digits(codigo_folha_var.get())

        if len(cpf) != 11:
            messagebox.showwarning("Atenção", "CPF inválido para este militar.", parent=win)
            return
        if not idt:
            messagebox.showwarning("Atenção", "Este militar está sem IDT. A automação do SIPPES precisa da IDT.", parent=win)
            return
        if not prec:
            messagebox.showwarning("Atenção", "Este militar está sem PREC/CP. A automação do SIPPES precisa do PREC/CP para preencher a tela.", parent=win)
            return
        if not codigo:
            messagebox.showwarning("Atenção", "Informe o código da folha do SIPPES.", parent=win)
            return

        validado = _validar_destino_contra()
        if not validado:
            return
        dst, overwrite = validado

        def _on_pdf_ready(pdf_path: Path):
            _salvar_pdf_contra_baixado(pdf_path, dst, overwrite)

        watcher_contra.start(_on_pdf_ready)
        status_contra.set("Automação iniciada. Se houver credencial salva, o SIGFUR faz login e baixa com o navegador minimizado…")
        _toast(win, "Automação iniciada ✅", 1500)

        def _set_status(text: str):
            try:
                win.after(0, lambda: status_contra.set(text))
            except Exception:
                pass

        def _show_error(title: str, msg: str):
            try:
                win.after(0, lambda: messagebox.showerror(title, msg, parent=win))
            except Exception:
                pass

        def _ask_login_ok() -> bool:
            """Pergunta no Tkinter, sem travar o Selenium em thread secundária."""
            ev = threading.Event()
            res = {"ok": False}

            def _ask():
                try:
                    res["ok"] = messagebox.askokcancel(
                        "Login no SIPPES",
                        "O navegador automático do SIPPES foi aberto.\n\n"
                        "Se cair no login ou se o link interno der erro, faça o login normalmente e, se precisar,\n"
                        "faça o caminho manual uma vez até a área de contracheque.\n\n"
                        "Quando a sessão estiver pronta, NÃO feche o navegador. Clique em OK aqui.\n\n"
                        "Depois disso o SIGFUR volta sozinho para o link certo e continua.",
                        parent=win,
                    )
                except Exception:
                    res["ok"] = False
                finally:
                    ev.set()

            try:
                win.after(0, _ask)
                ev.wait()
            except Exception:
                return False
            return bool(res["ok"])

        def _ask_manual_pesquisar_ok() -> bool:
            """Fallback humano: usado só se o SIPPES não aceitar o Pesquisar automático
            e o download direto também falhar.
            """
            ev = threading.Event()
            res = {"ok": False}

            def _ask():
                try:
                    res["ok"] = messagebox.askokcancel(
                        "Pesquisar no SIPPES",
                        "O SIGFUR preencheu o favorecido, mas o SIPPES não aceitou o comando automático de Pesquisar.\n\n"
                        "Clique manualmente no botão Pesquisar no navegador aberto.\n"
                        "Depois que aparecer o resultado, volte aqui e clique em OK.\n\n"
                        "O SIGFUR continuará do ponto seguinte e tentará baixar o contracheque.",
                        parent=win,
                    )
                except Exception:
                    res["ok"] = False
                finally:
                    ev.set()

            try:
                win.after(0, _ask)
                ev.wait()
            except Exception:
                return False
            return bool(res["ok"])

        def _wait_for(condition, timeout: float, interval: float = 0.5) -> bool:
            limite = time.time() + timeout
            while time.time() < limite:
                try:
                    if condition():
                        return True
                except Exception:
                    pass
                time.sleep(interval)
            return False

        def _worker():
            try:
                driver, reused_driver = _get_reusable_sippes_driver(temp)
                if reused_driver:
                    _set_status("Reutilizando o navegador do SIPPES que já estava aberto…")
                else:
                    _set_status("Abrindo navegador do SIPPES minimizado e tentando login automático…")
                driver.get(_sippes_consulta_url())

                # Se a sessão expirou, tenta login automático com a credencial salva
                # e segue os dois links corretos antes de pedir intervenção manual.
                if not _wait_for(lambda: _selenium_tem_funcao_selecionar_favorecido(driver), 12):
                    _set_status("Sessão não pronta. Tentando login automático no SIPPES…")
                    auto_ok = _preparar_fluxo_autenticado_sippes(driver, status_cb=lambda s: _set_status(s), tentar_login=True)
                    if not auto_ok:
                        _set_status("Aguardando login manual no SIPPES…")
                        if not _ask_login_ok():
                            watcher_contra.stop()
                            _set_status("Automação cancelada antes do login.")
                            return
                        _set_status("Aplicando os links corretos após o login…")
                        driver.get(_sippes_tela_consultar_url())
                        time.sleep(1.0)
                        driver.get(_sippes_consulta_url())
                    if not _wait_for(lambda: _selenium_tem_funcao_selecionar_favorecido(driver), 40):
                        watcher_contra.stop()
                        _set_status("Não localizei a função selecionarFavorecido no SIPPES.")
                        _show_error("SIPPES", "Não consegui carregar a tela de seleção de favorecido do SIPPES.\n\nConfira se você concluiu o login no navegador aberto ou configure as credenciais SIPPES.\nSe preferir, use o botão Baixar manualmente.")
                        return

                _set_status("Selecionando favorecido no SIPPES…")
                if not _selenium_preencher_favorecido(driver, idt, cpf, mref.nome, prec):
                    watcher_contra.stop()
                    _set_status("Não consegui executar selecionarFavorecido no SIPPES.")
                    _show_error(
                        "SIPPES",
                        "Não consegui executar o selecionarFavorecido automaticamente.\n\n"
                        "Confira se IDT, CPF, nome e PREC/CP estão cadastrados corretamente."
                    )
                    return

                _set_status("Aguardando a tela de consulta abrir…")
                # O HTML real do SIPPES mostrou que o botão Pesquisar chama:
                # pesquisarContracheque(). Então tentamos essa função direta primeiro.
                # Se o SIPPES/Selenium não aceitar, NÃO abortamos: tentamos baixar direto.
                # Só pedimos clique manual se o download direto também falhar.
                time.sleep(2.0)
                _selenium_switch_to_sippes_window(driver)
                _wait_for(lambda: _selenium_has_button_pesquisar(driver), 3)

                _set_status("Tentando acionar Pesquisar automaticamente…")
                pesquisou = _wait_for(
                    lambda: _selenium_executar_pesquisar_contracheque(driver) or _selenium_click_pesquisar(driver),
                    8,
                    0.8,
                )

                if pesquisou:
                    _set_status("Pesquisar acionado. Tentando solicitar o contracheque…")
                    time.sleep(2.0)
                    _wait_for(lambda: _selenium_tem_funcao_visualizar_contracheque(driver), 10)
                else:
                    _set_status("Não consegui acionar Pesquisar. Tentando baixar direto mesmo assim…")
                    time.sleep(1.0)

                _set_status(f"Solicitando contracheque — IDT + PREC/CP — código {codigo}…")
                try:
                    _selenium_executar_visualizar_contracheque(driver, idt, prec, codigo)
                except Exception as erro_visualizar:
                    # Fallback pedido: não parar só porque o Pesquisar não foi encontrado.
                    # Se o download direto falhar, deixa o usuário clicar Pesquisar manualmente
                    # e continua exatamente do ponto seguinte.
                    if not pesquisou:
                        _set_status("Aguardando clique manual no Pesquisar para continuar…")
                        if not _ask_manual_pesquisar_ok():
                            watcher_contra.stop()
                            _set_status("Automação cancelada antes do download.")
                            return
                        _set_status("Tentando solicitar o contracheque após o clique manual…")
                        _wait_for(lambda: _selenium_tem_funcao_visualizar_contracheque(driver), 10)
                        _selenium_executar_visualizar_contracheque(driver, idt, prec, codigo)
                    else:
                        raise erro_visualizar
                _selenium_minimize_window(driver)
                _set_status("Download solicitado. Navegador do SIPPES mantido aberto/minimizado. Aguardando o PDF terminar…")
            except Exception as e:
                watcher_contra.stop()
                _set_status("Falha na automação. O modo manual continua disponível.")
                _show_error(
                    "Automação do SIPPES",
                    "Não consegui concluir a automação. O botão manual continua funcionando.\n\n"
                    f"Detalhe:\n{e}"
                )

        threading.Thread(target=_worker, daemon=True).start()

    def _baixar_contra():
        cpf = _cpf_digits(mref.cpf)
        if len(cpf) != 11:
            messagebox.showwarning("Atenção", "CPF inválido para este militar.", parent=win)
            return

        dst = _dst_contra()
        overwrite = False
        if dst.exists():
            overwrite = messagebox.askyesno(
                "Já existe",
                f"Já existe um contracheque salvo para:\n\n{dst.name}\n\nDeseja salvar por cima?",
                parent=win,
            )
            if not overwrite:
                _toast(win, "Cancelado (já existia)", 1500)
                return

        try:
            win.clipboard_clear()
            win.clipboard_append(cpf)
        except Exception:
            pass

        _toast(win, "CPF copiado ✅")
        status_contra.set(f"Abrindo SIPPES… (monitorando: {temp} e {downloads})")

        def _on_pdf_ready(pdf_path: Path):
            try:
                if overwrite and dst.exists():
                    try:
                        dst.unlink()
                    except Exception:
                        try:
                            os.remove(str(dst))
                        except Exception:
                            pass

                shutil.copy2(str(pdf_path), str(dst))
                if not _looks_like_pdf(dst):
                    try:
                        dst.unlink(missing_ok=True)
                    except Exception:
                        try:
                            os.remove(str(dst))
                        except Exception:
                            pass
                    status_contra.set("PDF ainda não ficou pronto… aguardando…")
                    return

                try:
                    pdf_path.unlink()
                except Exception:
                    pass

                refresh_list_contra()
                status_contra.set(f"Salvo: {dst.name}")
                _toast(win, f"Salvo e aberto: {dst.name}", 1900)
                try:
                    win.after(250, lambda p=dst: _open_file(p))
                except Exception:
                    try:
                        _open_file(dst)
                    except Exception:
                        pass
            except Exception as e:
                status_contra.set("Erro ao salvar.")
                messagebox.showerror("Erro", f"Falha ao salvar contracheque.\n\n{e}", parent=win)

        watcher_contra.start(_on_pdf_ready)
        _open_firefox(_sippes_url())

    def _abrir_pdf_contra():
        sel = lb.curselection()
        if not sel:
            return
        p = folder / lb.get(sel[0])
        if not p.exists():
            messagebox.showwarning("Atenção", "Arquivo não encontrado.", parent=win)
            refresh_list_contra()
            return
        _toast(win, f"Abrindo: {p.name}", 1200)
        _open_file(p)

    def _excluir_pdf_contra():
        sel = lb.curselection()
        if not sel:
            messagebox.showwarning("Atenção", "Selecione um PDF na lista.", parent=win)
            return
        p = folder / lb.get(sel[0])
        if not p.exists():
            refresh_list_contra()
            return
        ok = messagebox.askyesno("Excluir", f"Excluir este contracheque?\n\n{p.name}", parent=win)
        if not ok:
            return
        try:
            p.unlink()
            _toast(win, "Excluído ✅", 1200)
        except Exception as e:
            messagebox.showerror("Erro", f"Não foi possível excluir.\n\n{e}", parent=win)
        refresh_list_contra()

    btns = Frame(tab_contra, bg="white")
    btns.pack(fill="x", padx=12, pady=(0, 12))

    # Botões em duas linhas para não cortar em monitores menores/DPI alto.
    # O individual também recebe Credenciais SIPPES, para baixar automático sem passar pelo lote.
    btns_top = Frame(btns, bg="white")
    btns_top.pack(fill="x")
    btns_bottom = Frame(btns, bg="white")
    btns_bottom.pack(fill="x", pady=(7, 0))

    Button(btns_top, text="Baixar manualmente", command=_baixar_contra,
           bg="#1e88e5", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left")
    Button(btns_top, text="Credenciais SIPPES", command=lambda: abrir_janela_credenciais_sippes(win),
           bg="#6a1b9a", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left", padx=8)
    Button(btns_top, text="Preparar SIPPES", command=_preparar_sessao_sippes,
           bg="#00695c", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left", padx=0)
    Button(btns_top, text="Baixar automático", command=_baixar_contra_automatico,
           bg="#2e7d32", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left", padx=8)

    Button(btns_bottom, text="Abrir contracheque", command=_abrir_pdf_contra,
           bg="#455A64", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left")
    Button(btns_bottom, text="Excluir", command=_excluir_pdf_contra,
           bg="#c62828", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left", padx=8)
    Button(btns_bottom, text="Abrir pasta", command=lambda: _open_folder(folder),
           bg="#607d8b", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left")

    lb.bind("<Double-Button-1>", lambda e: _abrir_pdf_contra())
    refresh_list_contra()

    # ---------------- FICHA FINANCEIRA ----------------
    status_ficha = StringVar(value="Pronto.")
    Label(tab_ficha, textvariable=status_ficha, bg="white", fg="#455a64",
          font=("Segoe UI", 8, "italic")).pack(anchor="w", padx=12, pady=(10, 6))

    instru = Label(
        tab_ficha,
        text="Clique em abrir o portal. O CPF será copiado automaticamente. Depois pesquise manualmente no site e baixe o PDF.",
        bg="white", fg="#37474f", justify="left", wraplength=760,
        font=("Segoe UI", 9),
    )
    instru.pack(anchor="w", padx=12, pady=(0, 8))

    row_f1 = Frame(tab_ficha, bg="white")
    row_f1.pack(fill="x", padx=12, pady=(0, 8))
    Label(row_f1, text="Ano de referência:", bg="white", fg="#263238",
          font=("Segoe UI", 9, "bold")).pack(side="left")

    ficha_year_var = StringVar(value=str(now.year))
    ficha_years = [str(y) for y in range(now.year, now.year - 8, -1)]
    cb_ficha_year = ttk.Combobox(row_f1, textvariable=ficha_year_var, values=ficha_years, width=8, state="readonly")
    cb_ficha_year.pack(side="left", padx=8)

    warn_ficha_var = StringVar(value="")
    Label(row_f1, textvariable=warn_ficha_var, bg="white", fg="#c62828",
          font=("Segoe UI", 9, "bold")).pack(side="left", padx=(10, 0))

    body_f = Frame(tab_ficha, bg="white")
    body_f.pack(fill="both", expand=True, padx=12, pady=(0, 10))

    lb_f = Listbox(body_f, font=("Consolas", 10))
    sb_f = Scrollbar(body_f, command=lb_f.yview)
    lb_f.configure(yscrollcommand=sb_f.set)
    lb_f.pack(side="left", fill="both", expand=True)
    sb_f.pack(side="right", fill="y")

    def _dst_ficha() -> Path:
        try:
            y = int(ficha_year_var.get())
        except Exception:
            y = now.year
        return folder / _format_filename_ficha(y, mref.nome)

    def _update_warn_ficha():
        dst = _dst_ficha()
        warn_ficha_var.set("Já existe (vai sobrescrever)" if dst.exists() else "")

    def refresh_list_ficha():
        lb_f.delete(0, "end")
        try:
            y = int(ficha_year_var.get())
        except Exception:
            y = now.year
        for p in _all_pdfs():
            py = _extract_year_from_name(p.name)
            if py == y and p.name.upper().startswith("FICHA FINANCEIRA - "):
                lb_f.insert("end", p.name)
        _update_warn_ficha()

    cb_ficha_year.bind("<<ComboboxSelected>>", lambda _=None: refresh_list_ficha())

    watcher_ficha = _DownloadWatcher([temp, downloads], status_cb=lambda s: status_ficha.set(s))
    watchers.append(watcher_ficha)

    def _abrir_portal_ficha():
        cpf = _cpf_digits(mref.cpf)
        if len(cpf) != 11:
            messagebox.showwarning("Atenção", "CPF inválido para este militar.", parent=win)
            return

        dst = _dst_ficha()
        overwrite = False
        if dst.exists():
            overwrite = messagebox.askyesno(
                "Já existe",
                f"Já existe uma ficha salva para:\n\n{dst.name}\n\nDeseja salvar por cima?",
                parent=win,
            )
            if not overwrite:
                _toast(win, "Cancelado (já existia)", 1500)
                return

        try:
            win.clipboard_clear()
            win.clipboard_append(cpf)
        except Exception:
            pass

        _toast(win, "CPF copiado ✅")
        status_ficha.set(f"Abrindo portal da ficha… (monitorando: {temp} e {downloads})")

        def _on_pdf_ready(pdf_path: Path):
            try:
                if overwrite and dst.exists():
                    try:
                        dst.unlink()
                    except Exception:
                        try:
                            os.remove(str(dst))
                        except Exception:
                            pass

                shutil.copy2(str(pdf_path), str(dst))
                if not _looks_like_pdf(dst):
                    try:
                        dst.unlink(missing_ok=True)
                    except Exception:
                        try:
                            os.remove(str(dst))
                        except Exception:
                            pass
                    status_ficha.set("PDF ainda não ficou pronto… aguardando…")
                    return

                try:
                    pdf_path.unlink()
                except Exception:
                    pass

                refresh_list_ficha()
                status_ficha.set(f"Salvo: {dst.name}")
                _toast(win, f"Salvo e aberto: {dst.name}", 1900)
                try:
                    win.after(250, lambda p=dst: _open_file(p))
                except Exception:
                    try:
                        _open_file(dst)
                    except Exception:
                        pass
            except Exception as e:
                status_ficha.set("Erro ao salvar.")
                messagebox.showerror("Erro", f"Falha ao salvar ficha financeira.\n\n{e}", parent=win)

        watcher_ficha.start(_on_pdf_ready)
        _open_firefox(_ficha_financeira_url())

    def _abrir_pdf_ficha():
        sel = lb_f.curselection()
        if not sel:
            return
        p = folder / lb_f.get(sel[0])
        if not p.exists():
            messagebox.showwarning("Atenção", "Arquivo não encontrado.", parent=win)
            refresh_list_ficha()
            return
        _toast(win, f"Abrindo: {p.name}", 1200)
        _open_file(p)

    def _excluir_pdf_ficha():
        sel = lb_f.curselection()
        if not sel:
            messagebox.showwarning("Atenção", "Selecione um PDF na lista.", parent=win)
            return
        p = folder / lb_f.get(sel[0])
        if not p.exists():
            refresh_list_ficha()
            return
        ok = messagebox.askyesno("Excluir", f"Excluir esta ficha financeira?\n\n{p.name}", parent=win)
        if not ok:
            return
        try:
            p.unlink()
            _toast(win, "Excluído ✅", 1200)
        except Exception as e:
            messagebox.showerror("Erro", f"Não foi possível excluir.\n\n{e}", parent=win)
        refresh_list_ficha()

    btns_f = Frame(tab_ficha, bg="white")
    btns_f.pack(fill="x", padx=12, pady=(0, 12))
    Button(btns_f, text="Abrir portal da ficha", command=_abrir_portal_ficha,
           bg="#1e88e5", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left")
    Button(btns_f, text="Abrir contracheque", command=_abrir_pdf_ficha,
           bg="#455A64", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left", padx=8)
    Button(btns_f, text="Excluir", command=_excluir_pdf_ficha,
           bg="#c62828", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left")
    Button(btns_f, text="Abrir pasta", command=lambda: _open_folder(folder),
           bg="#607d8b", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="left", padx=8)

    lb_f.bind("<Double-Button-1>", lambda e: _abrir_pdf_ficha())
    refresh_list_ficha()

    def _fechar():
        try:
            _save_window_geometry(win, "contracheque_geometry")
        except Exception:
            pass
        for watcher in watchers:
            try:
                watcher.stop()
            except Exception:
                pass
        win.destroy()

    footer = Frame(win, bg="white")
    footer.pack(fill="x", padx=12, pady=(0, 12))
    Button(footer, text="Fechar", command=_fechar,
           bg="#9e9e9e", fg="white", bd=0, cursor="hand2",
           font=("Segoe UI", 9, "bold"), padx=12, pady=7).pack(side="right")

    win.protocol("WM_DELETE_WINDOW", _fechar)
