namespace MarketCore.HistoricalImporter;

/// <summary>Destino dos negócios recebidos por <see cref="ProfitHistoryService"/>.</summary>
public interface IProfitHistoryTradeSink
{
    void SetCurrentContract(string symbol);

    void OnHistoricalTrade(
        int opType,
        string symbol,
        string date,
        string time,
        double price,
        int qty,
        int tradeNum,
        int buyOrder,
        int sellOrder,
        string buyBroker,
        string sellBroker,
        int aggressor);

    /// <summary>Grava pendências antes de mudar contrato/período.</summary>
    void FlushPendingExports();
}
