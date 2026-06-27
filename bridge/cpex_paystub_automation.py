# -*- coding: utf-8 -*-
"""Automação CPEx/SIPPES/SIAPPES extraída do fluxo operacional do SIGFUR Python.

Este módulo não cria interface Tk. Ele é chamado pela central WPF e executa o
Selenium em segundo plano, usando a Área Exclusiva da UA como autenticação comum.
"""
from __future__ import annotations

import os
import re
import time
import unicodedata
from datetime import datetime

_PF_SIPPES = "SIPPES"
_PF_SIAPPES = "SIAPPES"
_PF_CPEX_LOGIN_URL = "https://cpex-intranet.eb.mil.br/asplogon_nova.asp?url=area_ua_cpex/index.asp"
_PF_SIPPES_URL = "https://cpex-intranet.eb.mil.br/cc_sippes/consulta.asp"
_PF_SIAPPES_URL = "https://cpex-intranet.eb.mil.br/Contra_cheque_intra_siappes.asp"


def _sigfur_cc_norm_text(value: str) -> str:
    try:
        s = unicodedata.normalize("NFD", str(value or ""))
        s = "".join(ch for ch in s if unicodedata.category(ch) != "Mn")
    except Exception:
        s = str(value or "")
    return re.sub(r"\s+", " ", s).strip().lower()


def _sigfur_cc_safe_filename(value: str, default: str = "arquivo") -> str:
    try:
        s = str(value or default).strip() or default
        s = re.sub(r'[\\/:*?"<>|\x00-\x1f]+', "_", s)
        s = re.sub(r"\s+", " ", s).strip(" ._")
        return s[:140] or default
    except Exception:
        return default


def _sigfur_cc_wait_doc_ready(driver, timeout: float = 25.0) -> None:
    try:
        end = time.time() + float(timeout or 25)
        while time.time() < end:
            try:
                state = str(driver.execute_script("return document.readyState || ''") or "")
                if state.lower() == "complete":
                    return
            except Exception:
                pass
            time.sleep(0.25)
    except Exception:
        pass


def _sigfur_cc_texto_atual_driver(driver, max_chars: int = 90000) -> str:
    try:
        return str(driver.execute_script(
            "return [document.title||'', location.href||'', (document.body&&document.body.innerText)||''].join('\\n').slice(0, arguments[0]);",
            int(max_chars),
        ) or "")
    except Exception:
        try:
            return str(getattr(driver, "title", "") or "")
        except Exception:
            return ""

def _pf_digits(value) -> str:
        try:
            return re.sub(r"\D+", "", str(value or ""))
        except Exception:
            return ""

def _pf_limpar_cpf(value) -> str:
        d = _pf_digits(value)
        return d.zfill(11) if d and len(d) < 11 else d

def _pf_limpar_prec(value) -> str:
        return _pf_digits(value)

def _pf_normalizar_nome(value) -> str:
        nome = re.sub(r"\s+", " ", str(value or "")).strip(" .,:;-_")
        nome = re.sub(r"^(?:SD\s+RCR|SD\s+EV|SD\s+EF|CB|SOLDADO)\s+", "", nome, flags=re.I)
        nome = re.sub(r"[^A-Za-zÁÀÂÃÉÊÍÓÔÕÚÇÜáàâãéêíóôõúçü'\- ]+", " ", nome)
        nome = re.sub(r"\s+", " ", nome).strip(" .,:;-_")
        return nome.upper()

def _pf_norm_web(value) -> str:
        try:
            import unicodedata as _ud
            s = _ud.normalize("NFKD", str(value or ""))
            s = "".join(ch for ch in s if not _ud.combining(ch))
            return re.sub(r"\s+", " ", s).strip().lower().replace("º", "").replace("ª", "")
        except Exception:
            return str(value or "").strip().lower()

def _pf_create_cpex_driver(pasta_destino: str, navegador: str = "Edge"):
        """Cria navegador dedicado e totalmente oculto para SIPPES/SIAPPES.

        O login é feito explicitamente na Área Exclusiva da UA. O mesmo driver e
        a mesma sessão seguem para a tela final escolhida pelo operador.
        """
        last_err = None
        browser = str(navegador or "Edge").strip().lower()
        ordem = [browser] + [x for x in ("edge", "chrome") if x != browser]
        prefs = {
            "download.default_directory": os.path.abspath(pasta_destino),
            "download.prompt_for_download": False,
            "download.directory_upgrade": True,
            "plugins.always_open_pdf_externally": True,
            "safebrowsing.enabled": True,
        }
        for kind in ordem:
            if kind not in ("edge", "chrome"):
                continue
            try:
                from selenium import webdriver
                if kind == "edge":
                    from selenium.webdriver.edge.options import Options
                    opts = Options()
                else:
                    from selenium.webdriver.chrome.options import Options
                    opts = Options()
                try:
                    opts.add_experimental_option("prefs", prefs)
                except Exception:
                    pass
                # Headless de verdade: não minimiza e não deixa janela atrás do SIGFUR.
                for arg in (
                    "--headless=new", "--disable-gpu", "--window-size=1500,1000",
                    "--ignore-certificate-errors", "--allow-running-insecure-content",
                    "--disable-notifications", "--disable-popup-blocking", "--no-first-run",
                    "--no-default-browser-check", "--disable-extensions",
                ):
                    try:
                        opts.add_argument(arg)
                    except Exception:
                        pass
                driver = webdriver.Edge(options=opts) if kind == "edge" else webdriver.Chrome(options=opts)
                try:
                    driver.set_page_load_timeout(90)
                except Exception:
                    pass
                return driver
            except Exception as e:
                last_err = e
        raise RuntimeError(
            "Não consegui iniciar Edge/Chrome oculto para a Área Exclusiva da UA. "
            "Confira o Selenium e o navegador instalado.\n\n"
            f"Detalhe: {last_err}"
        )

def _pf_cpex_click_by_text(driver, texto: str) -> bool:
        try:
            from selenium.webdriver.common.by import By
            alvo = _pf_norm_web(texto)
            for el in driver.find_elements(By.CSS_SELECTOR, "input,button,a"):
                try:
                    txt = el.text or el.get_attribute("value") or el.get_attribute("title") or ""
                    if alvo and alvo in _pf_norm_web(txt) and el.is_displayed() and el.is_enabled():
                        el.click()
                        return True
                except Exception:
                    pass
        except Exception:
            pass
        return False

def _pf_cpex_login(driver, usuario: str, senha: str) -> None:
        """Autentica na Área Exclusiva da UA antes de abrir SIPPES ou SIAPPES."""
        from selenium.webdriver.common.by import By
        import time as _time

        usuario_d = _pf_digits(usuario)
        senha = str(senha or "")
        if not usuario_d or not senha:
            raise RuntimeError("Informe o usuário/CPF e a senha da Área Exclusiva da UA.")

        driver.get(_PF_CPEX_LOGIN_URL)
        _sigfur_cc_wait_doc_ready(driver, 35)
        _time.sleep(0.7)

        visiveis = []
        for el in driver.find_elements(By.TAG_NAME, "input"):
            try:
                typ = (el.get_attribute("type") or "text").lower()
                if typ not in ("hidden", "submit", "button", "image", "reset") and el.is_displayed():
                    visiveis.append((el, typ))
            except Exception:
                pass

        campos_texto = [el for el, typ in visiveis if typ != "password"]
        campos_senha = [el for el, typ in visiveis if typ == "password"]

        # Se a sessão já estiver autenticada, a página pode redirecionar sem exibir campos.
        if not campos_texto or not campos_senha:
            blob = _pf_norm_web(_sigfur_cc_texto_atual_driver(driver, 30000))
            if "senha" not in blob and "usuario" not in blob and "login" not in blob:
                return
            raise RuntimeError("Não encontrei os campos de usuário e senha da Área Exclusiva da UA.")

        user_el, pass_el = campos_texto[0], campos_senha[0]
        user_el.clear(); user_el.send_keys(usuario_d)
        pass_el.clear(); pass_el.send_keys(senha)
        if not _pf_cpex_click_by_text(driver, "Entrar"):
            try:
                pass_el.submit()
            except Exception as e:
                raise RuntimeError("Não consegui acionar o botão Entrar da Área Exclusiva da UA.") from e

        fim = _time.time() + 35
        while _time.time() < fim:
            try:
                _sigfur_cc_wait_doc_ready(driver, 4)
                pwd_visivel = False
                for el in driver.find_elements(By.CSS_SELECTOR, "input[type='password']"):
                    try:
                        if el.is_displayed():
                            pwd_visivel = True
                            break
                    except Exception:
                        pass
                blob = _pf_norm_web(_sigfur_cc_texto_atual_driver(driver, 30000))
                if not pwd_visivel and not ("usuario" in blob and "senha" in blob and "entrar" in blob):
                    return
                if any(x in blob for x in ("senha invalida", "usuario invalido", "acesso negado", "falha na autenticacao")):
                    break
            except Exception:
                pass
            _time.sleep(0.5)
        raise RuntimeError("O login da Área Exclusiva da UA não foi concluído. Confira usuário/CPF e senha.")

def _pf_select_option(select_el, aliases) -> bool:
        """Seleciona uma opção tolerando texto, value e páginas HTML antigas.

        O SIAPPES usa uma página ASP antiga. Em algumas máquinas o Selenium não
        consegue selecionar pelo texto visível, embora a opção apareça normalmente
        no navegador. Por isso há três tentativas: Select, comparação por dígitos e
        alteração direta via JavaScript com disparo dos eventos change/input.
        """
        aliases_raw = [str(x or "").strip() for x in aliases if str(x or "").strip()]
        aliases_n = [_pf_norm_web(x) for x in aliases_raw]
        aliases_digits = {re.sub(r"\D+", "", x) for x in aliases_raw if re.sub(r"\D+", "", x)}

        try:
            from selenium.webdriver.support.ui import Select
            sel = Select(select_el)
            options = list(sel.options)

            # 1) Igualdade exata por texto/value normalizados.
            for opt in options:
                txt_raw = str(opt.text or "").strip()
                val_raw = str(opt.get_attribute("value") or "").strip()
                txt = _pf_norm_web(txt_raw)
                val = _pf_norm_web(val_raw)
                txt_digits = re.sub(r"\D+", "", txt_raw)
                val_digits = re.sub(r"\D+", "", val_raw)
                for alvo in aliases_n:
                    if alvo and ((txt and txt == alvo) or (val and val == alvo)):
                        try:
                            sel.select_by_visible_text(opt.text)
                        except Exception:
                            try:
                                sel.select_by_value(val_raw)
                            except Exception:
                                sel.select_by_index(options.index(opt))
                        return True
                if aliases_digits and ({txt_digits, val_digits} & aliases_digits):
                    try:
                        sel.select_by_index(options.index(opt))
                        return True
                    except Exception:
                        pass

            # 2) Correspondência parcial, apenas como fallback.
            for opt in options:
                txt = _pf_norm_web(opt.text)
                val = _pf_norm_web(opt.get_attribute("value") or "")
                for alvo in aliases_n:
                    if alvo and (
                        (txt and (alvo in txt or txt in alvo))
                        or (val and (alvo in val or val in alvo))
                    ):
                        try:
                            sel.select_by_index(options.index(opt))
                            return True
                        except Exception:
                            pass
        except Exception:
            pass

        # 3) ASP antigo: seleciona diretamente pelo DOM e dispara os eventos.
        try:
            driver = select_el.parent
            ok = driver.execute_script(
                r"""
                const sel = arguments[0];
                const aliases = arguments[1] || [];
                const norm = (v) => String(v ?? '')
                    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
                    .toLowerCase().replace(/\s+/g, ' ').trim();
                const digits = (v) => String(v ?? '').replace(/\D/g, '');
                const aliasesN = aliases.map(norm).filter(Boolean);
                const aliasesD = aliases.map(digits).filter(Boolean);
                let idx = -1;
                for (let i = 0; i < sel.options.length; i++) {
                    const o = sel.options[i];
                    const t = norm(o.textContent || o.innerText || '');
                    const v = norm(o.value || '');
                    const td = digits(o.textContent || o.innerText || '');
                    const vd = digits(o.value || '');
                    if (aliasesN.includes(t) || aliasesN.includes(v) ||
                        aliasesD.includes(td) || aliasesD.includes(vd)) {
                        idx = i; break;
                    }
                }
                if (idx < 0) return false;
                sel.selectedIndex = idx;
                sel.options[idx].selected = true;
                sel.dispatchEvent(new Event('input', {bubbles: true}));
                sel.dispatchEvent(new Event('change', {bubbles: true}));
                return true;
                """,
                select_el, aliases_raw,
            )
            return bool(ok)
        except Exception:
            return False

def _pf_sippes_fill_and_consult(driver, pessoa: dict, mes: str, ano: str, processamento: str, tipo_folha: str):
        """Preenche a consulta do SIPPES/CPEx por CPF após o login comum da UA."""
        from selenium.webdriver.common.by import By
        import time as _time

        driver.get(_PF_SIPPES_URL)
        _sigfur_cc_wait_doc_ready(driver, 30)
        _time.sleep(0.6)
        cpf = _pf_limpar_cpf(pessoa.get("cpf", ""))

        inputs = []
        for el in driver.find_elements(By.CSS_SELECTOR, "input"):
            try:
                typ = (el.get_attribute("type") or "text").lower()
                if el.is_displayed() and typ in ("text", "tel", "number", ""):
                    meta = " ".join([
                        el.get_attribute("id") or "", el.get_attribute("name") or "",
                        el.get_attribute("placeholder") or "", el.get_attribute("title") or "",
                    ])
                    try:
                        meta += " " + str(driver.execute_script(
                            "return (arguments[0].parentElement && arguments[0].parentElement.innerText) || '';", el
                        ) or "")
                    except Exception:
                        pass
                    inputs.append((el, _pf_norm_web(meta)))
            except Exception:
                pass
        if not inputs:
            raise RuntimeError("Não encontrei o campo CPF da consulta SIPPES.")

        campo_cpf = next((el for el, meta in inputs if "cpf" in meta and "prec" not in meta), None)
        if campo_cpf is None:
            campo_cpf = inputs[0][0]
        campo_cpf.clear(); campo_cpf.send_keys(cpf)

        selects = []
        for el in driver.find_elements(By.TAG_NAME, "select"):
            try:
                if el.is_displayed():
                    selects.append(el)
            except Exception:
                pass
        if len(selects) < 4:
            raise RuntimeError(
                f"A página SIPPES abriu, mas encontrei somente {len(selects)} seleção(ões). "
                "Eram esperados Processamento, Mês, Ano e Tipo de folha."
            )

        def _opts(el):
            try:
                from selenium.webdriver.support.ui import Select
                return [(str(o.text or "").strip(), str(o.get_attribute("value") or "").strip()) for o in Select(el).options]
            except Exception:
                try:
                    return driver.execute_script(
                        "return Array.from(arguments[0].options).map(o => [String(o.text||''), String(o.value||'')]);", el
                    ) or []
                except Exception:
                    return []

        meses = ["janeiro", "fevereiro", "marco", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro"]
        mes_i = int(_pf_digits(mes) or datetime.now().month)
        mapa = {"proc": None, "mes": None, "ano": None, "tipo": None}
        for i, el in enumerate(selects):
            blob = " ".join(f"{t} {v}" for t, v in _opts(el))
            norm = _pf_norm_web(blob)
            digs = set(re.findall(r"\b\d+\b", blob))
            if mapa["proc"] is None and ("definitivo" in norm or "previa" in norm):
                mapa["proc"] = i
            if mapa["tipo"] is None and "normal" in norm and ("extra" in norm or "complementar" in norm):
                mapa["tipo"] = i
            if mapa["ano"] is None and (str(ano) in digs or re.search(r"\b20\d{2}\b", blob)):
                mapa["ano"] = i
            if mapa["mes"] is None:
                tem_nome = any(m in norm for m in meses)
                tem_numeros = len({str(x) for x in range(1, 13)} & digs) >= 6
                if tem_nome or tem_numeros:
                    mapa["mes"] = i

        # Ordem exibida na tela real: processamento, mês, ano e tipo de folha.
        usados = set()
        defaults = {"proc": 0, "mes": 1, "ano": 2, "tipo": 3}
        for k in ("proc", "mes", "ano", "tipo"):
            if mapa[k] is None or mapa[k] in usados:
                mapa[k] = defaults[k]
            usados.add(mapa[k])

        proc_alias = [processamento]
        if "1" in processamento:
            proc_alias += ["1ª Previa", "1a Previa", "Primeira Previa", "Previa 1"]
        elif "2" in processamento:
            proc_alias += ["2ª Previa", "2a Previa", "Segunda Previa", "Previa 2"]
        else:
            proc_alias += ["Definitivo"]
        tipo_alias = [tipo_folha]
        mes_alias = [str(mes_i), f"{mes_i:02d}", meses[mes_i - 1]]

        escolhas = (
            ("processamento", mapa["proc"], proc_alias),
            ("mês", mapa["mes"], mes_alias),
            ("ano", mapa["ano"], [str(ano)]),
            ("tipo de folha", mapa["tipo"], tipo_alias),
        )
        for rotulo, i, aliases in escolhas:
            if not _pf_select_option(selects[i], aliases):
                resumo = ", ".join((t or v) for t, v in _opts(selects[i])[:14])
                raise RuntimeError(f"Não consegui selecionar {rotulo} no SIPPES. Opções: {resumo or 'nenhuma'}.")

        handles_before = list(driver.window_handles)
        try:
            url_before = str(driver.current_url or "")
        except Exception:
            url_before = ""
        corpo_before = _pf_norm_web(_sigfur_cc_texto_atual_driver(driver, 50000))
        if not _pf_cpex_click_by_text(driver, "Consultar"):
            raise RuntimeError("Não consegui clicar no botão CONSULTAR do SIPPES.")

        abriu_resultado = False
        corpo = ""
        fim = _time.time() + 45
        while _time.time() < fim:
            try:
                novos = [h for h in driver.window_handles if h not in handles_before]
                if novos:
                    driver.switch_to.window(novos[-1])
                _sigfur_cc_wait_doc_ready(driver, 4)
                corpo = _pf_norm_web(_sigfur_cc_texto_atual_driver(driver, 70000))
                url_atual = str(driver.current_url or "")
                marcadores = (
                    "comprovante mensal de rendimentos", "p/g real", "p/g de pagamento",
                    "om de vinculacao", "receitas despesas liquido", "codigo descricao",
                )
                mudou = bool(novos) or (url_before and url_atual and url_atual != url_before) or (corpo and corpo != corpo_before)
                ainda_form = "consultar" in corpo and "processamento" in corpo and "tipo de folha" in corpo
                if any(m in corpo for m in marcadores):
                    abriu_resultado = True; break
                if mudou and not ainda_form and cpf in _pf_digits(corpo):
                    abriu_resultado = True; break
                if any(x in corpo for x in ("nao encontrado", "nenhum registro", "inexistente", "cpf invalido")):
                    break
            except Exception:
                pass
            _time.sleep(0.5)
        if any(x in corpo for x in ("nao encontrado", "nenhum registro", "inexistente", "cpf invalido")):
            raise RuntimeError(f"O SIPPES não encontrou contracheque para o CPF {cpf}.")
        if not abriu_resultado:
            raise RuntimeError("O SIPPES recebeu a consulta, mas a página do contracheque não abriu.")
        return handles_before

def _pf_siappes_fill_and_consult(driver, pessoa: dict, mes: str, ano: str):
        """Preenche a tela simples do SIAPPES: PREC-CP, ano e mês."""
        from selenium.webdriver.common.by import By
        import time as _time

        driver.get(_PF_SIAPPES_URL)
        _sigfur_cc_wait_doc_ready(driver, 30)
        _time.sleep(0.6)
        prec = _pf_limpar_prec(pessoa.get("prec", ""))

        inputs = []
        for el in driver.find_elements(By.CSS_SELECTOR, "input"):
            try:
                typ = (el.get_attribute("type") or "text").lower()
                if el.is_displayed() and typ in ("text", "tel", "number", ""):
                    meta = " ".join([
                        el.get_attribute("id") or "", el.get_attribute("name") or "",
                        el.get_attribute("placeholder") or "", el.get_attribute("title") or "",
                    ])
                    try:
                        meta += " " + str(driver.execute_script(
                            "return (arguments[0].parentElement && arguments[0].parentElement.innerText) || '';", el
                        ) or "")
                    except Exception:
                        pass
                    inputs.append((el, _pf_norm_web(meta)))
            except Exception:
                pass
        if not inputs:
            raise RuntimeError("Não encontrei o campo PREC-CP na página do SIAPPES.")

        campo_prec = None
        for el, meta in inputs:
            if campo_prec is None and "prec" in meta:
                campo_prec = el
        if campo_prec is None:
            campo_prec = inputs[0][0]
        campo_prec.clear()
        campo_prec.send_keys(prec)

        selects = []
        for el in driver.find_elements(By.TAG_NAME, "select"):
            try:
                if not el.is_displayed():
                    continue
                meta = " ".join([
                    el.get_attribute("id") or "", el.get_attribute("name") or "",
                    el.get_attribute("title") or "",
                ])
                try:
                    meta += " " + str(driver.execute_script(
                        "return (arguments[0].parentElement && arguments[0].parentElement.innerText) || '';", el
                    ) or "")
                except Exception:
                    pass
                selects.append((el, _pf_norm_web(meta)))
            except Exception:
                pass

        mes_int = int(_pf_digits(mes) or datetime.now().month)
        meses = [
            "janeiro", "fevereiro", "marco", "abril", "maio", "junho",
            "julho", "agosto", "setembro", "outubro", "novembro", "dezembro",
        ]
        alvos = {
            "ano": [str(ano)],
            "mes": [str(mes_int), f"{mes_int:02d}", meses[mes_int - 1]],
        }

        if len(selects) < 2:
            raise RuntimeError(
                f"A página do SIAPPES abriu, mas encontrei somente {len(selects)} campo(s) de seleção. "
                "Eram esperados Ano e Mês."
            )

        # Primeiro identifica os selects pelo conteúdo real das opções. Isso é mais
        # confiável que usar o texto do elemento pai, pois a página antiga coloca os
        # dois campos dentro da mesma tabela e ambos acabam recebendo a mesma legenda.
        def _opcoes_do_select(el):
            out = []
            try:
                from selenium.webdriver.support.ui import Select
                for opt in Select(el).options:
                    out.append((str(opt.text or "").strip(), str(opt.get_attribute("value") or "").strip()))
            except Exception:
                try:
                    out = driver.execute_script(
                        "return Array.from(arguments[0].options).map(o => [String(o.text||''), String(o.value||'')]);",
                        el,
                    ) or []
                except Exception:
                    out = []
            return out

        ano_idx = None
        mes_idx_select = None
        ano_d = re.sub(r"\D+", "", str(ano))
        for idx, (el, meta) in enumerate(selects):
            opts = _opcoes_do_select(el)
            textos = [f"{t} {v}" for t, v in opts]
            digitos = {re.sub(r"\D+", "", x) for x in textos}
            norm_opts = [_pf_norm_web(x) for x in textos]

            if ano_idx is None and (
                ano_d in digitos
                or any(re.search(r"\b20\d{2}\b", x) for x in textos)
                or "ano" in meta or "exercicio" in meta
            ):
                ano_idx = idx
                continue

            nomes_meses = set(meses)
            tem_mes_nome = any(any(m in n for m in nomes_meses) for n in norm_opts)
            numeros_opcoes = {d for d in digitos if d}
            tem_1_a_12 = len({str(i) for i in range(1, 13)} & numeros_opcoes) >= 6
            if mes_idx_select is None and (tem_mes_nome or tem_1_a_12 or "mes" in meta or "competencia" in meta):
                mes_idx_select = idx

        # A tela mostrada pelo usuário possui exatamente esta ordem: Ano e depois Mês.
        if ano_idx is None:
            ano_idx = 0
        if mes_idx_select is None or mes_idx_select == ano_idx:
            mes_idx_select = 1 if ano_idx != 1 and len(selects) > 1 else 0

        if not _pf_select_option(selects[ano_idx][0], alvos["ano"]):
            opcoes = _opcoes_do_select(selects[ano_idx][0])
            resumo = ", ".join((t or v) for t, v in opcoes[:12])
            raise RuntimeError(
                f"Não consegui selecionar o ano {ano} no SIAPPES. "
                f"Opções encontradas: {resumo or 'nenhuma'}."
            )
        if not _pf_select_option(selects[mes_idx_select][0], alvos["mes"]):
            opcoes = _opcoes_do_select(selects[mes_idx_select][0])
            resumo = ", ".join((t or v) for t, v in opcoes[:12])
            raise RuntimeError(
                f"Não consegui selecionar o mês {mes_int:02d} no SIAPPES. "
                f"Opções encontradas: {resumo or 'nenhuma'}."
            )

        handles_before = list(driver.window_handles)
        try:
            url_before = str(driver.current_url or "")
        except Exception:
            url_before = ""
        corpo_before = _pf_norm_web(_sigfur_cc_texto_atual_driver(driver, 50000))

        clicou = False
        for el in driver.find_elements(By.CSS_SELECTOR, "input,button,a"):
            try:
                txt = _pf_norm_web(el.text or el.get_attribute("value") or el.get_attribute("title") or "")
                # Na tela real o botão aparece como “Executar Consulta”.
                if ("executar" in txt and "consulta" in txt) or txt in ("consultar", "consulta"):
                    if el.is_displayed() and el.is_enabled():
                        el.click()
                        clicou = True
                        break
            except Exception:
                pass
        if not clicou:
            raise RuntimeError("Não consegui clicar no botão EXECUTAR CONSULTA do SIAPPES.")

        abriu_resultado = False
        corpo = ""
        fim = _time.time() + 40
        while _time.time() < fim:
            try:
                novos = [h for h in driver.window_handles if h not in handles_before]
                abriu_nova_aba = bool(novos)
                if novos:
                    driver.switch_to.window(novos[-1])
                _sigfur_cc_wait_doc_ready(driver, 4)
                corpo = _pf_norm_web(_sigfur_cc_texto_atual_driver(driver, 60000))
                try:
                    url_atual = str(driver.current_url or "")
                except Exception:
                    url_atual = ""

                marcadores_relatorio = (
                    "comprovante mensal de rendimentos",
                    "p/g real",
                    "p/g de pagamento",
                    "om de vinculacao",
                    "receitas despesas liquido",
                    "codigo descricao info complementar",
                )
                mudou_pagina = abriu_nova_aba or (url_before and url_atual and url_atual != url_before) or (corpo and corpo != corpo_before)
                ainda_formulario = "executar consulta" in corpo and "prec/cp" in corpo

                if any(m in corpo for m in marcadores_relatorio):
                    abriu_resultado = True
                    break
                if mudou_pagina and not ainda_formulario and _pf_norm_web(prec) in corpo:
                    abriu_resultado = True
                    break
                if any(x in corpo for x in ("nao encontrado", "nenhum registro", "inexistente", "prec-cp invalido")):
                    break
            except Exception:
                pass
            _time.sleep(0.5)

        corpo = corpo or _pf_norm_web(_sigfur_cc_texto_atual_driver(driver, 60000))
        if any(x in corpo for x in ("nao encontrado", "nenhum registro", "inexistente", "prec-cp invalido")):
            raise RuntimeError(f"O SIAPPES não encontrou contracheque para o PREC {prec}.")
        if not abriu_resultado:
            raise RuntimeError(
                "O SIAPPES recebeu a consulta, mas a página do contracheque não abriu. "
                "O formulário inicial não será salvo como PDF."
            )
        return handles_before

def _pf_print_current_page_pdf(driver, out_path: str) -> str:
        import base64 as _b64
        data = None
        try:
            from selenium.webdriver.common.print_page_options import PrintOptions
            opts = PrintOptions()
            try:
                opts.background = True
                opts.shrink_to_fit = True
            except Exception:
                pass
            data = driver.print_page(opts)
        except Exception:
            data = None
        if not data:
            try:
                res = driver.execute_cdp_cmd("Page.printToPDF", {
                    "printBackground": True,
                    "landscape": False,
                    "preferCSSPageSize": True,
                    "scale": 1.0,
                })
                data = res.get("data")
            except Exception as e:
                raise RuntimeError("A consulta abriu, mas não consegui gerar o PDF automaticamente.") from e
        raw = _b64.b64decode(data)
        os.makedirs(os.path.dirname(out_path), exist_ok=True)
        with open(out_path, "wb") as f:
            f.write(raw)
        return out_path

def _pf_unique_output_path(folder: str, filename: str) -> str:
        filename = _sigfur_cc_safe_filename(filename, "Contracheque.pdf")
        if not filename.lower().endswith(".pdf"):
            filename += ".pdf"
        base, ext = os.path.splitext(filename)
        out = os.path.join(folder, filename)
        n = 2
        while os.path.exists(out):
            out = os.path.join(folder, f"{base} ({n}){ext}")
            n += 1
        return out

def _contracheque_pessoas_fora_nome_from_pdf_text(texto: str, fallback: str = "") -> str:
        """Extrai o NOME do cabeçalho, sem confundir 'Pensão militar temporário' com pessoa."""
        texto = str(texto or "")
        linhas = [re.sub(r"\s+", " ", x).strip() for x in texto.splitlines() if str(x).strip()]

        # Layout CPEx: cabeçalho "PREC-CP NOME OM..." e os valores na linha seguinte.
        for i, linha in enumerate(linhas[:-1]):
            norm = _sigfur_cc_norm_text(linha)
            if "prec" in norm and "nome" in norm and ("om" in norm or "vincul" in norm):
                valor = linhas[i + 1]
                valor = re.sub(r"^\s*\d{1,3}\s+\d{6,12}\s+", "", valor)
                m = re.match(
                    r"([A-ZÁÀÂÃÉÊÍÓÔÕÚÇÜ][A-ZÁÀÂÃÉÊÍÓÔÕÚÇÜ'\- ]{5,90}?)(?=\s+(?:CIA|BIA|BATALH|REGIMENTO|HOSPITAL|BASE|PARQUE|ARSENAL|COL[EÉ]GIO|[0-9]+[ªº]?\s*(?:RM|BDA|DE|CIA|BIMTZ))\b|$)",
                    valor,
                    flags=re.I,
                )
                if m:
                    cand = _pf_normalizar_nome(m.group(1))
                    if len(cand.split()) >= 2:
                        return cand

        padroes = (
            r"\bNome\s*(?:do\s+Favorecido|do\s+Militar)?\s*[:\-]?\s*([A-ZÁÀÂÃÉÊÍÓÔÕÚÇÜ][A-ZÁÀÂÃÉÊÍÓÔÕÚÇÜ'\- ]{5,90})",
            r"\bFavorecido\s*[:\-]?\s*([A-ZÁÀÂÃÉÊÍÓÔÕÚÇÜ][A-ZÁÀÂÃÉÊÍÓÔÕÚÇÜ'\- ]{5,90})",
        )
        proibidos = ("MILITAR TEMPORARIO", "PENSAO MILITAR", "COMPROVANTE", "MINISTERIO", "EXERCITO")
        for linha in linhas:
            if len(linha) > 180:
                continue
            for pad in padroes:
                m = re.search(pad, linha, flags=re.I)
                if not m:
                    continue
                cand = _pf_normalizar_nome(re.sub(r"\b(CPF|PREC|CP|IDENTIDADE|IDT|POSTO|GRAD|OM)\b.*$", "", m.group(1), flags=re.I))
                if len(cand.split()) >= 2 and not any(p in _sigfur_cc_norm_text(cand).upper() for p in proibidos):
                    return cand

        # O nome fornecido na lista é mais confiável do que qualquer descrição de rubrica.
        return _pf_normalizar_nome(fallback)

def _pf_cpex_download_batch(
        sistema: str, pessoas: list[dict], pasta: str, mes: str, ano: str,
        usuario: str, senha: str, processamento: str = "Definitivo",
        tipo_folha: str = "Normal", navegador: str = "Edge", progress=None,
    ) -> tuple[list[str], list[str]]:
        """Login comum da UA e download direto, oculto, para SIPPES ou SIAPPES."""
        sistema = str(sistema or _PF_SIPPES).upper()
        salvos, erros = [], []
        driver = None
        try:
            driver = _pf_create_cpex_driver(pasta, navegador)
            if callable(progress):
                progress("Entrando na Área Exclusiva da UA…")
            _pf_cpex_login(driver, usuario, senha)
            total = len(pessoas)
            for idx, pessoa in enumerate(pessoas, start=1):
                nome_lista = _pf_normalizar_nome(pessoa.get("nome", ""))
                ident = _pf_limpar_prec(pessoa.get("prec", "")) if sistema == _PF_SIAPPES else _pf_limpar_cpf(pessoa.get("cpf", ""))
                handles_before = []
                try:
                    if callable(progress):
                        progress(f"{sistema}: consultando {idx}/{total} — {nome_lista or ident}")
                    if sistema == _PF_SIAPPES:
                        handles_before = _pf_siappes_fill_and_consult(driver, pessoa, mes, ano)
                    else:
                        handles_before = _pf_sippes_fill_and_consult(
                            driver, pessoa, mes, ano, processamento, tipo_folha,
                        )
                    texto_pagina = _sigfur_cc_texto_atual_driver(driver, 90000)
                    nome_final = _contracheque_pessoas_fora_nome_from_pdf_text(texto_pagina, nome_lista) or nome_lista
                    nome_final = _pf_normalizar_nome(nome_final) or (f"PREC {ident}" if sistema == _PF_SIAPPES else f"CPF {ident}")
                    if sistema == _PF_SIAPPES:
                        filename = f"Contracheque - {nome_final} - PREC {ident} - {ano}-{int(mes):02d}.pdf"
                    else:
                        filename = (
                            f"Contracheque - {nome_final} - CPF {ident} - {ano}-{int(mes):02d} - "
                            f"{processamento} - {tipo_folha}.pdf"
                        )
                    out = _pf_unique_output_path(pasta, filename)
                    _pf_print_current_page_pdf(driver, out)
                    salvos.append(out)
                except Exception as e:
                    erros.append(f"{nome_lista or ident}: {e}")
                finally:
                    # Fecha abas de resultado; se o relatório abriu na mesma aba, a
                    # próxima consulta simplesmente navega para a tela correta.
                    try:
                        atuais = list(driver.window_handles)
                        base_handle = handles_before[0] if handles_before else (atuais[0] if atuais else None)
                        for h in atuais:
                            if base_handle and h != base_handle:
                                try:
                                    driver.switch_to.window(h); driver.close()
                                except Exception:
                                    pass
                        if base_handle:
                            driver.switch_to.window(base_handle)
                    except Exception:
                        pass
            return salvos, erros
        finally:
            try:
                if driver:
                    driver.quit()
            except Exception:
                pass

def download_batch(
    sistema: str, pessoas: list[dict], pasta: str, mes: int | str, ano: int | str,
    usuario: str, senha: str, processamento: str = "Definitivo",
    tipo_folha: str = "Normal", navegador: str = "Edge", progress=None,
) -> dict:
    pasta = os.path.abspath(str(pasta or "").strip())
    if not pasta:
        raise RuntimeError("Escolha a pasta de destino dos contracheques.")
    os.makedirs(pasta, exist_ok=True)
    sistema = str(sistema or _PF_SIPPES).upper()
    if sistema not in (_PF_SIPPES, _PF_SIAPPES):
        raise RuntimeError("Sistema inválido. Use SIPPES ou SIAPPES.")
    pessoas_validas = []
    for pessoa in pessoas or []:
        item = dict(pessoa or {})
        if sistema == _PF_SIPPES and len(_pf_limpar_cpf(item.get("cpf", ""))) == 11:
            pessoas_validas.append(item)
        elif sistema == _PF_SIAPPES and len(_pf_limpar_prec(item.get("prec", ""))) in (9, 10):
            pessoas_validas.append(item)
    if not pessoas_validas:
        doc = "CPF" if sistema == _PF_SIPPES else "PREC-CP"
        raise RuntimeError(f"Nenhuma pessoa com {doc} válido foi informada.")
    salvos, erros = _pf_cpex_download_batch(
        sistema, pessoas_validas, pasta, str(mes), str(ano), usuario, senha,
        processamento, tipo_folha, navegador, progress=progress,
    )
    return {"saved": salvos, "errors": erros, "folder": pasta}
