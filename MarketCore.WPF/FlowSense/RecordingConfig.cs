using System;
using System.IO;
using System.Text.Json;

namespace MarketCore.FlowSense
{
    /// <summary>
    /// Gerencia a configuração de caminho de gravação dos históricos.
    /// Persiste em %AppData%\MarketCore\recording_config.json
    /// </summary>
    public class RecordingConfig
    {
        private static readonly string ConfigFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MarketCore", "recording_config.json");

        private static readonly string DefaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MarketCore", "Recordings");

        public string RecordingsPath { get; set; } = DefaultPath;

        /// <summary>
        /// Carrega configuração salva. Se não existir, retorna padrão.
        /// </summary>
        public static RecordingConfig Load()
        {
            try
            {
                if (File.Exists(ConfigFile))
                {
                    var json = File.ReadAllText(ConfigFile);
                    var config = JsonSerializer.Deserialize<RecordingConfig>(json);
                    if (config != null && !string.IsNullOrWhiteSpace(config.RecordingsPath))
                        return config;
                }
            }
            catch { }

            return new RecordingConfig();
        }

        /// <summary>
        /// Salva configuração atual.
        /// </summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigFile, json);
            }
            catch { }
        }

        /// <summary>
        /// Retorna o caminho padrão (%AppData%\MarketCore\Recordings)
        /// </summary>
        public static string GetDefaultPath() => DefaultPath;
    }
}
