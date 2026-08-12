using System;
using System.Windows;
using MarketCore.WPF.ViewModels.PregaoVivaVoz;

namespace MarketCore.WPF.Views.PregaoVivaVoz
{
    /// <summary>
    /// Janela principal do módulo Pregão Viva Voz — ZIP 3.
    /// Agora abre o Studio de Gravação de verdade.
    /// </summary>
    public partial class PregaoVivaVozWindow : Window
    {
        private readonly PregaoVivaVozViewModel _viewModel;
        private StudioGravacaoWindow? _studioWindow;
        
        public PregaoVivaVozWindow() : this(isRealMarket: false) { }

        public PregaoVivaVozWindow(bool isRealMarket)
        {
            InitializeComponent();

            _viewModel = new PregaoVivaVozViewModel { ModoMercadoReal = isRealMarket };
            DataContext = _viewModel;
            
            _viewModel.MensagemLog += OnMensagemLog;
            _viewModel.AbrirStudioSolicitado += OnAbrirStudio;
            
            Closing += async (s, e) =>
            {
                try
                {
                    // Fecha o Studio se estiver aberto
                    _studioWindow?.Close();
                    _studioWindow = null;

                    // Dispose ANTES do await: cancela as threads de background
                    // imediatamente, mesmo que o save abaixo não complete
                    // (ex.: Dispatcher já em shutdown). Isso evita o hang.
                    _viewModel.Dispose();

                    await _viewModel.SalvarConfiguracaoAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PregaoVivaVoz] Erro ao fechar: {ex.Message}");
                }
            };
        }
        
        private void OnMensagemLog(object? sender, string mensagem)
        {
            Dispatcher.InvokeAsync(() =>
            {
                StatusText.Text = $"[{DateTime.Now:HH:mm:ss}] {mensagem}";
            });
        }
        
        private void OnAbrirStudio(object? sender, EventArgs e)
        {
            try
            {
                // Se já tá aberto, só ativa
                if (_studioWindow != null && _studioWindow.IsLoaded)
                {
                    _studioWindow.Activate();
                    _studioWindow.Focus();
                    return;
                }
                
                _studioWindow = new StudioGravacaoWindow();
                _studioWindow.Owner = this;
                
                _studioWindow.Closed += (s, e) =>
                {
                    _studioWindow = null;
                };
                
                _studioWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Erro ao abrir Studio de Gravação:\n\n{ex.Message}",
                    "Erro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
