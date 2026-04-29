using System.Windows;
using MarketCore.FlowSense;

namespace MarketCore.WPF
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                var loginWindow = new ProfitLoginWindow();
                bool? result    = loginWindow.ShowDialog();

                if (result != true)
                {
                    Shutdown();
                    return;
                }

                var mainWindow = new MainWindow(loginWindow.Credentials, loginWindow.IsRealMarket);

                // Encerra o app quando a MainWindow for fechada
                mainWindow.Closed += (s, args) => Shutdown();

                mainWindow.Show();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(
                    $"Erro ao iniciar:\n\n{ex.GetType().Name}\n{ex.Message}\n\n{ex.StackTrace}",
                    "Erro de inicialização",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
            }
        }
    }
}
