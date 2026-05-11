using System.IO;
using System.Text.Json;

namespace MarketCore.FlowSense;

/// <summary>Preferências da interface FlowSense persistidas em JSON.</summary>
public sealed class FlowsenseUiSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool ShowHistoryDownloadOnStartup { get; set; }

    /// <summary>Ticker último na janela de download histórico (ex.: WINFUT).</summary>
    public string? HistoryDownloadTicker { get; set; }

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MarketCore",
            "flowsense_ui.json");

    public static FlowsenseUiSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<FlowsenseUiSettings>(json, JsonOptions)
                       ?? new FlowsenseUiSettings();
            }
        }
        catch { /* best effort */ }

        return new FlowsenseUiSettings();
    }

    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { /* best effort */ }
    }
}
