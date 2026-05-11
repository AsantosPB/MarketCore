using System.Runtime.InteropServices;
using System.Threading;

namespace MarketCore.HistoricalImporter;

/// <summary>Inicialização mínima da ProfitDLL64 para permitir histórico (<c>DLLNewHistoryTypedTradesByPeriod</c>).</summary>
public static class ProfitMarketInit
{
    private const string Dll = "ProfitDLL64.dll";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct TAssetID
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Ticker;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Bolsa;
        public int nFeedType;
    }

    private delegate void TStateCallback(int nConnStateType, int result);

    private delegate void TTradeCallback(
        TAssetID assetId,
        [MarshalAs(UnmanagedType.LPWStr)] string? date,
        uint tradeNumber, double price, double vol,
        int qtd, int buyAgent, int sellAgent, int tradeType, int bIsEdit);

    private delegate void TNewDailyCallback(
        TAssetID assetId,
        [MarshalAs(UnmanagedType.LPWStr)] string? date,
        double sOpen, double sHigh, double sLow, double sClose,
        double sVol, double sAjuste, double sMaxLimit, double sMinLimit,
        double sVolBuyer, double sVolSeller,
        int nQtd, int nNegocios, int nContratosOpen,
        int nQtdBuyer, int nQtdSeller, int nNegBuyer, int nNegSeller);

    private delegate void TPriceBookCallback(
        TAssetID assetId, int nAction, int nPosition,
        int side, int nQtd, int nCount, double sPrice,
        IntPtr pArraySell, IntPtr pArrayBuy);

    private delegate void TOfferBookCallbackV2(
        TAssetID assetId, int nAction, int nPosition,
        int side, long nQtd, int nAgent, long nOfferID, double sPrice,
        ushort bHasPrice, ushort bHasQtd, ushort bHasDate, ushort bHasOfferID, ushort bHasAgent,
        [MarshalAs(UnmanagedType.LPWStr)] string? date,
        IntPtr pArraySell, IntPtr pArrayBuy);

    private delegate void THistoryTradeCallback(
        TAssetID assetId,
        [MarshalAs(UnmanagedType.LPWStr)] string? date,
        uint tradeNumber, double price, double vol,
        int qtd, int buyAgent, int sellAgent, int tradeType);

    private delegate void TProgressCallBack(TAssetID assetId, int nProgress);
    private delegate void TNewTinyBookCallBack(TAssetID assetId, double price, int qtd, int side);

    private delegate void TChangeCotation(
        TAssetID assetId,
        [MarshalAs(UnmanagedType.LPWStr)] string? date,
        uint tradeNumber,
        double sPrice);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int SetChangeCotationCallback(TChangeCotation a_ChangeCotation);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int SetOfferBookCallbackV2(TOfferBookCallbackV2 a_OfferBookCallback);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int DLLInitializeMarketLogin(
        [MarshalAs(UnmanagedType.LPWStr)] string activationKey,
        [MarshalAs(UnmanagedType.LPWStr)] string username,
        [MarshalAs(UnmanagedType.LPWStr)] string password,
        TStateCallback stateCallback,
        TTradeCallback newTradeCallback,
        TNewDailyCallback? dailyCallback,
        TPriceBookCallback? priceBookCallback,
        TOfferBookCallbackV2? offerBookCallback,
        THistoryTradeCallback? historyTradeCallback,
        TProgressCallBack? progressCallBack,
        TNewTinyBookCallBack? tinyBookCallback);

    private static volatile bool s_marketReady;
    private static TStateCallback? s_state;
    private static TTradeCallback? s_trade;
    private static TNewDailyCallback? s_daily;
    private static TPriceBookCallback? s_priceBook;
    private static TOfferBookCallbackV2? s_offerBook;
    private static THistoryTradeCallback? s_history;
    private static TProgressCallBack? s_progress;
    private static TNewTinyBookCallBack? s_tiny;
    private static TChangeCotation? s_changeCotation;

    /// <summary>
    /// Se <paramref name="sessionAlreadyConnected"/> é <c>true</c>, assume que a ProfitDLL já foi inicializada
    /// pelo processo atual (ex.: MarketEngine) e não chama <c>DLLInitializeMarketLogin</c> novamente.
    /// </summary>
    public static Task<bool> TryEnsureMarketForHistoryAsync(
        ProfitCredentialsConfig creds,
        TimeSpan timeout,
        bool sessionAlreadyConnected,
        CancellationToken ct = default)
    {
        if (sessionAlreadyConnected)
            return Task.FromResult(true);

        return TryInitializeAsync(creds, timeout, ct);
    }

    /// <returns><c>true</c> se mercado conectado (callback estado 2 / resultado 4).</returns>
    public static async Task<bool> TryInitializeAsync(ProfitCredentialsConfig creds, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(creds);
        if (string.IsNullOrWhiteSpace(creds.Username) || string.IsNullOrWhiteSpace(creds.Password))
            return false;

        s_marketReady = false;
        RootDelegates();

        int code = DLLInitializeMarketLogin(
            creds.ActivationKey ?? "",
            creds.Username,
            creds.Password,
            s_state!,
            s_trade!,
            s_daily,
            s_priceBook,
            s_offerBook,
            s_history,
            s_progress,
            s_tiny);

        if (code != 0)
        {
            ProfitDllDiag.Append($"[ProfitMarketInit] DLLInitializeMarketLogin rc={code}");
            return false;
        }

        try
        {
            int rCot = SetChangeCotationCallback(s_changeCotation!);
            int rOb = SetOfferBookCallbackV2(s_offerBook!);
            ProfitDllDiag.Append($"[ProfitMarketInit] SetChangeCotationCallback rc={rCot} SetOfferBookCallbackV2 rc={rOb}");
        }
        catch (Exception ex)
        {
            ProfitDllDiag.Append($"[ProfitMarketInit] post-init ex: {ex.Message}");
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (s_marketReady) return true;
            await Task.Delay(100, ct);
        }

        return s_marketReady;
    }

    private static void RootDelegates()
    {
        s_state = static (t, r) =>
        {
            if (t == 2 && r == 4)
                s_marketReady = true;
        };
        s_trade = static (assetId, date, tradeNumber, price, vol, qtd, buyAgent, sellAgent, tradeType, _) =>
        {
            ProfitHistoryRelay.TryMirrorNewTradeDuringHistoricalDownload(
                assetId.Ticker, date, tradeNumber, price, vol, qtd, buyAgent, sellAgent, tradeType);
        };
        s_daily = static (_, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _) => { };
        s_priceBook = static (_, _, _, _, _, _, _, _, _) => { };
        s_offerBook = static (_, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _) => { };
        s_history = static (assetId, date, tradeNumber, price, vol, qtd, buyAgent, sellAgent, tradeType) =>
        {
            ProfitHistoryRelay.Raise(
                assetId.Ticker,
                date,
                tradeNumber,
                price,
                vol,
                qtd,
                buyAgent,
                sellAgent,
                tradeType);
        };
        s_progress = static (_, _) => { };
        s_tiny = static (_, _, _, _) => { };
        s_changeCotation = static (_, _, _, _) => { };
    }
}
