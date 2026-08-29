using System;
using System.IO;
using System.Threading;

namespace MarketCore.Providers.Nelogica;

/// <summary>
/// Log de DIAGNÓSTICO do Pregão Viva Voz em arquivo texto.
///
/// Escreve em:  %AppData%\MarketCore\Logs\pvv_debug.txt
///
/// Por que existe: os logs anteriores estavam em <c>_logger.Log()</c> (arquivo
/// interno do provider) ou <c>Console.WriteLine()</c> (stdout, invisível em app
/// WPF). Nenhum dos dois é visível no painel de LOG DE EVENTOS do Pregão Viva
/// Voz. Este helper grava num arquivo texto que o Anderson pode abrir a qualquer
/// hora para ver toda a cadeia: provider → bridge → engine.
///
/// Rate-limiting fica a cargo dos call sites (para não gerar 5000 linhas/segundo
/// em pico de trades). O helper apenas gate-lockeia contra acesso concorrente
/// entre threads (DLL callback thread, TradeProcessingLoop, bridge worker,
/// engine worker, UI thread).
/// </summary>
public static class PvvDebugFileLog
{
    private static readonly string PathTxt = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MarketCore", "Logs", "pvv_debug.txt");

    private static readonly object Gate = new();
    private static bool _initialized;
    private static long _totalLines;

    /// <summary>Caminho absoluto do arquivo de log (útil para logar).</summary>
    public static string FilePath => PathTxt;

    /// <summary>
    /// Controla se o log de diagnóstico está ativo.
    /// <c>false</c> (padrão) → <see cref="Write"/> retorna imediatamente sem
    /// nenhum I/O — zero custo em produção. Todo o código de instrumentação
    /// nos call sites é preservado; para reativar basta mudar para <c>true</c>.
    /// </summary>
    public static bool Ativo = false;

    /// <summary>
    /// Grava uma linha timestampada no arquivo. Best-effort — engole qualquer
    /// exceção (I/O, permissão, disco cheio) para não impactar o fluxo do PVV.
    /// </summary>
    public static void Write(string message)
    {
        if (!Ativo) return; // desativado — retorna imediatamente, zero I/O
        try
        {
            lock (Gate)
            {
                if (!_initialized)
                {
                    var dir = Path.GetDirectoryName(PathTxt);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    // Marker de sessão para distinguir runs no mesmo arquivo.
                    File.AppendAllText(PathTxt,
                        $"{Environment.NewLine}===== SESSÃO INICIADA em {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ====={Environment.NewLine}");
                    _initialized = true;
                }

                Interlocked.Increment(ref _totalLines);
                File.AppendAllText(PathTxt,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch { /* best effort — nunca propaga */ }
    }

    /// <summary>Total de linhas escritas nesta sessão (útil para heartbeat).</summary>
    public static long TotalLines => Interlocked.Read(ref _totalLines);
}
