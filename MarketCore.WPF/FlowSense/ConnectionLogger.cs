using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace MarketCore.FlowSense
{
    /// <summary>
    /// Logger de conexão — registra eventos de conexão, desconexão,
    /// instabilidades, latência e qualidade geral da sessão.
    ///
    /// Arquivo salvo em: %AppData%\MarketCore\Logs\conexao_YYYYMMDD.log
    /// Rotação diária automática — 1 arquivo por dia.
    /// </summary>
    public class ConnectionLogger
    {
        // ══════════════════════════════════════════════════════════════════
        // CONFIGURAÇÃO
        // ══════════════════════════════════════════════════════════════════

        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MarketCore", "Logs");

        private string LogFile => Path.Combine(LogDir,
            $"conexao_{DateTime.Now:yyyyMMdd}.log");

        // ══════════════════════════════════════════════════════════════════
        // ESTADO DA SESSÃO
        // ══════════════════════════════════════════════════════════════════

        private DateTime?  _sessionStart;
        private DateTime?  _lastConnected;
        private DateTime?  _lastDisconnect;
        private int        _reconnectCount     = 0;
        private int        _tradeCount         = 0;
        private int        _bookUpdateCount    = 0;
        private int        _dropCount          = 0;        // perdas de conexão
        private double     _totalDowntimeMs    = 0;
        private readonly List<double> _latencySamples = new(1000);
        private readonly object _lock = new();

        // Ticker/modo atual
        private string _ticker  = "";
        private string _mode    = "";

        // ══════════════════════════════════════════════════════════════════
        // INICIALIZAÇÃO
        // ══════════════════════════════════════════════════════════════════

        public ConnectionLogger()
        {
            Directory.CreateDirectory(LogDir);
        }

        // ══════════════════════════════════════════════════════════════════
        // EVENTOS DE SESSÃO
        // ══════════════════════════════════════════════════════════════════

        public void LogSessionStart(string ticker, string mode)
        {
            _ticker        = ticker;
            _mode          = mode;
            _sessionStart  = DateTime.Now;
            _reconnectCount = 0;
            _tradeCount    = 0;
            _bookUpdateCount = 0;
            _dropCount     = 0;
            _totalDowntimeMs = 0;
            _latencySamples.Clear();

            Write("═══════════════════════════════════════════════════════════");
            Write($"  SESSÃO INICIADA — {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
            Write($"  Modo   : {mode}");
            Write($"  Ticker : {ticker}");
            Write($"  Log    : {LogFile}");
            Write("═══════════════════════════════════════════════════════════");
        }

        public void LogSessionEnd()
        {
            var duration = _sessionStart.HasValue
                ? DateTime.Now - _sessionStart.Value
                : TimeSpan.Zero;

            var uptime = duration.TotalMilliseconds > 0
                ? (1 - _totalDowntimeMs / duration.TotalMilliseconds) * 100
                : 100;

            Write("");
            Write("───────────────────────────────────────────────────────────");
            Write($"  SESSÃO ENCERRADA — {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
            Write($"  Duração total  : {duration:hh\\:mm\\:ss}");
            Write($"  Uptime         : {uptime:F1}%");
            Write($"  Reconexões     : {_reconnectCount}");
            Write($"  Trades recebidos: {_tradeCount:N0}");
            Write($"  Updates book   : {_bookUpdateCount:N0}");
            if (_latencySamples.Count > 0)
            {
                Write($"  Latência média : {_latencySamples.Average():F1}ms");
                Write($"  Latência máx   : {_latencySamples.Max():F1}ms");
            }
            Write("───────────────────────────────────────────────────────────");
            Write("");
        }

        // ══════════════════════════════════════════════════════════════════
        // EVENTOS DE CONEXÃO
        // ══════════════════════════════════════════════════════════════════

        public void LogConnecting()
        {
            Write($"[CONECTANDO ] Tentando conectar ao servidor Nelogica...");
        }

        public void LogConnected(string? serverInfo = null)
        {
            _lastConnected = DateTime.Now;

            // Calcular downtime se houve desconexão anterior
            if (_lastDisconnect.HasValue)
            {
                var downtime = (DateTime.Now - _lastDisconnect.Value).TotalMilliseconds;
                _totalDowntimeMs += downtime;
                _reconnectCount++;
                Write($"[RECONECTADO] Reconectado após {downtime / 1000:F1}s de indisponibilidade" +
                      (serverInfo != null ? $" | Servidor: {serverInfo}" : ""));
            }
            else
            {
                Write($"[CONECTADO  ] Conexão estabelecida com sucesso" +
                      (serverInfo != null ? $" | Servidor: {serverInfo}" : ""));
            }

            _lastDisconnect = null;
        }

        public void LogDisconnected(string reason = "")
        {
            _lastDisconnect = DateTime.Now;
            _dropCount++;
            Write($"[DESCONECT. ] Conexão perdida" +
                  (string.IsNullOrEmpty(reason) ? "" : $" | Motivo: {reason}"));
        }

        public void LogError(string message, string? detail = null)
        {
            Write($"[ERRO       ] {message}" +
                  (detail != null ? $"\n             Detalhe: {detail}" : ""));
        }

        public void LogWarning(string message)
        {
            Write($"[AVISO      ] {message}");
        }

        public void LogInfo(string message)
        {
            Write($"[INFO       ] {message}");
        }

        // ══════════════════════════════════════════════════════════════════
        // QUALIDADE DE CONEXÃO
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Registra latência medida entre envio e recebimento de dados.
        /// Chame periodicamente para monitorar qualidade.
        /// </summary>
        public void LogLatency(double ms)
        {
            lock (_lock)
            {
                _latencySamples.Add(ms);

                // Alertas de latência
                if (ms > 500)
                    Write($"[LATÊNCIA   ] ⚠ Alta latência detectada: {ms:F0}ms");
                else if (ms > 1000)
                    Write($"[LATÊNCIA   ] ⛔ Latência crítica: {ms:F0}ms — verifique conexão");

                // Log periódico de qualidade (a cada 100 amostras)
                if (_latencySamples.Count % 100 == 0)
                    LogQualitySnapshot();
            }
        }

        /// <summary>
        /// Registra quando o fluxo de dados para (possível congelamento).
        /// </summary>
        public void LogDataFreeze(int secondsWithoutData)
        {
            Write($"[FREEZE     ] ⚠ Sem dados há {secondsWithoutData}s — possível instabilidade");
        }

        /// <summary>
        /// Snapshot periódico da qualidade da conexão.
        /// </summary>
        public void LogQualitySnapshot()
        {
            lock (_lock)
            {
                if (_latencySamples.Count == 0) return;

                var avg  = _latencySamples.Average();
                var max  = _latencySamples.Max();
                var min  = _latencySamples.Min();
                var last = _latencySamples.LastOrDefault();

                // Calcular p95 (percentil 95)
                var sorted = _latencySamples.OrderBy(x => x).ToList();
                var p95idx = (int)(sorted.Count * 0.95);
                var p95    = sorted[Math.Min(p95idx, sorted.Count - 1)];

                string quality = avg < 50   ? "EXCELENTE" :
                                 avg < 150  ? "BOA" :
                                 avg < 300  ? "REGULAR" :
                                 avg < 500  ? "RUIM" : "CRÍTICA";

                Write($"[QUALIDADE  ] {quality} | " +
                      $"Avg: {avg:F0}ms | Min: {min:F0}ms | Max: {max:F0}ms | " +
                      $"P95: {p95:F0}ms | Amostras: {_latencySamples.Count}");
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // CONTADORES DE DADOS
        // ══════════════════════════════════════════════════════════════════

        public void IncrementTrades()
        {
            lock (_lock) _tradeCount++;
        }

        public void IncrementBookUpdates()
        {
            lock (_lock) _bookUpdateCount++;
        }

        /// <summary>
        /// Log periódico de throughput (chame a cada minuto).
        /// </summary>
        public void LogThroughput()
        {
            lock (_lock)
            {
                Write($"[THROUGHPUT ] Trades: {_tradeCount:N0} | " +
                      $"Book updates: {_bookUpdateCount:N0} | " +
                      $"Reconexões: {_reconnectCount}");
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // ESCRITA NO ARQUIVO
        // ══════════════════════════════════════════════════════════════════

        private void Write(string message)
        {
            lock (_lock)
            {
                try
                {
                    var line = string.IsNullOrWhiteSpace(message)
                        ? ""
                        : $"{DateTime.Now:HH:mm:ss.fff} {message}";

                    File.AppendAllText(LogFile, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { /* nunca deixar o log travar o sistema */ }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // ACESSO AO LOG
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Retorna o caminho completo do arquivo de log atual.
        /// </summary>
        public string GetLogPath() => LogFile;

        /// <summary>
        /// Retorna o caminho da pasta de logs.
        /// </summary>
        public string GetLogDirectory() => LogDir;

        /// <summary>
        /// Lista todos os arquivos de log existentes.
        /// </summary>
        public IEnumerable<string> GetAllLogFiles()
        {
            if (!Directory.Exists(LogDir)) return Array.Empty<string>();
            return Directory.GetFiles(LogDir, "conexao_*.log")
                            .OrderByDescending(f => f);
        }

        /// <summary>
        /// Remove logs mais antigos que X dias.
        /// </summary>
        public void CleanOldLogs(int keepDays = 30)
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-keepDays);
                foreach (var file in GetAllLogFiles())
                {
                    if (File.GetCreationTime(file) < cutoff)
                        File.Delete(file);
                }
                Write($"[INFO       ] Logs antigos removidos (mantidos últimos {keepDays} dias)");
            }
            catch { }
        }
    }
}
