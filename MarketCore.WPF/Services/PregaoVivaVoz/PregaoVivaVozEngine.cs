using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
    /// 3. Engine verifica filtros → passa pro FraseBuilder → enfileira no AudioPlayback
    /// 4. AudioPlayback toca (ou loga no console se sem WAV)
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
        
        // ============ EVENTOS PÚBLICOS ============
        
        /// <summary>
        /// Disparado quando um evento é NARRADO (passou nos filtros).
        /// </summary>
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
                if (value) _audioPlayback.LimparFila();
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
            
            // Conecta eventos do detector
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
            
            Console.WriteLine($"[PregaoVivaVozEngine] Motor iniciado com {_playersConfig.Count} players configurados");
            EstatisticasAtualizadas?.Invoke(this, $"Motor iniciado · {_playersConfig.Count} players");
        }
        
        public void Parar()
        {
            _iniciado = false;
            _pausado = true;
            _audioPlayback.LimparFila();
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
        // Estes são os métodos que o Cowork vai chamar depois da integração
        
        /// <summary>
        /// PROCESSA UM TRADE (agressão ou passivo).
        /// Chame para CADA trade que chegar do ProfitDLL.
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

            // Verificar filtros
            if (!player.Agressao.Ativo) return;

            int limiteMinimo = lado == "compra"
                ? player.Agressao.TomouMinimo
                : player.Agressao.BateuMinimo;

            if (quantidade < limiteMinimo)
            {
                // Não passa no filtro, mas ainda alimenta o detector de rajada
                if (player.Rajada.Participa)
                {
                    _detectorRajada.RegistrarAgressao(player.Chave, player.Nome, lado, quantidade);
                }
                return;
            }

            // Passou no filtro! Cria evento e narra
            var tipo = lado == "compra" ? TipoEvento.AgressaoCompra : TipoEvento.AgressaoVenda;
            var evento = new EventoOrderFlow
            {
                Timestamp = DateTime.Now,
                PlayerChave = player.Chave,
                PlayerNome = player.Nome,
                Tipo = tipo,
                Quantidade = quantidade
            };

            NarrarEvento(evento, callbackInfo);

            // Alimenta o detector de rajada
            if (player.Rajada.Participa)
            {
                _detectorRajada.RegistrarAgressao(player.Chave, player.Nome, lado, quantidade);
            }
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
                Nivel = Math.Clamp(nivel, 1, 5)
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
        
        // ============ INTERNAL - NARRAÇÃO ============
        
        /// <summary>
        /// Envia um evento pro FraseBuilder e enfileira no AudioPlayback.
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

            Console.WriteLine($"🎙️ [NARRAR] {textoTextual}");
        }

        // ============ HANDLERS DE RAJADA ============

        private void OnRajadaIniciada(object? sender, EventoOrderFlow evento)
        {
            _rajadasDetectadas++;

            // Enriquece o nome do player
            var player = _playersConfig.Values.FirstOrDefault(p => p.Chave == evento.PlayerChave);
            if (player != null)
            {
                evento.PlayerNome = player.Nome;
            }

            // Rajada é derivada de N agressões — não há um único callback pra ela.
            // Marca explicitamente no log pra distinguir de narrações 1-para-1.
            NarrarEvento(evento, "RAJADA_INICIO (derivado de múltiplas agressões)");
        }

        private void OnRajadaParou(object? sender, EventoOrderFlow evento)
        {
            var player = _playersConfig.Values.FirstOrDefault(p => p.Chave == evento.PlayerChave);
            if (player != null)
            {
                evento.PlayerNome = player.Nome;
            }

            NarrarEvento(evento, "RAJADA_PAROU (timeout de silêncio)");
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
            _audioPlayback?.Dispose();
            _detectorRajada?.Dispose();
        }
    }
}