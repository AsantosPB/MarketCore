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
/// O parâmetro <c>exchangeTime</c> (UTC) carrega o timestamp do negócio na bolsa —
/// usado pelo agregador CASO 1 do PVV para agrupar callbacks que ocorreram no
/// MESMO milissegundo exato. Pode ser null quando a DLL não enviou o horário.
///
/// Zero overhead quando ninguém está escutando (delegate == null).
/// </summary>
public static class PregaoVivaVozHook
{
    /// <summary>
    /// Trade real: ticker, nome do agressor comprador, nome do agressor vendedor,
    /// qtd, tradeType (1 = compra-agressor, 2 = venda-agressor), callbackInfo
    /// (string pré-formatada com bolsa=HH:mm:ss.fff e demais campos), exchangeTime
    /// (UTC, timestamp do negócio na bolsa — usado pelo agregador de bloco).
    /// </summary>
    public static System.Action<string, string, string, int, int, string, System.DateTime?>? OnTradeReceived;

    /// <summary>
    /// Book real: ticker, nome da corretora, lado ("compra"|"venda"), nível (1..N),
    /// qtd, callbackInfo (string pré-formatada com bolsa=HH:mm:ss.fff e demais campos),
    /// exchangeTime (UTC, timestamp do evento na bolsa).
    /// </summary>
    public static System.Action<string, string, string, int, int, string, System.DateTime?>? OnBookUpdate;

    /// <summary>
    /// Resolve o nome de uma corretora a partir do ID numérico do agente.
    /// Delegate registrado pelo <c>ProfitDLLProvider</c> no seu construtor;
    /// chamado exclusivamente pelo worker do <c>ProfitDLLBridge</c>
    /// (thread separada da DLL, portanto SEGURO para chamar GetAgentName).
    /// Retorna o nome curto (ex.: "GOLDMAN", "JPM", "XP") ou o próprio ID
    /// numérico como string quando a DLL ainda não resolveu.
    /// </summary>
    public static System.Func<int, string>? ResolveAgentName;
}
