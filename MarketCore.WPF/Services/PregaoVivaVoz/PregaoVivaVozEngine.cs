using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarketCore.WPF.Models.PregaoVivaVoz;

namespace MarketCore.WPF.Services.PregaoVivaVoz
{
    /// <summary>
    /// MOTOR PRINCIPAL do Pregão Viva Voz.
    ///
    /// PADRÃO: Event Bridge
    /// - Expõe métodos públicos que qualquer código pode chamar
    /// - Não conhece diretamente o ProfitDLL nem o Simulator
    /// - O MarketCore chama estes métodos quando eventos chegam
    /// - Zero acoplamento com estruturas internas
    ///
    /// FLUXO:
    /// 1. MarketCore recebe callback do ProfitDLL (ou Simulator)
    /// 2. MarketCore chama: engine.ProcessarAgressao("goldman", "compra", 500)
    /// 3. Engine agrega callbacks em blocos → verifica filtros → narra via FraseBuilder → AudioPlayback
    /// 4. AudioPlayback toca (ou loga no console se sem WAV)
    ///
    /// AGREGAÇÃO:
    /// CASO 1 — Mesmo milissegundo exato: callbacks do mesmo broker+direção cujo
    ///          timestamp cai no MESMO milissegundo (mesmo segundo E milissegundo)
    ///          são acumulados. Ao fechar o bloco, verifica filtro por player e narra
    ///          "bateu/tomou [total]" uma única vez via FraseBuilderService.
    ///          O total é TAMBÉM alimentado no DetectorRajadaService
    ///          (via RegistrarAgressao) para players com Rajada.Participa=true.
    ///
    /// CASO 2 — Milissegundos diferentes: tratado INTEIRAMENTE pelo
    ///          DetectorRajadaService, que recebe os totais de bloco via RegistrarAgressao
    ///          e dispara eventos RajadaIniciada / RajadaParou.
    ///          O Engine apenas escuta esses eventos e narra "tomando/batendo" e
    ///          "parou de tomar/bater". Nenhuma lógica de rajada existe neste arquivo.
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

        // ============ AGREGADOR DE BLOCOS (CASO 1) ============

        /// <summary>
        /// Estado de agregação por player+lado. Chave: "playerChave|lado".
        /// </summary>
        private readonly ConcurrentDictionary<string, EstadoAgressao> _estadosAgressao = new();

        /// <summary>
        /// Timer que faz polling a cada 20ms para fechar blocos cujo milissegundo
        /// já ficou no passado (milissegundo atual > milissegundo do bloco).
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
            /// Milissegundo UTC truncado do bloco aberto.
            /// Calculado como DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond.
            /// Callbacks com o MESMO valor são agregados; valor diferente = bloco novo.
            /// </summary>
            public long MsBloco;
            /// <summary>Se há um bloco aberto aguardando mais callbacks.</summary>
            public bool BlocoAberto;
        }

        // ============ EVENTOS PÚBLICOS ============

        /// <summary>
        /// Disparado quando uma narração termina de tocar. Payload traz texto + callback
        /// original que a gerou (para correlação perfeita no log).
        /// </summary>
        public event EventHandler<NarracaoInfo>? EventoNarrado;

        /// <summary>
        /// Disparado com estatísticas periódicas.
        /// </summary>
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

        /// <summary>
        /// Retorna true se o motor está ativo (Iniciar() foi chamado E não está pausado).
        /// Usado pelo ProfitDLLBridge para descartar eventos quando o motor não está ativo.
        /// </summary>
        public bool MotorAtivo => _iniciado && !_pausado;

        public int VolumeMaster
        {
            get => _audioPlayback.VolumeMaster;
            set => _audioPlayback.VolumeMaster = value;
        }

        public int EventosProcessados => _eventosProcessados;
        public int EventosNarrados => _eventosNarrados;
        public int RajadasDetectadas => _rajadasDetectadas;

        // ============ CONSTRUTOR ============

        public PregaoVivaVozEngine(
            List<PlayerConfig> players,
            ConfigRajadaGlobal configRajada,
            string diretorioAudio)
        {
            _configRajada = configRajada ?? new ConfigRajadaGlobal();

            // Indexar players por chave pra lookup rápido
            foreach (var p in players)
            {
                _playersConfig[p.Chave.ToLower()] = p;
            }

            // Serviços dependentes
            _fraseBuilder = new FraseBuilderService(diretorioAudio);
            _audioPlayback = new AudioPlaybackService();
            _detectorRajada = new DetectorRajadaService(_configRajada);

            // Conecta eventos do detector de rajada — handlers PRIMÁRIOS
            _detectorRajada.RajadaIniciada += OnRajadaIniciada;
            _detectorRajada.RajadaParou += OnRajadaParou;

            // Conecta eventos do playback
            _audioPlayback.ItemReproduzido += (s, info) => EventoNarrado?.Invoke(this, info);
        }

        // ============ CONTROLE ============

        public void Iniciar()
        {
            if (_iniciado) return;
            _iniciado = true;
            _pausado = false;

            _audioPlayback.Iniciar();
            _detectorRajada.Iniciar();

            // Timer do agregador: poll a cada 20ms pra fechar blocos de ms passado
            _agregadorTimer = new Timer(PollAgregador, null, 20, 20);

            Console.WriteLine($"[PregaoVivaVozEngine] Motor iniciado com {_playersConfig.Count} players configurados");
            EstatisticasAtualizadas?.Invoke(this, $"Motor iniciado · {_playersConfig.Count} players");
        }

        public void Parar()
        {
            _iniciado = false;
            _pausado = true;
            _audioPlayback.LimparFila();
            _agregadorTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _estadosAgressao.Clear();
            Console.WriteLine("[PregaoVivaVozEngine] Motor parado");
        }

        /// <summary>
        /// Atualiza configuração de um player em tempo real.
        /// Chame quando o usuário mudar limites na UI.
        /// </summary>
        public void AtualizarPlayer(PlayerConfig player)
        {
            if (player == null) return;
            _playersConfig[player.Chave.ToLower()] = player;
        }

        // ============ EVENT BRIDGE - MÉTODOS PÚBLICOS ============
        // Estes são os métodos que o MarketCore chama depois da integração

        /// <summary>
        /// PROCESSA UM TRADE (agressão).
        /// Chame para CADA trade que chegar do ProfitDLL.
        ///
        /// Agrega callbacks cujo timestamp cai no MESMO milissegundo exato
        /// (mesmo segundo E mesmo milissegundo) e narra o total uma vez.
        /// Callbacks em milissegundos diferentes geram blocos separados.
        /// O filtro de volume por player é aplicado no fechamento do bloco.
        ///
        /// Parâmetros:
        /// - nomeCorretora: nome exato como vem do ProfitDLL (ex: "Goldman", "JPM")
        /// - lado: "compra" (agressão de compra/tomou) ou "venda" (agressão de venda/bateu)
        /// - quantidade: número de contratos
        /// </summary>
        public void ProcessarAgressao(string nomeCorretora, string lado, int quantidade, string? callbackInfo = null)
        {
            if (_pausado || string.IsNullOrEmpty(nomeCorretora)) return;

            _eventosProcessados++;

            // Identificar o player
            var player = IdentificarPlayer(nomeCorretora);
            if (player == null || !player.AtivoHoje) return;

            // Verificar se agressão está ativa para este player
            if (!player.Agressao.Ativo) return;

            // Alimenta o agregador — filtro de volume aplicado no fechamento do bloco
            AlimentarAgregador(player, lado, quantidade);
        }

        /// <summary>
        /// PROCESSA UMA ORDEM NO BOOK (ordem passiva colocada).
        ///
        /// Parâmetros:
        /// - nomeCorretora: nome da corretora
        /// - lado: "compra" (ordem de compra no bid) ou "venda" (ordem de venda no ask)
        /// - nivel: 1 = boca (L1), 2 a 5 = níveis mais afastados
        /// - quantidade: contratos ofertados
        /// </summary>
        public void ProcessarBook(string nomeCorretora, string lado, int nivel, int quantidade, string? callbackInfo = null)
        {
            if (_pausado || string.IsNullOrEmpty(nomeCorretora)) return;

            _eventosProcessados++;

            var player = IdentificarPlayer(nomeCorretora);
            if (player == null || !player.AtivoHoje) return;

            if (!player.Book.Ativo) return;

            int limiteMinimo = lado == "compra"
                ? player.Book.CompraMinima
                : player.Book.VendaMinima;

            if (quantidade < limiteMinimo) return;

            var tipo = lado == "compra" ? TipoEvento.BookCompra : TipoEvento.BookVenda;
            var evento = new EventoOrderFlow
            {
                Timestamp = DateTime.Now,
                PlayerChave = player.Chave,
                PlayerNome = player.Nome,
                Tipo = tipo,
                Quantidade = quantidade,
                Nivel = Math.Clamp(nivel, 1, 4)
            };

            NarrarEvento(evento, callbackInfo);
        }

        /// <summary>
        /// PROCESSA UM TRADE EXECUTADO (versão simplificada de agressão).
        /// Use se você só tem info de que houve trade, sem saber se foi agressão ou passivo.
        /// </summary>
        public void ProcessarTrade(string nomeCorretora, string lado, int quantidade)
        {
            // Por padrão, trata como agressão
            ProcessarAgressao(nomeCorretora, lado, quantidade);
        }

        // ============ AGREGADOR DE BLOCOS (CASO 1 APENAS) ============

        /// <summary>
        /// Alimenta o estado de agregação com um novo callback.
        ///
        /// Regra: compara o milissegundo exato (truncado) do timestamp atual
        /// com o milissegundo do bloco aberto.
        /// - MESMO milissegundo → acumula volume no bloco.
        /// - DIFERENTE → fecha bloco anterior (narra + alimenta detector), abre novo.
        ///
        /// Nenhuma janela fixa de tempo — apenas igualdade de milissegundo.
        /// </summary>
        private void AlimentarAgregador(PlayerConfig player, string lado, int quantidade)
        {
            var chaveEstado = string.Concat(player.Chave, "|", lado);
            long agoraMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

            var estado = _estadosAgressao.GetOrAdd(chaveEstado, _ => new EstadoAgressao
            {
                PlayerChave = player.Chave,
                PlayerNome = player.Nome,
                Lado = lado
            });

            lock (estado)
            {
                estado.PlayerNome = player.Nome; // refresh caso tenha mudado

                if (estado.BlocoAberto)
                {
                    if (agoraMs == estado.MsBloco)
                    {
                        // Mesmo milissegundo exato → agregar no bloco atual
                        estado.VolumeBloco += quantidade;
                        return;
                    }

                    // Milissegundo diferente → fechar bloco anterior, depois iniciar novo
                    FecharBloco(estado, player);
                }

                // Iniciar novo bloco
                estado.VolumeBloco = quantidade;
                estado.MsBloco = agoraMs;
                estado.BlocoAberto = true;
            }
        }

        /// <summary>
        /// Fecha bloco aberto e processa volume agregado.
        ///
        /// 1. Aplica filtro por player (TomouMinimo/BateuMinimo) sobre o total do bloco.
        ///    Se passa → narra "bateu/tomou [total]" via FraseBuilderService
        ///    (que já cuida do arredondamento e escolha do arquivo WAV correto).
        /// 2. Para players com Rajada.Participa=true: alimenta o DetectorRajadaService
        ///    com o volume do bloco (independente do filtro de narração).
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

                NarrarEvento(new EventoOrderFlow
                {
                    Timestamp = DateTime.Now,
                    PlayerChave = player.Chave,
                    PlayerNome = player.Nome,
                    Tipo = tipo,
                    Quantidade = volumeBloco
                }, $"BLOCO_AGREGADO (vol={volumeBloco})");
            }

            // ── Alimentar DetectorRajadaService para players de rajada ──
            // Independente do filtro de narração: blocos pequenos contribuem
            // para a detecção de rajada mesmo que não sejam narrados individualmente.
            if (player.Rajada.Participa)
            {
                _detectorRajada.RegistrarAgressao(
                    player.Chave,
                    player.Nome,
                    estado.Lado,
                    volumeBloco);
            }
        }

        /// <summary>
        /// Timer callback (20ms). Percorre todos os estados para fechar blocos
        /// cujo milissegundo já ficou no passado (ms atual > ms do bloco).
        ///
        /// NÃO faz detecção de rajada nem silêncio — isso é responsabilidade
        /// exclusiva do DetectorRajadaService.
        /// </summary>
        private void PollAgregador(object? state)
        {
            if (_pausado) return;

            long agoraMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

            foreach (var kvp in _estadosAgressao)
            {
                var estado = kvp.Value;

                lock (estado)
                {
                    if (estado.BlocoAberto && agoraMs > estado.MsBloco)
                    {
                        if (_playersConfig.TryGetValue(estado.PlayerChave, out var player))
                        {
                            FecharBloco(estado, player);
                        }
                        else
                        {
                            // Player removido — limpar bloco sem narrar
                            estado.BlocoAberto = false;
                            estado.VolumeBloco = 0;
                        }
                    }
                }
            }
        }

        // ============ INTERNAL - NARRAÇÃO ============

        /// <summary>
        /// Envia um evento pro FraseBuilder e enfileira no AudioPlayback.
        /// O FraseBuilderService cuida do arredondamento (ArredondarQuantidade) e
        /// da escolha do arquivo WAV correto para o número narrado.
        /// <paramref name="callbackInfo"/> é a string original do callback da DLL — viaja
        /// junto do evento até o log de narração pra manter correlação perfeita.
        /// </summary>
        private void NarrarEvento(EventoOrderFlow evento, string? callbackInfo = null)
        {
            var arquivos = _fraseBuilder.MontarFrase(evento);
            var textoTextual = _fraseBuilder.MontarFraseTextual(evento);

            if (arquivos.Count == 0) return;

            _audioPlayback.Enfileirar(arquivos, textoTextual, callbackInfo);
            _eventosNarrados++;

            Console.WriteLine($"[NARRAR] {textoTextual}");
        }

        // ============ HANDLERS DE RAJADA (PRIMÁRIOS) ============
        // Eventos disparados pelo DetectorRajadaService.
        // O detector recebe totais de bloco via RegistrarAgressao e cuida
        // de TODA a lógica de início e fim de rajada internamente.
        // O Engine apenas narra os eventos aqui.

        /// <summary>
        /// Handler chamado quando o DetectorRajadaService detecta INÍCIO de rajada.
        /// Narra "tomando" ou "batendo" para o player.
        /// </summary>
        private void OnRajadaIniciada(object? sender, EventoOrderFlow evento)
        {
            Interlocked.Increment(ref _rajadasDetectadas);

            // Enriquece o nome do player (detector pode não ter o nome completo)
            if (_playersConfig.TryGetValue(evento.PlayerChave, out var player))
            {
                evento.PlayerNome = player.Nome;
            }

            NarrarEvento(evento, "RAJADA_INICIO (detectada pelo DetectorRajadaService)");
        }

        /// <summary>
        /// Handler chamado quando o DetectorRajadaService detecta FIM de rajada
        /// (silêncio > SilencioParouMilissegundos).
        /// Narra "parou de tomar" ou "parou de bater" para o player.
        /// </summary>
        private void OnRajadaParou(object? sender, EventoOrderFlow evento)
        {
            if (_playersConfig.TryGetValue(evento.PlayerChave, out var player))
            {
                evento.PlayerNome = player.Nome;
            }

            NarrarEvento(evento, "RAJADA_PAROU (timeout de silencio)");
        }

        // ============ HELPERS ============

        /// <summary>
        /// Identifica o player a partir do nome que vem do ProfitDLL.
        /// Faz match tanto pelo nome completo quanto por variações.
        /// </summary>
        private PlayerConfig? IdentificarPlayer(string nomeCorretora)
        {
            if (string.IsNullOrEmpty(nomeCorretora)) return null;

            var nomeNorm = nomeCorretora.Trim().ToLower();

            // Match direto por chave
            if (_playersConfig.TryGetValue(nomeNorm, out var player))
                return player;

            // Match por nome exato
            foreach (var p in _playersConfig.Values)
            {
                if (p.Nome.Equals(nomeCorretora, StringComparison.OrdinalIgnoreCase))
                    return p;

                if (p.Codigo.Equals(nomeCorretora, StringComparison.OrdinalIgnoreCase))
                    return p;
            }

            // Match parcial (contém)
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
            _audioPlayback?.Dispose();
            _detectorRajada?.Dispose();
        }
    }
}
