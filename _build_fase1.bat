@echo off
cd /d "%~dp0"
echo === Build MarketCore - Fase 1 ===
dotnet build MarketCore.csproj -c Debug --no-restore 2>&1 | tee _build_output_fase1.txt
echo === Fim. Verifique _build_output_fase1.txt ===
pause
