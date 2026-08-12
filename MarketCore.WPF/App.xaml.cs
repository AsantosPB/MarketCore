using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MarketCore.FlowSense;
using MarketCore.WPF.Services.PregaoVivaVoz;

namespace MarketCore.WPF
{
    public partial class App : Application
    {
        private const string SingleInstanceMutexName = "Local\\MarketCore.FlowSense.SingleInstance";
        private static Mutex? _singleInstanceMutex;

        /// <summary>
        /// Ponte entre os callbacks reais da ProfitDLL e o motor do Pregão Viva Voz.
        /// Criada quando o motor do PVV é iniciado; nula quando parado.
        /// Os callbacks da ProfitDLL fazem `App.PregaoVivaVozBridge?.OnTradeReceived(...)`
        /// e o próprio bridge descarta eventos com zero overhead se o motor estiver parado.
        /// </summary>
        public static ProfitDLLBridge? PregaoVivaVozBridge { get; set; }
        /// <summary>Evita avalanche de MessageBox quando o mesmo erro dispara todos os ticks (ex.: timers ~30 Hz).</summary>
        private static long _lastDispatcherErrorDialogTicks;
        private const int DispatcherErrorDialogCooldownMs = 4500;

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

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            AppendLifecycle($"Application.SessionEnding reason={e.ReasonSessionEnding}");
            base.OnSessionEnding(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppendLifecycle($"Application.OnExit pid={Environment.ProcessId} exitCode={e.ApplicationExitCode}");
            ReleaseSingleInstanceMutex();
            base.OnExit(e);
        }

        private static void ReleaseSingleInstanceMutex()
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch { /* ignore */ }
            finally
            {
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = null;
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppendLifecycle($"Application.OnStartup pid={Environment.ProcessId}");

            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                AppendLifecycle("Application.SecondInstanceRejected");
                bool focused = TryFocusExistingInstance();
                MessageBox.Show(
                    focused
                        ? "O MarketCore já estava aberto - a janela existente foi trazida para a frente."
                        : "O MarketCore já está em execução.\n\nFeche a outra janela antes de abrir de novo.",
                    "MarketCore",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown(0);
                return;
            }

            Current.DispatcherUnhandledException += (_, args) =>
            {
                AppendCrashLog("DispatcherUnhandledException", args.Exception);
                try
                {
                    long now = Environment.TickCount64;
                    if (now - _lastDispatcherErrorDialogTicks >= DispatcherErrorDialogCooldownMs)
                    {
                        _lastDispatcherErrorDialogTicks = now;
                        _ = MessageBox.Show(
                            $"Erro na interface:\n\n{args.Exception?.GetType().Name}: {args.Exception?.Message}\n\n" +
                            $"O programa continuará em execução (erro marcado como tratado).\n" +
                            $"Registo técnico: {CrashLogPath}",
                            "MarketCore - erro",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                    else
                    {
                        AppendCrashLog("DispatcherUnhandledException repeated (dialog suppressed)", args.Exception);
                    }
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
                    AppendLifecycle("Login cancelled");
                    Shutdown();
                    return;
                }

                var mainWindow = new MainWindow(loginWindow.Credentials, loginWindow.IsRealMarket);

                // Encerra o app quando a MainWindow for fechada
                mainWindow.Closed += (_, _) =>
                {
                    AppendLifecycle("MainWindow.Closed");
                    Shutdown();
                };

                Current.MainWindow = mainWindow;
                mainWindow.Show();
                mainWindow.Activate();
                mainWindow.Focus();

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

        private static bool TryFocusExistingInstance()
        {
            int self = Environment.ProcessId;
            string processName = Process.GetCurrentProcess().ProcessName;

            foreach (Process process in Process.GetProcessesByName(processName))
            {
                if (process.Id == self)
                    continue;

                try
                {
                    process.Refresh();
                    IntPtr handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero)
                        continue;

                    ShowWindowAsync(handle, SwRestore);
                    return SetForegroundWindow(handle);
                }
                catch
                {
                    /* best effort */
                }
            }

            return false;
        }

        private const int SwRestore = 9;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    }
}
