using System;
using System.IO;

namespace MarketCore.WPF.Services.PregaoVivaVoz
{
    /// <summary>
    /// Log unificado do Pregão Viva Voz — TODAS as linhas (callbacks brutos + narrações)
    /// escritas em um único arquivo, intercaladas na ordem cronológica em que chegam.
    ///
    /// Formato: <c>[HH:mm:ss.fff] TIPO  conteúdo</c>
    ///   • TIPO = CALLBACK ou NARRACAO
    ///   • Timestamp local do momento em que a linha foi escrita (não confundir com
    ///     <c>bolsa=</c> dentro do conteúdo, que é o horário da execução na bolsa
    ///     enviado pela DLL).
    ///
    /// Os arquivos individuais (<c>pregao_viva_voz_callbacks.log</c> e
    /// <c>pregao_viva_voz_eventos.log</c>) continuam existindo — este é um arquivo
    /// ADICIONAL para análise cronológica cruzada (ver qual callback virou narração,
    /// medir latência, detectar callbacks que "sumiram" sem virar narração, etc.).
    /// </summary>
    internal static class PregaoVivaVozUnifiedLog
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarketCore",
            "pregao_viva_voz_unificado.log");

        // Gate global: callbacks vêm de threads da DLL e narrações do worker de áudio.
        // Um único lock garante ordenação e evita linhas truncadas/interleaved.
        private static readonly object _gate = new();

        /// <param name="tipo">"CALLBACK" ou "NARRACAO" — padroniza as 8 letras para colunar bonito.</param>
        /// <param name="conteudo">Payload já formatado (não precisa incluir timestamp).</param>
        public static void Append(string tipo, string conteudo)
        {
            try
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                // Padroniza a coluna do tipo em 8 caracteres pra alinhar visualmente.
                string tipoPad = (tipo ?? "?").PadRight(8);
                string linha = $"[{DateTime.Now:HH:mm:ss.fff}] {tipoPad} {conteudo}{Environment.NewLine}";

                lock (_gate)
                {
                    File.AppendAllText(LogPath, linha);
                }
            }
            catch
            {
                // Log é best-effort — nunca deve quebrar o fluxo de trading.
            }
        }
    }
}
