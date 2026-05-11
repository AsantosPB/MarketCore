using System.Globalization;
using System.IO;

namespace MarketCore.HistoricalImporter;

/// <summary>Log partilhado para init da DLL e pedidos de histórico (<c>%AppData%\MarketCore\history_dll.log</c>).</summary>
internal static class ProfitDllDiag
{
    public static void Append(string line)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MarketCore");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "history_dll.log");
            File.AppendAllText(path, $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {line}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }

    internal static string ResolveProfitDllFullPathOrName()
    {
        try
        {
            string p = Path.Combine(AppContext.BaseDirectory, "ProfitDLL64.dll");
            if (File.Exists(p))
                return p;
        }
        catch { }

        return "ProfitDLL64.dll";
    }
}
