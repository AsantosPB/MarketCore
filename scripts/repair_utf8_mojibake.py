#!/usr/bin/env python3
"""
Repara texto UTF-8 com mojibake (double/triple encoding) em ficheiros do repo.
Mantém acentos e — (U+2014); grava UTF-8 sem BOM.
"""
from __future__ import annotations

import sys
from pathlib import Path

import ftfy

# Substituições (bytes antigos -> bytes correctos). Ordem: mais longas primeiro.
BYTE_REPLACEMENTS: list[tuple[bytes, bytes]] = [
    # Travessão — (várias rotas de corrupção)
    (b"\xc3\xa2\xe2\x82\xac\xe2\x80\x9d", b"\xe2\x80\x94"),
    (b"\xc3\xa2\xc2\x80\xc2\x94", b"\xe2\x80\x94"),
    (b"\xc3\xa2\xc2\x80\xc2\x93", b"\xe2\x80\x93"),  # en dash
    # "Ã©" / "Ã¡" clássicos (UTF-8 lido como Latin-1 e voltar a gravar)
    (b"\xc3\x83\xc2\xa1", b"\xc3\xa1"),  # á
    (b"\xc3\x83\xc2\xa0", b"\xc3\xa0"),  # à
    (b"\xc3\x83\xc2\xa3", b"\xc3\xa3"),  # ã
    (b"\xc3\x83\xc2\xa9", b"\xc3\xa9"),  # é
    (b"\xc3\x83\xc2\xaa", b"\xc3\xaa"),  # ê
    (b"\xc3\x83\xc2\xad", b"\xc3\xad"),  # í
    (b"\xc3\x83\xc2\xb3", b"\xc3\xb3"),  # ó
    (b"\xc3\x83\xc2\xb5", b"\xc3\xb5"),  # õ
    (b"\xc3\x83\xc2\xba", b"\xc3\xba"),  # ú
    (b"\xc3\x83\xc2\xa7", b"\xc3\xa7"),  # ç
    (b"\xc3\x83\xc2\x81", b"\xc3\x81"),  # Á
    (b"\xc3\x83\xc2\x89", b"\xc3\x89"),  # É
    (b"\xc3\x83\xc2\x93", b"\xc3\x93"),  # Ó
    (b"\xc3\x83\xc2\x9a", b"\xc3\x9a"),  # Ú
    (b"\xc3\x83\xc2\x87", b"\xc3\x87"),  # Ç
    # Variante observada em XAML (bytes UTF-8 duplicados sobre um carácter de 2 bytes)
    (b"\xc3\xa1\xc2\xa1", b"\xc3\xa1"),
    (b"\xc3\xa9\xc2\xa9", b"\xc3\xa9"),
    (b"\xc3\xad\xc2\xad", b"\xc3\xad"),
    (b"\xc3\xb3\xc2\xb3", b"\xc3\xb3"),
    (b"\xc3\xba\xc2\xba", b"\xc3\xba"),
    (b"\xc3\xa0\xc2\xa0", b"\xc3\xa0"),
    (b"\xc3\xa3\xc2\xa3", b"\xc3\xa3"),
    (b"\xc3\xa7\xc2\xa7", b"\xc3\xa7"),
    (b"\xc3\xaa\xc2\xaa", b"\xc3\xaa"),
    (b"\xc3\xb5\xc2\xb5", b"\xc3\xb5"),
    # Camada extra (triple) comum: C3 83 C2 83 …
    (b"\xc3\x83\xc2\x83", b"\xc3\x83"),
]


def repair_bytes(data: bytes) -> bytes:
    """Aplica substituições byte-a-byte até estabilizar."""
    # Ordenar por tamanho decrescente
    reps = sorted(BYTE_REPLACEMENTS, key=lambda x: -len(x[0]))
    for _ in range(48):
        changed = False
        for old, new in reps:
            if old in data and old != new:
                data = data.replace(old, new)
                changed = True
        if not changed:
            break
    return data


def repair_text_with_ftfy(text: str) -> str:
    t = ftfy.fix_text(text)
    if t != text:
        t2 = ftfy.fix_text(t)
        if len(t2) <= len(t) * 2:
            t = t2
    return t


def process_file(path: Path) -> None:
    raw = path.read_bytes()
    if raw.startswith(b"\xef\xbb\xbf"):
        raw = raw[3:]

    fixed_b = repair_bytes(raw)
    text = fixed_b.decode("utf-8", errors="strict")
    text = repair_text_with_ftfy(text)
    path.write_bytes(text.encode("utf-8"))


def main() -> int:
    root = Path(__file__).resolve().parents[1]
    rels = [
        r"Engine\Detectors\ExhaustionDetector.cs",
        r"Engine\Detectors\SpoofDetector.cs",
        r"MarketCore.HistoricalImporter\ProfitHistoryService.cs",
        r"MarketCore.HistoricalImporter\TradeRecordFactory.cs",
        r"MarketCore.WPF\AgentPanel\Detectors\AllDetectors.cs",
        r"MarketCore.WPF\AgentPanel\Detectors\DetectorRegistry.cs",
        r"MarketCore.WPF\AgentPanel\AgentPanelWindow.xaml",
        r"MarketCore.WPF\AgentPanel\AgentPanelWindow.xaml.cs",
        r"MarketCore.WPF\AgentPanel\AgentViewModel.cs",
        r"MarketCore.WPF\AgentPanel\SignalAggregator.cs",
        r"MarketCore.WPF\AnaliseQuantitativa\Analisequantitativaviewmodel.cs",
        r"MarketCore.WPF\AnaliseQuantitativa\Analisequantitativawindow.xaml",
        r"MarketCore.WPF\AnaliseQuantitativa\Analisequantitativawindow.xaml.cs",
        r"MarketCore.WPF\FlowSense\BookAnalyzer.cs",
        r"MarketCore.WPF\FlowSense\ConnectionLogger.cs",
        r"MarketCore.WPF\FlowSense\FlowScoreConfig.cs",
        r"MarketCore.WPF\FlowSense\FlowScoreConfigWindow.xaml",
        r"MarketCore.WPF\FlowSense\FlowScoreConfigWindow.xaml.cs",
        r"MarketCore.WPF\FlowSense\FlowScoreEngine.cs",
        r"MarketCore.WPF\FlowSense\FlowScorePanel.xaml",
        r"MarketCore.WPF\FlowSense\FlowScorePanel.xaml.cs",
        r"MarketCore.WPF\FlowSense\ProfitCredentials.cs",
        r"MarketCore.WPF\FlowSense\ProfitLoginWindow.xaml",
        r"MarketCore.WPF\FlowSense\ProfitLoginWindow.xaml.cs",
        r"MarketCore.WPF\FlowCandleChart.xaml",
        r"MarketCore.WPF\FlowCandleChart.xaml.cs",
        r"MarketCore.WPF\MainWindow.xaml.cs",
        r"Providers\Nelogica\ProfitDLL.cs",
        r"Providers\Nelogica\ProfitDLLProvider.cs",
        r"Providers\Replay\Replayprovider.cs",
        r"Program.cs",
    ]

    for rel in rels:
        p = root / rel.replace("\\", "/")
        if not p.is_file():
            print(f"SKIP missing: {p}", file=sys.stderr)
            continue
        try:
            process_file(p)
        except UnicodeDecodeError as e:
            print(f"FAIL decode {rel}: {e}", file=sys.stderr)
            continue
        print(f"OK {rel}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())