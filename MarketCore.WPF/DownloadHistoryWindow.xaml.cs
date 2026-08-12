using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MarketCore.FlowSense;
using MarketCore.HistoricalImporter;

namespace MarketCore.WPF;

public partial class DownloadHistoryWindow : Window
{
    private readonly Func<bool> _isProfitDllSessionActive;
    private readonly ProfitCredentials _creds;
    private volatile bool _busy;
    private volatile string _attemptStatusLine = "";

    public DownloadHistoryWindow(Window owner, Func<bool> isProfitDllSessionActive, ProfitCredentials creds)
    {
        Owner = owner;
        _isProfitDllSessionActive = isProfitDllSessionActive;
        _creds = creds;
        InitializeComponent();

        DpEnd.SelectedDate = DateTime.Today;
        DpStart.SelectedDate = DateTime.Today.AddDays(-30);

        var ui = FlowsenseUiSettings.Load();
        CkStartup.IsChecked = ui.ShowHistoryDownloadOnStartup;
        TxTicker.Text = string.IsNullOrWhiteSpace(ui.HistoryDownloadTicker)
            ? "WINFUT"
            : ui.HistoryDownloadTicker.Trim().ToUpperInvariant();

        var dbCfg = AppConfig.Load().Database;
        TxFolder.Text = $"{dbCfg.Host}:{dbCfg.Port}/{dbCfg.Database}";
    }

    private void CkStartup_Changed(object sender, RoutedEventArgs e)
    {
        var s = FlowsenseUiSettings.Load();
        s.ShowHistoryDownloadOnStartup = CkStartup.IsChecked == true;
        s.Save();
    }

    private void SetProgressText(string text)
    {
        if (Dispatcher.CheckAccess())
            TbProgress.Text = text;
        else
            Dispatcher.Invoke(() => TbProgress.Text = text);
    }

    private void RunOnUi(Action action)
    {
        if (Dispatcher.CheckAccess())
            action();
        else
            Dispatcher.Invoke(action);
    }

    private void SetDownloadBusy(bool busy)
    {
        RunOnUi(() =>
        {
            _busy = busy;
            BtnDownload.IsEnabled = !busy;
            TxTicker.IsEnabled = !busy;
        });
    }

    private async void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (DpStart.SelectedDate is not DateTime start || DpEnd.SelectedDate is not DateTime end)
        {
            System.Windows.MessageBox.Show(this, "Indique as duas datas.", "Histórico", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        start = start.Date;
        end = end.Date;
        if (end < start)
        {
            System.Windows.MessageBox.Show(this, "A data final não pode ser anterior à inicial.", "Histórico", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string futTicker = TxTicker.Text.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(futTicker))
        {
            System.Windows.MessageBox.Show(this, "Indique o ticker do ativo (ex.: WINFUT, WINM26, PETR4).", "Histórico", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        {
            var uiSave = FlowsenseUiSettings.Load();
            uiSave.HistoryDownloadTicker = futTicker;
            uiSave.Save();
        }

        var config = AppConfig.Load();
        string dbTarget = $"{config.Database.Host}:{config.Database.Port}/{config.Database.Database}";

        var credsCfg = new ProfitCredentialsConfig
        {
            ActivationKey = _creds.ActivationKey ?? "",
            Username = _creds.Username ?? "",
            Password = _creds.Password ?? ""
        };

        SetDownloadBusy(true);
        SetProgressText("A preparar…");
        App.AppendLifecycle("DownloadHistoryWindow.BtnDownload start");

        MarketCore.HistoricalImporter.HistoricalImporter? sink = null;
        DispatcherTimer? tickUi = null;

        try
        {
            SetProgressText("A preparar PostgreSQL…");
            try
            {
                var setup = new DatabaseSetup(config);
                await setup.EnsureReadyForImportAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                SetProgressText(
                    $"PostgreSQL indisponível.\n{ex.Message}\n\n" +
                    $"Config: {AppConfig.GetDefaultConfigPath()}");
                App.AppendCrashLog(nameof(DownloadHistoryWindow) + ".PostgreSQL", ex);
                return;
            }

            bool loginOk = await ProfitMarketInit.TryEnsureMarketForHistoryAsync(
                credsCfg,
                TimeSpan.FromSeconds(45),
                sessionAlreadyConnected: _isProfitDllSessionActive()).ConfigureAwait(false);

            if (!loginOk)
            {
                SetProgressText("Sem sessão Profit. Faça login no início ou preencha credenciais válidas.");
                return;
            }

            sink = new MarketCore.HistoricalImporter.HistoricalImporter(config);
            sink.SetCurrentContract(futTicker);

            // Subscrever ao status detalhado das tentativas (ticker/bolsa/formato/observação)
            Action<string, string, string, string> attemptHandler = (tk, bolsa, fmt, note) =>
            {
                _attemptStatusLine = $"[{bolsa}] {tk}  fmt={fmt}\n  {note}";
            };
            ProfitHistoryService.AttemptStatus += attemptHandler;

            var swDl = Stopwatch.StartNew();
            tickUi = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            tickUi.Tick += (_, _) =>
            {
                try
                {
                    if (!IsLoaded)
                        return;

                    string attempt = _attemptStatusLine;
                    SetProgressText(
                        $"{futTicker} {start:yyyy-MM-dd} → {end:yyyy-MM-dd}\n\n" +
                        $"A descarregar… {(int)swDl.Elapsed.TotalSeconds}s\n" +
                        $"Recebidos: {sink.TotalAccepted:N0} | Buffer: {sink.TotalBuffered:N0} | Gravados: {sink.TotalFlushed:N0}\n" +
                        (sink.TotalRejected > 0 ? $"Rejeitados pelo factory: {sink.TotalRejected:N0}\n" : "") +
                        (string.IsNullOrWhiteSpace(attempt) ? "" : $"\nTentativa atual:\n{attempt}\n") +
                        "\nBolsa: F (BMF) para futuros; B (Bovespa) para ações no padrão XXXX#. Outros símbolos tentam F e depois B.");
                }
                catch
                {
                    /* timer UI - não propagar */
                }
            };

            SetProgressText(
                $"{futTicker} {start:yyyy-MM-dd} → {end:yyyy-MM-dd}\nA descarregar…");

            // 60 min de teto global - Progress=100 normalmente finaliza em <2 min por dia,
            // este teto é só salvaguarda para casos patológicos (servidor preso).
            using var dlCts = new CancellationTokenSource(TimeSpan.FromMinutes(60));

            tickUi.Start();
            try
            {
                using var history = new ProfitHistoryService(sink);

                int rc = await history.RequestHistoricalDataAsync(futTicker, start, end, dlCts.Token)
                    .ConfigureAwait(false);

                sink.WaitForPendingFlushes(TimeSpan.FromMinutes(2));
                SafeFlush(sink);
                string logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MarketCore",
                    "history_dll.log");

                SetProgressText(
                    $"Concluído.\n{futTicker} {start:yyyy-MM-dd} → {end:yyyy-MM-dd}\n" +
                    $"Total de negócios gravados no PostgreSQL: {sink.TotalFlushed:N0}\n" +
                    $"Recebidos da DLL: {sink.TotalAccepted:N0}" +
                    (sink.TotalRejected > 0 ? $" | Rejeitados pelo factory: {sink.TotalRejected:N0}" : "") +
                    (rc != 0 ? $"\nDLL rc={rc} (ver manual Nelogica)" : "") +
                    FormatFlushError(sink) +
                    $"\n\nLog:\n{logPath}\n\nPostgreSQL:\n{dbTarget}.trades");
            }
            catch (OperationCanceledException)
            {
                sink.WaitForPendingFlushes(TimeSpan.FromMinutes(2));
                SafeFlush(sink);
                SetProgressText(
                    "Pedido terminou por tempo limite (60 min) ou sessão foi cancelada.\n" +
                    $"Negócios gravados até aqui: {sink.TotalFlushed:N0}" +
                    FormatFlushError(sink) +
                    "\n\nSe ficou sempre em \"descarregar\" antes disto: a ProfitDLL pode estar bloqueada em GetHistoryTrades – vê:\n" +
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "MarketCore",
                        "history_dll.log"));
            }
            finally
            {
                var timer = tickUi;
                RunOnUi(() => timer?.Stop());
                ProfitHistoryService.AttemptStatus -= attemptHandler;
                if (sink != null)
                {
                    sink.WaitForPendingFlushes(TimeSpan.FromMinutes(2));
                    SafeFlush(sink);
                }
            }
        }
        catch (Exception ex)
        {
            SetProgressText($"Erro: {ex.Message}");
            if (ex is not OperationCanceledException)
            {
                App.AppendCrashLog(nameof(DownloadHistoryWindow) + ".BtnDownload", ex);
                RunOnUi(() =>
                    System.Windows.MessageBox.Show(
                        this,
                        $"O download falhou.\n\n{ex.Message}",
                        "Histórico",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error));
            }
        }
        finally
        {
            RunOnUi(() => SetDownloadBusy(false));
            App.AppendLifecycle("DownloadHistoryWindow.BtnDownload end");
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static void SafeFlush(MarketCore.HistoricalImporter.HistoricalImporter sink)
    {
        try
        {
            sink.FlushPendingExports();
        }
        catch (Exception ex)
        {
            App.AppendCrashLog(nameof(DownloadHistoryWindow) + ".PostgreSQL.FlushFinal", ex);
        }
    }

    private static string FormatFlushError(MarketCore.HistoricalImporter.HistoricalImporter sink) =>
        string.IsNullOrWhiteSpace(sink.LastFlushError)
            ? ""
            : $"\nÚltimo erro PostgreSQL: {sink.LastFlushError}";
}
