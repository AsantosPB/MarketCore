using System.Runtime.InteropServices;

namespace MarketCore.Providers.Nelogica;

/// <summary>
/// Mapeamento P/Invoke da ProfitDLL64.dll.
/// Baseado no Manual ProfitDLL v4.0.0.34 da Nelogica.
/// </summary>
internal static class ProfitDLL
{
    private const string DLL = "ProfitDLL64.dll";

    // ── Delegates ────────────────────────────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate void TStateCallback(int state, [MarshalAs(UnmanagedType.LPWStr)] string? message);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate void TNewTradeCallback(
        [MarshalAs(UnmanagedType.LPWStr)] string ticker,
        double price, int volume,
        [MarshalAs(UnmanagedType.LPWStr)] string buyBroker,
        [MarshalAs(UnmanagedType.LPWStr)] string sellBroker,
        int aggressor, long time);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate void TOfferBookCallback(
        [MarshalAs(UnmanagedType.LPWStr)] string ticker,
        int side, int position,
        double price, int volume,
        [MarshalAs(UnmanagedType.LPWStr)] string broker,
        long time);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate void TChangeCotationCallback(
        [MarshalAs(UnmanagedType.LPWStr)] string ticker,
        double last, double bid, double ask,
        double open, double high, double low,
        long volume, long time);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate void TNewDailyCallback(
        [MarshalAs(UnmanagedType.LPWStr)] string ticker,
        double open, double high, double low, double close,
        long volume, long time);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate void TPriceBookCallback(
        [MarshalAs(UnmanagedType.LPWStr)] string ticker,
        int side, int position,
        double price, long quantity, long time);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate void THistoryTradeCallback(
        [MarshalAs(UnmanagedType.LPWStr)] string ticker,
        double price, int volume,
        [MarshalAs(UnmanagedType.LPWStr)] string buyBroker,
        [MarshalAs(UnmanagedType.LPWStr)] string sellBroker,
        int aggressor, long time);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void TProgressCallback(int progress);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate void TTinyBookCallback(
        [MarshalAs(UnmanagedType.LPWStr)] string ticker,
        double bid, double ask,
        int bidSize, int askSize);

    // ── Inicialização ────────────────────────────────────────────────

    /// <summary>
    /// Market Data + Roteamento de ordens.
    /// Parâmetros: ActivationKey, User, Password,
    /// StateCallback, HistoryCallback, OrderChangeCallback, AccountCallback,
    /// NewTradeCallback, NewDailyCallback, PriceBookCallback,
    /// OfferBookCallback, HistoryTradeCallback, ProgressCallback, TinyBookCallback
    /// </summary>
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public static extern int DLLInitializeLogin(
        [MarshalAs(UnmanagedType.LPWStr)] string activationCode,
        [MarshalAs(UnmanagedType.LPWStr)] string username,
        [MarshalAs(UnmanagedType.LPWStr)] string password,
        TStateCallback        stateCallback,
        IntPtr                historyCallback,
        IntPtr                orderChangeCallback,
        IntPtr                accountCallback,
        TNewTradeCallback     tradeCallback,
        TNewDailyCallback?    dailyCallback,
        TPriceBookCallback?   priceBookCallback,
        TOfferBookCallback?   offerBookCallback,
        THistoryTradeCallback? historyTradeCallback,
        TProgressCallback?    progressCallback,
        TTinyBookCallback?    tinyBookCallback);

    /// <summary>
    /// Apenas Market Data (sem roteamento).
    /// Parâmetros: ActivationKey, User, Password,
    /// StateCallback, NewTradeCallback, NewDailyCallback, PriceBookCallback,
    /// OfferBookCallback, HistoryTradeCallback, ProgressCallback, TinyBookCallback
    /// </summary>
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public static extern int DLLInitializeMarketLogin(
        [MarshalAs(UnmanagedType.LPWStr)] string activationCode,
        [MarshalAs(UnmanagedType.LPWStr)] string username,
        [MarshalAs(UnmanagedType.LPWStr)] string password,
        TStateCallback        stateCallback,
        TNewTradeCallback?    tradeCallback,
        TNewDailyCallback?    dailyCallback,
        TPriceBookCallback?   priceBookCallback,
        TOfferBookCallback?   offerBookCallback,
        THistoryTradeCallback? historyTradeCallback,
        TProgressCallback?    progressCallback,
        TTinyBookCallback?    tinyBookCallback);

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
    public static extern int DLLFinalize();

    // ── Callbacks manuais ────────────────────────────────────────────

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
    public static extern int SetChangeCotationCallback(TChangeCotationCallback cb);

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
    public static extern int SetStateCallback(TStateCallback cb);

    // ── Assinaturas ──────────────────────────────────────────────────

    /// <summary>ticker = "WINFUT", bolsa = "BMF"</summary>
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public static extern int SubscribeTicker(
        [MarshalAs(UnmanagedType.LPWStr)] string ticker,
        [MarshalAs(UnmanagedType.LPWStr)] string bolsa);

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public static extern int UnsubscribeTicker(
        [MarshalAs(UnmanagedType.LPWStr)] string ticker,
        [MarshalAs(UnmanagedType.LPWStr)] string bolsa);

    /// <summary>Assina livro de ofertas — ticker = "WINFUT", bolsa = "BMF"</summary>
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public static extern int SubscribeOfferBook(
        [MarshalAs(UnmanagedType.LPWStr)] string ticker,
        [MarshalAs(UnmanagedType.LPWStr)] string bolsa);

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public static extern int UnsubscribeOfferBook(
        [MarshalAs(UnmanagedType.LPWStr)] string ticker,
        [MarshalAs(UnmanagedType.LPWStr)] string bolsa);
}
