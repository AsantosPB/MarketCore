using MarketCore.Models;

namespace MarketCore.Contracts;

/// <summary>
/// Contrato único que qualquer provedor de dados deve implementar.
/// A aplicação só conhece essa interface — nunca a DLL diretamente.
/// Troca de provedor = troca de implementação, zero impacto no resto.
/// </summary>
public interface IMarketDataProvider : IDisposable
{
    // --- Eventos que o provedor dispara para a aplicação ---
    event Action<TradeEvent>?             OnTrade;
    event Action<BookLevel>?              OnBook;
    event Action<QuoteEvent>?             OnQuote;
    event Action<ConnectionChangedEvent>? OnConnectionChanged;

    // --- Controle de conexão ---
    Task ConnectAsync(ProviderCredentials credentials);
    Task DisconnectAsync();
    ConnectionStatus Status { get; }

    // --- Assinaturas de ativos ---
    void Subscribe(string ticker);
    void Unsubscribe(string ticker);
    IReadOnlyList<string> SubscribedTickers { get; }

    // --- Identidade do provedor (para logs e UI) ---
    string ProviderName { get; }
}

/// <summary>
/// Credenciais genéricas — cada provedor usa o que precisa.
/// </summary>
public record ProviderCredentials(
    string ActivationCode,   // código de licença (Nelogica) ou API Key (outros)
    string Username,
    string Password,
    bool   RoutingEnabled = false  // habilita OMS além de market data
);
