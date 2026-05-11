using System.Threading;

namespace MarketCore.HistoricalImporter;

/// <summary>
/// Ponto único para negócios históricos vindos da ProfitDLL, quer o login seja feito por
/// <see cref="ProfitMarketInit"/> ou por <c>ProfitDLLProvider</c>.
/// </summary>
public static class ProfitHistoryRelay
{
    private static int _activeHistoricalDownloads;

    /// <summary>Increments while <see cref="ProfitHistoryService"/> está a pedir negócios históricos.</summary>
    public static void BeginHistoricalDownloadScope() =>
        Interlocked.Increment(ref _activeHistoricalDownloads);

    public static void EndHistoricalDownloadScope() =>
        Interlocked.Decrement(ref _activeHistoricalDownloads);

    internal static bool IsHistoricalDownloadScopeActive =>
        Volatile.Read(ref _activeHistoricalDownloads) > 0;

    /// <summary>
    /// Algumas builds enviam replay de histórico pelo callback <b>NewTrade</b> com <c>qtd=0</c> e quantidade em <c>vol</c>.
    /// Espelha para o mesmo pipeline do callback de histórico durante um download.
    /// </summary>
    public static void TryMirrorNewTradeDuringHistoricalDownload(
        string? ticker,
        string? date,
        uint tradeNumber,
        double price,
        double vol,
        int qtd,
        int buyAgent,
        int sellAgent,
        int tradeType)
    {
        if (!IsHistoricalDownloadScopeActive)
            return;

        // Replay/histórico por NewTrade costuma vir com qtd==0 e quantidade em vol.
        // Negócios ao vivo têm qtd>0; espelhá-los aqui incrementava o contador usado para
        // saber se GetHistoryTrades "recebeu" dados — falso positivo fora do pregão ou no 2.º pedido.
        if (qtd > 0)
            return;

        string tk = ticker ?? string.Empty;
        if (tk.IndexOf("WIN", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        if (price <= 0 || price > 10_000_000 || double.IsNaN(price))
            return;

        Raise(tk, date ?? string.Empty, tradeNumber, price, vol, qtd, buyAgent, sellAgent, tradeType);
    }

    public static event Action<string, string, uint, double, double, int, int, int, int>? NativeHistoryTrade;

    /// <summary>Progresso reportado pela DLL durante carregamentos longos (ex. histórico) — ticker + 0–100+.</summary>
    public static event Action<string, int>? HistoryProgress;

    public static void RaiseHistoryProgress(string? ticker, int percent) =>
        HistoryProgress?.Invoke(ticker ?? string.Empty, percent);

    public static void Raise(
        string? ticker,
        string? date,
        uint tradeNumber,
        double price,
        double vol,
        int qtd,
        int buyAgent,
        int sellAgent,
        int tradeType)
    {
        NativeHistoryTrade?.Invoke(
            ticker ?? string.Empty,
            date ?? string.Empty,
            tradeNumber,
            price,
            vol,
            qtd,
            buyAgent,
            sellAgent,
            tradeType);
    }
}
