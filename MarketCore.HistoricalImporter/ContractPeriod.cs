namespace MarketCore.HistoricalImporter;

/// <summary>Período negociável de um contrato WIN (mini índice) — 60 dias antes do vencimento até o vencimento.</summary>
public sealed record ContractPeriod(
    string Symbol,
    DateTime StartDate,
    DateTime EndDate,
    DateTime ExpirationDate);
