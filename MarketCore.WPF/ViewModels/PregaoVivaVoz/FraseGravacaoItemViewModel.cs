using MarketCore.WPF.Models.PregaoVivaVoz;

namespace MarketCore.WPF.ViewModels.PregaoVivaVoz
{
    /// <summary>
    /// ViewModel de uma linha da tabela de frases no Studio.
    /// Wrapper reativo sobre o FraseGravacao model.
    /// </summary>
    public class FraseGravacaoItemViewModel : ViewModelBase
    {
        public FraseGravacao Frase { get; }
        
        public FraseGravacaoItemViewModel(FraseGravacao frase)
        {
            Frase = frase;
        }
        
        // ============ PROPRIEDADES DE EXIBIÇÃO ============
        
        public string TextoParaFalar => Frase.TextoParaFalar;
        public string DicaContexto => Frase.DicaContexto;
        public string NomeArquivo => Frase.NomeArquivo;
        public string Id => Frase.Id;
        public bool Prioritaria => Frase.Prioritaria;
        
        // ============ ESTADO ============
        
        public bool Gravado => Frase.Gravado;
        public bool TemErro => Frase.TemErro;
        public string MensagemErro => Frase.MensagemErro;
        
        public string DuracaoTexto
        {
            get
            {
                if (!Frase.Gravado) return "—";
                if (Frase.DuracaoSegundos < 0.1) return "0.0s";
                return $"{Frase.DuracaoSegundos:F1}s";
            }
        }
        
        // ============ ESTADO DE GRAVAÇÃO ============
        
        private bool _gravando = false;
        public bool Gravando
        {
            get => _gravando;
            set 
            { 
                SetProperty(ref _gravando, value);
                OnPropertyChanged(nameof(TextoBotaoGravar));
                OnPropertyChanged(nameof(CorLinha));
                OnPropertyChanged(nameof(BotaoOuvirHabilitado));
            }
        }
        
        // ============ TEXTOS DINÂMICOS ============
        
        public string TextoBotaoGravar
        {
            get
            {
                if (_gravando) return "⏹ Parar";
                if (Frase.Gravado) return "🎙 Regravar";
                return "🎙 Gravar";
            }
        }
        
        public string StatusIcone
        {
            get
            {
                if (TemErro) return "⚠";
                if (Frase.Gravado) return "✓";
                return "…";
            }
        }
        
        public string CorStatus
        {
            get
            {
                if (TemErro) return "#ff4444";
                if (Frase.Gravado) return "#3fb950";
                return "#7d8590";
            }
        }
        
        public string CorLinha
        {
            get
            {
                if (_gravando) return "#0d1a2e";       // azul (gravando)
                if (TemErro) return "#1a0a0a";         // vermelho (erro)
                if (Frase.Gravado) return "#0a1a12";   // verde suave (ok)
                return "#161b22";                       // padrão
            }
        }
        
        public bool BotaoOuvirHabilitado => Frase.Gravado && !_gravando;
        
        // ============ NOTIFICAÇÃO EXTERNA ============
        
        /// <summary>
        /// Notifica que o model interno mudou (ex: após gravar).
        /// </summary>
        public void NotificarMudancasDoModel()
        {
            OnPropertyChanged(nameof(Gravado));
            OnPropertyChanged(nameof(TemErro));
            OnPropertyChanged(nameof(MensagemErro));
            OnPropertyChanged(nameof(DuracaoTexto));
            OnPropertyChanged(nameof(StatusIcone));
            OnPropertyChanged(nameof(CorStatus));
            OnPropertyChanged(nameof(CorLinha));
            OnPropertyChanged(nameof(BotaoOuvirHabilitado));
            OnPropertyChanged(nameof(TextoBotaoGravar));
        }
    }
}
