@echo off
cd /d "%~dp0"
echo === Building MarketCore.WPF === > _build_output.txt 2>&1
dotnet build MarketCore.WPF\MarketCore.WPF.csproj >> _build_output.txt 2>&1
echo. >> _build_output.txt
echo === EXIT CODE: %ERRORLEVEL% === >> _build_output.txt
