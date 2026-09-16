@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
title SIGFUR 6.1.6 - Preparar automacao

echo ============================================================
echo  SIGFUR 6.1.6 - preparar SIPPES / SIAPPES
echo ============================================================
echo.
echo Este instalador cria um Python local para o SIGFUR e instala o
echo Selenium e os leitores de PDF nele. Assim o programa nao usa um Python diferente.
echo.

set "BASE_PY="
if exist "%LocalAppData%\Programs\Python\Python314\python.exe" set "BASE_PY=%LocalAppData%\Programs\Python\Python314\python.exe"
if not defined BASE_PY if exist "%LocalAppData%\Programs\Python\Python313\python.exe" set "BASE_PY=%LocalAppData%\Programs\Python\Python313\python.exe"
if not defined BASE_PY (
    where py >nul 2>nul
    if not errorlevel 1 set "BASE_PY=py -3"
)
if not defined BASE_PY (
    where python >nul 2>nul
    if not errorlevel 1 set "BASE_PY=python"
)
if not defined BASE_PY (
    echo ERRO: Python 3 nao foi encontrado.
    echo Instale o Python 3 ou configure o executavel no Perfil do SIGFUR.
    pause
    exit /b 1
)

if not exist ".venv\Scripts\python.exe" (
    echo [1/4] Criando ambiente local .venv com acesso aos pacotes atuais...
    %BASE_PY% -m venv --system-site-packages ".venv"
    if errorlevel 1 goto :erro
) else (
    echo [1/4] Ambiente local ja existe.
)

set "PYTHON_CMD=%CD%\.venv\Scripts\python.exe"
echo Python do SIGFUR: %PYTHON_CMD%

echo [2/4] Garantindo o pip...
"%PYTHON_CMD%" -m ensurepip --upgrade >nul 2>nul

echo [3/4] Atualizando pip...
"%PYTHON_CMD%" -m pip install --disable-pip-version-check --upgrade pip
if errorlevel 1 goto :erro

echo [4/4] Instalando/atualizando Selenium e leitores de PDF...
"%PYTHON_CMD%" -m pip install --disable-pip-version-check --upgrade selenium pypdf pymupdf
if errorlevel 1 goto :erro

echo.
"%PYTHON_CMD%" -c "import sys, selenium, pypdf; print('Python:', sys.executable); print('Selenium pronto - versao', selenium.__version__); print('Leitor PDF pronto - pypdf', pypdf.__version__)"
if errorlevel 1 goto :erro

echo.
echo Preparacao concluida.
echo O SIGFUR vai localizar automaticamente este Python local.
echo.
pause
exit /b 0

:erro
echo.
echo Nao foi possivel preparar a automacao.
echo Verifique a internet e tente novamente. Dentro da Central de
echo Contracheques tambem existe o botao Preparar automacao.
echo.
pause
exit /b 1
