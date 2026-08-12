using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MarketCore.Models;

public enum TradeAggressor { Buy, Sell, Unknown }
public enum BookSide { Bid, Ask }
/// <summary>
/// Evento de negócio.
/// <list type="bullet">
/// <item><description><see cref="Time"/> — preferencialmente instante do passe na bolsa quando a DLL envia data/hora; caso contrário momento de recebimento local.</description></item>
/// <item><description><see cref="ExchangeTimeUtc"/> — UTC quando o callback traz string de data/hora válida.</description></item>
/// <item><description><see cref="ReceivedUtc"/> — UTC quando o processador de trades emitiu o evento para o motor (inclui fila + processamento DLL→app).</description></item>
/// </list>
/// </summary>
public record TradeEvent(
    string Ticker,
    decimal Price,
    int Volume,
    string Broker,
    TradeAggressor Aggressor,
    DateTime Time,
    DateTime? ExchangeTimeUtc = null,
    DateTime? ReceivedUtc = null);
/// <param name="ExchangeTime">Horário da oferta vindo da DLL (<c>bHasDate</c>); usado para fila FIFO no mesmo preço, alinhada ao ProfitChart.</param>
/// <param name="VolumeIsDelta">Em <c>atEdit</c>, quantidade vem como delta (<c>+=</c>) e não como valor absoluto.</param>
/// <param name="AgentId">Código do agente na DLL; usado para resolver o nome da corretora no filtro.</param>
public record BookLevel(string Ticker, BookSide Side, decimal Price, int Volume, string Broker, DateTime Time, long OfferId = 0, int Action = 0, int Position = 0, DateTime? ExchangeTime = null, bool VolumeIsDelta = false, int AgentId = 0);
public record QuoteEvent(string Ticker, decimal Last, decimal Bid, decimal Ask, decimal Open, decimal High, decimal Low, long Volume, DateTime Time);
public enum ConnectionStatus { Disconnected, Connecting, Connected, Error }
public record ConnectionChangedEvent(ConnectionStatus Status, string Message);
/// <param name="RawBids">Ofertas individuais (não agregadas por preço) do lado de compra — uma entrada por corretora/oferta, igual ao book do ProfitChart. Pode ser <c>null</c> em snapshots antigos/vazios; use <c>RawBids ?? Bids</c>.</param>
/// <param name="RawAsks">Idem para o lado de venda.</param>
public record BookSnapshot(string Ticker, IReadOnlyList<BookLevel> Bids, IReadOnlyList<BookLevel> Asks, DateTime Time, IReadOnlyList<BookLevel>? RawBids = null, IReadOnlyList<BookLevel>? RawAsks = null);

/// <summary>Snapshot de um lado do livro (ex. arrays do <c>atFullBook</c>); o motor mescla por <c>OfferId</c> sem limpar o incremental.</summary>
public record BookFullRefresh(
    string Ticker,
    IReadOnlyList<BookLevel>? Bids,
    IReadOnlyList<BookLevel>? Asks);

/// <summary>
/// Agrega pelo <b>texto</b> do preço no grid (<c>N0</c> pt-BR): todas as linhas que mostram o mesmo valor são somadas —
/// garante fusão mesmo se <c>decimal</c>s internos divergirem.
/// </summary>
public static class BookSnapshotAggregation
{
    /// <summary>Cultura do preço mostrado no book (inteiro grande com separador milhar).</summary>
    public static readonly CultureInfo BookPriceCulture = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Igual ao texto da coluna preço (<c>N0</c>) — usado em detectores e merge.</summary>
    public static string FormatBookPrice(decimal price) =>
        price.ToString("N0", BookPriceCulture);

    /// <summary>Chave numérica determinística igual ao texto <see cref="FormatBookPrice"/>.</summary>
    public static decimal PriceBucketFromGridRules(decimal rawPrice)
    {
        string s = rawPrice.ToString("N0", BookPriceCulture);
        return TryParseFormattedDisplayPrice(s, out decimal parsed)
            ? parsed
            : decimal.Round(rawPrice, 0, MidpointRounding.ToEven);
    }

    /// <inheritdoc cref="decimal.TryParse(ReadOnlySpan{char},NumberStyles,IFormatProvider?,out decimal)"/>
    public static bool TryParseFormattedDisplayPrice(string displayKey, out decimal value) =>
        decimal.TryParse(displayKey, NumberStyles.Number, BookPriceCulture, out value);

    /// <remarks>Retorno idempotente: uma entrada por nível efectivo (<c>OfferId</c>=0).</remarks>
    public static List<BookLevel> AggregateByPrice(IReadOnlyList<BookLevel> levels, BookSide side, string ticker, DateTime timeUtc)
    {
        if (levels.Count == 0)
            return [];

        // Chave = string formatada igual à coluna Preço → fundir qualquer ruído de decimal sob o mesmo texto.
        var tiers = new Dictionary<string, TierAcc>(StringComparer.Ordinal);

        foreach (var x in levels)
        {
            string dk = FormatBookPrice(x.Price);
            decimal canonPrice = TryParseFormattedDisplayPrice(dk, out decimal parsed)
                ? parsed
                : PriceBucketFromGridRules(x.Price);

            long add = Math.Max(x.Volume, 0);
            if (!tiers.TryGetValue(dk, out TierAcc row))
            {
                tiers[dk] = new TierAcc(canonPrice, add, x.ExchangeTime);
                continue;
            }

            long cum = row.CumVol;
            if (cum <= int.MaxValue - add)
                cum += add;
            else
                cum = int.MaxValue;

            DateTime? bestExch = row.BestExchange;
            if (x.ExchangeTime is DateTime dt && (!bestExch.HasValue || dt > bestExch.Value))
                bestExch = dt;

            tiers[dk] = new TierAcc(row.CanonPrice, cum, bestExch);
        }

        IEnumerable<KeyValuePair<string, TierAcc>> sorted = side == BookSide.Bid
            ? tiers.OrderByDescending(kv => kv.Value.CanonPrice)
            : tiers.OrderBy(kv => kv.Value.CanonPrice);

        var dst = new List<BookLevel>();
        foreach (var kv in sorted)
        {
            TierAcc t = kv.Value;
            dst.Add(new BookLevel(
                ticker,
                side,
                t.CanonPrice,
                t.CumVol > int.MaxValue ? int.MaxValue : (int)t.CumVol,
                Broker: "",
                timeUtc,
                OfferId: 0,
                Action: 0,
                Position: 0,
                ExchangeTime: t.BestExchange,
                VolumeIsDelta: false,
                AgentId: 0));
        }

        return dst;
    }

    /// <summary>
    /// Enquanto o melhor bid ≥ melhor ask, remove o topo do ask; se ainda cruzar, remove o topo do bid.
    /// Serve para deslocamento entre lados (fila/mesclagem) e para filtros da UI que deixam o subconjunto visível logicamente cruzado.
    /// </summary>
    public static void NormalizeEconomicalTop(List<BookLevel> bidsSortedDesc, List<BookLevel> asksSortedAsc)
    {
        const int MaxIterations = 512;
        for (int i = 0; i < MaxIterations && bidsSortedDesc.Count > 0 && asksSortedAsc.Count > 0; i++)
        {
            decimal bestBid = PriceBucketFromGridRules(bidsSortedDesc[0].Price);
            decimal bestAsk = PriceBucketFromGridRules(asksSortedAsc[0].Price);
            if (bestBid < bestAsk)
                break;

            asksSortedAsc.RemoveAt(0);

            if (bidsSortedDesc.Count == 0 || asksSortedAsc.Count == 0)
                break;

            bestBid = PriceBucketFromGridRules(bidsSortedDesc[0].Price);
            bestAsk = PriceBucketFromGridRules(asksSortedAsc[0].Price);
            if (bestBid >= bestAsk)
                bidsSortedDesc.RemoveAt(0);
        }
    }

    private readonly record struct TierAcc(decimal CanonPrice, long CumVol, DateTime? BestExchange);
}
