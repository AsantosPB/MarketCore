using System;
using System.Windows;
using MarketCore.WPF.ViewModels.PregaoVivaVoz;

namespace MarketCore.WPF.Views.PregaoVivaVoz
{
    /// <summary>
    /// Janela do Studio de Gravação — ZIP 3.
    /// </summary>
    public partial class StudioGravacaoWindow : Window
    {
        private readonly StudioGravacaoViewModel _viewModel;
        
        public StudioGravacaoWindow()
        {
            InitializeComponent();
            
            _viewModel = new StudioGravacaoViewModel();
            DataContext = _viewModel;
            
            _viewModel.MensagemStatus += OnMensagemStatus;
            
            Closing += (s, e) =>
            {
                try
                {
                    _viewModel.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Studio] Erro ao fechar: {ex.Message}");
                }
            };
        }
        
        private void OnMensagemStatus(object? sender, string mensagem)
        {
            Dispatcher.InvokeAsync(() =>
            {
                StatusText.Text = $"[{DateTime.Now:HH:mm:ss}] {mensagem}";
            });
        }
    }
}
