using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MarketCore.Contracts;
using MarketCore.Models;
using MarketCore.HistoricalImporter;

namespace MarketCore.Providers.Nelogica
{
    public class ProfitDLLProvider : IMarketDataProvider
    {
        #region Constantes

        private const string DLL_PATH      = @"ProfitDLL64.dll";
        private const string EXCHANGE_BMF  = "F";
        private const string EXCHANGE_BVMF = "B";

        /// <summary>
        /// TTL de entrada para trades: eventos com exchangeTime mais antigo que este valor
        /// são descartados imediatamente em <see cref="OnTradeCallbackCore"/> antes de entrar
        /// na fila. Protege contra replay histórico pós-PARTIAL_CONNECTED (callbacks com
        /// timestamps de 20–30s atrás que encheriam a fila com dados obsoletos).
        /// Eventos sem exchangeTime (null) passam normalmente — fallback seguro.
        /// </summary>
        private static readonly TimeSpan TtlEntrada = TimeSpan.FromSeconds(15);

        #endregion

        #region Structs e Delegates da Nelogica

        // TAssetIDRec no manual é packed record.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        private struct TAssetID
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string Ticker;
            [MarshalAs(UnmanagedType.LPWStr)] public string Bolsa;
            public int nFeedType;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TConnectorAssetIdentifier
        {
            public byte Version;
            [MarshalAs(UnmanagedType.LPWStr)] public string Ticker;
            [MarshalAs(UnmanagedType.LPWStr)] public string Exchange;
            public byte FeedType;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TConnectorPriceGroup
        {
            public byte   Version;
            public double Price;
            public uint   Count;
            public long   Quantity;
            public uint   PriceGroupFlags;
        }

        // FullBook arrays (OfferBookCallbackV2): cada linha = 53 bytes após cabeçalho 8
        // (Manual + exemplo MarshalOfferBuffer: Preço+Int64 qty+Agent+OfferID+tamdata+payload).

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void TStateCallback(int nResult, int result);

        // ProfitDLL 4.0.0.41+ — callback disparado quando o estado das threads da DLL
        // muda entre "responsive" (0) e "frozen" (1). Novo em 4.0.0.41.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void THealthCallback(int nHealthStatus);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate void TTradeCallback(
            TAssetID assetId,
            [MarshalAs(UnmanagedType.LPWStr)] string date,
            uint tradeNumber, double price, double vol,
            int qtd, int buyAgent, int sellAgent, int tradeType, int bIsEdit);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate void TNewDailyCallback(
            TAssetID assetId,
            [MarshalAs(UnmanagedType.LPWStr)] string date,
            double sOpen, double sHigh, double sLow, double sClose,
            double sVol, double sAjuste, double sMaxLimit, double sMinLimit,
            double sVolBuyer, double sVolSeller,
            int nQtd, int nNegocios, int nContratosOpen,
            int nQtdBuyer, int nQtdSeller, int nNegBuyer, int nNegSeller);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate void TPriceBookCallback(
            TAssetID assetId, int nAction, int nPosition,
            int side, int nQtd, int nCount, double sPrice,
            IntPtr pArraySell, IntPtr pArrayBuy);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate void TOfferBookCallbackV2(
            TAssetID assetId, int nAction, int nPosition,
            int side, long nQtd, int nAgent, long nOfferID, double sPrice,
            ushort bHasPrice, ushort bHasQtd, ushort bHasDate, ushort bHasOfferID, ushort bHasAgent,
            [MarshalAs(UnmanagedType.LPWStr)] string date,
            IntPtr pArraySell, IntPtr pArrayBuy);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate void THistoryTradeCallback(
            TAssetID assetId,
            [MarshalAs(UnmanagedType.LPWStr)] string date,
            uint tradeNumber, double price, double vol,
            int qtd, int buyAgent, int sellAgent, int tradeType);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate void TProgressCallBack(TAssetID assetId, int nProgress);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate void TNewTinyBookCallBack(TAssetID assetId, double price, int qtd, int side);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate void TChangeCotation(
            TAssetID assetId,
            [MarshalAs(UnmanagedType.LPWStr)] string date,
            uint tradeNumber, double sPrice);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate void TConnectorPriceDepthCallback(
            TConnectorAssetIdentifier assetID, byte side, int position, byte updateType);

        #endregion

        #region DLL Imports

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int DLLInitializeMarketLogin(
            [MarshalAs(UnmanagedType.LPWStr)] string activationKey,
            [MarshalAs(UnmanagedType.LPWStr)] string user,
            [MarshalAs(UnmanagedType.LPWStr)] string password,
            TStateCallback       stateCallback,
            TTradeCallback       newTradeCallback,
            TNewDailyCallback    newDailyCallback,
            TPriceBookCallback   priceBookCallback,
            TOfferBookCallbackV2 offerBookCallback,
            THistoryTradeCallback newHistoryCallback,
            TProgressCallBack    progressCallBack,
            TNewTinyBookCallBack newTinyBookCallBack);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int SetChangeCotationCallback(TChangeCotation a_ChangeCotation);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int SetOfferBookCallback(TOfferBookCallbackV2 a_OfferBookCallback);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int SetOfferBookCallbackV2(TOfferBookCallbackV2 a_OfferBookCallback);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int SetPriceDepthCallback(TConnectorPriceDepthCallback a_Callback);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int SubscribeTicker(
            [MarshalAs(UnmanagedType.LPWStr)] string pwcTicker,
            [MarshalAs(UnmanagedType.LPWStr)] string pwcBolsa);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int UnsubscribeTicker(
            [MarshalAs(UnmanagedType.LPWStr)] string pwcTicker,
            [MarshalAs(UnmanagedType.LPWStr)] string pwcBolsa);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int SubscribeOfferBook(
            [MarshalAs(UnmanagedType.LPWStr)] string pwcTicker,
            [MarshalAs(UnmanagedType.LPWStr)] string pwcBolsa);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int UnsubscribeOfferBook(
            [MarshalAs(UnmanagedType.LPWStr)] string pwcTicker,
            [MarshalAs(UnmanagedType.LPWStr)] string pwcBolsa);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int FreePointer(IntPtr pointer, int nSize);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int SubscribePriceDepth(in TConnectorAssetIdentifier assetID);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int UnsubscribePriceDepth(in TConnectorAssetIdentifier assetID);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int GetPriceDepthSideCount(
            in TConnectorAssetIdentifier assetID, byte side);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int GetPriceGroup(
            in TConnectorAssetIdentifier assetID,
            byte side, int position, ref TConnectorPriceGroup priceGroup);

        // ProfitDLL 4.0.0.41 (conforme Exemplo C# oficial da Nelogica, arquivo
        // ProfitFunctions.cs linhas 310 e 313):
        //   int GetAgentNameLength(int a_AgentID, AgentNameFlags a_nShortName)
        //   int GetAgentName(int a_AgentLen, int a_AgentID, StringBuilder AgentName, AgentNameFlags a_nShortName)
        //
        // AgentNameFlags é enum : uint  { CM_NONE = 0, CM_IS_SHORT_NAME = 1 }.
        // Passamos uint diretamente — o CLR marshalla igual ao enum. No stdcall
        // int/uint têm o mesmo layout (4 bytes), então o parâmetro é ABI-compat
        // com a assinatura antiga (int nShortName). Trocamos para uint apenas
        // para casar exatamente com o exemplo oficial.
        //
        // MUDANÇA DE COMPORTAMENTO 4.0.0.36+ (changelog Nelogica):
        // - GetAgentNameLength retorna o TAMANHO do buffer necessário (>0 sucesso; <0 erro).
        // - GetAgentName retorna o TAMANHO realmente COPIADO (>0 sucesso; <0 erro).
        // Antes (<= 4.0.0.35) GetAgentName retornava NL_OK (0) para sucesso.
        // GetBrokerNameSafe e ResolveAgentNameForPvv foram atualizados para
        // interpretar o novo retorno corretamente.
        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int GetAgentNameLength(int a_AgentID, uint a_nShortName);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int GetAgentName(
            int a_AgentLen, int a_AgentID,
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder AgentName,
            uint a_nShortName);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall, EntryPoint = "GetAgentName")]
        private static extern int GetAgentNamePtr(
            int a_AgentLen, int a_AgentID,
            IntPtr pwcAgent,
            uint a_nShortName);

        // ══════════════════════════════════════════════════════════════════════
        //  ProfitDLL 4.0.0.41 — HEALTH MONITORING
        // ══════════════════════════════════════════════════════════════════════
        //  Manual da Nelogica:
        //    int GetHealthStatus(ref int nState)
        //      - Retorno: NL_OK (0) em sucesso; NL_NOT_INITIALIZED / outro erro caso contrário.
        //      - nState (saída): 0 = shsResponsive (threads OK) · 1 = shsFrozen (thread travada)
        //    int SetHealthCallback(THealthCallback callback)
        //      - Registra callback disparado APENAS quando o estado muda.
        //      - Se a DLL nunca sair de Responsive, o callback nunca dispara — por isso
        //        o MainWindow também precisa fazer polling ativo via GetHealthStatus.
        //  Se a DLL instalada for antiga (< 4.0.0.41), essas duas funções não existirão
        //  no export table — o P/Invoke lança EntryPointNotFoundException na primeira
        //  chamada, o que é tratado em QueryHealthStatus/TryRegisterHealthCallback.
        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int GetHealthStatus(ref int nState);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int SetHealthCallback(THealthCallback callback);

        #endregion

        #region Tipos internos para filas

        private readonly struct RawTrade
        {
            public readonly string Ticker;
            public readonly double Price;
            public readonly int    Qtd;
            public readonly int    BuyAgent;
            public readonly int    SellAgent;
            public readonly int    TradeType;
            /// <summary>Instante UTC do passe (string <c>date</c> do NewTrade).</summary>
            public readonly DateTime? ExchangeUtc;
            public RawTrade(string t, double p, int q, int b, int s, int tt, DateTime? exchangeUtc = null)
            {
                Ticker = t;
                Price = p;
                Qtd = q;
                BuyAgent = b;
                SellAgent = s;
                TradeType = tt;
                ExchangeUtc = exchangeUtc;
            }
        }

        private readonly struct RawBook
        {
            public readonly string Ticker;
            public readonly int    Action;
            public readonly int    Position;
            public readonly int    Side;
            public readonly double Price;
            public readonly int    Volume;
            public readonly int    Agent;
            public readonly long   OfferId;
            public readonly DateTime? ExchangeTime;
            public readonly bool   HasQuantityUpdate;
            public RawBook(string t, int ac, int pos, int si, double p, int v, int a, long o, DateTime? exch = null, bool hasQuantityUpdate = false)
            {
                Ticker = t;
                Action = ac;
                Position = pos;
                Side = si;
                Price = p;
                Volume = v;
                Agent = a;
                OfferId = o;
                ExchangeTime = exch;
                HasQuantityUpdate = hasQuantityUpdate;
            }
        }

        /// <summary>Tenta interpretar <paramref name="date"/> do OfferBookCallback (vários layouts já vistos em BMF).</summary>
        /// <summary>
        /// Cached format index: a DLL da Nelogica manda quase sempre o mesmo formato
        /// ("HH:mm:ss.fff" tipicamente). Em vez de tentar 6 formatos a cada callback
        /// (2.000/s), lembramos qual formato funcionou da última vez e tentamos esse
        /// primeiro. Se acertar — caso comum — é um único TryParseExact.
        /// Thread-safety: escrita/leitura de int é atômica em x64; volatile garante visibilidade.
        /// </summary>
        private static volatile int _lastMatchedFormatIdx = 0;
        private static readonly string[] DateFormats =
        [
            "dd/MM/yyyy HH:mm:ss.fff",
            "dd/MM/yyyy HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss.fff",
            "yyyy-MM-dd HH:mm:ss",
            "HH:mm:ss.fff",
            "HH:mm:ss"
        ];
        private static readonly System.Globalization.CultureInfo PtBrCulture =
            System.Globalization.CultureInfo.GetCultureInfo("pt-BR");

        private static bool TryParseOfferBookDate(string? date, out DateTime utc)
        {
            utc = default;
            if (string.IsNullOrWhiteSpace(date)) return false;
            string s = date.Trim();

            // Fast path: tenta o último formato que funcionou PRIMEIRO.
            // Em regime estável, ~100% dos callbacks usam o mesmo formato.
            int lastIdx = _lastMatchedFormatIdx;
            DateTime dt;
            if (lastIdx >= 0 && lastIdx < DateFormats.Length
                && DateTime.TryParseExact(s, DateFormats[lastIdx],
                       System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.AssumeLocal, out dt))
            {
                utc = DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime();
                return true;
            }

            // Slow path: tenta todos os formatos.
            for (int i = 0; i < DateFormats.Length; i++)
            {
                if (i == lastIdx) continue; // já tentamos
                if (DateTime.TryParseExact(s, DateFormats[i],
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal, out dt))
                {
                    _lastMatchedFormatIdx = i;
                    utc = DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime();
                    return true;
                }
            }

            // Fallbacks livres — tenta pt-BR PRIMEIRO (DMY), invariant só se pt-BR falhar.
            if (DateTime.TryParse(s, PtBrCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal | System.Globalization.DateTimeStyles.AllowWhiteSpaces, out dt)
                || DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal | System.Globalization.DateTimeStyles.AllowWhiteSpaces, out dt))
            {
                utc = DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Versão sem alocação: converte bytes direto para <c>Span&lt;char&gt;</c> via stackalloc,
        /// evitando <c>new StringBuilder</c> + <c>ToString()</c> por entrada do full book
        /// (até 10.000 entradas por refresh = 10.000 alocações eliminadas).
        /// </summary>
        private static bool TryParseOfferBookDateBytes(byte[] data, int start, int length, out DateTime utc)
        {
            utc = default;
            if (length <= 0 || start < 0 || start + length > data.Length)
                return false;

            // stackalloc: zero GC. OfferBookMaxDateBytes = 256, cabe na stack.
            Span<char> chars = stackalloc char[length];
            for (int i = 0; i < length; i++)
                chars[i] = (char)data[start + i];

            // Trim trailing nulls/whitespace
            int end = length;
            while (end > 0 && (chars[end - 1] == '\0' || char.IsWhiteSpace(chars[end - 1])))
                end--;
            if (end <= 0) return false;

            var span = chars[..end];

            // Try the cached format first (same optimization as TryParseOfferBookDate)
            int lastIdx = _lastMatchedFormatIdx;
            DateTime dt;
            if (lastIdx >= 0 && lastIdx < DateFormats.Length
                && DateTime.TryParseExact(span, DateFormats[lastIdx].AsSpan(),
                       System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.AssumeLocal, out dt))
            {
                utc = DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime();
                return true;
            }

            for (int i = 0; i < DateFormats.Length; i++)
            {
                if (i == lastIdx) continue;
                if (DateTime.TryParseExact(span, DateFormats[i].AsSpan(),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal, out dt))
                {
                    _lastMatchedFormatIdx = i;
                    utc = DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime();
                    return true;
                }
            }

            // Fallback: precisa de string para TryParse com CultureInfo
            return TryParseOfferBookDate(span.ToString(), out utc);
        }

        /// <summary>Fila única: delta incremental ou substituição completa (ordem preservada).</summary>
        private readonly struct BookWorkItem
        {
            public readonly bool IsFullRefresh;
            public readonly RawBook Delta;
            public readonly string? FullTicker;
            public readonly BookLevel[]? FullBids;
            public readonly BookLevel[]? FullAsks;
            public readonly byte[]? FullRawSell;
            public readonly byte[]? FullRawBuy;

            private BookWorkItem(
                bool full,
                RawBook d,
                string? ft,
                BookLevel[]? b,
                BookLevel[]? a,
                byte[]? rawSell,
                byte[]? rawBuy)
            {
                IsFullRefresh = full;
                Delta = d;
                FullTicker = ft;
                FullBids = b;
                FullAsks = a;
                FullRawSell = rawSell;
                FullRawBuy = rawBuy;
            }

            public static BookWorkItem FromDelta(RawBook d) => new(false, d, null, null, null, null, null);

            public static BookWorkItem FromFull(string ticker, BookLevel[] bids, BookLevel[] asks)
                => new(true, default, ticker, bids, asks, null, null);

            public static BookWorkItem FromFullRaw(string ticker, byte[]? rawSell, byte[]? rawBuy)
                => new(true, default, ticker, null, null, rawSell, rawBuy);
        }

        /// <summary>
        /// Tracker leve dos 4 melhores preços por lado (bid/ask) para o Pregão Viva Voz.
        /// Elimina necessidade do BookState completo — determina o nível de preço
        /// (1=boca, 2=segundo, 3=terceiro, 4=quarto) direto no callback da DLL.
        ///
        /// Auto-reset a cada 30s para limpar preços obsoletos (ordens canceladas, preço
        /// que saiu do top). Os ~800 callbacks/s repopulam o tracker em &lt;100ms.
        /// </summary>
        private sealed class PvvPriceTracker
        {
            private readonly double[] _bids = new double[4]; // descending (highest = boca)
            private readonly double[] _asks = new double[4]; // ascending  (lowest  = boca)
            private int _bidCount;
            private int _askCount;
            private long _lastResetTicks = DateTime.UtcNow.Ticks;
            private const long ResetIntervalTicks = 30 * TimeSpan.TicksPerSecond;

            /// <summary>
            /// Registra um preço e retorna o rank (1-4) se estiver nos 4 melhores.
            /// Retorna 0 se o preço está fora dos top 4.
            /// side: 0 = bid, 1 = ask.
            /// </summary>
            public int RegisterAndGetRank(int side, double price)
            {
                long now = DateTime.UtcNow.Ticks;
                if (now - _lastResetTicks > ResetIntervalTicks)
                {
                    Reset();
                    _lastResetTicks = now;
                }
                return side == 0 ? RegisterBid(price) : RegisterAsk(price);
            }

            public void Reset()
            {
                _bidCount = 0;
                _askCount = 0;
                Array.Clear(_bids);
                Array.Clear(_asks);
            }

            private int RegisterBid(double price)
            {
                // Preço já existe? Retorna rank.
                for (int i = 0; i < _bidCount; i++)
                    if (Math.Abs(_bids[i] - price) < 0.001) return i + 1;

                // Ponto de inserção (descending — maior preço primeiro).
                int pos = _bidCount;
                for (int i = 0; i < _bidCount; i++)
                {
                    if (price > _bids[i]) { pos = i; break; }
                }

                if (pos >= 4) return 0; // fora do top 4

                int newCount = Math.Min(_bidCount + 1, 4);
                for (int i = newCount - 1; i > pos; i--)
                    _bids[i] = _bids[i - 1];
                _bids[pos] = price;
                _bidCount = newCount;
                return pos + 1;
            }

            private int RegisterAsk(double price)
            {
                for (int i = 0; i < _askCount; i++)
                    if (Math.Abs(_asks[i] - price) < 0.001) return i + 1;

                int pos = _askCount;
                for (int i = 0; i < _askCount; i++)
                {
                    if (price < _asks[i]) { pos = i; break; }
                }

                if (pos >= 4) return 0;

                int newCount = Math.Min(_askCount + 1, 4);
                for (int i = newCount - 1; i > pos; i--)
                    _asks[i] = _asks[i - 1];
                _asks[pos] = price;
                _askCount = newCount;
                return pos + 1;
            }
        }

        /// <summary>
        /// Candidato PVV pré-filtrado no callback (rank já calculado).
        /// Struct leve (~40 bytes) — enfileirado no callback, drenado no TradeProcessingLoop.
        /// Isso mantém o callback da DLL em ~1µs, evitando bloquear trades.
        /// </summary>
        private readonly struct PvvBookCandidate
        {
            public readonly string Ticker;
            public readonly int Agent;
            public readonly int Side;   // 0=bid, 1=ask
            public readonly int Rank;   // 1-4
            public readonly int Volume;
            public readonly DateTime? ExchangeTime;

            public PvvBookCandidate(string ticker, int agent, int side, int rank, int volume, DateTime? exchangeTime)
            {
                Ticker = ticker; Agent = agent; Side = side;
                Rank = rank; Volume = volume; ExchangeTime = exchangeTime;
            }
        }

        private readonly struct RawDepth
        {
            public readonly byte Side;
            public readonly byte UpdateType;
            public RawDepth(byte s, byte u) { Side=s; UpdateType=u; }
        }

        #endregion

        #region Campos Privados

        private readonly ConnectionLogger _logger;
        private readonly object _lock = new object();
        private readonly List<string> _subscribedTickers = new();
        private volatile string? _primaryBookTicker;

        // _tradeQueue: fila lock-free. Protegida por TTL na entrada (OnTradeCallbackCore):
        // eventos com exchangeTime > 15s são descartados antes de chegar aqui — replay
        // histórico pós-PARTIAL_CONNECTED nunca enche a fila nem chega a subsistemas.
        // _tradeSignal mantido para wake-up sub-ms do TradeProcessingLoop (pattern síncrono preservado).
        private readonly ConcurrentQueue<RawTrade> _tradeQueue = new();
        private readonly ManualResetEventSlim _tradeSignal = new(false);
        private readonly ConcurrentQueue<BookWorkItem> _bookQueue  = new();
        private readonly ConcurrentQueue<RawDepth> _depthQueue = new();

        // PVV: tracker leve de 4 níveis de preço (substitui BookState+queue completo).
        private readonly PvvPriceTracker _pvvPriceTracker = new();
        // PVV: candidatos pré-filtrados — callback só enfileira struct leve (~1µs),
        // drenagem com broker lookup + PVV hook roda no TradeProcessingLoop (~idle path).
        private readonly ConcurrentQueue<PvvBookCandidate> _pvvBookQueue = new();

        // Cache de corretoras (concurrent: threads separadas de livro e negócios).
        private readonly ConcurrentDictionary<int, string> _brokerCache = new();
        private readonly ConcurrentDictionary<long, string> _offerBrokerCache = new();
        private readonly ConcurrentQueue<int> _brokerResolveQueue = new();

        // Threads de processamento: livro e negócios separados para trades não atrasarem ofertas.
        private Thread? _bookProcessingThread;
        private Thread? _tradeProcessingThread;
        private volatile bool _processingRunning = false;

        private bool _disposed       = false;
        private bool _initialized    = false;
        private ProviderCredentials? _lastCredentials = null;

        // GC protection dos delegates
        private TStateCallback?               _stateCallback;
        private THealthCallback?              _healthCallback;
        private TTradeCallback?               _tradeCallback;
        private TNewDailyCallback?            _dailyCallback;
        private TPriceBookCallback?           _priceBookCallback;
        private TOfferBookCallbackV2?         _offerBookCallback;
        /// <summary>Slot V1 do <c>DLLInitializeMarketLogin</c> recebe um no-op: o real fica em <c>SetOfferBookCallbackV2</c> para evitar callback duplicado.</summary>
        private TOfferBookCallbackV2?         _offerBookCallbackV1Stub;
        private THistoryTradeCallback?        _historyCallback;
        private TProgressCallBack?            _progressCallback;
        private TNewTinyBookCallBack?         _tinyBookCallback;
        private TChangeCotation?              _cotationCallback;
        private TConnectorPriceDepthCallback? _priceDepthCb;

        private volatile bool _readyToSubscribe = false;
        private int _offerBookSubscribeSeq;
        private int _bookEventsProcessed;
        private TConnectorAssetIdentifier _currentAssetID;

        // Log RAW do OfferBook
        private System.IO.StreamWriter? _rawBookLog;
        private readonly object _rawLogLock = new object();
        private int _rawLogCount = 0;
        private const int RAW_LOG_MAX = 5000;

        /// <summary>Diagnóstico: liga se existir o arquivo gatilho <c>%AppData%\MarketCore\offerbook_raw.on</c> ou se <c>MARKETCORE_OFFERBOOK_RAW=1</c>.</summary>
        private static readonly bool EnableRawOfferBookLog = IsRawOfferBookLogRequested();

        private static bool IsRawOfferBookLogRequested()
        {
            try
            {
                if (string.Equals(Environment.GetEnvironmentVariable("MARKETCORE_OFFERBOOK_RAW"), "1", StringComparison.Ordinal))
                    return true;

                string trigger = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MarketCore",
                    "offerbook_raw.on");
                return System.IO.File.Exists(trigger);
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region IMarketDataProvider — Eventos

        public event Action<TradeEvent>?             OnTrade;
        public event Action<BookLevel>?              OnBook;
        public event Action<BookFullRefresh>?        OnBookFullRefresh;
        public event Action<QuoteEvent>?             OnQuote;
        public event Action<ConnectionChangedEvent>? OnConnectionChanged;
        /// <summary>
        /// Encaminha negócios históricos vindos do callback nativo da DLL.
        /// Útil para módulos externos (ex.: importador histórico) sem acoplar ao provider.
        /// </summary>
        public static event Action<string, string, uint, double, double, int, int, int, int>? OnNativeHistoryTrade;

        #endregion

        #region IMarketDataProvider — Propriedades

        public string ProviderName => "Nelogica ProfitDLL";
        public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;
        public IReadOnlyList<string> SubscribedTickers
        {
            get { lock (_lock) return _subscribedTickers.AsReadOnly(); }
        }

        #endregion

        #region Construtor

        public ProfitDLLProvider()
        {
            _logger = new ConnectionLogger();
            _logger.Log($"{ProviderName} inicializado");

            // Registra o resolver do PVV. O worker do Bridge (thread separada
            // da DLL) invoca este delegate — chamada SEGURA para GetAgentName.
            // O bloco do pvvHook no TradeProcessingLoop NÃO deve chamar isso
            // (roda na ConnectorThread da DLL — reentrância proibida).
            PregaoVivaVozHook.ResolveAgentName = ResolveAgentNameForPvv;

            string raizMarketCore = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MarketCore");
            string triggerPath = System.IO.Path.Combine(raizMarketCore, "offerbook_raw.on");
            string logPath = System.IO.Path.Combine(raizMarketCore, "offerbook_raw.txt");
            _logger.Log($"[RAW LOG] Trigger={triggerPath} | exists={System.IO.File.Exists(triggerPath)} | EnableRawOfferBookLog={EnableRawOfferBookLog}");

            if (!EnableRawOfferBookLog)
                return;

            try
            {
                System.IO.Directory.CreateDirectory(raizMarketCore);
                _rawBookLog = new System.IO.StreamWriter(logPath, append: false, System.Text.Encoding.UTF8)
                {
                    AutoFlush = true
                };
                _rawBookLog.WriteLine($"=== OfferBook RAW Log — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                _rawBookLog.WriteLine("Colunas: TIME | TICKER | ACTION | POS | SIDE | QTD | AGENT | OFFER_ID | PRICE | bHasPrice | bHasQtd | bHasOfferID | bHasAgent");
                _logger.Log($"[RAW LOG] OfferBook log em: {logPath}");
            }
            catch (Exception ex)
            {
                _logger.Log($"[RAW LOG] Falha ao criar log: {ex.Message}");
            }
        }

        #endregion

        #region Thread de Processamento

        private void StartProcessingThread()
        {
            _processingRunning = true;
            // [PERF] BookProcessingLoop desativado — subscription de book cortada, thread ociosa consumia CPU.
            // _bookProcessingThread = new Thread(BookProcessingLoop)
            // {
            //     IsBackground = true,
            //     Name = "ProfitDLL-Book",
            //     Priority = ThreadPriority.AboveNormal
            // };
            _tradeProcessingThread = new Thread(TradeProcessingLoop)
            {
                IsBackground = true,
                Name = "ProfitDLL-Trades",
                Priority = ThreadPriority.AboveNormal
            };
            // _bookProcessingThread.Start();
            _tradeProcessingThread.Start();

            // Monitor de latência: amostra a cada 1s idade dos callbacks + tamanho das filas.
            // Grava em %LocalAppData%\MarketCore\dll_latency.log — independente do PVV.
            DllLatencyMonitor.Start(() => (_tradeQueue.Count, _bookQueue.Count, _depthQueue.Count));
        }

        private void StopProcessingThread()
        {
            DllLatencyMonitor.Stop();
            _processingRunning = false;
            _bookProcessingThread?.Join(TimeSpan.FromSeconds(3));
            _tradeProcessingThread?.Join(TimeSpan.FromSeconds(3));
        }

        private void BookProcessingLoop()
        {
            // Drena até esvaziar por volta da fila (yield periódico) — evita backlog de deltas vs trades em pico.
            while (_processingRunning)
            {
                int bookSlice = 0;
                while (_bookQueue.TryDequeue(out var work))
                {
                    bookSlice++;
                    if ((bookSlice & 4095) == 0)
                        Thread.Sleep(0);

                    // Atualiza profundidade da fila para backpressure do PVV (a cada 256 eventos, barato).
                    if ((bookSlice & 255) == 0)
                        DllLatencyMonitor.BookQueueDepth = _bookQueue.Count;

                    // Diagnostic de latência: contabiliza processed + idade do último processado.
                    Interlocked.Increment(ref DllLatencyMonitor.BooksProcessedTotal);
                    if (!work.IsFullRefresh && work.Delta.ExchangeTime.HasValue)
                        Interlocked.Exchange(ref DllLatencyMonitor.LastBookProcessedExchangeTicks, work.Delta.ExchangeTime.Value.ToLocalTime().Ticks);

                    try
                    {
                        if (Interlocked.Increment(ref _bookEventsProcessed) == 1)
                            _logger.Log($"[OfferBook] Primeiro evento processado: {work.FullTicker ?? work.Delta.Ticker}");

                        if (work.IsFullRefresh)
                        {
                            BookLevel[]? bids = null;
                            BookLevel[]? asks = null;
                            if (work.FullRawSell != null || work.FullRawBuy != null)
                            {
                                // ═══════════════════════════════════════════════════════════════
                                // ATENÇÃO: Na DLL Nelogica, os nomes são da perspectiva do TRADER:
                                //   pArrayBuy  (FullRawBuy)  = "onde posso COMPRAR" = ofertas de VENDA (ASK/side=1)
                                //   pArraySell (FullRawSell)  = "onde posso VENDER"  = ofertas de COMPRA (BID/side=0)
                                // ═══════════════════════════════════════════════════════════════
                                if (work.FullRawSell is { Length: >= 8 } rawSell)
                                {
                                    int q = BitConverter.ToInt32(rawSell, 0);
                                    var parsed = ParseOfferBookSnapshotToLevels(work.FullTicker!, rawSell, side: 0);
                                    if (q == 0 || parsed.Length > 0)
                                        bids = parsed;
                                }

                                if (work.FullRawBuy is { Length: >= 8 } rawBuy)
                                {
                                    int q = BitConverter.ToInt32(rawBuy, 0);
                                    var parsed = ParseOfferBookSnapshotToLevels(work.FullTicker!, rawBuy, side: 1);
                                    if (q == 0 || parsed.Length > 0)
                                        asks = parsed;
                                }
                            }
                            else if (work.FullBids != null || work.FullAsks != null)
                            {
                                bids = work.FullBids;
                                asks = work.FullAsks;
                            }
                            else
                            {
                                continue;
                            }

                            if (bids == null && asks == null)
                            {
                                if (_fullBookRouteLogCount < 10)
                                {
                                    _fullBookRouteLogCount++;
                                    _logger.Log($"[FullBook DIAG] SKIP: both null for {work.FullTicker}");
                                }
                                continue;
                            }

                            if (_fullBookRouteLogCount < 10)
                            {
                                _fullBookRouteLogCount++;
                                _logger.Log($"[FullBook DIAG] EMIT: {work.FullTicker} bids={bids?.Length ?? -1} asks={asks?.Length ?? -1}");
                            }

                            OnBookFullRefresh?.Invoke(new BookFullRefresh(
                                work.FullTicker!,
                                bids,
                                asks));
                            continue;
                        }

                        RawBook rawBook = work.Delta;

                        if (rawBook.Ticker == "__CLEAR__")
                            continue;

                        decimal price = 0;
                        if (rawBook.Action == 1)
                        {
                            if (rawBook.Price > 0 && !TryPriceToDecimal(rawBook.Price, out price))
                                continue;
                        }
                        else if (rawBook.Action == 2 || rawBook.Action == 3)
                        {
                            // delete: preço opcional conforme DLL; sem preço válido pode ser 0
                            if (rawBook.Price != 0 && !TryPriceToDecimal(rawBook.Price, out price))
                                continue;
                        }
                        else if (!TryPriceToDecimal(rawBook.Price, out price))
                        {
                            continue;
                        }

                        string broker = ResolveBookBrokerFromCache(rawBook.Agent, rawBook.OfferId, rawBook.Agent > 0);

                        OnBook?.Invoke(new BookLevel(
                            Ticker:   rawBook.Ticker,
                            Side:     rawBook.Side == 0 ? BookSide.Bid : BookSide.Ask,
                            Price:    price,
                            Volume:   rawBook.Volume,
                            Broker:   broker,
                            Time:     DateTime.UtcNow,
                            OfferId:  rawBook.OfferId,
                            Action:   rawBook.Action,
                            Position: rawBook.Position,
                            ExchangeTime: rawBook.ExchangeTime,
                            VolumeIsDelta: rawBook.Action == 1 && rawBook.HasQuantityUpdate,
                            AgentId: rawBook.Agent
                        ));
                        // Bridge do Pregão Viva Voz para BOOK é chamado no MarketEngine.HandleBook —
                        // lá temos acesso ao depth atual do book para computar o nível correto
                        // (a Nelogica manda nPosition contado do final da fila, então precisamos
                        // do count para inverter: nivel = depth - position).
                    }
                    catch (Exception ex) { _logger.Log($"Erro ProcessBook: {ex.Message}"); }
                }

                // Atualiza profundidade final (0 se drenou tudo; count real se yield).
                DllLatencyMonitor.BookQueueDepth = _bookQueue.Count;

                if (bookSlice > 0)
                {
                    DrainBrokerResolveQueue(32);
                    Thread.Sleep(0);
                }
                else
                {
                    DrainBrokerResolveQueue(128);
                    Thread.Sleep(1);
                }
            }
        }

        private void TradeProcessingLoop()
        {
            while (_processingRunning)
            {
                bool hadWork = false;

                int tradeSlice = 0;
                while (_tradeQueue.TryDequeue(out var raw))
                {
                    tradeSlice++;
                    hadWork = true;
                    if ((tradeSlice & 2047) == 0)
                        Thread.Sleep(0);

                    // Diagnostic de latência: contabiliza processed + idade do último processado.
                    Interlocked.Increment(ref DllLatencyMonitor.TradesProcessedTotal);
                    if (raw.ExchangeUtc.HasValue)
                        Interlocked.Exchange(ref DllLatencyMonitor.LastTradeProcessedExchangeTicks, raw.ExchangeUtc.Value.ToLocalTime().Ticks);

                    try
                    {
                        // Manual DLL (TNewTradeCallback): 1=cross 2=compra agressão 3=venda agressão … 32=desconhecido
                        var aggressor = raw.TradeType == 2 ? TradeAggressor.Buy
                                      : raw.TradeType == 3 ? TradeAggressor.Sell
                                      : TradeAggressor.Unknown;

                        int agentId = aggressor == TradeAggressor.Buy ? raw.BuyAgent : raw.SellAgent;

                        DateTime receivedUtc = DateTime.UtcNow;
                        DateTime timeLocal =
                            raw.ExchangeUtc.HasValue ? raw.ExchangeUtc.Value.ToLocalTime() : DateTime.Now;

                        OnTrade?.Invoke(new TradeEvent(
                            Ticker:    raw.Ticker ?? string.Empty,
                            Price:     (decimal)raw.Price,
                            Volume:    raw.Qtd,
                            Broker:    FormatBookBroker(agentId),
                            Aggressor: aggressor,
                            Time:      timeLocal,
                            ExchangeTimeUtc: raw.ExchangeUtc,
                            ReceivedUtc: receivedUtc));

                        // Pregão Viva Voz: encaminha o trade real para a Bridge (se motor ativo).
                        //
                        // Convenção oficial da Nelogica ProfitDLL, confirmada no exemplo Python
                        // publicado em https://ajuda.nelogica.com.br (artigo "Do tick ao dashboard"):
                        //   if trade_type == 2:  # agressao de compra   → usa BUY_AGENT
                        //   elif trade_type == 3:  # agressao de venda  → usa SELL_AGENT
                        //
                        // Portanto:
                        //   Nelogica raw.TradeType == 2  →  agressor COMPROU (tomou o ask)
                        //   Nelogica raw.TradeType == 3  →  agressor VENDEU (bateu no bid)
                        //
                        // Bridge espera: 1 = agressor comprou; 2 = agressor vendeu.
                        var pvvHook = PregaoVivaVozHook.OnTradeReceived;

                        // OBS: os PvvDebugFileLog.Write que existiam AQUI foram REMOVIDOS.
                        // Motivo (ver relatório de diagnóstico): PvvDebugFileLog usa
                        // lock(Gate) + File.AppendAllText síncrono. Rodando na
                        // TradeProcessingLoop, qualquer I/O bloqueado (antivírus,
                        // OneDrive segurando o AppData, disco lento) congelava a
                        // thread inteira → parava também a fita de trades do
                        // MarketCore principal. Diagnóstico agora fica só nas threads
                        // seguras: Bridge worker e Engine worker.
                        // Contador _pvvHookLogCount ainda existe (compat), sem uso aqui.
                        System.Threading.Interlocked.Increment(ref _pvvHookLogCount);

                        if (pvvHook != null)
                        {
                            int pvvTradeType = raw.TradeType == 2 ? 1   // Nelogica 2 = compra-agr → PVV 1
                                             : raw.TradeType == 3 ? 2   // Nelogica 3 = venda-agr  → PVV 2
                                             : 0;
                            if (pvvTradeType != 0)
                            {
                                // Passa apenas o agentId como string numérica — o worker do Bridge
                                // (thread segura) resolve o nome real via PregaoVivaVozHook.ResolveAgentName.
                                string buyName  = raw.BuyAgent.ToString();
                                string sellName = raw.SellAgent.ToString();

                                string bolsa = raw.ExchangeUtc.HasValue
                                    ? raw.ExchangeUtc.Value.ToLocalTime().ToString("HH:mm:ss.fff")
                                    : "--:--:--.---";
                                string callbackInfo =
                                    $"TRADE bolsa={bolsa} ticker={raw.Ticker} buyId={buyName} sellId={sellName} qtd={raw.Qtd} tradeType={raw.TradeType}";

                                pvvHook(raw.Ticker ?? string.Empty, buyName, sellName, raw.Qtd, pvvTradeType, callbackInfo, raw.ExchangeUtc);
                            }
                        }
                    }
                    catch (Exception ex) { _logger.Log($"Erro ProcessTrade: {ex.Message}"); }
                }

                while (_depthQueue.TryDequeue(out _)) { }

                // PVV book: drena candidatos enfileirados pelo callback (broker lookup + hook).
                DrainPvvBookCandidates(hadWork ? 64 : 256);

                if (!hadWork)
                {
                    DrainBrokerResolveQueue(128);
                    if (_brokerResolveQueue.IsEmpty && _pvvBookQueue.IsEmpty)
                    {
                        _tradeSignal.Wait(5); // wake-up sub-ms quando trade chega (vs ~15ms do Sleep(1))
                        _tradeSignal.Reset();
                    }
                }
                else
                    Thread.Sleep(0);
            }
        }

        /// <summary>
        /// Drena candidatos PVV de book enfileirados pelo callback da DLL.
        /// Roda no TradeProcessingLoop — nunca no thread da DLL.
        /// Faz: broker cache lookup → ShortBrokerLabel → callbackInfo → PVV hook.
        /// </summary>
        private void DrainPvvBookCandidates(int maxItems)
        {
            for (int i = 0; i < maxItems && _pvvBookQueue.TryDequeue(out var c); i++)
            {
                try
                {
                    var pvvHook = PregaoVivaVozHook.OnBookUpdate;
                    if (pvvHook == null) continue;

                    if (!_brokerCache.TryGetValue(c.Agent, out var brokerName) ||
                        string.IsNullOrWhiteSpace(brokerName))
                        continue;

                    string shortLabel = ShortBrokerLabel(brokerName);
                    if (string.IsNullOrWhiteSpace(shortLabel)) continue;

                    string lado = c.Side == 0 ? "compra" : "venda";
                    string bolsa = c.ExchangeTime.HasValue
                        ? c.ExchangeTime.Value.ToLocalTime().ToString("HH:mm:ss.fff")
                        : "--:--:--.---";
                    string callbackInfo =
                        $"BOOK  bolsa={bolsa} ticker={c.Ticker} agent={shortLabel} lado={lado} nivel={c.Rank} qtd={c.Volume}";

                    pvvHook(c.Ticker, shortLabel, lado, c.Rank, c.Volume, callbackInfo, c.ExchangeTime);
                }
                catch (Exception ex)
                {
                    _logger.Log($"Erro DrainPvvBook: {ex.Message}");
                }
            }
        }

        public string ResolveDisplayBroker(BookLevel level)
        {
            if (!string.IsNullOrWhiteSpace(level.Broker) && !int.TryParse(level.Broker, out _))
                return level.Broker;

            if (level.AgentId > 0 && _brokerCache.TryGetValue(level.AgentId, out var cached))
            {
                string label = ShortBrokerLabel(cached);
                if (label.Length > 0)
                    return label;
            }

            if (level.OfferId > 0 && _offerBrokerCache.TryGetValue(level.OfferId, out var byOffer)
                && !string.IsNullOrWhiteSpace(byOffer))
            {
                return byOffer;
            }

            return level.Broker ?? string.Empty;
        }

        private void DrainBrokerResolveQueue(int maxAgents)
        {
            for (int i = 0; i < maxAgents && _brokerResolveQueue.TryDequeue(out int agentId); i++)
            {
                if (!_brokerCache.ContainsKey(agentId))
                    GetBrokerNameSafe(agentId);
            }
        }

        /// <summary>Corretoras cujo nome legal não se reduz corretamente ao token esperado pelo corte
        /// genérico de <see cref="ShortBrokerLabel"/> (primeiro espaço/traço/barra/ponto). Dois casos já
        /// confirmados: (1) iniciais com ponto logo no começo, ex. "J.P. MORGAN..." — o corte pega só a
        /// primeira letra ("J"); (2) o token esperado é só o PREFIXO da primeira palavra do nome legal, sem
        /// nenhum delimitador no meio, ex. "CITIGROUP..." ou "CITIBANK..." — o corte pega a palavra inteira
        /// ("CITIGROUP"), nunca "CITI". Os padrões abaixo são checados ANTES do corte genérico. Se outra
        /// corretora aparecer zerada pelo mesmo motivo, um padrão novo entra aqui (o log "[BrokerResolve]"
        /// em <see cref="GetBrokerNameSafe"/> mostra o nome legal exato pra calibrar o padrão certo).</summary>
        private static readonly (Regex Pattern, string Token)[] _brokerAliasPatterns =
        {
            // Sem "^" no início: cobre tanto "J.P. MORGAN S.A." quanto variações com prefixo
            // (ex.: "BANCO J.P. MORGAN S.A."), sem depender de saber o texto exato registrado na B3.
            (new Regex(@"\bJ\.?\s*P\.?\s*MORGAN", RegexOptions.Compiled), "JPM"),
            // "CITI" é só o prefixo de "CITIGROUP"/"CITIBANK" — sem delimitador no meio da palavra,
            // o corte genérico nunca isola "CITI" sozinho.
            (new Regex(@"\bCITI", RegexOptions.Compiled), "CITI"),
        };

        private static string ShortBrokerLabel(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            string trimmed = name.Trim();
            if (trimmed.Length == 0)
                return string.Empty;

            if (int.TryParse(trimmed, out _))
                return trimmed;

            string upper = trimmed.ToUpperInvariant();
            foreach (var (pattern, token) in _brokerAliasPatterns)
            {
                if (pattern.IsMatch(upper))
                    return token;
            }

            int splitAt = trimmed.IndexOfAny([' ', '-', '/', '.']);
            string token2 = splitAt > 0 ? trimmed[..splitAt] : trimmed;
            return token2.Trim().ToUpperInvariant();
        }

        /// <summary>
        /// Converte double→decimal. O range check elimina a necessidade de try-catch
        /// para OverflowException (decimal max = ±7.9×10²⁸, nosso teto é 10M).
        /// O <c>decimal.Round(raw, 10)</c> foi removido: preços da B3 já vêm com
        /// precisão adequada do double; Round(10) não alterava o valor mas custava ~50ns.
        /// </summary>
        private static bool TryPriceToDecimal(double price, out decimal value)
        {
            value = 0;
            if (price <= 0 || price > 10_000_000 || double.IsNaN(price) || double.IsInfinity(price))
                return false;

            // Range já validado acima — (decimal)price não pode dar overflow.
            value = (decimal)price;
            return true;
        }

        private string FormatBookBroker(int agentId)
        {
            if (agentId <= 0)
                return string.Empty;

            if (_brokerCache.TryGetValue(agentId, out var cached))
            {
                string label = ShortBrokerLabel(cached);
                if (label.Length > 0)
                    return label;
            }

            _brokerResolveQueue.Enqueue(agentId);
            return agentId.ToString();
        }

        private string ResolveBookBrokerFromCache(int agentId, long offerId, bool hasAgent)
        {
            if (hasAgent && agentId > 0)
            {
                if (_brokerCache.TryGetValue(agentId, out var cached))
                {
                    string label = ShortBrokerLabel(cached);
                    if (offerId > 0 && label.Length > 0)
                    {
                        _offerBrokerCache[offerId] = label;
                        if (_offerBrokerCache.Count > 10_000)
                        {
                            _offerBrokerCache.Clear();
                            _brokerLastResolveAttemptTicks.Clear();
                        }
                    }
                    return label;
                }

                _brokerResolveQueue.Enqueue(agentId);
                return agentId.ToString();
            }

            if (offerId > 0 && _offerBrokerCache.TryGetValue(offerId, out var cachedByOffer))
                return cachedByOffer ?? string.Empty;

            return string.Empty;
        }

        /// <summary>Resolve o token curto de uma corretora (ex.: "XP", "BTG") a partir do código numérico
        /// do agente, consultando a ProfitDLL de forma síncrona se ainda não estiver em cache. Diferente de
        /// <see cref="FormatBookBroker"/> (que enfileira e devolve o código na primeira chamada, pensado para
        /// o refresh contínuo do book), este método bloqueia até resolver — usado pelo backfill histórico da
        /// Leitura de Fluxo, onde poucas dezenas de corretoras distintas se repetem em milhares de negócios,
        /// então o custo da chamada à DLL só é pago uma vez por corretora.</summary>
        public string ResolveBrokerShortName(int agentId)
        {
            if (agentId <= 0) return string.Empty;
            return ShortBrokerLabel(GetBrokerNameSafe(agentId));
        }

        // ══════════════════════════════════════════════════════════════════════
        //  RESOLUÇÃO DE AGENTE PARA O PREGÃO VIVA VOZ (com retry rate-limited)
        // ══════════════════════════════════════════════════════════════════════
        //  Problema histórico: quando a DLL ainda não carregou o catálogo de
        //  agentes (primeiros segundos após conexão, ou catálogo parcial), o
        //  GetAgentName retorna string vazia. O GetBrokerNameSafe original
        //  cacheava o fallback numérico ("88", "3", "85") PERMANENTEMENTE →
        //  todos os callbacks subsequentes daquele agente vinham como número
        //  para o PVV, que não achava match no players_catalogo.json.
        //
        //  Solução: cache com retry. Se o cache tem nome REAL, devolve.
        //  Se tem só o fallback numérico, tenta a DLL de novo (rate-limited
        //  a 1 tentativa/2s por agente, para não sobrecarregar). Tenta as
        //  duas variantes de GetAgentName: nShortName=0 (nome completo) e
        //  nShortName=1 (nome curto) — algumas versões da DLL só respondem
        //  em uma delas.

        private readonly ConcurrentDictionary<int, long> _brokerLastResolveAttemptTicks = new();
        private const long BrokerRetryIntervalTicks = 20_000_000; // 2 segundos

        /// <summary>
        /// Resolve o nome do agente para o Pregão Viva Voz. Se o cache já tem um
        /// nome real (não é apenas o ID numérico), devolve imediatamente. Caso
        /// contrário, tenta consultar a DLL (rate-limitado a 1 tentativa/2s por
        /// agente). Tenta nome completo (<c>nShortName=0</c>) e curto
        /// (<c>nShortName=1</c>) para cobrir diferenças entre versões da DLL.
        /// </summary>
        public string ResolveAgentNameForPvv(int agentId)
        {
            if (agentId <= 0) return string.Empty;

            string numericFallback = agentId.ToString();

            // Cache tem nome REAL (não apenas o ID numérico)?
            if (_brokerCache.TryGetValue(agentId, out var cached)
                && !string.IsNullOrEmpty(cached)
                && !cached.Equals(numericFallback, StringComparison.Ordinal))
            {
                return ShortBrokerLabel(cached);
            }

            // Rate-limit: 1 tentativa a cada 2s por agente.
            long now = DateTime.UtcNow.Ticks;
            long lastAttempt = _brokerLastResolveAttemptTicks.TryGetValue(agentId, out var t) ? t : 0L;
            if (lastAttempt > 0 && (now - lastAttempt) < BrokerRetryIntervalTicks)
            {
                // Muito cedo — devolve o que temos (fallback numérico).
                return string.IsNullOrEmpty(cached) ? numericFallback : ShortBrokerLabel(cached);
            }
            _brokerLastResolveAttemptTicks[agentId] = now;

            // Retry: chama a DLL usando o novo contrato 4.0.0.36+ (padrão do exemplo
            // oficial da Nelogica). Tenta nome completo (CM_NONE=0), depois curto
            // (CM_IS_SHORT_NAME=1) — algumas corretoras só tem uma variante.
            try
            {
                string? name = TryGetAgentName(agentId, 0u);      // CM_NONE
                if (string.IsNullOrWhiteSpace(name))
                    name = TryGetAgentName(agentId, 1u);           // CM_IS_SHORT_NAME

                if (!string.IsNullOrWhiteSpace(name)
                    && !name.Equals(numericFallback, StringComparison.Ordinal))
                {
                    _brokerCache[agentId] = name;
                    _logger.Log($"[BrokerResolve][PVV-retry] agentId={agentId} rawName=\"{name}\" shortToken=\"{ShortBrokerLabel(name)}\"");
                    MarketCore.Providers.Nelogica.PvvDebugFileLog.Write(
                        $"[PROVIDER-RESOLVE] agentId={agentId} → \"{name}\" (shortToken=\"{ShortBrokerLabel(name)}\") — RESOLVIDO");
                    return ShortBrokerLabel(name);
                }
            }
            catch
            {
                /* devolve fallback abaixo */
            }

            // DLL ainda não sabe o nome. NÃO cacheia (permite retry na próxima janela
            // do _brokerLastResolveAttemptTicks). Devolve o número.
            MarketCore.Providers.Nelogica.PvvDebugFileLog.Write(
                $"[PROVIDER-RESOLVE] agentId={agentId} → DLL não resolveu (fallback \"{numericFallback}\"), retry em 2s");
            return numericFallback;
        }

        /// <summary>
        /// Chama GetAgentNameLength + GetAgentName conforme padrão oficial 4.0.0.41
        /// (Exemplo C# Nelogica, Program.cs::DoGetAgentName). Retorna null se a DLL
        /// não resolver o agente (length<=0 ou copied<=0).
        /// </summary>
        private static string? TryGetAgentName(int agentId, uint nShortName)
        {
            int neededLen = GetAgentNameLength(agentId, nShortName);
            if (neededLen <= 0) return null;

            var sb = new StringBuilder(neededLen + 1);
            int copied = GetAgentName(neededLen + 1, agentId, sb, nShortName);
            if (copied <= 0) return null;

            string name = sb.ToString(0, Math.Min(copied, sb.Length)).Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }

        // ProfitDLL 4.0.0.36+ mudou a semântica de GetAgentName:
        //   ANTES (<= 4.0.0.35):  retornava NL_OK (0) em sucesso, buffer com string null-terminated
        //   AGORA (4.0.0.36+):    retorna o TAMANHO REALMENTE COPIADO (>0 = sucesso; <0 = erro)
        //
        // Padrão oficial (Exemplo C# Nelogica, Program.cs::DoGetAgentName):
        //   int len = GetAgentNameLength(agentId, flags);   // >0 = ok
        //   var sb = new StringBuilder(len);
        //   int copied = GetAgentName(len, agentId, sb, flags);   // >0 = ok, copiou N chars
        //   string name = sb.ToString(0, copied);
        //
        // POLÍTICA DE CACHE (pipeline principal — fita, book, OnTrade?.Invoke):
        //
        // Cacheamos o fallback numérico (agentId.ToString()) quando a DLL não
        // resolve. Isso impede retry storm — DrainBrokerResolveQueue verifica
        // ContainsKey e nunca mais chama a DLL para aquele agente, mantendo o
        // TradeProcessingLoop responsivo. Custo: pipeline principal fica com
        // "88" para agents que a DLL não conhece.
        //
        // A rota do Pregão Viva Voz é INDEPENDENTE: ResolveAgentNameForPvv
        // detecta o fallback numérico no cache (cached.Equals(numericFallback))
        // e retenta a DLL usando seu próprio rate-limit (_brokerLastResolveAttemptTicks,
        // 2s por agente). Se o catálogo carregar depois, o PVV atualiza o cache
        // com o nome real e o pipeline principal passa a usar o nome também.
        private string GetBrokerNameSafe(int agentId)
        {
            if (agentId <= 0) return string.Empty;
            if (_brokerCache.TryGetValue(agentId, out var cached) && !string.IsNullOrEmpty(cached))
                return cached;

            string numericFallback = agentId.ToString();

            try
            {
                // 1) Descobre o tamanho necessário via GetAgentNameLength.
                int neededLen = GetAgentNameLength(agentId, 0u);   // 0 = CM_NONE (nome completo)
                if (neededLen <= 0)
                {
                    // DLL não sabe o agente ou catálogo ainda não carregou. CACHEIA o
                    // fallback numérico para evitar retry storm no TradeProcessingLoop.
                    // A rota do PVV (ResolveAgentNameForPvv) retenta com rate-limit próprio.
                    _brokerCache[agentId] = numericFallback;
                    return numericFallback;
                }

                // 2) Aloca buffer exato + 1 (margem para eventual null-terminator).
                var sb = new StringBuilder(neededLen + 1);
                int copied = GetAgentName(neededLen + 1, agentId, sb, 0u);
                if (copied <= 0)
                {
                    _brokerCache[agentId] = numericFallback;
                    return numericFallback;
                }

                string name = sb.ToString(0, Math.Min(copied, sb.Length)).Trim();
                if (string.IsNullOrEmpty(name))
                {
                    _brokerCache[agentId] = numericFallback;
                    return numericFallback;
                }

                _brokerCache[agentId] = name;
                _logger.Log($"[BrokerResolve] agentId={agentId} rawName=\"{name}\" shortToken=\"{ShortBrokerLabel(name)}\" (len={copied})");
                return name;
            }
            catch (Exception ex)
            {
                _logger.Log($"[BrokerResolve] agentId={agentId} EXCEÇÃO: {ex.Message}");
                _brokerCache[agentId] = numericFallback;
                return numericFallback;
            }
        }

        #endregion

        #region IMarketDataProvider — Conexão

        public async Task ConnectAsync(ProviderCredentials credentials)
        {
            await ProfitMarketInit.DllBootstrapGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (string.IsNullOrEmpty(credentials.Username) ||
                    string.IsNullOrEmpty(credentials.Password))
                {
                    _logger.Log("✗ Username ou Password vazios");
                    SetStatus(ConnectionStatus.Error, "Credenciais inválidas");
                    return;
                }

                if (Status == ConnectionStatus.Connected)
                {
                    _logger.Log("Já conectado - reutilizando sessão");
                    return;
                }

                _lastCredentials = credentials;

                if (ProfitMarketInit.IsDllInitializedInProcess && !_initialized)
                {
                    await ReattachToExistingDllSessionAsync().ConfigureAwait(false);
                    return;
                }

                _readyToSubscribe = false;

                SetStatus(ConnectionStatus.Connecting, "Conectando...");
                _logger.Log($"Iniciando DLLInitializeMarketLogin — usuário: {credentials.Username}");

                try
                {
                    _stateCallback     = OnStateCallback;
                    _tradeCallback     = OnTradeCallback;
                    _dailyCallback     = OnDailyCallback;
                    _priceBookCallback = OnPriceBookCallback;
                    _offerBookCallback = OnOfferBookCallback;
                    _offerBookCallbackV1Stub = (TAssetID _, int _, int _, int _, long _, int _, long _, double _,
                                                ushort _, ushort _, ushort _, ushort _, ushort _,
                                                string _, IntPtr _, IntPtr _) => { };
                    _historyCallback   = OnHistoryCallback;
                    _progressCallback  = OnProgressCallback;
                    _tinyBookCallback  = OnTinyBookCallback;
                    _cotationCallback  = OnCotationCallback;
                    _priceDepthCb      = OnPriceDepthCallback;

                    int result = DLLInitializeMarketLogin(
                        credentials.ActivationCode ?? string.Empty,
                        credentials.Username,
                        credentials.Password,
                        _stateCallback,
                        _tradeCallback,
                        _dailyCallback,
                        _priceBookCallback,
                        _offerBookCallbackV1Stub,
                        _historyCallback,
                        _progressCallback,
                        _tinyBookCallback);

                    _logger.Log($"DLLInitializeMarketLogin retornou: {result}");

                    if (result != 0)
                    {
                        SetStatus(ConnectionStatus.Error, $"Erro ao inicializar: código {result}");
                        return;
                    }

                    ProfitMarketInit.MarkDllInitializedFromProvider();

                    SetChangeCotationCallback(_cotationCallback);
                    SetPriceDepthCallback(_priceDepthCb);

                    int r2 = SetOfferBookCallbackV2(_offerBookCallback);
                    _logger.Log($"SetOfferBookCallbackV2 retornou: {r2}");

                    int waited = 0;
                    while (!_readyToSubscribe && waited < 20000)
                    {
                        await Task.Delay(100).ConfigureAwait(false);
                        waited += 100;
                    }

                    if (!_readyToSubscribe)
                    {
                        SetStatus(ConnectionStatus.Error, "Timeout aguardando DLL ficar pronta");
                        return;
                    }

                    StartProcessingThread();

                    _initialized = true;
                    SetStatus(ConnectionStatus.Connected, "Conectado");
                    _logger.Log("✓ CONEXÃO ESTABELECIDA!");
                }
                catch (Exception ex)
                {
                    _logger.Log($"✗ Exceção ConnectAsync: {ex.Message}");
                    SetStatus(ConnectionStatus.Error, ex.Message);
                }
            }
            finally
            {
                ProfitMarketInit.DllBootstrapGate.Release();
            }
        }

        private async Task ReattachToExistingDllSessionAsync()
        {
            _logger.Log("ProfitDLL já ativa no processo — reanexando callbacks sem novo login");

            try
            {
                _stateCallback     = OnStateCallback;
                _tradeCallback     = OnTradeCallback;
                _dailyCallback     = OnDailyCallback;
                _priceBookCallback = OnPriceBookCallback;
                _offerBookCallback = OnOfferBookCallback;
                _offerBookCallbackV1Stub ??= (TAssetID _, int _, int _, int _, long _, int _, long _, double _,
                                              ushort _, ushort _, ushort _, ushort _, ushort _,
                                              string _, IntPtr _, IntPtr _) => { };
                _historyCallback   = OnHistoryCallback;
                _progressCallback  = OnProgressCallback;
                _tinyBookCallback  = OnTinyBookCallback;
                _cotationCallback  = OnCotationCallback;
                _priceDepthCb      = OnPriceDepthCallback;

                SetChangeCotationCallback(_cotationCallback);
                SetOfferBookCallbackV2(_offerBookCallback);
            }
            catch (Exception ex)
            {
                _logger.Log($"Reattach callbacks: {ex.Message}");
            }

            if (!_readyToSubscribe)
            {
                int waited = 0;
                while (!_readyToSubscribe && waited < 5000)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                    waited += 100;
                }
            }

            if (!_readyToSubscribe)
                _readyToSubscribe = true;

            StartProcessingThread();
            _initialized = true;
            SetStatus(ConnectionStatus.Connected, "Conectado");

            List<string> pending;
            lock (_lock)
                pending = new List<string>(_subscribedTickers);

            foreach (string ticker in pending)
                InternalSubscribe(ticker);
        }

        public async Task DisconnectAsync()
        {
            await Task.Run(() =>
            {
                StopProcessingThread();
                lock (_lock)
                {
                    _logger.Log("Desconectando...");
                    foreach (var ticker in _subscribedTickers.ToArray())
                        InternalUnsubscribe(ticker);
                    _initialized      = false;
                    _readyToSubscribe = false;
                    SetStatus(ConnectionStatus.Disconnected, "Desconectado");
                    _logger.Log("✓ Desconectado");
                }
            });

            lock (_rawLogLock)
            {
                _rawBookLog?.WriteLine($"=== FIM DO LOG — {DateTime.Now:HH:mm:ss} — {_rawLogCount} eventos ===");
                _rawBookLog?.Close();
                _rawBookLog = null;
            }
        }

        #endregion

        #region Callbacks da DLL

        /// <summary>
        /// Excepções que atravessam código gerido chamado pela DLL fazem muitos runtimes darem teardown imediato.
        /// Jamais propagar erro para unmanaged.
        /// </summary>
        private void SafeDllCallback(string callbackName, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                try
                {
                    _logger.Log($"✗ CALLBACK {callbackName}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
                catch { /* evita segunda excepção durante logging */ }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  HEALTH MONITOR (ProfitDLL 4.0.0.41+) — API pública para o MainWindow
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>Estados de thread da DLL (retorno de GetHealthStatus + payload de SetHealthCallback).</summary>
        public enum DllHealthStatus
        {
            /// <summary>Threads da DLL respondendo normalmente (shsResponsive = 0).</summary>
            Responsive = 0,
            /// <summary>Ao menos uma thread da DLL travou (shsFrozen = 1).</summary>
            Frozen = 1,
            /// <summary>Estado desconhecido (DLL antiga sem a função, ou erro).</summary>
            Unknown = -1
        }

        private long _ultimoTradeRecebidoUtcTicks;

        // [PVV-DEBUG] Contador para rate-limit do log do pvvHook (não bloqueia
        // o fluxo — apenas serve para o Anderson conferir se o hook é invocado).
        private static long _pvvHookLogCount;
        private volatile int _lastHealthStatus = (int)DllHealthStatus.Unknown;
        private volatile bool _healthCallbackRegistered = false;

        /// <summary>
        /// Timestamp UTC do último trade recebido no callback da DLL.
        /// Consumido pelo indicador "Status de Atualização" do MainWindow para
        /// calcular delay vs relógio local. Retorna <see cref="DateTime.MinValue"/>
        /// enquanto nenhum trade chegou nesta sessão.
        /// </summary>
        public DateTime UltimoTradeRecebidoUtc
        {
            get
            {
                long t = Interlocked.Read(ref _ultimoTradeRecebidoUtcTicks);
                return t <= 0 ? DateTime.MinValue : new DateTime(t, DateTimeKind.Utc);
            }
        }

        /// <summary>
        /// Estado corrente das threads da DLL. Consulta em tempo real via
        /// GetHealthStatus (polling ativo — necessário porque o callback só
        /// dispara em MUDANÇA de estado; se a DLL iniciou já em Responsive
        /// e nunca travou, o callback nunca fira).
        ///
        /// Contrato do manual (4.0.0.41):
        ///   int GetHealthStatus(ref int nState)
        ///     retorno: 0 = NL_OK (nState válido); outro = erro (ignorar nState)
        ///     nState:  0 = shsResponsive; 1 = shsFrozen
        ///
        /// Se a DLL for anterior à 4.0.0.41, retorna Unknown (EntryPointNotFoundException).
        /// Se a DLL não estiver inicializada ainda, retorna Unknown (rc != 0).
        /// </summary>
        public DllHealthStatus QueryHealthStatus()
        {
            try
            {
                int state = 0;
                int rc = GetHealthStatus(ref state);
                if (rc != 0)
                {
                    // NL_NOT_INITIALIZED ou outro erro — indicador fica "--".
                    return DllHealthStatus.Unknown;
                }
                _lastHealthStatus = state;
                return state == 0 ? DllHealthStatus.Responsive
                     : state == 1 ? DllHealthStatus.Frozen
                     : DllHealthStatus.Unknown;
            }
            catch (EntryPointNotFoundException)
            {
                // DLL antiga (< 4.0.0.41): função não existe no export table.
                return DllHealthStatus.Unknown;
            }
            catch
            {
                return DllHealthStatus.Unknown;
            }
        }

        /// <summary>
        /// Disparado quando a DLL notifica mudança de estado das threads
        /// (shsResponsive ↔ shsFrozen). Handler roda na thread da DLL — o
        /// consumidor (MainWindow) deve marshallar para a UI via Dispatcher.
        /// </summary>
        public event EventHandler<DllHealthStatus>? OnHealthChanged;

        /// <summary>
        /// Disparado a cada TStateCallback com <c>nConnStateType == 2</c>.
        /// Payload é o <c>result</c> bruto: 0=desconectado, 4=conectado,
        /// 5=PERFORMANCE_WARNING (4.0.0.41+), 6=PARTIAL_CONNECTED (4.0.0.41+).
        /// </summary>
        public event EventHandler<int>? OnFeedStateChanged;

        /// <summary>
        /// Registra o SetHealthCallback na DLL (idempotente). Chamado após o
        /// login bem-sucedido. Se a DLL for antiga, apenas loga e segue —
        /// o MainWindow ainda pode usar QueryHealthStatus() via polling.
        /// </summary>
        private void TryRegisterHealthCallback()
        {
            if (_healthCallbackRegistered) return;
            try
            {
                _healthCallback = OnHealthCallback;    // GC-protection
                int rc = SetHealthCallback(_healthCallback);
                _healthCallbackRegistered = true;
                _logger.Log($"[HealthCallback] Registrado (rc={rc})");
            }
            catch (EntryPointNotFoundException)
            {
                _logger.Log("[HealthCallback] DLL antiga (< 4.0.0.41), sem SetHealthCallback — usando apenas polling.");
            }
            catch (Exception ex)
            {
                _logger.Log($"[HealthCallback] Falha ao registrar: {ex.Message}");
            }

            // Poll IMEDIATO após conectar: dispara OnHealthChanged com o estado atual.
            // Sem isso, se a DLL iniciar em Responsive e nunca travar, o callback
            // nunca fira → o card THR ficaria "--" para sempre no MainWindow (embora
            // o timer de 500ms também polle, o HUD ganha um valor inicial imediato).
            try
            {
                var initial = QueryHealthStatus();
                if (initial != DllHealthStatus.Unknown)
                {
                    OnHealthChanged?.Invoke(this, initial);
                    _logger.Log($"[HealthCallback] Estado inicial polled: {initial}");
                }
            }
            catch { /* best effort */ }
        }

        private void OnHealthCallback(int nHealthStatus)
            => SafeDllCallback(nameof(OnHealthCallback), () =>
            {
                _lastHealthStatus = nHealthStatus;
                var status = nHealthStatus == 0 ? DllHealthStatus.Responsive
                           : nHealthStatus == 1 ? DllHealthStatus.Frozen
                           : DllHealthStatus.Unknown;
                _logger.Log($"[HealthCallback] status={status}");
                try { OnHealthChanged?.Invoke(this, status); } catch { /* handler externo */ }
            });

        private void OnStateCallback(int nConnStateType, int result)
            => SafeDllCallback(nameof(OnStateCallback), () => OnStateCallbackCore(nConnStateType, result));

        private void OnStateCallbackCore(int nConnStateType, int result)
        {
            _logger.Log($"[StateCallback] nConnStateType={nConnStateType} result={result}");

            switch (nConnStateType)
            {
                case 0:
                    switch (result)
                    {
                        case 0: _logger.Log("[Login] Conectado"); break;
                        case 1: SetStatus(ConnectionStatus.Error, "Login inválido"); break;
                        case 2: SetStatus(ConnectionStatus.Error, "Senha inválida"); break;
                        case 3: SetStatus(ConnectionStatus.Error, "Senha bloqueada"); break;
                        case 4: SetStatus(ConnectionStatus.Error, "Senha expirada"); break;
                    }
                    break;

                case 1:
                    _logger.Log($"[Broker] result={result}");
                    break;

                case 2:
                    // Notifica o feed HUD do MainWindow (health monitor) sobre qualquer mudança.
                    try { OnFeedStateChanged?.Invoke(this, result); } catch { /* handler externo */ }

                    switch (result)
                    {
                        case 0:
                            _logger.Log("[Market] Desconectado — aguardando reconexão automática");
                            _readyToSubscribe = false;
                            if (_initialized)
                            {
                                _initialized = false;
                                SetStatus(ConnectionStatus.Connecting, "Reconectando...");
                            }
                            break;
                        case 4:
                            _logger.Log("[Market] CONECTADO — pronto para subscrições!");
                            _readyToSubscribe = true;
                            if (!_initialized && Status != ConnectionStatus.Disconnected)
                            {
                                _initialized = true;
                                SetStatus(ConnectionStatus.Connected, "Conectado");
                                List<string> pendentes;
                                lock (_lock) pendentes = new List<string>(_subscribedTickers);
                                foreach (var ticker in pendentes)
                                    InternalSubscribe(ticker);
                            }
                            // Health monitor (4.0.0.41+): registra o callback assim que
                            // o market estiver conectado. É idempotente — chamar 2x é seguro.
                            //
                            // CRÍTICO: NÃO chamar TryRegisterHealthCallback() de forma síncrona
                            // aqui. Estamos DENTRO do state callback da DLL, que segura o mutex
                            // interno da Nelogica. TryRegisterHealthCallback re-entra na DLL
                            // (SetHealthCallback + GetHealthStatus) e produz DEADLOCK — a mesma
                            // thread também é usada por outras callbacks (trade/book), então
                            // todo o fluxo do Pregão Viva Voz congela junto.
                            //
                            // Fix: despachar para ThreadPool. O state callback retorna imediato,
                            // a DLL libera o mutex, e a registration/poll do health roda depois
                            // em thread livre.
                            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                            {
                                try { TryRegisterHealthCallback(); }
                                catch (Exception ex) { _logger.Log($"[HealthCallback] worker: {ex.Message}"); }
                            });
                            break;
                        case 5:
                            // ProfitDLL 4.0.0.41+ : MARKET_PERFORMANCE_WARNING
                            // Feed conectado, mas com sinal de degradação.
                            _logger.Log("[Market] PERFORMANCE_WARNING — feed lento");
                            break;
                        case 6:
                            // ProfitDLL 4.0.0.41+ : MARKET_PARTIAL_CONNECTED
                            // Feed apenas parcialmente disponível.
                            _logger.Log("[Market] PARTIAL_CONNECTED — feed parcial/parado");
                            break;
                        default:
                            _logger.Log($"[Market] result={result}");
                            break;
                    }
                    break;

                case 3:
                    if (result != 0)
                        SetStatus(ConnectionStatus.Error, "Licença inválida");
                    else
                        _logger.Log("[Atividade] Válida ✓");
                    break;
            }
        }

        private void OnTradeCallback(
            TAssetID assetId, string date, uint tradeNumber,
            double price, double vol, int qtd,
            int buyAgent, int sellAgent, int tradeType, int bIsEdit)
            => SafeDllCallback(nameof(OnTradeCallback), () =>
                OnTradeCallbackCore(assetId, date, tradeNumber, price, vol, qtd, buyAgent, sellAgent, tradeType, bIsEdit));

        private void OnTradeCallbackCore(
            TAssetID assetId, string date, uint tradeNumber,
            double price, double vol, int qtd,
            int buyAgent, int sellAgent, int tradeType, int bIsEdit)
        {
            // Histórico/replay pode vir só por NewTrade com qtd=0 e quantidade em vol (callback de histórico por vezes nem dispara).
            ProfitHistoryRelay.TryMirrorNewTradeDuringHistoricalDownload(
                assetId.Ticker, date, tradeNumber, price, vol, qtd, buyAgent, sellAgent, tradeType);

            if (price <= 0 || price > 10_000_000 || double.IsNaN(price)) return;
            if (qtd <= 0) return;
            DateTime? exUtc = null;
            if (!string.IsNullOrWhiteSpace(date) && TryParseOfferBookDate(date, out DateTime parsedEx))
                exUtc = parsedEx;
            // TTL de entrada: descarta replay histórico pós-PARTIAL_CONNECTED antes de enfileirar.
            // Nenhum subsistema downstream (MarketDataManager, DeltaEngine, PVV) vê eventos velhos.
            // Eventos sem exchangeTime (null) passam normalmente — fallback seguro para callbacks sem data.
            if (exUtc.HasValue && (DateTime.UtcNow - exUtc.Value) > TtlEntrada)
            {
                _tradeSignal.Set();
                return;
            }

            _tradeQueue.Enqueue(new RawTrade(
                assetId.Ticker ?? string.Empty,
                price, qtd, buyAgent, sellAgent, tradeType, exUtc));
            _tradeSignal.Set(); // wake-up imediato da thread de processamento

            // Diagnostic de latência: idade do último trade recebido (bolsa vs now).
            Interlocked.Increment(ref DllLatencyMonitor.TradesReceivedTotal);
            if (exUtc.HasValue)
                Interlocked.Exchange(ref DllLatencyMonitor.LastTradeExchangeTicks, exUtc.Value.ToLocalTime().Ticks);

            // Health monitor (MainWindow): marca "chegou trade agora" — usado pelo
            // indicador "Status de Atualização" para calcular delay vs relógio local.
            Interlocked.Exchange(ref _ultimoTradeRecebidoUtcTicks, DateTime.UtcNow.Ticks);
        }

        private void OnPriceBookCallback(
            TAssetID assetId, int nAction, int nPosition,
            int side, int nQtd, int nCount, double sPrice,
            IntPtr pArraySell, IntPtr pArrayBuy)
        {
            // Ignorado — usamos OfferBook
        }

        private void OnOfferBookCallback(
            TAssetID assetId, int nAction, int nPosition,
            int side, long nQtd, int nAgent, long nOfferID, double sPrice,
            ushort bHasPrice, ushort bHasQtd, ushort bHasDate, ushort bHasOfferID, ushort bHasAgent,
            string date,
            IntPtr pArraySell, IntPtr pArrayBuy)
            => SafeDllCallback(nameof(OnOfferBookCallback), () =>
                OnOfferBookCallbackCore(assetId, nAction, nPosition,
                    side, nQtd, nAgent, nOfferID, sPrice,
                    bHasPrice, bHasQtd, bHasDate, bHasOfferID, bHasAgent,
                    date,
                    pArraySell, pArrayBuy));

        private void OnOfferBookCallbackCore(
            TAssetID assetId, int nAction, int nPosition,
            int side, long nQtd, int nAgent, long nOfferID, double sPrice,
            ushort bHasPrice, ushort bHasQtd, ushort bHasDate, ushort bHasOfferID, ushort bHasAgent,
            string date,
            IntPtr pArraySell, IntPtr pArrayBuy)
        {
            // ═══════════════════════════════════════════════════════════════
            // CALLBACK ULTRA-LEVE PARA PVV — ~1µs por chamada.
            //
            // O thread de callback da DLL é COMPARTILHADO com trades.
            // Qualquer processamento aqui atrasa entrega de trades.
            // 800 callbacks/s × 50µs = 40ms/s → 144s de atraso em 1h.
            //
            // Solução: callback faz só validate + tracker + enqueue struct.
            // Broker lookup, string formatting e PVV hook rodam no
            // TradeProcessingLoop (DrainPvvBookCandidates).
            // ═══════════════════════════════════════════════════════════════

            // nAction=4 (atFullBook): libera memória nativa, reseta price tracker.
            if (nAction == 4)
            {
                int sellSize = pArraySell != IntPtr.Zero ? Marshal.ReadInt32(pArraySell, 4) : 0;
                int buySize  = pArrayBuy  != IntPtr.Zero ? Marshal.ReadInt32(pArrayBuy, 4)  : 0;

                if (pArraySell != IntPtr.Zero && sellSize > 0)
                    FreePointer(pArraySell, sellSize);
                if (pArrayBuy != IntPtr.Zero && buySize > 0)
                    FreePointer(pArrayBuy, buySize);

                _pvvPriceTracker.Reset();
                return;
            }

            // nAction=2/3 (delete) e desconhecidos: descarta.
            if (nAction != 0 && nAction != 1)
                return;

            // Validar preço e quantidade.
            if (nAction == 0)
            {
                if (bHasQtd == 0 || nQtd <= 0) return;
                if (bHasPrice == 0 || sPrice <= 0 || sPrice > 10_000_000 ||
                    double.IsNaN(sPrice) || double.IsInfinity(sPrice)) return;
            }
            else // nAction == 1
            {
                if (bHasPrice == 0 && bHasQtd == 0) return;
                if (bHasPrice != 0 && (sPrice <= 0 || sPrice > 10_000_000 ||
                    double.IsNaN(sPrice) || double.IsInfinity(sPrice))) return;
                if (bHasQtd == 0 || nQtd <= 0) return;
            }

            // Registrar preço no tracker → rank (1-4), ou 0 se fora do top 4.
            int rank = _pvvPriceTracker.RegisterAndGetRank(side, sPrice);
            if (rank < 1 || rank > 4) return;

            // PVV hook ativo?
            if (PregaoVivaVozHook.OnBookUpdate == null) return;

            // Filtro de agent: sem agent, sem narração.
            int agent = bHasAgent != 0 ? nAgent : 0;
            if (agent <= 0) return;

            // Parse exchange time (leve — sem alocação se bHasDate == 0).
            DateTime? exchangeTime = null;
            if (bHasDate != 0 && TryParseOfferBookDate(date, out DateTime parsedExchange))
                exchangeTime = parsedExchange;

            // Ticker: captura rápido pra struct.
            string ticker = assetId.Ticker ?? Volatile.Read(ref _primaryBookTicker) ?? string.Empty;

            int volume = nQtd > int.MaxValue ? int.MaxValue : (int)nQtd;

            // Enfileira struct leve — broker lookup + PVV hook rodam no TradeProcessingLoop.
            // Guard: descarta se fila já tem >5.000 entries (TTL de 15s descartaria de qualquer forma).
            if (_pvvBookQueue.Count <= 5_000)
                _pvvBookQueue.Enqueue(new PvvBookCandidate(ticker, agent, side, rank, volume, exchangeTime));
            _tradeSignal.Set(); // acorda TradeProcessingLoop para drenar candidatos PVV
        }

        /// <summary>Máximo de linhas lidas de um snapshot <c>atFullBook</c> (array da DLL), não é limite da <c>_bookQueue</c>.</summary>
        private const int OfferBookMaxEntries = 10_000;
        private const int OfferBookMaxDateBytes = 256;
        private const int OfferBookMaxSnapshotBytes = 4 * 1024 * 1024;
        /// <summary>Largura fixa de cada entrada no snapshot <see cref="TOfferBookCallbackV2"/> (<c>atFullBook</c>) — igual ao exemplo <c>MarshalOfferBuffer</c>.</summary>
        private const int OfferBookFullRowStrideV2 = 53;

        /// <summary>Copia o array TOfferBook no callback nativo; o parse pesado roda na thread de livro.</summary>
        private static byte[]? SnapshotOfferBookArray(IntPtr arrayPtr, out int nativeSize)
        {
            nativeSize = 0;
            if (arrayPtr == IntPtr.Zero)
                return null;

            try
            {
                int Q = Marshal.ReadInt32(arrayPtr, 0);
                // Segundo Int32 do header: tamanho do buffer nativo alocado pela DLL
                // (para ser usado em FreePointer conforme manual Nelogica)
                nativeSize = Marshal.ReadInt32(arrayPtr, 4);
                if (Q <= 0)
                {
                    var emptyHeader = new byte[8];
                    BitConverter.TryWriteBytes(emptyHeader.AsSpan(0, 4), 0);
                    return emptyHeader;
                }

                if (Q > OfferBookMaxEntries)
                    Q = OfferBookMaxEntries;

                int offset = 8;
                int parsed = 0;
                for (int i = 0; i < Q; i++)
                {
                    int next = offset + OfferBookFullRowStrideV2;
                    if (next > OfferBookMaxSnapshotBytes)
                        break;

                    offset = next;
                    parsed++;
                }

                if (parsed <= 0 || offset <= 8)
                    return null;

                var buffer = new byte[offset];
                Marshal.Copy(arrayPtr, buffer, 0, offset);
                if (parsed < Q)
                    BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), parsed);

                return buffer;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Converte um lado do snapshot <c>atFullBook</c> (<see cref="TOfferBookCallbackV2"/> stride 53 bytes/linha).</summary>
        private BookLevel[] ParseOfferBookSnapshotToLevels(string ticker, byte[]? snapshot, int side)
        {
            if (snapshot == null || snapshot.Length < 8)
                return Array.Empty<BookLevel>();

            try
            {
                int Q = BitConverter.ToInt32(snapshot, 0);

                if (EnableRawOfferBookLog)
                {
                    lock (_rawLogLock)
                    {
                        if (_rawLogCount < RAW_LOG_MAX)
                            _rawBookLog?.WriteLine($"[FullBook] ticker={ticker} side={side} Q={Q}");
                    }
                }

                if (Q <= 0 || Q > 10_000)
                    return Array.Empty<BookLevel>();

                int headerCount = Math.Min(Q, 10_000);
                var list = new List<BookLevel>(headerCount);
                int offset = 8;
                BookSide bookSide = side == 0 ? BookSide.Bid : BookSide.Ask;

                for (int i = 0; i < headerCount; i++)
                {
                    try
                    {
                        if (offset + OfferBookFullRowStrideV2 > snapshot.Length)
                            break;

                        int rowBase = offset;
                        double price = BitConverter.ToDouble(snapshot, rowBase);
                        long qtd64 = BitConverter.ToInt64(snapshot, rowBase + 8);
                        int agent = BitConverter.ToInt32(snapshot, rowBase + 16);
                        long offerId = BitConverter.ToInt64(snapshot, rowBase + 20);
                        ushort tamData = BitConverter.ToUInt16(snapshot, rowBase + 28);
                        int tailStart = rowBase + 30;
                        int tailMax = OfferBookFullRowStrideV2 - 30;
                        int parseLen = tamData > 0
                            ? Math.Min(Math.Min(OfferBookMaxDateBytes, (int)tamData), tailMax)
                            : tailMax;

                        DateTime? exchangeTime = null;
                        if (parseLen > 0
                            && tailStart + parseLen <= snapshot.Length
                            && TryParseOfferBookDateBytes(snapshot, tailStart, parseLen, out DateTime parsedExchange))
                        {
                            exchangeTime = parsedExchange;
                        }

                        offset += OfferBookFullRowStrideV2;

                        if (!TryPriceToDecimal(price, out decimal priceDecimal))
                            continue;
                        int qtd = qtd64 <= 0 ? 0
                            : qtd64 > int.MaxValue ? int.MaxValue
                            : (int)qtd64;
                        if (qtd <= 0) continue;

                        string broker = FormatBookBroker(agent);
                        if (offerId > 0 && broker.Length > 0)
                        {
                            _offerBrokerCache[offerId] = broker;
                            if (_offerBrokerCache.Count > 10_000)
                            {
                                _offerBrokerCache.Clear();
                                _brokerLastResolveAttemptTicks.Clear();
                            }
                        }

                        list.Add(new BookLevel(
                            Ticker: ticker,
                            Side: bookSide,
                            Price: priceDecimal,
                            Volume: qtd,
                            Broker: broker,
                            Time: DateTime.Now,
                            OfferId: offerId,
                            Action: 5,
                            Position: i,
                            ExchangeTime: exchangeTime,
                            AgentId: agent));
                    }
                    catch { break; }
                }

                if (_fullBookParseLogCount < 20)
                {
                    _fullBookParseLogCount++;
                    _logger.Log($"[FullBook DIAG] side={side} Q={Q} parsed={list.Count} snapshotLen={snapshot.Length}" +
                        (list.Count > 0 ? $" firstPrice={list[0].Price} lastPrice={list[list.Count-1].Price}" : ""));
                }

                return list.ToArray();
            }
            catch (Exception ex)
            {
                _logger.Log($"[FullBook DIAG] EXCEPTION side={side}: {ex.Message}");
                return Array.Empty<BookLevel>();
            }
        }

        private int _fullBookParseLogCount;
        private int _fullBookRouteLogCount;

        private void OnPriceDepthCallback(
            TConnectorAssetIdentifier assetID, byte side, int position, byte updateType)
            => SafeDllCallback(nameof(OnPriceDepthCallback), () => { });

        private void OnCotationCallback(
            TAssetID assetId, string date, uint tradeNumber, double sPrice)
            => SafeDllCallback(nameof(OnCotationCallback), () => OnCotationCallbackCore(assetId, date, tradeNumber, sPrice));

        private void OnCotationCallbackCore(
            TAssetID assetId, string date, uint tradeNumber, double sPrice)
        {
            if (sPrice <= 0 || sPrice > 10_000_000) return;
            try
            {
                OnQuote?.Invoke(new QuoteEvent(
                    Ticker: assetId.Ticker ?? string.Empty,
                    Last:   (decimal)sPrice,
                    Bid: 0, Ask: 0, Open: 0, High: 0, Low: 0,
                    Volume: 0, Time: DateTime.Now
                ));
            }
            catch { }
        }

        private void OnDailyCallback(
            TAssetID assetId, string date,
            double sOpen, double sHigh, double sLow, double sClose,
            double sVol, double sAjuste, double sMaxLimit, double sMinLimit,
            double sVolBuyer, double sVolSeller,
            int nQtd, int nNegocios, int nContratosOpen,
            int nQtdBuyer, int nQtdSeller, int nNegBuyer, int nNegSeller)
            => SafeDllCallback(nameof(OnDailyCallback), () => OnDailyCallbackCore(
                assetId, date, sOpen, sHigh, sLow, sClose, sVol, sAjuste, sMaxLimit, sMinLimit,
                sVolBuyer, sVolSeller,
                nQtd, nNegocios, nContratosOpen, nQtdBuyer, nQtdSeller, nNegBuyer, nNegSeller));

        private void OnDailyCallbackCore(
            TAssetID assetId, string date,
            double sOpen, double sHigh, double sLow, double sClose,
            double sVol, double sAjuste, double sMaxLimit, double sMinLimit,
            double sVolBuyer, double sVolSeller,
            int nQtd, int nNegocios, int nContratosOpen,
            int nQtdBuyer, int nQtdSeller, int nNegBuyer, int nNegSeller)
        {
            try
            {
                OnQuote?.Invoke(new QuoteEvent(
                    Ticker: assetId.Ticker ?? string.Empty,
                    Last:   (decimal)sClose,
                    Bid: 0, Ask: 0,
                    Open:   (decimal)sOpen,
                    High:   (decimal)sHigh,
                    Low:    (decimal)sLow,
                    Volume: nQtd, Time: DateTime.Now
                ));
            }
            catch { }
        }

        private void OnHistoryCallback(
            TAssetID assetId, string date, uint tradeNumber,
            double price, double vol, int qtd,
            int buyAgent, int sellAgent, int tradeType)
            => SafeDllCallback(nameof(OnHistoryCallback), () =>
            {
                string tkr = assetId.Ticker ?? string.Empty;
                string dt = date ?? string.Empty;

                // Tratamentos independentes: um subscritor com erro não pode "engolir"
                // a notificação dos restantes (já vimos isso causar perda de negócios
                // quando o handler legacy lançava antes do Relay ser chamado).
                try
                {
                    OnNativeHistoryTrade?.Invoke(
                        tkr,
                        dt,
                        tradeNumber,
                        price,
                        vol,
                        qtd,
                        buyAgent,
                        sellAgent,
                        tradeType);
                }
                catch { /* legacy event — best effort */ }

                try
                {
                    ProfitHistoryRelay.Raise(
                        tkr,
                        dt,
                        tradeNumber,
                        price,
                        vol,
                        qtd,
                        buyAgent,
                        sellAgent,
                        tradeType);
                }
                catch { /* relay — best effort */ }
            });

        private void OnProgressCallback(TAssetID assetId, int nProgress)
            => SafeDllCallback(nameof(OnProgressCallback), () =>
            {
                try
                {
                    ProfitHistoryRelay.RaiseHistoryProgress(assetId.Ticker, nProgress);
                }
                catch { /* best effort */ }
            });

        private void OnTinyBookCallback(TAssetID assetId, double price, int qtd, int side)
            => SafeDllCallback(nameof(OnTinyBookCallback), () => { });

        #endregion

        #region IMarketDataProvider — Subscrições

        public void Subscribe(string ticker)
        {
            lock (_lock)
            {
                if (_subscribedTickers.Contains(ticker)) return;
                _subscribedTickers.Add(ticker);
                _primaryBookTicker = ticker;
                _logger.Log($"Subscribe agendado: {ticker}");
            }
            if (_initialized) InternalSubscribe(ticker);
        }

        public void Unsubscribe(string ticker)
        {
            lock (_lock)
            {
                if (!_subscribedTickers.Contains(ticker)) return;
                _subscribedTickers.Remove(ticker);
                if (string.Equals(_primaryBookTicker, ticker, StringComparison.OrdinalIgnoreCase))
                    _primaryBookTicker = _subscribedTickers.Count > 0 ? _subscribedTickers[0] : null;
            }
            if (_initialized) InternalUnsubscribe(ticker);
        }

        private void InternalSubscribe(string ticker)
        {
            try
            {
                int r1 = SubscribeTicker(ticker, EXCHANGE_BMF);
                _logger.Log($"SubscribeTicker {ticker}/{EXCHANGE_BMF}: {r1}");
                if (r1 != 0)
                {
                    r1 = SubscribeTicker(ticker, EXCHANGE_BVMF);
                    _logger.Log($"SubscribeTicker {ticker}/{EXCHANGE_BVMF}: {r1}");
                }

                _currentAssetID = new TConnectorAssetIdentifier
                {
                    Version  = 0,
                    Ticker   = ticker,
                    Exchange = EXCHANGE_BMF,
                    FeedType = 0
                };

                // ══ BOOK DESATIVADO — modo análise (só trades) ══
                // SubscribeOfferBook removido: sem dados de book, o BookProcessingLoop
                // fica ocioso, snapshot/detector threads não consomem CPU, e o pipeline
                // de trades opera sem contenção. Para reativar, descomentar o bloco abaixo.
                //
                // int subscribeSeq = Interlocked.Increment(ref _offerBookSubscribeSeq);
                // _ = Task.Run(async () =>
                // {
                //     try
                //     {
                //         await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                //         if (!_processingRunning || !_initialized || _disposed)
                //             return;
                //         if (subscribeSeq != Volatile.Read(ref _offerBookSubscribeSeq))
                //             return;
                //         bool stillSubscribed;
                //         lock (_lock)
                //             stillSubscribed = _subscribedTickers.Contains(ticker);
                //         if (!stillSubscribed)
                //             return;
                //         _logger.Log($"SubscribeOfferBook a iniciar: {ticker}/{EXCHANGE_BMF}");
                //         int r2 = SubscribeOfferBook(ticker, EXCHANGE_BMF);
                //         _logger.Log($"SubscribeOfferBook {ticker}/{EXCHANGE_BMF}: {r2}");
                //         if (r2 != 0)
                //         {
                //             r2 = SubscribeOfferBook(ticker, EXCHANGE_BVMF);
                //             _logger.Log($"SubscribeOfferBook {ticker}/{EXCHANGE_BVMF} (fallback): {r2}");
                //         }
                //     }
                //     catch (Exception ex)
                //     {
                //         _logger.Log($"✗ InternalSubscribe offer book {ticker}: {ex.Message}");
                //     }
                // });
                _logger.Log($"[MODO ANÁLISE] SubscribeOfferBook DESATIVADO para {ticker} — só trades ativos");
            }
            catch (Exception ex)
            {
                _logger.Log($"✗ InternalSubscribe {ticker}: {ex.Message}");
            }
        }

        private void InternalUnsubscribe(string ticker)
        {
            Interlocked.Increment(ref _offerBookSubscribeSeq);
            try
            {
                UnsubscribeTicker(ticker, EXCHANGE_BMF);
                UnsubscribeTicker(ticker, EXCHANGE_BVMF);
                UnsubscribeOfferBook(ticker, EXCHANGE_BMF);
                UnsubscribeOfferBook(ticker, EXCHANGE_BVMF);
                UnsubscribePriceDepth(_currentAssetID);
                _logger.Log($"Unsubscribe {ticker} OK");
            }
            catch (Exception ex)
            {
                _logger.Log($"✗ InternalUnsubscribe {ticker}: {ex.Message}");
            }
        }

        #endregion

        #region Utilitários

        private void SetStatus(ConnectionStatus status, string message)
        {
            Status = status;
            _logger.Log($"[Status] {status}: {message}");
            try
            {
                OnConnectionChanged?.Invoke(new ConnectionChangedEvent(status, message));
            }
            catch (Exception ex)
            {
                try { _logger.Log($"✗ OnConnectionChanged subscriber: {ex.Message}"); } catch { /* ignore */ }
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _tradeSignal.Set(); // acorda thread para que saia do Wait antes do Join
            StopProcessingThread();
            _tradeSignal.Dispose();
            lock (_lock)
            {
                foreach (var ticker in _subscribedTickers.ToArray())
                    InternalUnsubscribe(ticker);
                _initialized      = false;
                _readyToSubscribe = false;
            }

            lock (_rawLogLock)
            {
                _rawBookLog?.WriteLine($"=== DISPOSE — {DateTime.Now:HH:mm:ss} — {_rawLogCount} eventos ===");
                _rawBookLog?.Close();
                _rawBookLog = null;
            }

            _logger?.Dispose();
            GC.SuppressFinalize(this);
        }

        ~ProfitDLLProvider() => Dispose();

        #endregion
    }
}