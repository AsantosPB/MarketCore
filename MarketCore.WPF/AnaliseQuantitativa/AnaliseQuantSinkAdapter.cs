using System.Collections.Concurrent;
using System.Windows.Threading;
using MarketCore.Contracts;
using MarketCore.Models;

namespace MarketCore.WPF.AnaliseQuantitativa;

/// <summary>
/// Implementa IAnaliseQuantDataSink usando fila + dreno em lote.
/// Em vez de um BeginInvoke por trade (causa travamento com 500+ trades/s),
/// enfileira na fila interna da janela e drena em lote no timer de 500ms.
/// </summary>
internal sealed class AnaliseQuantSinkAdapter : IAnaliseQuantDataSink
{
    private AnaliseQuantitativaWindow? _window;

    public void Bind(AnaliseQuantitativaWindow? window) => _window = window;

    public void OnTrade(TradeEvent trade)
    {
        var w = _window;
        if (w == null) return;
        // Enfileira na fila interna da janela — zero Dispatcher aqui
        w.EnqueueTrade(trade);
    }

    public void OnFlowAlert(string detectorName, string signalType, string message, decimal price, double probability)
    {
        var w = _window;
        if (w == null) return;

        var alerta = new AlertaViewModel
        {
            HoraStr       = DateTime.Now.ToString("HH:mm:ss"),
            Tipo          = signalType,
            Detector      = detectorName,
            Mensagem      = message,
            Probabilidade = probability,
            Preco         = (int)price,
            Resultado     = "—",
        };

        // Alertas são raros — BeginInvoke direto ainda é aceitável
        if (w.Dispatcher.CheckAccess())
            w.AdicionarAlerta(alerta);
        else
            w.Dispatcher.BeginInvoke(() => w.AdicionarAlerta(alerta), DispatcherPriority.Background);
    }
}