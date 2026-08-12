using System;
using System.Threading;
using MarketCore.HistoricalImporter;
using MarketCore.Models;
using MarketCore.WPF.Data;

namespace MarketCore.WPF.LeituraFluxo
{
    /// <summary>
    /// Destino do backfill "hoje desde a abertura" via <c>ProfitHistoryService.GetHistoryTrades</c>
    /// (mesmo mecanismo já usado pela janela "Download Histórico", só que disparado automaticamente
    /// e para o dia corrente). Alimenta o <see cref="FlowReadingEngine"/> em memória e também grava em
    /// <c>trades_intraday</c> com <c>source="historical"</c>, para fechar a lacuna de quando o MarketCore
    /// não estava aberto — a tabela já tinha essa coluna pronta para isso.
    ///
    /// A deduplicação entre este backfill e o que já foi (ou vier a ser) capturado ao vivo é feita pela
    /// própria constraint <c>ON CONFLICT ... DO NOTHING</c> já existente em <c>MarketDataManager</c>.
    /// </summary>
    public sealed class FlowReadingHistorySink : IProfitHistoryTradeSink
    {
        private readonly FlowReadingEngine _engine;
        private readonly MarketDataManager? _marketDataManager;
        private readonly Func<int, string>? _resolveBrokerName;
        private string _contractSymbol = "";
        private long _totalAccepted;
        private long _totalRejected;

        public long TotalAccepted => Interlocked.Read(ref _totalAccepted);
        public long TotalRejected => Interlocked.Read(ref _totalRejected);

        /// <param name="resolveBrokerName">Resolve um código numérico de agente (corretora) para o token
        /// curto usado em toda a Leitura de Fluxo (ex.: "XP", "BTG") — tipicamente
        /// <c>ProfitDLLProvider.ResolveBrokerShortName</c>. O <c>GetHistoryTrades</c> só devolve o código
        /// numérico do agente (não o nome), então sem isso todo negócio histórico cairia numa corretora
        /// diferente das 7 colunas fixas da janela, mesmo com dados corretos baixados.</param>
        public FlowReadingHistorySink(FlowReadingEngine engine, MarketDataManager? marketDataManager, Func<int, string>? resolveBrokerName = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _marketDataManager = marketDataManager;
            _resolveBrokerName = resolveBrokerName;
        }

        /// <summary>Se <paramref name="raw"/> for um código numérico de agente e houver resolvedor
        /// disponível, devolve o nome curto da corretora; caso contrário devolve <paramref name="raw"/>
        /// como veio (já seria um nome, no fallback typed-callback que traz string direto da DLL).</summary>
        private string ResolveBroker(string raw)
        {
            if (_resolveBrokerName != null && int.TryParse(raw, out int agentId) && agentId > 0)
            {
                string resolved = _resolveBrokerName(agentId);
                if (!string.IsNullOrWhiteSpace(resolved))
                    return resolved;
            }
            return raw;
        }

        public void SetCurrentContract(string symbol) => _contractSymbol = symbol ?? "";

        public void OnHistoricalTrade(
            int opType,
            string symbol,
            string date,
            string time,
            double price,
            int qty,
            int tradeNum,
            int buyOrder,
            int sellOrder,
            string buyBroker,
            string sellBroker,
            int aggressor)
        {
            if (qty <= 0 || price <= 0 || double.IsNaN(price) || double.IsInfinity(price))
            {
                Interlocked.Increment(ref _totalRejected);
                return;
            }

            // Mesma convenção do fluxo ao vivo (Manual ProfitDLL, TNewTradeCallback): 2=compra agressão, 3=venda agressão.
            var aggr = aggressor switch
            {
                2 => TradeAggressor.Buy,
                3 => TradeAggressor.Sell,
                _ => TradeAggressor.Unknown
            };

            if (aggr == TradeAggressor.Unknown)
            {
                // Sem agressor claro (ex.: leilão/cross) não dá pra classificar compra/venda nos padrões.
                Interlocked.Increment(ref _totalRejected);
                return;
            }

            if (!TryParseTimestamp(date, time, out DateTime timestamp))
            {
                Interlocked.Increment(ref _totalRejected);
                return;
            }

            // Mesma regra do live (ProfitDLLProvider.TradeProcessingLoop): a corretora relevante é a do lado agressor.
            // GetHistoryTrades só traz o código numérico do agente aqui — ResolveBroker converte pro token
            // curto (ex. "XP") usando o mesmo cache/DLL do live, senão nada bate com as colunas fixas.
            string broker = ResolveBroker(aggr == TradeAggressor.Buy ? (buyBroker ?? "") : (sellBroker ?? ""));
            decimal decPrice = (decimal)price;

            Interlocked.Increment(ref _totalAccepted);

            _engine.OnTrade(broker, timestamp, decPrice, qty, aggr == TradeAggressor.Buy);

            // Mesma tabela/colunas que MainWindow.Engine_OnTrade já usa para o fluxo ao vivo — só muda a "source".
            _marketDataManager?.EnqueueRealtimeTrade(
                timestamp: timestamp,
                symbol: "WIN",
                price: (int)decPrice,
                quantity: qty,
                side: aggr == TradeAggressor.Buy ? "buy" : "sell",
                aggressor: aggr == TradeAggressor.Buy ? 1 : -1,
                brokerCode: 0,
                brokerName: broker,
                source: "historical");

            if (_totalAccepted % 500 == 0)
                _engine.SetBackfillStatus($"Carregando histórico de hoje… {_totalAccepted:N0} negócios");
        }

        public void FlushPendingExports()
        {
            // Nada a fazer: cada negócio já é gravado no ato (via EnqueueRealtimeTrade), sem buffer próprio.
        }

        /// <summary>Data <c>YYYYMMDD</c> + hora <c>HHmmssmmm</c> (9 dígitos, ms no fim) — mesmo formato usado
        /// no resto do projeto para os callbacks de histórico da ProfitDLL.</summary>
        private static bool TryParseTimestamp(string? yyyymmdd, string? hhmmssmmm, out DateTime result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(yyyymmdd) || yyyymmdd.Length < 8)
                return false;

            if (!int.TryParse(yyyymmdd.AsSpan(0, 4), out int y)) return false;
            if (!int.TryParse(yyyymmdd.AsSpan(4, 2), out int mo)) return false;
            if (!int.TryParse(yyyymmdd.AsSpan(6, 2), out int d)) return false;

            string t = (hhmmssmmm ?? "").Trim();
            if (t.Length == 0) t = "000000000";
            t = t.PadLeft(9, '0');
            if (t.Length > 9) t = t[^9..];

            if (!int.TryParse(t.AsSpan(0, 2), out int h)) return false;
            if (!int.TryParse(t.AsSpan(2, 2), out int mi)) return false;
            if (!int.TryParse(t.AsSpan(4, 2), out int sec)) return false;
            if (!int.TryParse(t.AsSpan(6, 3), out int ms)) return false;

            try
            {
                result = new DateTime(y, mo, d, h, mi, sec, ms, DateTimeKind.Unspecified);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }
    }
}
