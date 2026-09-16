@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title SIGFUR - Limpar duplicados e compilar

echo ============================================================
echo  SIGFUR - limpeza de duplicados antigos e build limpo
echo ============================================================
echo.

echo [1/4] Removendo arquivos temporarios de compilacao...
if exist "bin" rmdir /s /q "bin" >nul 2>&1
if exist "obj" rmdir /s /q "obj" >nul 2>&1

echo [2/4] Removendo backups antigos que quebram o WPF...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$root=(Resolve-Path '.').Path; $patterns=@('*-Otavio.cs','*_Otavio.cs','*Otavio*.cs','*-old.cs','*_old.cs','*.old.cs','*-backup.cs','*_backup.cs','*.backup.cs','*-bak.cs','*_bak.cs','*.bak.cs','* - Copia.cs','* - Cópia.cs','* - Copy.cs','*.orig.cs','*.tmp.cs','*.disabled.cs','*-Otavio.xaml','*_Otavio.xaml','*Otavio*.xaml','* - Copia.xaml','* - Cópia.xaml','* - Copy.xaml'); foreach($pat in $patterns){ Get-ChildItem -LiteralPath $root -Recurse -File -Filter $pat -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ('Removendo: ' + $_.FullName); Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue } }"
if exist "Services\PythonBridgeService.cs" del /f /q "Services\PythonBridgeService.cs" >nul 2>&1

echo.
echo [3/4] Conferindo SDK .NET...
where dotnet >nul 2>nul
if errorlevel 1 (
    echo ERRO: SDK .NET nao encontrado no PATH.
    pause
    exit /b 1
)
dotnet --version

echo.
echo [4/4] Build limpo...
dotnet build "%~dp0SIGFUR.Wpf.csproj" -c Debug --no-incremental
if errorlevel 1 (
    echo.
    echo A compilacao ainda falhou. Copie o erro novo e envie.
    pause
    exit /b 1
)

echo.
echo Build concluido com sucesso.
pause
exit /b 0
