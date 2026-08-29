using MarketCore.Models;

namespace MarketCore.Contracts;

/// <summary>
/// Recebe trades e alertas do <see cref="Engine.MarketEngine"/> para a janela de Análise Quantitativa (implementação WPF).
/// </summary>
public interface IAnaliseQuantDataSink
{
    void OnTrade(TradeEvent trade);

    void OnFlowAlert(string detectorName, string signalType, string message, decimal price, double probability);
}
