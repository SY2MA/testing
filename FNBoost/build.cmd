@echo off
REM Compila FN Boost in un unico FNBoost.exe (non serve installare .NET per eseguirlo).
REM Requisito per compilare: .NET 10 SDK  ->  winget install Microsoft.DotNet.SDK.10
setlocal
cd /d "%~dp0"
dotnet publish src\FNBoost\FNBoost.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -o dist
if errorlevel 1 (
  echo.
  echo Compilazione non riuscita. Verifica di avere installato il .NET 10 SDK.
  pause
  exit /b 1
)
echo.
echo Fatto: %~dp0dist\FNBoost.exe
pause
