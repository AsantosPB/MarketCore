using System.Runtime.InteropServices;

namespace MarketCore.Providers.Nelogica;

/// <summary>
/// Mapeamento P/Invoke da ProfitDLL64.dll.
/// Isolado aqui para que nenhuma outra classe precise importar a DLL diretamente.
/// Baseado no Manual ProfitDLL e exemplos oficiais da Nelogica.
/// </summary>
internal static class ProfitDLL
{
    private const string DLL = "ProfitDLL64.dll";

    // ----------------------------------------------------------------
    // DELEGATES (tipos dos callbacks)
    // ----------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate void TStateCallback(int state, string? message);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate void TNewTradeCallback(
        string ticker, double price, int volume,
        string buyBroker, string sellBroker,
        int aggressor, long time);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate void TNewBookCallback(
        string ticker, int side, int position,
        double price, int volume, string broker, long time);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate void TChangeCotationCallback(
        string ticker, double last, double bid, double ask,
        double open, double high, double low,
        long volume, long time);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate void THistoryCallback(
        string ticker, double open, double high,
        double low, double close, long volume, long time);

    // ----------------------------------------------------------------
    // INICIALIZAÇÃO
    // ----------------------------------------------------------------

    // Market Data + Roteamento de ordens
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int DLLInitializeLogin(
        string activationCode, string username, string password,
        TStateCallback       stateCallback,
        TNewTradeCallback    tradeCallback,
        TNewBookCallback     bookCallback,
        TChangeCotationCallback cotationCallback,
        THistoryCallback     historyCallback,
        TStateCallback       accountCallback,
        TStateCallback       orderCallback);

    // Apenas Market Data (sem roteamento)
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int DLLInitializeMarketLogin(
        string activationCode, string username, string password,
        TStateCallback          stateCallback,
        TNewTradeCallback       tradeCallback,
        TNewBookCallback        bookCallback,
        TChangeCotationCallback cotationCallback);

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
    public static extern int DLLFinalize();

    // ----------------------------------------------------------------
    // CALLBACKS QUE PRECISAM SER REGISTRADOS MANUALMENTE
    // ----------------------------------------------------------------

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
    public static extern void SetChangeCotationCallback(TChangeCotationCallback cb);

    // ----------------------------------------------------------------
    // ASSINATURAS
    // ----------------------------------------------------------------

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int SubscribeTicker(string ticker);

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int UnsubscribeTicker(string ticker);

    // ----------------------------------------------------------------
    // ENVIO DE ORDENS (só disponível com DLLInitializeLogin)
    // ----------------------------------------------------------------

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int SendBuyOrder(
        string ticker, int quantity, double price,
        string brokerCode, string accountCode);

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int SendSellOrder(
        string ticker, int quantity, double price,
        string brokerCode, string accountCode);

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int CancelOrder(string orderId);

    // ----------------------------------------------------------------
    // HISTÓRICO
    // ----------------------------------------------------------------

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int GetHistoryData(string ticker, int periodType, int periodValue);
}
