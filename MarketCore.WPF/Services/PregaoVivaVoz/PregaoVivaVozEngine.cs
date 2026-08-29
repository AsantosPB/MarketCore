using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MarketCore.Providers.Nelogica;   // PvvDebugFileLog
using MarketCore.WPF.Models.PregaoVivaVoz;

namespace MarketCore.WPF.Services.PregaoVivaVoz
{
    /// <summary>
    /// MOTOR PRINCIPAL do Pregão Viva Voz.
    ///
    /// PADRÃO: Event Bridge assíncrono (Objetivo 1).
    /// - Callbacks entram por ProcessarAgressao/ProcessarBook e são APENAS
    ///   enfileirados. Um worker task drena a fila e faz todo o trabalho.
    /// - O caller (Bridge, que já é assíncrono) nunca é bloqueado pela
    ///   aggregação, narração ou I/O de log.
    /// - Se o worker cair atrás, o Bridge dropa velhos antes de chegar aqui;
    ///   este canal interno tem folga curta para absorver microbursts.
    ///
    /// AGREGAÇÃO:
    /// CASO 1 — Mesmo milissegundo exato do TIMESTAMP DE BOLSA (Objetivo 2):
    ///          callbacks do mesmo broker+direção cujo timestamp exchangeTime
    ///          (segundo + milissegundo) é IDÊNTICO são acumulados. Ao fechar
    ///          o bloco, verifica filtro por player e narra "bateu/tomou [total]"
    ///          uma única vez via FraseBuilderService. O total é TAMBÉM alimentado
    ///          no DetectorRajadaService (via RegistrarAgressao) para players com
    ///          Rajada.Participa=true, passando o VolumeMinimo do próprio player
    ///          (Objetivo 3).
    ///
    /// CASO 2 — Timestamps diferentes: tratado INTEIRAMENTE pelo
    ///          DetectorRajadaService, que recebe os totais de bloco via
    ///          RegistrarAgressao e dispara RajadaIniciada / RajadaParou.
    ///          O Engine apenas escuta esses eventos e narra "tomando/batendo"
    ///          e "parou de tomar/bater". Nenhuma lógica de rajada aqui.
    ///
    /// FALLBACK: se um callback vem SEM exchangeTime (DateTime? null), o
    /// agregador usa DateTime.UtcNow como aproximação — evita perder eventos
    /// quando a DLL ocasionalmente omite bHasDate.
    /// </summary>
    public class PregaoVivaVozEngine : IDisposable
    {
        private readonly Dictionary<string, PlayerConfig> _playersConfig = new();
        private readonly ConfigRajadaGlobal _configRajada;
        private readonly FraseBuilderService _fraseBuilder;
        private readonly AudioPlaybackService _audioPlayback;
        private readonly DetectorRajadaService _detectorRajada;

        private bool _iniciado = false;
        private bool _pausado = false;

        // Estatísticas
        private int _eventosProcessados = 0;
        private int _eventosNarrados = 0;
        private int _rajadasDetectadas = 0;
        private long _eventosDescartados_FilaCheia = 0;

        // [PVV-DIAG] Contadores para rate-limit dos logs de diagnóstico.
        private long _diagEngineRecebidos;
        private long _diagEngineProcessados;

        // ============ FILA INTERNA ASSÍNCRONA (Objetivo 1) ============

        /// <summary>
        /// Capacidade da fila interna do engine. Absorve microbursts entre
        /// o Bridge e o worker do engine. Menor que a fila do Bridge — ele
        /// já protege contra picos maiores.
        /// </summary>
        private const int FilaInternaCapacidade = 2048;

        // Não-readonly: pode ser recriado pelo WorkerLoop se completado inesperadamente.
        private Channel<EventoInterno> _filaInterna = null!;
        private readonly CancellationTokenSource _cts = new();
        private Task? _workerTask;

        /// <summary>
        /// Flag ativa durante narração prioritária de book. Quando <c>true</c>,
        /// o WorkerLoop descarta eventos normais sem processá-los — o áudio já foi
        /// limpo por <see cref="AudioPlaybackService.NarrarComPrioridade"/>.
        /// <c>volatile</c> garante visibilidade imediata entre threads sem lock.
        /// </summary>
        private volatile bool _descartarFilaAtePrioritariaTerminar = false;

        /// <summary>
        /// TTL máximo de um evento de mercado. Eventos com <c>ExchangeTime</c>
        /// mais antigo que isso são descartados silenciosamente — o momento de
        /// mercado já passou e a narração não teria valor para o trader.
        /// Aplica-se APENAS quando <c>ExchangeTime</c> é não-null.
        /// Eventos sem timestamp (null) são sempre processados como fallback seguro.
        /// Narração prioritária nunca é afetada (eventos de book chegam com
        /// ExchangeTime=null — a verificação é no-op para eles).
        /// </summary>
        private static readonly TimeSpan TtlMaximo = TimeSpan.FromSeconds(15);

        // ============ AGREGADOR DE BLOCOS (CASO 1) ============

        /// <summary>
        /// Estado de agregação por player+lado. Chave: "playerChave|lado".
        /// </summary>
        private readonly ConcurrentDictionary<string, EstadoAgressao> _estadosAgressao = new();

        /// <summary>
        /// Timer que faz polling a cada 20ms para fechar blocos cujo milissegundo
        /// de bolsa já ficou no passado (ms atual > ms do bloco).
        /// Executa no ThreadPool — não bloqueia a thread da DLL.
        /// </summary>
        private Timer? _agregadorTimer;

        /// <summary>
        /// Estado de agregação por player+direção.
        /// Rastreia APENAS bloco de mesmo milissegundo (CASO 1).
        /// CASO 2 (rajada) é tratado inteiramente pelo DetectorRajadaService.
        /// </summary>
        private class EstadoAgressao
        {
            public string PlayerChave = "";
            public string PlayerNome = "";
            public string Lado = ""; // "compra" ou "venda"

            /// <summary>Volume acumulado do bloco aberto.</summary>
            public int VolumeBloco;
            /// <summary>
            /// Milissegundo do bloco aberto — derivado do TIMESTAMP DE BOLSA
            /// (exchangeTime.Ticks / TicksPerMillisecond) quando disponível,
            /// ou de UtcNow como fallback. Callbacks com o MESMO valor são
            /// agregados; valor diferente = bloco novo.
            /// </summary>
            public long MsBloco;
            /// <summary>Se há um bloco aberto aguardando mais callbacks.</summary>
            public bool BlocoAberto;
            /// <summary>
            /// DateTime.UtcNow.Ticks no instante em que o ÚLTIMO callback foi
            /// agregado a este bloco. Base para o critério de "silêncio" do
            /// PollAgregador — o bloco só fecha depois de <c>CarenciaFechamentoTicks</c>
            /// sem novo callback. Antes o critério comparava agora vs. MsBloco
            /// (timestamp da bolsa), que já era passado no instante de abertura
            /// → o timer fechava o bloco antes dos late arrivals do mesmo ms.
            /// </summary>
            public long UltimoCallbackTicks;
        }

        // ============ EVENTOS PÚBLICOS ============

        public event EventHandler<NarracaoInfo>? EventoNarrado;
        public event EventHandler<string>? EstatisticasAtualizadas;

        // ============ PROPRIEDADES ============

        public bool Pausado
        {
            get => _pausado;
            set
            {
                _pausado = value;
                _audioPlayback.Pausado = value;
                if (value)
                {
                    _audioPlayback.LimparFila();
                    _estadosAgressao.Clear();
                }
            }
        }

        public bool MotorAtivo => _iniciado && !_pausado;

        public int VolumeMaster
        {
            get => _audioPlayback.VolumeMaster;
            set => _audioPlayback.VolumeMaster = value;
        }

        public int EventosProcessados => _eventosProcessados;
        public int EventosNarrados => _eventosNarrados;
        public int RajadasDetectadas => _rajadasDetectadas;
        public long EventosDescartados_FilaCheia => Interlocked.Read(ref _eventosDescartados_FilaCheia);

        // ============ CONSTRUTOR ============

        public PregaoVivaVozEngine(
            List<PlayerConfig> players,
            ConfigRajadaGlobal configRajada,
            string diretorioAudio)
        {
            _configRajada = configRajada ?? new ConfigRajadaGlobal();

            foreach (var p in players)
            {
                _playersConfig[p.Chave.ToLower()] = p;
            }

            _fraseBuilder = new FraseBuilderService(diretorioAudio);
            _audioPlayback = new AudioPlaybackService();
            _detectorRajada = new DetectorRajadaService(_configRajada);

            _detectorRajada.RajadaIniciada += OnRajadaIniciada;
            _detectorRajada.RajadaParou += OnRajadaParou;

            _audioPlayback.ItemReproduzido += (s, info) => EventoNarrado?.Invoke(this, info);

            // Fila interna do engine — DropOldest evita bloqueio no caller.
            var opts = new BoundedChannelOptions(FilaInternaCapacidade)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            };
            _filaInterna = Channel.CreateBounded<EventoInterno>(opts);
        }

        // ============ CONTROLE ============

        public void Iniciar()
        {
            if (_iniciado) return;
            _iniciado = true;
            _pausado = false;

            _audioPlayback.Iniciar();
            _detectorRajada.Iniciar();

            _workerTask = Task.Run(() => WorkerLoop(_cts.Token));

            _agregadorTimer = new Timer(PollAgregador, null, 20, 20);

            int ativos = 0;
            foreach (var p in _playersConfig.Values) if (p.AtivoHoje) ativos++;
            PvvDebugFileLog.Write($"[ENGINE] Iniciar() OK — worker + timer + detector iniciados. players={_playersConfig.Count} (ativoHoje={ativos})");
            if (ativos == 0)
                PvvDebugFileLog.Write($"[ENGINE] ⚠ NENHUM PLAYER ESTÁ COM ativoHoje=true — nenhuma narração vai ocorrer. Marque players na aba 'Players que estou monitorando' e Salve.");

            Console.WriteLine($"[PregaoVivaVozEngine] Motor iniciado (assíncrono) com {_playersConfig.Count} players");
            EstatisticasAtualizadas?.Invoke(this, $"Motor iniciado · {_playersConfig.Count} players");
        }

        public void Parar()
        {
            _iniciado = false;
            _pausado = true;
            _audioPlayback.LimparFila();
            _agregadorTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _estadosAgressao.Clear();

            try
            {
                // ORDEM CRÍTICA: cancelar o CTS PRIMEIRO para que o WorkerLoop saia via
                // OperationCanceledException (saída limpa). TryComplete() depois — belt+suspenders.
                _cts.Cancel();
                _filaInterna.Writer.TryComplete();
                _workerTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch { /* best effort */ }

            Console.WriteLine("[PregaoVivaVozEngine] Motor parado");
        }

        public void AtualizarPlayer(PlayerConfig player)
        {
            if (player == null) return;
            _playersConfig[player.Chave.ToLower()] = player;
        }

        // ============ EVENT BRIDGE - MÉTODOS PÚBLICOS ============
        // APENAS ENFILEIRAM. Retornam em microsegundos. Trabalho pesado no worker.

        /// <summary>
        /// PROCESSA UM TRADE (agressão). Apenas enfileira — retorna imediatamente.
        /// O agregador usa o <paramref name="exchangeTime"/> para agrupar
        /// callbacks do MESMO milissegundo de bolsa (CASO 1). Se ausente,
        /// cai em fallback usando UtcNow.
        /// </summary>
        public void ProcessarAgressao(string nomeCorretora, string lado, int quantidade, string? callbackInfo = null, DateTime? exchangeTime = null)
        {
            // [PVV-DIAG] Primeira chamada + rate-limited depois. Confirma que o Bridge
            // realmente está chamando o Engine (não é só o Bridge que tá "aparentemente" ok).
            long recN = Interlocked.Increment(ref _diagEngineRecebidos);
            if (recN <= 20 || (recN % 500) == 0)
                PvvDebugFileLog.Write($"[ENGINE-IN] ProcessarAgressao #{recN}: nome={nomeCorretora} lado={lado} qtd={quantidade} pausado={_pausado} iniciado={_iniciado}");

            if (_pausado || string.IsNullOrEmpty(nomeCorretora))
            {
                if (recN <= 5)
                    PvvDebugFileLog.Write($"[ENGINE-IN] SAINDO cedo: pausado={_pausado} nomeVazio={string.IsNullOrEmpty(nomeCorretora)}");
                return;
            }

            if (!_filaInterna.Writer.TryWrite(new EventoInterno
            {
                Tipo = TipoEventoInterno.Agressao,
                NomeCorretora = nomeCorretora,
                Lado = lado,
                Quantidade = quantidade,
                CallbackInfo = callbackInfo,
                ExchangeTime = exchangeTime,
                Nivel = 0
            }))
            {
                Interlocked.Increment(ref _eventosDescartados_FilaCheia);
                if (_eventosDescartados_FilaCheia <= 5 || (_eventosDescartados_FilaCheia % 500) == 0)
                    PvvDebugFileLog.Write($"[ENGINE-IN] TryWrite falhou (fila cheia?) total={_eventosDescartados_FilaCheia}");
            }
        }

        /// <summary>
        /// PROCESSA UMA ORDEM NO BOOK (ordem passiva). Apenas enfileira.
        /// </summary>
        public void ProcessarBook(string nomeCorretora, string lado, int nivel, int quantidade, string? callbackInfo = null, DateTime? exchangeTime = null)
        {
            if (_pausado || string.IsNullOrEmpty(nomeCorretora)) return;

            if (!_filaInterna.Writer.TryWrite(new EventoInterno
            {
                Tipo = TipoEventoInterno.Book,
                NomeCorretora = nomeCorretora,
                Lado = lado,
                Quantidade = quantidade,
                CallbackInfo = callbackInfo,
                ExchangeTime = exchangeTime,
                Nivel = nivel
            }))
            {
                Interlocked.Increment(ref _eventosDescartados_FilaCheia);
            }
        }

        public void ProcessarTrade(string nomeCorretora, string lado, int quantidade)
        {
            ProcessarAgressao(nomeCorretora, lado, quantidade);
        }

        // ============ WORKER ============

        private async Task WorkerLoop(CancellationToken ct)
        {
            PvvDebugFileLog.Write("[ENGINE-WORKER] WorkerLoop INICIADO");

            // Loop externo — NUNCA sai enquanto o motor estiver ativo.
            // Única saída legítima: OperationCanceledException (motor sendo parado).
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Loop interno — drena o channel até completar ou cancelar.
                    await foreach (var e in _filaInterna.Reader.ReadAllAsync(ct))
                    {
                        try
                        {
                            if (_pausado) continue;

                            // Modo prioritário: descarta eventos normais enquanto
                            // NarrarEventoComPrioridade está sendo enfileirado no áudio.
                            if (_descartarFilaAtePrioritariaTerminar)
                            {
                                PvvDebugFileLog.Write("[ENGINE-WORKER] descartando evento (modo prioritário ativo)");
                                continue;
                            }

                            if (e.Tipo == TipoEventoInterno.Agressao)
                                ProcessarAgressaoInterno(e);
                            else
                                ProcessarBookInterno(e);
                        }
                        catch (Exception exEvento)
                        {
                            PvvDebugFileLog.Write($"[ENGINE-WORKER] EXCEÇÃO no evento: {exEvento.GetType().Name}: {exEvento.Message}");
                            Console.WriteLine($"[PregaoVivaVozEngine] Worker erro no evento: {exEvento.Message}");
                        }
                    }

                    // Se chegou aqui o channel foi completado — NÃO deveria acontecer
                    // durante uso normal (Parar() cancela o CTS ANTES de completar o channel).
                    // Recria o channel e retoma o loop externo.
                    PvvDebugFileLog.Write("[ENGINE-WORKER] ALERTA: channel completou inesperadamente — recriando");

                    _filaInterna = Channel.CreateBounded<EventoInterno>(
                        new BoundedChannelOptions(FilaInternaCapacidade)
                        {
                            FullMode = BoundedChannelFullMode.DropOldest,
                            SingleReader = true,
                            SingleWriter = false,
                            AllowSynchronousContinuations = false
                        });
                }
                catch (OperationCanceledException)
                {
                    // Cancelamento normal (Parar() chamou _cts.Cancel()) — sair do loop.
                    PvvDebugFileLog.Write("[ENGINE-WORKER] WorkerLoop cancelado (Parar)");
                    break;
                }
                catch (Exception exLoop)
                {
                    PvvDebugFileLog.Write($"[ENGINE-WORKER] erro no loop: {exLoop.GetType().Name}: {exLoop.Message} — reiniciando em 100ms");
                    Console.WriteLine($"[PregaoVivaVozEngine] Worker erro crítico: {exLoop.Message}");
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
            }

            PvvDebugFileLog.Write("[ENGINE-WORKER] WorkerLoop encerrado (cancelamento solicitado)");
        }

        private void ProcessarAgressaoInterno(EventoInterno e)
        {
            // TTL — descartar agressões com mais de 15s de atraso.
            // Só se aplica quando a DLL forneceu timestamp de bolsa (ExchangeTime não-null).
            // ExchangeTime=null → processar normalmente (fallback seguro).
            if (e.ExchangeTime.HasValue)
            {
                var idade = DateTime.UtcNow - e.ExchangeTime.Value;
                if (idade > TtlMaximo)
                {
                    PvvDebugFileLog.Write(
                        $"[ENGINE-WORKER] TTL expirado — descartando agressão de {idade.TotalSeconds:F1}s atrás " +
                        $"(nome={e.NomeCorretora} lado={e.Lado} qtd={e.Quantidade})");
                    return;
                }
            }

            _eventosProcessados++;

            long procN = Interlocked.Increment(ref _diagEngineProcessados);
            bool procLog = procN <= 20 || (procN % 500) == 0;

            var player = IdentificarPlayer(e.NomeCorretora!);
            if (player == null)
            {
                if (procLog)
                    PvvDebugFileLog.Write($"[ENGINE-WORKER] #{procN} SEM PLAYER match para nome='{e.NomeCorretora}' (evento descartado)");
                return;
            }
            if (!player.AtivoHoje)
            {
                if (procLog)
                    PvvDebugFileLog.Write($"[ENGINE-WORKER] #{procN} player='{player.Chave}' está DESATIVADO (ativoHoje=false) — não narrar");
                return;
            }
            if (!player.Agressao.Ativo)
            {
                if (procLog)
                    PvvDebugFileLog.Write($"[ENGINE-WORKER] #{procN} player='{player.Chave}' agressão desabilitada — não narrar");
                return;
            }

            if (procLog)
                PvvDebugFileLog.Write($"[ENGINE-WORKER] #{procN} player='{player.Chave}' OK → agregando (lado={e.Lado} qtd={e.Quantidade})");

            AlimentarAgregador(player, e.Lado!, e.Quantidade, e.ExchangeTime);
        }

        private void ProcessarBookInterno(EventoInterno e)
        {
            // TTL — descartar eventos de book com mais de 15s de atraso.
            // Na implementação atual os eventos de book chegam sempre com
            // ExchangeTime=null (definido em ProcessarBook), portanto esta
            // verificação é no-op — garante proteção caso timestamps de book
            // sejam adicionados no futuro, e preserva narração prioritária
            // (que também é book, também com ExchangeTime=null → nunca descartada).
            if (e.ExchangeTime.HasValue)
            {
                var idade = DateTime.UtcNow - e.ExchangeTime.Value;
                if (idade > TtlMaximo)
                {
                    PvvDebugFileLog.Write(
                        $"[ENGINE-WORKER] TTL expirado — descartando book de {idade.TotalSeconds:F1}s atrás " +
                        $"(nome={e.NomeCorretora} lado={e.Lado} qtd={e.Quantidade} nivel={e.Nivel})");
                    return;
                }
            }

            _eventosProcessados++;

            var player = IdentificarPlayer(e.NomeCorretora!);
            if (player == null || !player.AtivoHoje) return;
            if (!player.Book.Ativo) return;

            var tipo = e.Lado == "compra" ? TipoEvento.BookCompra : TipoEvento.BookVenda;

            // ── VERIFICAÇÃO DE PRIORIDADE (independente dos filtros CompraMinima/VendaMinima) ──
            // Só aciona quando PrioridadeMinima está definido (não null), a quantidade
            // atinge esse limiar E o nível é 1..4 (frases gravadas só cobrem esses).
            // Prioritária → interrompe áudio corrente + descarta fila + narra imediato.
            // Se acionar, retorna: a normal NÃO deve narrar em cima.
            int? limitePrio = player.Book.PrioridadeMinima;
            if (limitePrio.HasValue
                && e.Quantidade >= limitePrio.Value
                && e.Nivel >= 1 && e.Nivel <= 4)
            {
                var eventoPrio = new EventoOrderFlow
                {
                    Timestamp = DateTime.Now,
                    PlayerChave = player.Chave,
                    PlayerNome = player.Nome,
                    Tipo = tipo,
                    Quantidade = e.Quantidade,
                    Nivel = Math.Clamp(e.Nivel, 1, 4)
                };
                NarrarEventoComPrioridade(eventoPrio, e.CallbackInfo);
                return;
            }

            // ── Fluxo normal (comportamento anterior, inalterado) ──
            int limiteMinimo = e.Lado == "compra"
                ? player.Book.CompraMinima
                : player.Book.VendaMinima;

            if (e.Quantidade < limiteMinimo) return;

            var evento = new EventoOrderFlow
            {
                Timestamp = DateTime.Now,
                PlayerChave = player.Chave,
                PlayerNome = player.Nome,
                Tipo = tipo,
                Quantidade = e.Quantidade,
                Nivel = Math.Clamp(e.Nivel, 1, 4)
            };

            NarrarEvento(evento, e.CallbackInfo);
        }

        // ============ AGREGADOR DE BLOCOS (CASO 1) ============

        /// <summary>
        /// Alimenta o estado de agregação com um novo callback.
        ///
        /// Regra: compara o milissegundo do TIMESTAMP DE BOLSA (exchangeTime).
        /// - MESMO ms → acumula volume no bloco.
        /// - DIFERENTE → fecha bloco anterior (narra + alimenta detector), abre novo.
        ///
        /// Fallback (exchangeTime == null): usa DateTime.UtcNow — degradação
        /// segura quando a DLL não enviou bHasDate.
        /// </summary>
        private void AlimentarAgregador(PlayerConfig player, string lado, int quantidade, DateTime? exchangeTime)
        {
            var chaveEstado = string.Concat(player.Chave, "|", lado);

            long tsMs = exchangeTime.HasValue
                ? exchangeTime.Value.Ticks / TimeSpan.TicksPerMillisecond
                : DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

            var estado = _estadosAgressao.GetOrAdd(chaveEstado, _ => new EstadoAgressao
            {
                PlayerChave = player.Chave,
                PlayerNome = player.Nome,
                Lado = lado
            });

            long nowTicks = DateTime.UtcNow.Ticks;

            lock (estado)
            {
                estado.PlayerNome = player.Nome;

                if (estado.BlocoAberto)
                {
                    if (tsMs == estado.MsBloco)
                    {
                        // Mesmo milissegundo exato de bolsa → agregar no bloco atual
                        estado.VolumeBloco += quantidade;
                        // Renova a janela de silêncio: bloco só fecha após N ms
                        // sem novo callback (evita fechar entre late arrivals do
                        // mesmo ms de bolsa).
                        estado.UltimoCallbackTicks = nowTicks;
                        return;
                    }

                    // Milissegundo diferente → fechar bloco anterior, depois iniciar novo
                    FecharBloco(estado, player);
                }

                estado.VolumeBloco = quantidade;
                estado.MsBloco = tsMs;
                estado.BlocoAberto = true;
                estado.UltimoCallbackTicks = nowTicks;
            }
        }

        /// <summary>
        /// Fecha bloco aberto e processa volume agregado.
        ///
        /// 1. Aplica filtro por player (TomouMinimo/BateuMinimo) sobre o total
        ///    do bloco. Se passa → narra "bateu/tomou [total]" via FraseBuilderService
        ///    (que cuida do arredondamento e da escolha do WAV correto entre as
        ///    45 gravações de números existentes).
        /// 2. Para players com Rajada.Participa=true: alimenta o DetectorRajadaService
        ///    com o volume do bloco + VolumeMinimo do próprio player (Objetivo 3),
        ///    independente do filtro de narração acima.
        ///
        /// DEVE ser chamado dentro de lock(estado).
        /// </summary>
        private void FecharBloco(EstadoAgressao estado, PlayerConfig player)
        {
            if (!estado.BlocoAberto || estado.VolumeBloco == 0) return;

            int volumeBloco = estado.VolumeBloco;
            estado.BlocoAberto = false;
            estado.VolumeBloco = 0;

            // ── Narrar "bateu/tomou [total]" se filtro por player OK ──
            int limiteMinimo = estado.Lado == "compra"
                ? player.Agressao.TomouMinimo
                : player.Agressao.BateuMinimo;

            if (volumeBloco >= limiteMinimo)
            {
                var tipo = estado.Lado == "compra"
                    ? TipoEvento.AgressaoCompra
                    : TipoEvento.AgressaoVenda;

                // Reconstrói o horário de bolsa a partir do MsBloco (UTC ms desde DateTime.MinValue).
                var bolsaUtc = new DateTime(estado.MsBloco * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
                string bolsaStr = bolsaUtc.ToLocalTime().ToString("HH:mm:ss.fff");

                NarrarEvento(new EventoOrderFlow
                {
                    Timestamp = DateTime.Now,
                    PlayerChave = player.Chave,
                    PlayerNome = player.Nome,
                    Tipo = tipo,
                    Quantidade = volumeBloco
                }, $"BLOCO_AGREGADO bolsa={bolsaStr} agent={player.Nome} lado={estado.Lado} vol={volumeBloco}");
            }

            // ── Alimentar DetectorRajadaService com o VolumeMinimo do player (Objetivo 3) ──
            if (player.Rajada.Participa)
            {
                _detectorRajada.RegistrarAgressao(
                    player.Chave,
                    player.Nome,
                    estado.Lado,
                    volumeBloco,
                    player.Rajada.VolumeMinimo);
            }
        }

        /// <summary>
        /// Janela de silêncio (Ticks) antes de fechar um bloco aberto no PollAgregador.
        /// Se nenhum novo callback chegar por este intervalo desde o último agregado,
        /// o bloco é fechado. 50 ms cobre folgadamente o pior caso de latência
        /// (~15-25 ms) entre callbacks do mesmo ms de bolsa que atravessam
        /// Provider → Bridge worker → Engine worker sequencialmente.
        /// </summary>
        private const long CarenciaFechamentoTicks = 50L * TimeSpan.TicksPerMillisecond;

        /// <summary>
        /// Timer callback (20ms). Fecha blocos que ficaram <c>CarenciaFechamentoTicks</c>
        /// (50 ms) sem receber novo callback — não usa mais o timestamp da bolsa como
        /// referência (esse é sempre passado no instante em que chega no engine).
        /// NÃO faz detecção de rajada nem silêncio — DetectorRajadaService cuida disso.
        /// </summary>
        private void PollAgregador(object? state)
        {
            if (_pausado) return;

            long agoraTicks = DateTime.UtcNow.Ticks;

            foreach (var kvp in _estadosAgressao)
            {
                var estado = kvp.Value;

                lock (estado)
                {
                    // Só fecha depois de N ms de silêncio desde o último callback
                    // agregado — dá chance para late arrivals do mesmo ms de bolsa
                    // chegarem via Bridge worker → Engine worker (que serializa).
                    if (estado.BlocoAberto &&
                        (agoraTicks - estado.UltimoCallbackTicks) >= CarenciaFechamentoTicks)
                    {
                        if (_playersConfig.TryGetValue(estado.PlayerChave, out var player))
                        {
                            FecharBloco(estado, player);
                        }
                        else
                        {
                            estado.BlocoAberto = false;
                            estado.VolumeBloco = 0;
                        }
                    }
                }
            }
        }

        // ============ INTERNAL - NARRAÇÃO ============

        private void NarrarEvento(EventoOrderFlow evento, string? callbackInfo = null)
        {
            var arquivos = _fraseBuilder.MontarFrase(evento);
            var textoTextual = _fraseBuilder.MontarFraseTextual(evento);

            if (arquivos.Count == 0) return;

            _audioPlayback.Enfileirar(arquivos, textoTextual, callbackInfo);
            _eventosNarrados++;

            Console.WriteLine($"[NARRAR] {textoTextual}");
        }

        /// <summary>
        /// Narração PRIORITÁRIA: usa o mesmo FraseBuilderService (mesmas gravações,
        /// mesmo arredondamento de quantidade), mas entrega ao AudioPlayback via
        /// <see cref="AudioPlaybackService.NarrarComPrioridade"/> — o que INTERROMPE
        /// o áudio corrente, descarta toda a fila normal e reproduz imediatamente.
        /// </summary>
        private void NarrarEventoComPrioridade(EventoOrderFlow evento, string? callbackInfo = null)
        {
            var arquivos = _fraseBuilder.MontarFrase(evento);
            var textoTextual = _fraseBuilder.MontarFraseTextual(evento);

            if (arquivos.Count == 0) return;

            // Ativa flag: o WorkerLoop descarta eventos normais pendentes na
            // _filaInterna enquanto o clip prioritário é enfileirado no áudio.
            _descartarFilaAtePrioritariaTerminar = true;
            PvvDebugFileLog.Write($"[ENGINE-WORKER] modo prioritário ativado — descartando fila");
            try
            {
                // NarrarComPrioridade: drena fila de áudio normal + cancela áudio
                // corrente + enfileira na fila prioritária do AudioPlaybackService.
                _audioPlayback.NarrarComPrioridade(arquivos, textoTextual, callbackInfo);
                _eventosNarrados++;
                Console.WriteLine($"[NARRAR-PRIO] {textoTextual}");
            }
            finally
            {
                _descartarFilaAtePrioritariaTerminar = false;
                PvvDebugFileLog.Write($"[ENGINE-WORKER] narração prioritária concluída — retomando normal");
            }
        }

        // ============ HANDLERS DE RAJADA (PRIMÁRIOS) ============

        private void OnRajadaIniciada(object? sender, EventoOrderFlow evento)
        {
            Interlocked.Increment(ref _rajadasDetectadas);

            if (_playersConfig.TryGetValue(evento.PlayerChave, out var player))
            {
                evento.PlayerNome = player.Nome;
            }

            // ExchangeTime não disponível no DetectorRajadaService — usa evento.Timestamp
            // (DateTime.Now local do momento da detecção) como fallback, conforme spec.
            string bolsaStr = evento.Timestamp.ToString("HH:mm:ss.fff");
            string ladoStr = evento.Tipo == TipoEvento.RajadaInicioCompra ? "compra" : "venda";

            NarrarEvento(evento, $"RAJADA_INICIO bolsa={bolsaStr} agent={evento.PlayerNome} lado={ladoStr}");
        }

        private void OnRajadaParou(object? sender, EventoOrderFlow evento)
        {
            if (_playersConfig.TryGetValue(evento.PlayerChave, out var player))
            {
                evento.PlayerNome = player.Nome;
            }

            // Mesmo fallback: usa evento.Timestamp (DateTime.Now do MonitorSilencioLoop).
            string bolsaStr = evento.Timestamp.ToString("HH:mm:ss.fff");
            string ladoStr = evento.Tipo == TipoEvento.RajadaPararCompra ? "compra" : "venda";

            NarrarEvento(evento, $"RAJADA_PAROU bolsa={bolsaStr} agent={evento.PlayerNome} lado={ladoStr}");
        }

        // ============ HELPERS ============

        private PlayerConfig? IdentificarPlayer(string nomeCorretora)
        {
            if (string.IsNullOrEmpty(nomeCorretora)) return null;

            var nomeNorm = nomeCorretora.Trim().ToLower();

            if (_playersConfig.TryGetValue(nomeNorm, out var player))
                return player;

            foreach (var p in _playersConfig.Values)
            {
                if (p.Nome.Equals(nomeCorretora, StringComparison.OrdinalIgnoreCase))
                    return p;

                if (p.Codigo.Equals(nomeCorretora, StringComparison.OrdinalIgnoreCase))
                    return p;
            }

            foreach (var p in _playersConfig.Values)
            {
                if (nomeNorm.Contains(p.Chave.ToLower()) ||
                    p.Nome.ToLower().Contains(nomeNorm))
                    return p;
            }

            return null;
        }

        public void Dispose()
        {
            _agregadorTimer?.Dispose();
            try
            {
                // Mesma ordem de Parar(): CTS primeiro para saída limpa, TryComplete depois.
                _cts.Cancel();
                _filaInterna.Writer.TryComplete();
                _workerTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch { /* best effort */ }
            _cts.Dispose();
            _audioPlayback?.Dispose();
            _detectorRajada?.Dispose();
        }

        // ============ TIPOS INTERNOS ============

        private enum TipoEventoInterno { Agressao, Book }

        private class EventoInterno
        {
            public TipoEventoInterno Tipo;
            public string? NomeCorretora;
            public string? Lado;
            public int Quantidade;
            public int Nivel;
            public string? CallbackInfo;
            public DateTime? ExchangeTime;
        }
    }
}
