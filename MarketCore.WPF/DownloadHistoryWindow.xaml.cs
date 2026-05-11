using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using MarketCore.FlowSense;
using MarketCore.HistoricalImporter;

namespace MarketCore.WPF;

public partial class DownloadHistoryWindow : Window
{
    private readonly Func<bool> _isProfitDllSessionActive;
    private readonly ProfitCredentials _creds;
    private volatile bool _busy;

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

        string def = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "MarketCoreHistoricoWIN");
        TxFolder.Text = def;
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        using var d = new FolderBrowserDialog
        {
            Description = "Pasta onde guardar os ficheiros CSV",
            SelectedPath = Directory.Exists(TxFolder.Text) ? TxFolder.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ShowNewFolderButton = true
        };

        if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TxFolder.Text = d.SelectedPath;
    }

    private void CkStartup_Changed(object sender, RoutedEventArgs e)
    {
        var s = FlowsenseUiSettings.Load();
        s.ShowHistoryDownloadOnStartup = CkStartup.IsChecked == true;
        s.Save();
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

        string folder = TxFolder.Text.Trim();
        if (string.IsNullOrEmpty(folder))
        {
            System.Windows.MessageBox.Show(this, "Escolha uma pasta para gravar os CSV.", "Histórico", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Não foi possível usar a pasta:\n{ex.Message}", "Histórico", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var credsCfg = new ProfitCredentialsConfig
        {
            ActivationKey = _creds.ActivationKey ?? "",
            Username = _creds.Username ?? "",
            Password = _creds.Password ?? ""
        };

        BtnDownload.IsEnabled = BtnBrowse.IsEnabled = TxTicker.IsEnabled = false;
        _busy = true;
        TbProgress.Text = "A preparar…";

        try
        {
            bool loginOk = await ProfitMarketInit.TryEnsureMarketForHistoryAsync(
                credsCfg,
                TimeSpan.FromSeconds(45),
                sessionAlreadyConnected: _isProfitDllSessionActive());

            if (!loginOk)
            {
                TbProgress.Text = "Sem sessão Profit. Faça login no início ou preencha credenciais válidas.";
                return;
            }

            string sessionLabel = $"{futTicker}_{start:yyyyMMdd}_{end:yyyyMMdd}";
            string folderCaptured = folder;

            // ===== ALTERAÇÃO: Gravar direto no PostgreSQL ao invés de CSV =====
            var config = new AppConfig
            {
                Database = new DatabaseConfig
                {
                    Host = "127.0.0.1",
                    Port = 5432,
                    Database = "marketcore_historical",
                    Username = "postgres",
                    Password = "postgres"
                }
            };
            var sink = new MarketCore.HistoricalImporter.HistoricalImporter(config);
            sink.SetCurrentContract(futTicker);
            // ===== FIM DA ALTERAÇÃO =====

            var swDl = Stopwatch.StartNew();
            var tickUi = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            tickUi.Tick += (_, _) =>
            {
                long buffered = sink.TotalBuffered;
                long flushed = sink.TotalFlushed;
                TbProgress.Text =
                    $"{futTicker} {start:yyyy-MM-dd} → {end:yyyy-MM-dd}\n\n" +
                    $"A descarregar… {(int)swDl.Elapsed.TotalSeconds}s\n" +
                    $"Buffer: {buffered:N0} | Gravados: {flushed:N0}\n\n" +
                    "Nota: a DLL pode ficar vários segundos bloqueada à espera da Nelogica antes de aparecer qualquer negócio.";
            };

            TbProgress.Text =
                $"{futTicker} {start:yyyy-MM-dd} → {end:yyyy-MM-dd}\nA descarregar…";

            using var dlCts = new CancellationTokenSource(TimeSpan.FromMinutes(45));

            tickUi.Start();
            try
            {
                using var history = new ProfitHistoryService(sink);

                int rc = await history.RequestHistoricalDataAsync(futTicker, start, end, dlCts.Token);

                sink.FlushPendingExports();
                string logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MarketCore",
                    "history_dll.log");

                TbProgress.Text =
                    $"Concluído.\n{futTicker} {start:yyyy-MM-dd} → {end:yyyy-MM-dd}\n" +
                    $"Total de negócios gravados no PostgreSQL: {sink.TotalFlushed:N0}" +
                    (rc != 0 ? $"\nDLL rc={rc} (ver manual Nelogica)" : "") +
                    $"\n\nLog:\n{logPath}\n\nDados gravados em:\nPostgreSQL: marketcore_historical.trades";
            }
            catch (OperationCanceledException)
            {
                sink.FlushPendingExports();
                TbProgress.Text =
                    "Pedido terminou por tempo limite (45 min) ou sessão foi cancelada.\n" +
                    $"Negócios gravados até aqui: {sink.TotalFlushed:N0}\n\n" +
                    "Se ficou sempre em \"descarregar\" antes disto: a ProfitDLL pode estar bloqueada em GetHistoryTrades – vê:\n" +
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "MarketCore",
                        "history_dll.log");
            }
            finally
            {
                tickUi.Stop();
                sink.FlushPendingExports();
            }
        }
        catch (Exception ex)
        {
            TbProgress.Text = $"Erro: {ex.Message}";
            if (ex is not OperationCanceledException)
                App.AppendCrashLog(nameof(DownloadHistoryWindow) + ".BtnDownload", ex);
        }
        finally
        {
            BtnDownload.IsEnabled = BtnBrowse.IsEnabled = TxTicker.IsEnabled = true;
            _busy = false;
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
