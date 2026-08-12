using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using MarketCore.WPF.Models.PregaoVivaVoz;
using MarketCore.WPF.Services.PregaoVivaVoz;

namespace MarketCore.WPF.ViewModels.PregaoVivaVoz
{
    /// <summary>
    /// ViewModel principal do Studio de Gravação.
    /// Gerencia categorias, frases selecionadas, gravação e reprodução.
    /// </summary>
    public class StudioGravacaoViewModel : ViewModelBase, IDisposable
    {
        private readonly ConfigPersistenceService _persistence;
        private readonly AudioRecorderService _recorder;
        private readonly AudioProcessorService _processor;
        private readonly AudioPlaybackService _playback;
        
        private List<FraseGravacao> _todasFrases = new();
        private List<PlayerConfig> _todosPlayers = new();
        
        // ============ CATEGORIAS ============
        
        /// <summary>Lista de categorias exibidas na coluna esquerda.</summary>
        public ObservableCollection<CategoriaItem> Categorias { get; } = new();
        
        private CategoriaItem? _categoriaAtiva;
        public CategoriaItem? CategoriaAtiva
        {
            get => _categoriaAtiva;
            set 
            { 
                if (SetProperty(ref _categoriaAtiva, value))
                {
                    AtualizarFrasesDaCategoria();
                    OnPropertyChanged(nameof(TituloPainel));
                    OnPropertyChanged(nameof(SubtituloPainel));
                }
            }
        }
        
        // ============ FRASES ============
        
        /// <summary>Frases da categoria selecionada.</summary>
        public ObservableCollection<FraseGravacaoItemViewModel> FrasesDaCategoria { get; } = new();
        
        // ============ ESTADO ============
        
        private string _tituloPainel = "Selecione uma categoria";
        public string TituloPainel
        {
            get => _tituloPainel;
            set => SetProperty(ref _tituloPainel, value);
        }
        
        private string _subtituloPainel = "Clique numa categoria à esquerda pra começar";
        public string SubtituloPainel
        {
            get => _subtituloPainel;
            set => SetProperty(ref _subtituloPainel, value);
        }
        
        private int _totalGravados = 0;
        public int TotalGravados
        {
            get => _totalGravados;
            set 
            { 
                SetProperty(ref _totalGravados, value);
                OnPropertyChanged(nameof(PercentualGeral));
                OnPropertyChanged(nameof(TextoProgressoGeral));
            }
        }
        
        private int _totalFrases = 0;
        public int TotalFrases
        {
            get => _totalFrases;
            set 
            { 
                SetProperty(ref _totalFrases, value);
                OnPropertyChanged(nameof(PercentualGeral));
                OnPropertyChanged(nameof(TextoProgressoGeral));
            }
        }
        
        public int PercentualGeral => _totalFrases == 0 ? 0 : (_totalGravados * 100 / _totalFrases);
        public string TextoProgressoGeral => $"{_totalGravados} / {_totalFrases} · {PercentualGeral}%";
        
        // ============ MICROFONE ============
        
        private string[] _dispositivos = Array.Empty<string>();
        public string[] Dispositivos
        {
            get => _dispositivos;
            set => SetProperty(ref _dispositivos, value);
        }
        
        private int _dispositivoSelecionado = 0;
        public int DispositivoSelecionado
        {
            get => _dispositivoSelecionado;
            set => SetProperty(ref _dispositivoSelecionado, value);
        }
        
        private float _nivelAudioAtual = 0f;
        public float NivelAudioAtual
        {
            get => _nivelAudioAtual;
            set 
            { 
                SetProperty(ref _nivelAudioAtual, value);
                OnPropertyChanged(nameof(NivelAudioPercentual));
            }
        }
        
        public int NivelAudioPercentual => (int)(_nivelAudioAtual * 100);
        
        // ============ FRASE EM GRAVAÇÃO ============
        
        private FraseGravacaoItemViewModel? _fraseGravando;
        public FraseGravacaoItemViewModel? FraseGravando
        {
            get => _fraseGravando;
            set => SetProperty(ref _fraseGravando, value);
        }
        
        // ============ COMANDOS ============
        
        public ICommand SelecionarCategoriaCommand { get; }
        public ICommand IniciarGravacaoCommand { get; }
        public ICommand PararGravacaoCommand { get; }
        public ICommand OuvirFraseCommand { get; }
        public ICommand DeletarFraseCommand { get; }
        public ICommand OuvirTudoCommand { get; }
        public ICommand LimparBuscaCommand { get; }
        public ICommand ToggleGravacaoCommand { get; }
        public ICommand AtualizarDispositivosCommand { get; }
        
        // ============ EVENTOS ============
        
        public event EventHandler<string>? MensagemStatus;
        
        // ============ CONSTRUTOR ============
        
        public StudioGravacaoViewModel()
        {
            _persistence = new ConfigPersistenceService();
            _recorder = new AudioRecorderService();
            _processor = new AudioProcessorService();
            _playback = new AudioPlaybackService();
            
            _recorder.NivelAudioMudou += (s, nivel) =>
            {
                // NAudio dispara este callback em thread de áudio do Windows —
                // usar InvokeAsync (não-bloqueante) para não travar no shutdown.
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    NivelAudioAtual = nivel;
                });
            };
            
            SelecionarCategoriaCommand = new RelayCommand(param => CategoriaAtiva = param as CategoriaItem);
            IniciarGravacaoCommand = new RelayCommand(param => IniciarGravacao(param as FraseGravacaoItemViewModel));
            PararGravacaoCommand = new RelayCommand(_ => PararGravacao());
            ToggleGravacaoCommand = new RelayCommand(param => ToggleGravacao(param as FraseGravacaoItemViewModel));
            OuvirFraseCommand = new RelayCommand(param => OuvirFrase(param as FraseGravacaoItemViewModel));
            DeletarFraseCommand = new RelayCommand(param => DeletarFrase(param as FraseGravacaoItemViewModel));
            OuvirTudoCommand = new RelayCommand(_ => OuvirTudo());
            LimparBuscaCommand = new RelayCommand(_ => { });
            AtualizarDispositivosCommand = new RelayCommand(_ => AtualizarDispositivos());
            
            _playback.Iniciar();
            
            _ = InicializarAsync();
        }
        
        // ============ INICIALIZAÇÃO ============
        
        private async Task InicializarAsync()
        {
            try
            {
                // Carrega players e frases
                _todosPlayers = await _persistence.CarregarPlayersAsync();
                _todasFrases = await _persistence.CarregarFrasesAsync();
                
                // Dispositivos de áudio - carrega e sempre seleciona o primeiro
                AtualizarDispositivos();
                
                // Constrói categorias
                ConstruirCategorias();
                
                // Verifica arquivos já gravados no disco
                await VerificarArquivosGravadosAsync();
                
                // Recalcula totais
                RecalcularTotais();
                
                MensagemStatus?.Invoke(this, $"{_todasFrases.Count} frases carregadas · {_totalGravados} já gravadas · {Dispositivos.Length} mic(s)");
            }
            catch (Exception ex)
            {
                MensagemStatus?.Invoke(this, $"Erro na inicialização: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Recarrega a lista de dispositivos de entrada de áudio.
        /// Chamado no init e pelo botão "🔄 Atualizar mics".
        /// </summary>
        public void AtualizarDispositivos()
        {
            try
            {
                var lista = AudioRecorderService.ListarDispositivosEntrada();
                
                // Fallback DE SEGURANÇA - nunca deixa lista vazia
                if (lista == null || lista.Length == 0)
                {
                    lista = new[] { "⚠ Nenhum dispositivo encontrado" };
                }
                
                Dispositivos = lista;
                
                // Sempre seleciona o primeiro item (índice 0)
                if (DispositivoSelecionado < 0 || DispositivoSelecionado >= lista.Length)
                {
                    DispositivoSelecionado = 0;
                }
                
                Console.WriteLine($"[StudioVM] Dispositivos carregados: {lista.Length} · Selecionado índice: {DispositivoSelecionado}");
                MensagemStatus?.Invoke(this, $"🎤 {lista.Length} dispositivo(s) detectado(s) · usando: {lista[DispositivoSelecionado]}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StudioVM] Erro em AtualizarDispositivos: {ex.Message}");
                Dispositivos = new[] { $"⚠ Erro: {ex.Message}" };
                DispositivoSelecionado = 0;
                MensagemStatus?.Invoke(this, $"Erro ao listar dispositivos: {ex.Message}");
            }
        }
        
        private void ConstruirCategorias()
        {
            Categorias.Clear();
            
            // Base compartilhada
            AdicionarCategoria("🎯 Números", CategoriaFrase.Numero, "base");
            AdicionarCategoria("Níveis L2-L5", CategoriaFrase.Nivel, "base");
            AdicionarCategoria("Complementos", CategoriaFrase.Complemento, "base");
            AdicionarCategoria("Alertas de Rajada", CategoriaFrase.AlertaRajada, "base");
            
            // Players (agrupa todas as frases de cada player)
            foreach (var player in _todosPlayers.OrderBy(p => p.Nome))
            {
                var frasesPlayer = _todasFrases.Where(f => f.PlayerChave == player.Chave).ToList();
                if (frasesPlayer.Count > 0)
                {
                    Categorias.Add(new CategoriaItem
                    {
                        Titulo = $"{player.Bandeira} {player.Nome}",
                        PlayerChave = player.Chave,
                        Grupo = "player",
                        Frases = frasesPlayer
                    });
                }
            }
        }
        
        private void AdicionarCategoria(string titulo, CategoriaFrase cat, string grupo)
        {
            var frases = _todasFrases.Where(f => f.Categoria == cat && string.IsNullOrEmpty(f.PlayerChave)).ToList();
            
            Categorias.Add(new CategoriaItem
            {
                Titulo = titulo,
                CategoriaFrase = cat,
                Grupo = grupo,
                Frases = frases
            });
        }
        
        private async Task VerificarArquivosGravadosAsync()
        {
            var dirBase = _persistence.GetDiretorioAudio();
            
            foreach (var frase in _todasFrases)
            {
                var caminho = ConstruirCaminhoArquivo(frase, dirBase);
                frase.CaminhoCompleto = caminho;
                
                if (File.Exists(caminho))
                {
                    var info = new FileInfo(caminho);
                    if (info.Length > 0)
                    {
                        frase.Gravado = true;
                        frase.DataGravacao = info.LastWriteTime;
                        
                        // Análise rápida
                        var diag = await Task.Run(() => _processor.Analisar(caminho));
                        frase.DuracaoSegundos = diag.DuracaoSegundos;
                        frase.TemErro = diag.TemProblema;
                        frase.MensagemErro = diag.MensagemProblema;
                    }
                }
            }
        }
        
        private string ConstruirCaminhoArquivo(FraseGravacao frase, string dirBase)
        {
            string subpasta = frase.Categoria switch
            {
                CategoriaFrase.Numero => "Numeros",
                CategoriaFrase.Nivel => "Niveis",
                CategoriaFrase.Complemento => "Complementos",
                CategoriaFrase.AlertaRajada => "AlertasRajada",
                _ => "Players"
            };
            
            if (!string.IsNullOrEmpty(frase.PlayerChave))
            {
                // É frase de player - vai pra Players/<Nome>/
                var pasta = char.ToUpper(frase.PlayerChave[0]) + frase.PlayerChave.Substring(1);
                return Path.Combine(dirBase, "Players", pasta, frase.NomeArquivo);
            }
            
            return Path.Combine(dirBase, subpasta, frase.NomeArquivo);
        }
        
        // ============ ATUALIZAÇÃO DE CATEGORIA ============
        
        private void AtualizarFrasesDaCategoria()
        {
            FrasesDaCategoria.Clear();
            
            if (_categoriaAtiva == null) return;
            
            TituloPainel = _categoriaAtiva.Titulo;
            
            var frases = _categoriaAtiva.Frases;
            int gravados = frases.Count(f => f.Gravado);
            
            SubtituloPainel = $"{frases.Count} frases · {gravados} gravadas · botão azul Ouvir + botão vermelho Gravar";
            
            foreach (var frase in frases)
            {
                var vm = new FraseGravacaoItemViewModel(frase);
                FrasesDaCategoria.Add(vm);
            }
        }
        
        // ============ AÇÕES DE GRAVAÇÃO ============
        
        /// <summary>
        /// Comando único: decide se inicia ou para a gravação baseado no estado atual.
        /// Muito mais confiável que trocar Command via DataTrigger.
        /// </summary>
        private void ToggleGravacao(FraseGravacaoItemViewModel? item)
        {
            if (item == null) return;
            
            // Se este item já está gravando → PARA
            if (item.Gravando)
            {
                PararGravacao();
                return;
            }
            
            // Se outro item está gravando → ignora (usuário precisa parar o outro primeiro)
            if (FraseGravando != null && FraseGravando != item)
            {
                MensagemStatus?.Invoke(this, "Já está gravando outra frase - pare primeiro");
                return;
            }
            
            // Ninguém gravando → INICIA
            IniciarGravacao(item);
        }
        
        private void IniciarGravacao(FraseGravacaoItemViewModel? item)
        {
            if (item == null) return;
            
            if (FraseGravando != null)
            {
                MensagemStatus?.Invoke(this, "Já está gravando outra frase - pare primeiro");
                return;
            }
            
            try
            {
                var frase = item.Frase;
                var dirBase = _persistence.GetDiretorioAudio();
                var caminho = ConstruirCaminhoArquivo(frase, dirBase);
                
                // Garante diretório
                var dir = Path.GetDirectoryName(caminho);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                
                if (_recorder.Iniciar(caminho, DispositivoSelecionado))
                {
                    item.Gravando = true;
                    FraseGravando = item;
                    MensagemStatus?.Invoke(this, $"🎙️ Gravando: \"{frase.TextoParaFalar}\"");
                }
                else
                {
                    MensagemStatus?.Invoke(this, "Erro ao iniciar gravação");
                }
            }
            catch (Exception ex)
            {
                MensagemStatus?.Invoke(this, $"Erro: {ex.Message}");
            }
        }
        
        private void PararGravacao()
        {
            if (FraseGravando == null) return;
            
            try
            {
                var item = FraseGravando;
                var caminho = _recorder.Parar();
                
                item.Gravando = false;
                FraseGravando = null;
                NivelAudioAtual = 0;
                
                if (caminho != null && File.Exists(caminho))
                {
                    // Análise pós-gravação
                    var diag = _processor.Analisar(caminho);
                    
                    item.Frase.Gravado = true;
                    item.Frase.CaminhoCompleto = caminho;
                    item.Frase.DuracaoSegundos = diag.DuracaoSegundos;
                    item.Frase.DataGravacao = DateTime.Now;
                    item.Frase.TemErro = diag.TemProblema;
                    item.Frase.MensagemErro = diag.MensagemProblema;
                    
                    item.NotificarMudancasDoModel();
                    
                    if (diag.TemProblema)
                    {
                        MensagemStatus?.Invoke(this, $"⚠️ Gravado com aviso: {diag.MensagemProblema}");
                    }
                    else
                    {
                        MensagemStatus?.Invoke(this, $"✅ Salvo: {Path.GetFileName(caminho)} ({diag.DuracaoSegundos:F1}s)");
                    }
                    
                    RecalcularTotais();
                }
            }
            catch (Exception ex)
            {
                MensagemStatus?.Invoke(this, $"Erro ao parar: {ex.Message}");
            }
        }
        
        // ============ AÇÕES DE REPRODUÇÃO ============
        
        private void OuvirFrase(FraseGravacaoItemViewModel? item)
        {
            if (item == null || !item.Frase.Gravado) return;
            
            try
            {
                var lista = new List<string> { item.Frase.CaminhoCompleto };
                _playback.Enfileirar(lista, item.Frase.TextoParaFalar);
                MensagemStatus?.Invoke(this, $"▶ Ouvindo: \"{item.Frase.TextoParaFalar}\"");
            }
            catch (Exception ex)
            {
                MensagemStatus?.Invoke(this, $"Erro ao ouvir: {ex.Message}");
            }
        }
        
        private void DeletarFrase(FraseGravacaoItemViewModel? item)
        {
            if (item == null || !item.Frase.Gravado) return;
            
            try
            {
                if (File.Exists(item.Frase.CaminhoCompleto))
                {
                    File.Delete(item.Frase.CaminhoCompleto);
                }
                
                item.Frase.Gravado = false;
                item.Frase.DuracaoSegundos = 0;
                item.Frase.DataGravacao = null;
                item.Frase.TemErro = false;
                item.Frase.MensagemErro = "";
                
                item.NotificarMudancasDoModel();
                RecalcularTotais();
                
                MensagemStatus?.Invoke(this, $"🗑️ Deletado: {item.Frase.NomeArquivo}");
            }
            catch (Exception ex)
            {
                MensagemStatus?.Invoke(this, $"Erro ao deletar: {ex.Message}");
            }
        }
        
        private void OuvirTudo()
        {
            if (_categoriaAtiva == null) return;
            
            try
            {
                var gravados = _categoriaAtiva.Frases.Where(f => f.Gravado).ToList();
                if (gravados.Count == 0)
                {
                    MensagemStatus?.Invoke(this, "Nenhum áudio gravado nesta categoria ainda");
                    return;
                }
                
                foreach (var f in gravados)
                {
                    _playback.Enfileirar(new List<string> { f.CaminhoCompleto }, f.TextoParaFalar);
                }
                
                MensagemStatus?.Invoke(this, $"▶ Reproduzindo {gravados.Count} clips em sequência");
            }
            catch (Exception ex)
            {
                MensagemStatus?.Invoke(this, $"Erro ao ouvir tudo: {ex.Message}");
            }
        }
        
        // ============ HELPERS ============
        
        private void RecalcularTotais()
        {
            TotalGravados = _todasFrases.Count(f => f.Gravado);
            TotalFrases = _todasFrases.Count;
            
            // Recalcula contadores das categorias
            foreach (var cat in Categorias)
            {
                cat.NotificarContadores();
            }
        }
        
        public void Dispose()
        {
            _recorder?.Dispose();
            _playback?.Dispose();
        }
    }
    
    /// <summary>
    /// Representa uma categoria na coluna esquerda do Studio.
    /// </summary>
    public class CategoriaItem : ViewModelBase
    {
        public string Titulo { get; set; } = "";
        public string PlayerChave { get; set; } = "";
        public string Grupo { get; set; } = "";
        public CategoriaFrase? CategoriaFrase { get; set; }
        public List<FraseGravacao> Frases { get; set; } = new();
        
        public int TotalGravadas => Frases.Count(f => f.Gravado);
        public int Total => Frases.Count;
        public string Contador => $"{TotalGravadas}/{Total}";
        
        public string StatusVisual
        {
            get
            {
                if (Total == 0) return "vazia";
                if (TotalGravadas == Total) return "completa";
                if (TotalGravadas > 0) return "parcial";
                return "pendente";
            }
        }
        
        public void NotificarContadores()
        {
            OnPropertyChanged(nameof(TotalGravadas));
            OnPropertyChanged(nameof(Contador));
            OnPropertyChanged(nameof(StatusVisual));
        }
    }
}
