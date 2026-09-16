@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title SIGFUR 6.1.8 - Compilar e abrir

echo ============================================================
echo  SIGFUR 6.1.8 - compilacao limpa e abertura
echo ============================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo ERRO: o SDK do .NET nao foi encontrado no PATH.
    echo Este projeto usa .NET 10 para Windows.
    echo Instale ou repare o .NET 10 SDK x64 e execute novamente.
    echo.
    pause
    exit /b 1
)

echo SDK encontrado:
dotnet --version
echo.

echo [PRE] Limpando residuos de versoes antigas deixados na pasta do projeto...
if exist "Services\PythonBridgeService.cs" del /f /q "Services\PythonBridgeService.cs" >nul 2>&1
for /r %%F in (*-Otavio.cs *_Otavio.cs *Otavio*.cs *-old.cs *_old.cs *.old.cs *-backup.cs *_backup.cs *.backup.cs *-bak.cs *_bak.cs *.bak.cs *.orig.cs *.tmp.cs *.disabled.cs) do (
    echo Ignorando/removendo backup: %%~nxF
    del /f /q "%%F" >nul 2>&1
)
for /r %%F in ("* - Copia.cs" "* - Cópia.cs" "* - Copy.cs" "* - Copia.xaml" "* - Cópia.xaml" "* - Copy.xaml") do (
    echo Ignorando/removendo copia antiga: %%~nxF
    del /f /q "%%F" >nul 2>&1
)

echo.
echo [0/3] Fechando instancias abertas do SIGFUR...
call :FecharSigfur
if errorlevel 1 exit /b 1

echo.
echo [1/3] Limpando compilacoes antigas e XAML em cache...
if exist "%~dp0bin" rmdir /s /q "%~dp0bin"
if exist "%~dp0obj" rmdir /s /q "%~dp0obj"
if exist "%~dp0bin" (
    echo ERRO: nao foi possivel limpar a pasta bin.
    echo Feche o SIGFUR, o Visual Studio e qualquer janela usando os arquivos do projeto.
    echo Se o OneDrive estiver sincronizando, aguarde alguns segundos e tente novamente.
    echo.
    pause
    exit /b 1
)
if exist "%~dp0obj" (
    echo ERRO: nao foi possivel limpar a pasta obj.
    echo Feche o SIGFUR, o Visual Studio e qualquer janela usando os arquivos do projeto.
    echo.
    pause
    exit /b 1
)

echo.
echo [2/3] Compilando o projeto corrigido do zero...
dotnet build "%~dp0SIGFUR.Wpf.csproj" -c Debug --no-incremental
if errorlevel 1 (
    echo.
    echo A compilacao falhou. A janela permanecera aberta para voce copiar o erro.
    pause
    exit /b 1
)

echo.
echo [3/3] Abrindo o SIGFUR...
dotnet run --project "%~dp0SIGFUR.Wpf.csproj" -c Debug --no-build
set "EXITCODE=%ERRORLEVEL%"

echo.
echo O SIGFUR encerrou com codigo %EXITCODE%.
echo Em caso de erro, confira:
echo %%LOCALAPPDATA%%\SIGFUR\logs\wpf_app.log
pause
exit /b %EXITCODE%

:FecharSigfur
tasklist /FI "IMAGENAME eq SIGFUR.exe" 2>nul | find /I "SIGFUR.exe" >nul
if errorlevel 1 (
    echo Nenhuma instancia aberta encontrada.
    exit /b 0
)

echo SIGFUR esta aberto. Encerrando para liberar o executavel...
taskkill /F /T /IM SIGFUR.exe >nul 2>nul
timeout /t 2 /nobreak >nul

tasklist /FI "IMAGENAME eq SIGFUR.exe" 2>nul | find /I "SIGFUR.exe" >nul
if not errorlevel 1 (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Process -Name SIGFUR -ErrorAction SilentlyContinue ^| Stop-Process -Force" >nul 2>nul
    timeout /t 1 /nobreak >nul
)

tasklist /FI "IMAGENAME eq SIGFUR.exe" 2>nul | find /I "SIGFUR.exe" >nul
if not errorlevel 1 (
    echo.
    echo ERRO: o SIGFUR continua aberto e esta bloqueando a compilacao.
    echo Feche-o pelo Gerenciador de Tarefas e execute este arquivo novamente.
    echo.
    pause
    exit /b 1
)

echo SIGFUR fechado com sucesso.
exit /b 0
