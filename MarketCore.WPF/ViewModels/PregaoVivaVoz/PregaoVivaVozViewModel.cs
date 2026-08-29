using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using MarketCore.Providers.Nelogica;
using MarketCore.WPF.Models.PregaoVivaVoz;
using MarketCore.WPF.Services.PregaoVivaVoz;

namespace MarketCore.WPF.ViewModels.PregaoVivaVoz
{
    /// <summary>
    /// ViewModel principal da janela Pregão Viva Voz — VERSÃO ZIP 2.
    /// Adiciona: motor, simulador de eventos, log em tempo real, estatísticas.
    /// </summary>
    public class PregaoVivaVozViewModel : ViewModelBase, IDisposable
    {
        private readonly ConfigPersistenceService _persistence;
        private PregaoVivaVozEngine? _engine;
        private EventoSimulador? _simulador;

        // [PVV-DIAG] Timer 1s que atualiza os contadores no card mesmo sem narração.
        private System.Windows.Threading.DispatcherTimer? _statsTimer;
        
        // ============ COLEÇÕES PRA BINDING ============
        
        public ObservableCollection<PlayerConfig> Players { get; } = new();
        
        private ConfigRajadaGlobal _configRajada = new();
        public ConfigRajadaGlobal ConfigRajada
        {
            get => _configRajada;
            set => SetProperty(ref _configRajada, value);
        }
        
        /// <summary>Log de eventos narrados (últimos 100).</summary>
        public ObservableCollection<string> LogEventos { get; } = new();
        
        // ============ ESTADO GERAL ============
        
        private bool _sessaoAtiva = true;
        public bool SessaoAtiva
        {
            get => _sessaoAtiva;
            set 
            { 
                if (SetProperty(ref _sessaoAtiva, value))
                {
                    if (_engine != null) _engine.Pausado = !value;
                    OnPropertyChanged(nameof(TextoBotaoPausar));
                }
            }
        }
        
        public string TextoBotaoPausar => SessaoAtiva ? "⏸ Pausar sessão" : "▶ Retomar sessão";
        
        private int _volumeMaster = 70;
        public int VolumeMaster
        {
            get => _volumeMaster;
            set 
            { 
                if (SetProperty(ref _volumeMaster, value))
                {
                    if (_engine != null) _engine.VolumeMaster = value;
                }
            }
        }
        
        private bool _motorRodando = false;
        public bool MotorRodando
        {
            get => _motorRodando;
            set 
            { 
                if (SetProperty(ref _motorRodando, value))
                {
                    OnPropertyChanged(nameof(StatusMotor));
                    OnPropertyChanged(nameof(TextoBotaoMotor));
                }
            }
        }
        
        public string StatusMotor => _motorRodando ? "🟢 Motor ativo" : "⚫ Motor parado";
        public string TextoBotaoMotor => _motorRodando ? "⏹ Parar motor" : "▶ Iniciar motor";
        
        private bool _simuladorRodando = false;
        public bool SimuladorRodando
        {
            get => _simuladorRodando;
            set 
            { 
                if (SetProperty(ref _simuladorRodando, value))
                {
                    OnPropertyChanged(nameof(TextoBotaoSimulador));
                }
            }
        }
        
        public string TextoBotaoSimulador
        {
            get
            {
                if (_modoMercadoReal) return "🔒 Simulador desativado (mercado real)";
                return _simuladorRodando ? "⏹ Parar simulador de teste" : "🧪 Iniciar simulador de teste";
            }
        }

        private bool _modoMercadoReal;
        /// <summary>
        /// Quando true (ProfitDLL ligada no MarketCore), desabilita o simulador
        /// pra não misturar eventos fake com o fluxo real da ProfitDLL.
        /// </summary>
        public bool ModoMercadoReal
        {
            get => _modoMercadoReal;
            set
            {
                if (SetProperty(ref _modoMercadoReal, value))
                {
                    OnPropertyChanged(nameof(SimuladorDisponivel));
                    OnPropertyChanged(nameof(TextoBotaoSimulador));
                    if (value && _simuladorRodando) PararSimulador();
                }
            }
        }

        /// <summary>Binding pra IsEnabled do botão do simulador.</summary>
        public bool SimuladorDisponivel => !_modoMercadoReal;
        
        // ============ ESTATÍSTICAS ============
        
        private int _eventosProcessados;
        public int EventosProcessados
        {
            get => _eventosProcessados;
            set => SetProperty(ref _eventosProcessados, value);
        }
        
        private int _eventosNarrados;
        public int EventosNarrados
        {
            get => _eventosNarrados;
            set => SetProperty(ref _eventosNarrados, value);
        }
        
        private int _rajadasDetectadas;
        public int RajadasDetectadas
        {
            get => _rajadasDetectadas;
            set => SetProperty(ref _rajadasDetectadas, value);
        }
        
        // ============ BUSCAS ============
        
        private string _buscaBook = "";
        public string BuscaBook
        {
            get => _buscaBook;
            set 
            { 
                if (SetProperty(ref _buscaBook, value))
                    OnPropertyChanged(nameof(PlayersBookFiltrados));
            }
        }
        
        private string _buscaAgressao = "";
        public string BuscaAgressao
        {
            get => _buscaAgressao;
            set 
            { 
                if (SetProperty(ref _buscaAgressao, value))
                    OnPropertyChanged(nameof(PlayersAgressaoFiltrados));
            }
        }
        
        public IEnumerable<PlayerConfig> PlayersBookFiltrados =>
            string.IsNullOrWhiteSpace(_buscaBook) ? Players
                : Players.Where(p => p.Nome.Contains(_buscaBook, StringComparison.OrdinalIgnoreCase));
        
        public IEnumerable<PlayerConfig> PlayersAgressaoFiltrados =>
            string.IsNullOrWhiteSpace(_buscaAgressao) ? Players
                : Players.Where(p => p.Nome.Contains(_buscaAgressao, StringComparison.OrdinalIgnoreCase));
        
        public int PlayersAtivos => Players.Count(p => p.AtivoHoje);
        public int TotalPlayers => Players.Count;
        public string ResumoPlayers => $"{TotalPlayers} no catálogo · {PlayersAtivos} ativos";
        
        // ============ COMANDOS ============
        
        public ICommand PausarRetomarCommand { get; }
        public ICommand SalvarCommand { get; }
        public ICommand TogglePlayerCommand { get; }
        public ICommand AbrirStudioCommand { get; }
        public ICommand ToggleMotorCommand { get; }
        public ICommand ToggleSimuladorCommand { get; }
        public ICommand LimparLogCommand { get; }
        
        // ============ EVENTOS ============
        
        public event EventHandler? AbrirStudioSolicitado;
        public event EventHandler<string>? MensagemLog;
        
        // ============ CONSTRUTOR ============
        
        public PregaoVivaVozViewModel()
        {
            _persistence = new ConfigPersistenceService();
            
            PausarRetomarCommand = new RelayCommand(_ => SessaoAtiva = !SessaoAtiva);
            SalvarCommand = new RelayCommand(async _ => await SalvarConfiguracaoAsync());
            TogglePlayerCommand = new RelayCommand(param => TogglePlayer(param as PlayerConfig));
            AbrirStudioCommand = new RelayCommand(_ => AbrirStudioSolicitado?.Invoke(this, EventArgs.Empty));
            ToggleMotorCommand = new RelayCommand(_ => ToggleMotor());
            ToggleSimuladorCommand = new RelayCommand(_ => ToggleSimulador());
            LimparLogCommand = new RelayCommand(_ => LogEventos.Clear());
            
            _ = InicializarAsync();
        }
        
        // ============ INICIALIZAÇÃO ============
        
        private async Task InicializarAsync()
        {
            try
            {
                var players = await _persistence.CarregarPlayersAsync();
                foreach (var p in players)
                {
                    Players.Add(p);
                }
                
                ConfigRajada = await _persistence.CarregarConfigRajadaAsync();
                
                OnPropertyChanged(nameof(PlayersAtivos));
                OnPropertyChanged(nameof(ResumoPlayers));
                OnPropertyChanged(nameof(PlayersBookFiltrados));
                OnPropertyChanged(nameof(PlayersAgressaoFiltrados));
                
                MensagemLog?.Invoke(this, $"Carregados {Players.Count} players do catálogo");
            }
            catch (Exception ex)
            {
                MensagemLog?.Invoke(this, $"Erro ao carregar: {ex.Message}");
            }
        }
        
        // ============ CONTROLE DO MOTOR ============
        
        private void ToggleMotor()
        {
            if (_motorRodando)
            {
                PararMotor();
            }
            else
            {
                IniciarMotor();
            }
        }
        
        public void IniciarMotor()
        {
            if (_motorRodando) return;
            
            try
            {
                var dirAudio = _persistence.GetDiretorioAudio();
                _engine = new PregaoVivaVozEngine(Players.ToList(), ConfigRajada, dirAudio);
                
                _engine.EventoNarrado += OnEventoNarrado;
                _engine.EstatisticasAtualizadas += OnEstatisticasAtualizadas;
                
                _engine.VolumeMaster = VolumeMaster;
                _engine.Pausado = !SessaoAtiva;
                _engine.Iniciar();

                // Cria o bridge e conecta os hooks estáticos que o ProfitDLLProvider invoca.
                // (Bridge tem filtro interno: só processa WIN, ignora se motor parado.)
                var bridge = new ProfitDLLBridge(_engine);
                App.PregaoVivaVozBridge = bridge;
                PregaoVivaVozHook.OnTradeReceived = bridge.OnTradeReceived;
                PregaoVivaVozHook.OnBookUpdate    = bridge.OnBookUpdate;

                // [PVV-DIAG] Confirma no arquivo que os hooks foram wirados de fato.
                MarketCore.Providers.Nelogica.PvvDebugFileLog.Write(
                    $"[VIEWMODEL] Hooks wirados: OnTradeReceived={PregaoVivaVozHook.OnTradeReceived != null} " +
                    $"OnBookUpdate={PregaoVivaVozHook.OnBookUpdate != null} " +
                    $"bridgeInstance={bridge != null}");

                // [PVV-DIAG] Timer de UI: atualiza os contadores EventosProcessados/Narrados/Rajadas
                // no card do PVV mesmo quando nenhuma narração acontece. Antes, o refresh só
                // rodava em OnEventoNarrado — se nada narrava, o UI ficava eternamente em 0
                // mesmo com o engine processando internamente.
                if (_statsTimer == null)
                {
                    _statsTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(1000)
                    };
                    _statsTimer.Tick += (s, ev) =>
                    {
                        if (_engine != null)
                        {
                            EventosProcessados = _engine.EventosProcessados;
                            EventosNarrados = _engine.EventosNarrados;
                            RajadasDetectadas = _engine.RajadasDetectadas;
                        }
                    };
                }
                _statsTimer.Start();

                MotorRodando = true;
                AdicionarLog("🟢 Motor iniciado · aguardando eventos");
                AdicionarLog($"📄 Diagnóstico em: {MarketCore.Providers.Nelogica.PvvDebugFileLog.FilePath}");
                MensagemLog?.Invoke(this, "Motor Pregão Viva Voz iniciado");
            }
            catch (Exception ex)
            {
                MensagemLog?.Invoke(this, $"Erro ao iniciar motor: {ex.Message}");
                AdicionarLog($"❌ Erro: {ex.Message}");
            }
        }
        
        public void PararMotor()
        {
            if (!_motorRodando) return;
            
            try
            {
                if (_simuladorRodando)
                {
                    PararSimulador();
                }
                
                // Desconecta hooks ANTES de dispor o motor — evita callback race com _engine null.
                PregaoVivaVozHook.OnTradeReceived = null;
                PregaoVivaVozHook.OnBookUpdate    = null;
                var bridgeAtual = App.PregaoVivaVozBridge;
                App.PregaoVivaVozBridge = null;
                bridgeAtual?.Dispose();

                _engine?.Parar();
                _engine?.Dispose();
                _engine = null;

                _statsTimer?.Stop();

                MotorRodando = false;
                AdicionarLog("⚫ Motor parado");
                MensagemLog?.Invoke(this, "Motor parado");
            }
            catch (Exception ex)
            {
                MensagemLog?.Invoke(this, $"Erro ao parar motor: {ex.Message}");
            }
        }
        
        // ============ CONTROLE DO SIMULADOR ============
        
        private void ToggleSimulador()
        {
            if (_simuladorRodando)
            {
                PararSimulador();
            }
            else
            {
                IniciarSimulador();
            }
        }
        
        public void IniciarSimulador()
        {
            if (_simuladorRodando) return;

            // Bloqueio quando em mercado real — evita mistura de eventos fake com dados da ProfitDLL.
            if (_modoMercadoReal)
            {
                AdicionarLog("🔒 Simulador bloqueado: MarketCore está em modo mercado real (ProfitDLL ativa)");
                MensagemLog?.Invoke(this, "Simulador desativado — dados reais em uso");
                return;
            }

            // Motor precisa estar rodando
            if (!_motorRodando)
            {
                IniciarMotor();
            }
            
            if (_engine == null) return;
            
            try
            {
                _simulador = new EventoSimulador(_engine);
                _simulador.Iniciar();
                
                SimuladorRodando = true;
                AdicionarLog("🧪 Simulador de eventos INICIADO · gerando eventos aleatórios");
                MensagemLog?.Invoke(this, "Simulador de teste iniciado");
            }
            catch (Exception ex)
            {
                MensagemLog?.Invoke(this, $"Erro ao iniciar simulador: {ex.Message}");
            }
        }
        
        public void PararSimulador()
        {
            if (!_simuladorRodando) return;
            
            try
            {
                _simulador?.Parar();
                _simulador?.Dispose();
                _simulador = null;
                
                SimuladorRodando = false;
                AdicionarLog("🧪 Simulador PARADO");
            }
            catch (Exception ex)
            {
                MensagemLog?.Invoke(this, $"Erro ao parar simulador: {ex.Message}");
            }
        }
        
        // ============ HANDLERS DE EVENTOS DO MOTOR ============
        
        // ============ LOG PERSISTENTE (arquivo) ============
        // Cada narração + callback é gravada em arquivo pra depuração posterior de
        // possíveis inversões de mapeamento. Deixe o motor rodando por um período,
        // envie o arquivo e conseguimos ver todos os casos de uma vez.
        private static readonly string EventoLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarketCore",
            "pregao_viva_voz_eventos.log");

        private static readonly object _eventoLogGate = new();

        private static void TryAppendEventoLog(string linha)
        {
            try
            {
                var dir = Path.GetDirectoryName(EventoLogPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                string linhaFinal = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {linha}{Environment.NewLine}";
                lock (_eventoLogGate)
                {
                    File.AppendAllText(EventoLogPath, linhaFinal);
                }
            }
            catch { /* best effort */ }

            // Também grava no arquivo unificado (callbacks + narrações intercalados).
            PregaoVivaVozUnifiedLog.Append("NARRACAO", linha);
        }

        private void OnEventoNarrado(object? sender, NarracaoInfo info)
        {
            // Correlação PERFEITA: callbackInfo veio pareado com a narração desde o Hook.
            // Não depende mais de variável global — não há decorrelação por race de callbacks.
            string texto = info?.Texto ?? string.Empty;
            string? callbackInfo = info?.CallbackInfo;

            string linhaLog = callbackInfo != null
                ? $"🎙️ {texto}  |  📡 {callbackInfo}"
                : $"🎙️ {texto}";

            // Grava no arquivo — sem passar pelo Dispatcher, roda em qualquer thread.
            TryAppendEventoLog(linhaLog);

            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                AdicionarLog(linhaLog);

                if (_engine != null)
                {
                    EventosProcessados = _engine.EventosProcessados;
                    EventosNarrados = _engine.EventosNarrados;
                    RajadasDetectadas = _engine.RajadasDetectadas;
                }
            });
        }
        
        private void OnEstatisticasAtualizadas(object? sender, string status)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                MensagemLog?.Invoke(this, status);
            });
        }
        
        private void AdicionarLog(string texto)
        {
            var linha = $"[{DateTime.Now:HH:mm:ss.fff}] {texto}";
            
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                LogEventos.Insert(0, linha);
                
                // Mantém últimos 100
                while (LogEventos.Count > 100)
                {
                    LogEventos.RemoveAt(LogEventos.Count - 1);
                }
            });
        }
        
        // ============ AÇÕES ============
        
        private void TogglePlayer(PlayerConfig? player)
        {
            if (player == null) return;
            
            player.AtivoHoje = !player.AtivoHoje;
            OnPropertyChanged(nameof(PlayersAtivos));
            OnPropertyChanged(nameof(ResumoPlayers));
            
            // Atualiza no motor se estiver rodando
            _engine?.AtualizarPlayer(player);
        }
        
        public async Task SalvarConfiguracaoAsync()
        {
            try
            {
                await _persistence.SalvarPlayersAsync(Players.ToList());
                await _persistence.SalvarConfigRajadaAsync(ConfigRajada);
                
                int totalAtivos = Players.Count(p => p.AtivoHoje);
                string mensagem = $"Configurações salvas em {DateTime.Now:HH:mm:ss} · {totalAtivos} players ativos";
                MensagemLog?.Invoke(this, "✅ " + mensagem);
                
                // Popup de confirmação visual
                System.Windows.MessageBox.Show(
                    $"✅ Configurações salvas com sucesso!\n\n" +
                    $"📊 {Players.Count} players no catálogo\n" +
                    $"🟢 {totalAtivos} players monitorados\n" +
                    $"⏰ Salvo em: {DateTime.Now:HH:mm:ss}",
                    "Pregão Viva Voz",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MensagemLog?.Invoke(this, $"❌ Erro ao salvar: {ex.Message}");
                System.Windows.MessageBox.Show(
                    $"❌ Erro ao salvar configurações:\n\n{ex.Message}",
                    "Pregão Viva Voz - Erro",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
        
        public void Dispose()
        {
            PararSimulador();
            PararMotor();
        }
    }
}