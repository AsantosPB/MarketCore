namespace MarketCore.Providers.Nelogica;

/// <summary>
/// Ponto de acoplamento entre o ProfitDLLProvider (que vive no projeto base MarketCore)
/// e o Pregão Viva Voz (que vive no projeto MarketCore.WPF).
///
/// Por que existe: MarketCore.WPF referencia MarketCore, então o provider não pode
/// referenciar App.PregaoVivaVozBridge diretamente. Este hook inverte a dependência —
/// o provider apenas invoca os delegates estáticos; a camada WPF conecta os delegates
/// à Bridge quando o motor do PVV é iniciado, e desconecta quando é parado.
///
/// O último parâmetro (<c>callbackInfo</c>) é uma string já formatada com todos os
/// campos do callback + horário da bolsa (<c>bolsa=HH:mm:ss.fff</c>). Ela viaja
/// pareada com o evento por toda a cadeia até o log — isso elimina a decorrelação
/// entre narração e callback que existia quando o Bridge usava uma variável global
/// (o "último callback" era sobrescrito por callbacks mais recentes antes da narração
/// chegar ao arquivo de log).
///
/// Zero overhead quando ninguém está escutando (delegate == null).
/// </summary>
public static class PregaoVivaVozHook
{
    /// <summary>
    /// Trade real: ticker, nome do agressor comprador, nome do agressor vendedor,
    /// qtd, tradeType (1 = compra-agressor, 2 = venda-agressor), callbackInfo
    /// (string pré-formatada com bolsa=HH:mm:ss.fff e demais campos).
    /// </summary>
    public static System.Action<string, string, string, int, int, string>? OnTradeReceived;

    /// <summary>
    /// Book real: ticker, nome da corretora, lado ("compra"|"venda"), nível (1..N),
    /// qtd, callbackInfo (string pré-formatada com bolsa=HH:mm:ss.fff e demais campos).
    /// </summary>
    public static System.Action<string, string, string, int, int, string>? OnBookUpdate;
}
