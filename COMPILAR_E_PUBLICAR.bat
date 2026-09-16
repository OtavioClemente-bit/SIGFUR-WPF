@echo off
REM Limpeza de resíduos de versões antigas antes da compilação
if exist "Services\PythonBridgeService.cs" del /f /q "Services\PythonBridgeService.cs" >nul 2>&1
if exist "bin" rmdir /s /q "bin" >nul 2>&1
if exist "obj" rmdir /s /q "obj" >nul 2>&1
setlocal EnableExtensions
cd /d "%~dp0"
title SIGFUR 6.1.8 - Compilar e publicar

echo ============================================================
echo  SIGFUR 6.1.8 - PUBLICACAO WINDOWS X64
echo ============================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo ERRO: SDK .NET 10 x64 nao encontrado.
    echo Instale o SDK completo do .NET 10 e tente novamente.
    pause
    exit /b 1
)

taskkill /F /T /IM SIGFUR.exe >nul 2>nul
if exist "bin" rmdir /s /q "bin"
if exist "obj" rmdir /s /q "obj"
if exist "PUBLICADO" rmdir /s /q "PUBLICADO"

echo [1/3] Restaurando pacotes...
dotnet restore "SIGFUR.Wpf.csproj" --runtime win-x64
if errorlevel 1 goto :erro

echo [2/3] Compilando...
dotnet build "SIGFUR.Wpf.csproj" -c Release --no-restore --no-incremental
if errorlevel 1 goto :erro

echo [3/3] Publicando pasta portatil autossuficiente...
dotnet publish "SIGFUR.Wpf.csproj" -c Release -r win-x64 --self-contained true --no-restore -o "PUBLICADO"
if errorlevel 1 goto :erro

echo.
echo CONCLUIDO: %~dp0PUBLICADO\SIGFUR.exe
echo Leve a pasta PUBLICADO inteira para o outro computador.
start "" "%~dp0PUBLICADO"
pause
exit /b 0

:erro
echo.
echo A compilacao falhou. Copie o erro exibido acima.
pause
exit /b 1
