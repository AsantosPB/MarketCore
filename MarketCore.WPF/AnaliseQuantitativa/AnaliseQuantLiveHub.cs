using MarketCore.Models;
using MarketCore.WPF;

namespace MarketCore.WPF.AnaliseQuantitativa;

/// <summary>Encaminha trades e wiring da MainWindow para a janela de análise (qualquer forma de abertura).</summary>
internal static class AnaliseQuantLiveHub
{
    private static WeakReference<MainWindow>? _host;
    private static WeakReference<AnaliseQuantitativaWindow>? _analise;

    public static void SetHost(MainWindow host) => _host = new WeakReference<MainWindow>(host);

    public static void Register(AnaliseQuantitativaWindow window) => _analise = new WeakReference<AnaliseQuantitativaWindow>(window);

    public static void Unregister(AnaliseQuantitativaWindow window)
    {
        if (_analise != null && _analise.TryGetTarget(out var w) && ReferenceEquals(w, window))
            _analise = null;
    }

    public static void TryWire(AnaliseQuantitativaWindow window)
    {
        if (MainWindow.ActiveInstance != null)
        {
            MainWindow.ActiveInstance.WireAnaliseQuantitativa(window);
            return;
        }

        if (_host != null && _host.TryGetTarget(out var host))
            host.WireAnaliseQuantitativa(window);
    }

    public static void PushTrade(TradeEvent trade)
    {
        if (_analise != null && _analise.TryGetTarget(out var w))
            w.OnTradeReceived(trade);
    }
}
