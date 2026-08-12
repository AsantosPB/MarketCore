using System;
using System.IO;

namespace MarketCore.Providers.Nelogica
{
    public class ConnectionLogger : IDisposable
    {
        private readonly string _logFilePath;
        private readonly object _lockObject = new object();
        private StreamWriter? _writer = null;
        private bool _disposed = false;

        public ConnectionLogger()
        {
            // Cria pasta de logs no AppData
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string logFolder = Path.Combine(appDataPath, "MarketCore", "Logs");

            if (!Directory.Exists(logFolder))
            {
                Directory.CreateDirectory(logFolder);
            }

            // Nome do arquivo com data
            string fileName = $"ProfitDLL_{DateTime.Now:yyyy-MM-dd}.log";
            _logFilePath = Path.Combine(logFolder, fileName);

            // Abre o arquivo para escrita
            try
            {
                _writer = new StreamWriter(_logFilePath, append: true)
                {
                    AutoFlush = true
                };

                Log("=== ConnectionLogger Iniciado ===");
            }
            catch (Exception ex)
            {
                // Se n-o conseguir criar o log, continua sem ele
                Console.WriteLine($"Erro ao criar log: {ex.Message}");
            }

            PurgeOldLogFiles(logFolder, daysToKeep: 10);
        }

        /// <summary>Apaga arquivos de log (um por dia, "ProfitDLL_AAAA-MM-DD.log") com mais de
        /// <paramref name="daysToKeep"/> dias — mesma janela de retenção rolante usada em trades_intraday.
        /// Roda uma vez por abertura do programa; sem isso os logs diários se acumulam para sempre na pasta.</summary>
        private static void PurgeOldLogFiles(string logFolder, int daysToKeep)
        {
            try
            {
                DateTime cutoff = DateTime.Now.Date.AddDays(-daysToKeep);
                foreach (var path in Directory.GetFiles(logFolder, "ProfitDLL_*.log"))
                {
                    if (File.GetLastWriteTime(path).Date < cutoff)
                        File.Delete(path);
                }
            }
            catch
            {
                // Limpeza de log é best-effort — nunca deve impedir o app de abrir.
            }
        }

        public void Log(string message)
        {
            lock (_lockObject)
            {
                try
                {
                    string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                    string logLine = $"[{timestamp}] {message}";

                    // Escreve no arquivo
                    _writer?.WriteLine(logLine);

                    // Tamb-m escreve no console para debug
                    Console.WriteLine(logLine);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Erro ao escrever log: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    lock (_lockObject)
                    {
                        try
                        {
                            Log("=== ConnectionLogger Finalizado ===");
                            _writer?.Flush();
                            _writer?.Dispose();
                        }
                        catch
                        {
                            // Ignora erros ao fechar
                        }
                    }
                }

                _disposed = true;
            }
        }

        ~ConnectionLogger()
        {
            Dispose(false);
        }
    }
}

