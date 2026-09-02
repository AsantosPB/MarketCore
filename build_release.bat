@echo off
cd /d "%~dp0"
echo Building MarketCore Release...
dotnet build MarketCore.WPF\MarketCore.WPF.csproj --configuration Release > build_output.txt 2>&1
echo Build finished. Exit code: %ERRORLEVEL% >> build_output.txt
echo Done. See build_output.txt
pause
