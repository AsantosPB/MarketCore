using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MarketCore.FlowSense;

namespace MarketCore.WPF
{
    public partial class App : Application
    {
        private static readonly string CrashLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarketCore",
            "crash.log");

        /// <summary>Linha cronológica: arranques, fechos de janela, etc. Ajuda a separar falha gerida vs. crash nativo.</summary>
        private static readonly string LifecycleLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarketCore",
            "lifecycle.log");

        public static void AppendLifecycle(string line)
        {
            try
            {
                var dir = Path.GetDirectoryName(LifecycleLogPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(
                    LifecycleLogPath,
                    $"{DateTime.UtcNow:o}\t{line}{Environment.NewLine}");
            }
            catch { /* best effort */ }
        }

        public static void AppendCrashLog(string heading, Exception? ex)
        {
            try
            {
                var dir = Path.GetDirectoryName(CrashLogPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                sb.AppendLine($"==== {DateTime.UtcNow:o} ====");
                sb.AppendLine(heading);
                if (ex != null)
                {
                    sb.AppendLine(ex.ToString());
                }
                sb.AppendLine();
                File.AppendAllText(CrashLogPath, sb.ToString());
            }
            catch { /* best effort */ }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppendLifecycle("Application.OnStartup");

            Current.DispatcherUnhandledException += (_, args) =>
            {
                AppendCrashLog("DispatcherUnhandledException", args.Exception);
                try
                {
                    _ = MessageBox.Show(
                        $"Erro na interface:\n\n{args.Exception?.GetType().Name}: {args.Exception?.Message}\n\n" +
                        $"O programa continuará em execução (erro marcado como tratado).\n" +
                        $"Registo técnico: {CrashLogPath}",
                        "MarketCore — erro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch { /* ignore */ }
                finally
                {
                    // Por omissão o WPF pode encerrar o processo; evita fecho “fantasma” após erro no timer/UI.
                    args.Handled = true;
                }
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                AppendLifecycle($"AppDomain.UnhandledException isTerminating={args.IsTerminating}");
                AppendCrashLog(
                    args.IsTerminating ? "AppDomain.UnhandledException (fatal)" : "AppDomain.UnhandledException",
                    args.ExceptionObject as Exception ?? new InvalidOperationException(args.ExceptionObject?.ToString()));
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                AppendCrashLog("TaskScheduler.UnobservedTaskException", e.Exception);
                e.SetObserved();
            };

            Current.Exit += (_, ev) =>
            {
                AppendLifecycle($"Application.Exit exitCode={ev.ApplicationExitCode}");
            };

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                AppendLifecycle("ProcessExit");
            };

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

                AppendLifecycle("MainWindow.Show completed");
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
