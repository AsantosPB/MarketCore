using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MarketCore.Providers.Nelogica;   // PvvDebugFileLog

namespace MarketCore.WPF.Services.PregaoVivaVoz
{
    /// <summary>
    /// Ponte entre os callbacks reais da ProfitDLL (chegam pelo MarketCore)
    /// e o motor do Pregão Viva Voz.
    ///
    /// PADRÃO NÃO-BLOQUEANTE (Objetivo 1):
    /// - O hook do provider entra aqui na thread do TradeProcessingLoop.
    ///   Qualquer operação pesada nessa thread trava o resto do MarketCore
    ///   (delta, tape, book, etc) → acúmulo de atraso de vários MINUTOS
    ///   entre a bolsa e a narração.
    /// - Solução: OnTradeReceived/OnBookUpdate apenas enfileiram em um
    ///   Channel bounded (DropOldest) e retornam em microsegundos.
    /// - Uma Task worker drena o channel e chama o Engine em thread separada.
    /// - Se o worker cair atrás (fila cheia), os eventos MAIS ANTIGOS são
    ///   descartados — a bolsa não espera; melhor perder narração velha do
    ///   que travar tudo.
    ///
    /// callbackInfo é uma string pré-formatada (ex: "TRADE bolsa=17:20:04.987
    /// ticker=WINFUT buy=XP sell=IDEAL qtd=1 tradeType=2") que viaja pareada
    /// com o evento até o log de narração — garante correlação perfeita mesmo
    /// com muitos callbacks concorrentes.
    ///
    /// exchangeTime (UTC) é o timestamp do evento na bolsa — usado pelo
    /// agregador CASO 1 do Engine para agrupar callbacks do MESMO milissegundo.
    /// </summary>
    public class ProfitDLLBridge : IDisposable
    {
        private readonly PregaoVivaVozEngine _engine;

        // Whitelist de símbolos contínuos + prefixos de contratos específicos.
        //
        // Histórico: a whitelist original aceitava SÓ "WINFUT" porque a Nelogica
        // pode entregar o MESMO trade sob dois tickers (contínuo + contrato do mês)
        // QUANDO o cliente subscreve ambos. Como o MarketCore subscreve UM ticker de
        // cada vez via TxPrimaryTicker do MainWindow (ex: "WINQ26"), aceitar apenas
        // WINFUT fazia com que TODOS os trades do contrato específico caíssem no
        // EventosDescartados_AtivoErrado — PVV nunca processava nada.
        //
        // Fix: aceita WINFUT/WDOFUT explicitamente E qualquer contrato com prefixo
        // WIN* ou WDO* (mini-índice / mini-dólar). Ver EhAtivoAceito abaixo.
        // A duplicação continua sendo prevenida no nível de subscrição — o usuário
        // subscreve um único ticker de cada vez, então o provider só dispara um
        // evento por trade.
        private static readonly HashSet<string> TickersAceitos = new(StringComparer.OrdinalIgnoreCase)
        {
            "WINFUT",
            "WDOFUT"
        };

        // Estatísticas (opcional, útil pra debug)
        public long EventosRecebidos { get; private set; }
        public long EventosDescartados_MotorParado { get; private set; }
        public long EventosDescartados_AtivoErrado { get; private set; }
        public long EventosDescartados_FilaCheia { get; private set; }
        public long EventosEnviadosAoEngine { get; private set; }

        // [PVV-BRIDGE-DEBUG] Contador para rate-limit dos logs de diagnóstico.
        private long _bridgeInLogCount;

        // ============ FILA ASSÍNCRONA (Objetivo 1) ============

        /// <summary>
        /// Capacidade máxima da fila de eventos aguardando processamento.
        /// Se estourar (worker lento ou pico enorme), os mais antigos são
        /// descartados — bolsa não espera.
        /// Dimensionado para pico de ~2s @ 2000 evt/s.
        /// </summary>
        private const int FilaCapacidade = 4096;

        private readonly Channel<EventoFila> _fila;
        private readonly CancellationTokenSource _cts = new();
        private Task? _workerTask;

        // Log bruto de callbacks — TODOS os callbacks que chegam no Bridge, com timestamp.
        // Permite correlação independente com o log de narrações.
        //
        // OTIMIZAÇÃO: buffer em memória + flush periódico. Antes, cada callback fazia
        // File.AppendAllText (2× por evento: aqui + UnifiedLog), ou seja 400+ I/O ops/sec
        // no mesmo thread que processa o book. Agora enfileira e faz flush a cada ~500ms
        // ou 100 linhas, o que vier primeiro.
        //
        // OBJETIVO 1: com o Bridge assíncrono, o AppendCallbackLog roda no worker,
        // não mais na thread do TradeProcessingLoop — remove esse I/O do caminho crítico.
        private static readonly string CallbacksLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarketCore",
            "pregao_viva_voz_callbacks.log");

        private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _callbackLogBuffer = new();
        private static volatile int _callbackLogCount;
        private static long _lastCallbackFlushTicks = DateTime.UtcNow.Ticks;
        private static readonly object _callbackFlushGate = new();
        private const int CallbackLogFlushThreshold = 100;
        private const long CallbackLogFlushIntervalTicks = 5_000_000; // 500ms

        private static void AppendCallbackLog(string linha)
        {
            string linhaFinal = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {linha}";
            _callbackLogBuffer.Enqueue(linhaFinal);
            int count = Interlocked.Increment(ref _callbackLogCount);

            long now = DateTime.UtcNow.Ticks;
            bool shouldFlush = count >= CallbackLogFlushThreshold
                || (now - Volatile.Read(ref _lastCallbackFlushTicks)) > CallbackLogFlushIntervalTicks;

            if (shouldFlush)
                FlushCallbackLog();
        }

        private static void FlushCallbackLog()
        {
            if (!Monitor.TryEnter(_callbackFlushGate))
                return; // outro thread já está flushing
            try
            {
                var sb = new System.Text.StringBuilder(4096);
                while (_callbackLogBuffer.TryDequeue(out var line))
                {
                    Interlocked.Decrement(ref _callbackLogCount);
                    sb.AppendLine(line);
                }
                if (sb.Length == 0) return;

                string content = sb.ToString();
                Volatile.Write(ref _lastCallbackFlushTicks, DateTime.UtcNow.Ticks);

                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(CallbacksLogPath);
                        if (!string.IsNullOrEmpty(dir))
                            Directory.CreateDirectory(dir);
                        File.AppendAllText(CallbacksLogPath, content);
                    }
                    catch { /* best effort */ }

                    try
                    {
                        PregaoVivaVozUnifiedLog.AppendBatch("CALLBACK", content);
                    }
                    catch { /* best effort */ }
                });
            }
            finally
            {
                Monitor.Exit(_callbackFlushGate);
            }
        }

        public ProfitDLLBridge(PregaoVivaVozEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));

            // Bounded channel com DropOldest — nunca bloqueia o writer (a DLL).
            var opts = new BoundedChannelOptions(FilaCapacidade)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            };
            _fila = Channel.CreateBounded<EventoFila>(opts);

            _workerTask = Task.Run(() => WorkerLoop(_cts.Token));

            // Heartbeat: a cada 5s escreve as estatísticas no pvv_debug.txt para
            // provar que o worker está vivo mesmo quando não chegam eventos.
            _heartbeatTask = Task.Run(() => HeartbeatLoop(_cts.Token));

            PvvDebugFileLog.Write($"[BRIDGE] Construtor OK — worker + heartbeat iniciados. Capacidade fila={FilaCapacidade}. Arquivo: {PvvDebugFileLog.FilePath}");
            Console.WriteLine("[ProfitDLLBridge] Bridge inicializado (assíncrono, capacidade " + FilaCapacidade + "). Aguardando eventos da ProfitDLL...");
        }

        private Task? _heartbeatTask;

        private async Task HeartbeatLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                    PvvDebugFileLog.Write(
                        $"[BRIDGE-HB] recebidos={EventosRecebidos} " +
                        $"enviados={EventosEnviadosAoEngine} " +
                        $"desc_motor={EventosDescartados_MotorParado} " +
                        $"desc_ativo={EventosDescartados_AtivoErrado} " +
                        $"desc_fila={EventosDescartados_FilaCheia} " +
                        $"motor_ativo={_engine.MotorAtivo}");
                }
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception ex) { PvvDebugFileLog.Write($"[BRIDGE-HB] morreu: {ex.Message}"); }
        }

        /// <summary>
        /// Chamado pelo MarketCore no callback de trade real (NewTradeCallback).
        /// APENAS ENFILEIRA — retorna em microsegundos, não bloqueia a thread do provider.
        /// </summary>
        public void OnTradeReceived(string ticker, string buyAgentName, string sellAgentName, int qtd, int tradeType, string callbackInfo, DateTime? exchangeTime)
        {
            EventosRecebidos++;

            // [PVV-DIAG] Log rate-limited na entrada do Bridge — primeiras 20 + 1/500.
            if (EventosRecebidos <= 20 || (EventosRecebidos % 500) == 0)
            {
                PvvDebugFileLog.Write($"[BRIDGE-IN] Trade #{EventosRecebidos}: ticker={ticker} qtd={qtd} buy={buyAgentName} sell={sellAgentName} tt={tradeType}");
            }

            var evento = new EventoFila
            {
                Tipo = TipoEventoFila.Trade,
                Ticker = ticker,
                BuyAgent = buyAgentName,
                SellAgent = sellAgentName,
                Lado = null,
                Qtd = qtd,
                Nivel = 0,
                TradeType = tradeType,
                CallbackInfo = callbackInfo,
                ExchangeTime = exchangeTime
            };

            // TryWrite nunca bloqueia com DropOldest — no pior caso descarta o mais velho.
            if (!_fila.Writer.TryWrite(evento))
            {
                EventosDescartados_FilaCheia++;
                if (EventosDescartados_FilaCheia <= 5 || (EventosDescartados_FilaCheia % 500) == 0)
                    PvvDebugFileLog.Write($"[BRIDGE-IN] TryWrite FALHOU (fila cheia?) total={EventosDescartados_FilaCheia}");
            }
        }

        /// <summary>
        /// Chamado pelo MarketCore no callback de book (OfferBookCallback).
        /// APENAS ENFILEIRA — retorna em microsegundos.
        /// </summary>
        public void OnBookUpdate(string ticker, string agentName, string lado, int nivel, int qtd, string callbackInfo, DateTime? exchangeTime)
        {
            EventosRecebidos++;

            var evento = new EventoFila
            {
                Tipo = TipoEventoFila.Book,
                Ticker = ticker,
                BuyAgent = agentName,   // reuso: BuyAgent carrega o agent do book
                SellAgent = null,
                Lado = lado,
                Qtd = qtd,
                Nivel = nivel,
                TradeType = 0,
                CallbackInfo = callbackInfo,
                ExchangeTime = exchangeTime
            };

            if (!_fila.Writer.TryWrite(evento))
            {
                EventosDescartados_FilaCheia++;
            }
        }

        /// <summary>
        /// Worker que drena a fila e chama o engine — tudo em thread separada
        /// da DLL. Aqui roda log de callback, filtro por ticker/motor, e
        /// processamento no engine.
        /// </summary>
        private async Task WorkerLoop(CancellationToken token)
        {
            var reader = _fila.Reader;
            try
            {
                while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var evento))
                    {
                        try
                        {
                            ProcessarEvento(evento);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ProfitDLLBridge] Erro no worker: {ex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* normal on shutdown */ }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProfitDLLBridge] Worker morreu: {ex.Message}");
            }
        }

        private void ProcessarEvento(EventoFila e)
        {
            // Log bruto de callback — antes bloqueava a DLL, agora é neutro (roda no worker).
            AppendCallbackLog(e.CallbackInfo ?? string.Empty);

            // [PVV-DIAG] Log de entrada no worker (primeiras 20 + 1 a cada 500).
            long inN = System.Threading.Interlocked.Increment(ref _bridgeInLogCount);
            bool inLog = inN <= 20 || (inN % 500) == 0;
            if (inLog)
                PvvDebugFileLog.Write($"[BRIDGE-WORKER] dequeueou #{inN}: tipo={e.Tipo} ticker={e.Ticker} qtd={e.Qtd} motorAtivo={_engine.MotorAtivo}");

            // FILTRO 1: motor parado? ignora
            if (!_engine.MotorAtivo)
            {
                EventosDescartados_MotorParado++;
                if (EventosDescartados_MotorParado <= 5 || (EventosDescartados_MotorParado % 500) == 0)
                    PvvDebugFileLog.Write($"[BRIDGE-WORKER] DESCARTADO (motor parado) total={EventosDescartados_MotorParado} ticker={e.Ticker}");
                return;
            }

            // FILTRO 2: ativo errado? ignora
            if (!EhAtivoAceito(e.Ticker))
            {
                EventosDescartados_AtivoErrado++;
                if (EventosDescartados_AtivoErrado <= 5 || (EventosDescartados_AtivoErrado % 500) == 0)
                    PvvDebugFileLog.Write($"[BRIDGE-WORKER] DESCARTADO (ticker '{e.Ticker}' fora do filtro WIN*/WDO*) total={EventosDescartados_AtivoErrado}");
                return;
            }

            if (e.Tipo == TipoEventoFila.Trade)
            {
                // FIX v3 (reentrância): Provider agora passa APENAS o agentId como
                // string numérica. A resolução para nome ("GOLDMAN", "JPM"…) acontece
                // AQUI, no worker do Bridge, que roda em Task.Run — thread separada
                // da DLL, segura para chamar GetAgentName sem risco de deadlock.
                //
                // Usa o resolver estático registrado pelo ProfitDLLProvider no seu
                // construtor. Se não estiver registrado (ex: provider Simulator),
                // devolve o próprio ID numérico e o Engine trata como "sem match".
                var resolver = MarketCore.Providers.Nelogica.PregaoVivaVozHook.ResolveAgentName;
                if (resolver != null)
                {
                    if (int.TryParse(e.BuyAgent, out var buyId) && buyId > 0)
                        e.BuyAgent = resolver(buyId);
                    if (int.TryParse(e.SellAgent, out var sellId) && sellId > 0)
                        e.SellAgent = resolver(sellId);
                }

                // tradeType 1 = agressor comprou (tomou o ask) → nome do agressor = buyAgent
                // tradeType 2 = agressor vendeu (bateu no bid) → nome do agressor = sellAgent
                string nomeAgressor;
                string lado;
                if (e.TradeType == 1)
                {
                    nomeAgressor = e.BuyAgent ?? string.Empty;
                    lado = "compra";
                }
                else if (e.TradeType == 2)
                {
                    nomeAgressor = e.SellAgent ?? string.Empty;
                    lado = "venda";
                }
                else return;

                if (string.IsNullOrWhiteSpace(nomeAgressor)) return;

                _engine.ProcessarAgressao(nomeAgressor, lado, e.Qtd, e.CallbackInfo, e.ExchangeTime);
                EventosEnviadosAoEngine++;
                if (EventosEnviadosAoEngine <= 20 || (EventosEnviadosAoEngine % 500) == 0)
                    PvvDebugFileLog.Write($"[BRIDGE-WORKER] → Engine.ProcessarAgressao #{EventosEnviadosAoEngine}: nome={nomeAgressor} lado={lado} qtd={e.Qtd}");
            }
            else // Book
            {
                if (string.IsNullOrWhiteSpace(e.BuyAgent)) return;
                if (e.Lado != "compra" && e.Lado != "venda") return;

                _engine.ProcessarBook(e.BuyAgent!, e.Lado!, e.Nivel, e.Qtd, e.CallbackInfo, e.ExchangeTime);
                EventosEnviadosAoEngine++;
                if (EventosEnviadosAoEngine <= 20 || (EventosEnviadosAoEngine % 500) == 0)
                    PvvDebugFileLog.Write($"[BRIDGE-WORKER] → Engine.ProcessarBook #{EventosEnviadosAoEngine}: nome={e.BuyAgent} lado={e.Lado} niv={e.Nivel} qtd={e.Qtd}");
            }
        }

        /// <summary>
        /// Alternativa: se o MarketCore preferir mandar trade puro (sem agressor identificado).
        /// Útil pra tickers de agressão total sem detalhe.
        /// </summary>
        public void OnTradeGenerico(string ticker, string agentName, string lado, int qtd)
        {
            EventosRecebidos++;

            if (!_engine.MotorAtivo)
            {
                EventosDescartados_MotorParado++;
                return;
            }

            if (!EhAtivoAceito(ticker))
            {
                EventosDescartados_AtivoErrado++;
                return;
            }

            if (string.IsNullOrWhiteSpace(agentName)) return;

            _engine.ProcessarTrade(agentName, lado, qtd);
            EventosEnviadosAoEngine++;
        }

        /// <summary>
        /// Verifica se o ticker é aceito pelo PVV.
        /// Aceita:
        ///   - Símbolos contínuos exatos: WINFUT / WDOFUT
        ///   - Contratos específicos por prefixo: WIN* (WINQ26, WINV26 …) e WDO* (WDOU26 …)
        /// Fix relativo à whitelist antiga (WINFUT-only) que descartava todo trade
        /// quando o usuário subscrevia o contrato do mês (WINQ26 etc.).
        /// </summary>
        private bool EhAtivoAceito(string? ticker)
        {
            if (string.IsNullOrWhiteSpace(ticker)) return false;
            if (TickersAceitos.Contains(ticker)) return true;
            return ticker.StartsWith("WIN", StringComparison.OrdinalIgnoreCase)
                || ticker.StartsWith("WDO", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Retorna estatísticas de uso do bridge (útil pra debug).
        /// </summary>
        public string ObterEstatisticas()
        {
            return $"Bridge stats: recebidos={EventosRecebidos}, " +
                   $"descartados_motor_parado={EventosDescartados_MotorParado}, " +
                   $"descartados_ativo_errado={EventosDescartados_AtivoErrado}, " +
                   $"descartados_fila_cheia={EventosDescartados_FilaCheia}, " +
                   $"enviados_engine={EventosEnviadosAoEngine}";
        }

        /// <summary>
        /// Reseta contadores de estatísticas.
        /// </summary>
        public void ResetarEstatisticas()
        {
            EventosRecebidos = 0;
            EventosDescartados_MotorParado = 0;
            EventosDescartados_AtivoErrado = 0;
            EventosDescartados_FilaCheia = 0;
            EventosEnviadosAoEngine = 0;
        }

        public void Dispose()
        {
            try
            {
                _fila.Writer.TryComplete();
                _cts.Cancel();
                _workerTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch { /* best effort */ }
            _cts.Dispose();
        }

        // ============ TIPOS INTERNOS DA FILA ============

        private enum TipoEventoFila { Trade, Book }

        private class EventoFila
        {
            public TipoEventoFila Tipo;
            public string? Ticker;
            public string? BuyAgent;
            public string? SellAgent;
            public string? Lado;
            public int Qtd;
            public int Nivel;
            public int TradeType;
            public string? CallbackInfo;
            public DateTime? ExchangeTime;
        }
    }
}
