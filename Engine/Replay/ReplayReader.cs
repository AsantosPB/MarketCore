using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MarketCore.Engine.Recording;

namespace MarketCore.Engine.Replay;

/// <summary>
/// Lê os arquivos binários brutos (_trades.bin, _book.bin) gravados pelo MarketRecorder
/// e devolve os eventos intercalados em ordem cronológica por ExchangeTimestamp.
/// </summary>
public class ReplayReader : IDisposable
{
    // ── Constantes ─────────────────────────────────────────────────────────

    /// <summary>Header binário de 64 bytes gravado em todos os arquivos (Fase 2).</summary>
    private const int HEADER_SIZE = 64;

    /// <summary>Layout fixo do registro de book: 272 bytes.</summary>
    private const int BOOK_RECORD_SIZE = 272;

    // ── API pública ────────────────────────────────────────────────────────

    /// <summary>
    /// Lê todos os eventos de um dia e retorna intercalados por ExchangeTimestamp (merge sort O(n+m)).
    /// Lança InvalidDataException se a integridade de algum arquivo falhar.
    /// </summary>
    public IReadOnlyList<object> LerEventos(DateTime date, string rawDataPath)
    {
        string dayDir    = Path.Combine(rawDataPath, date.ToString("yyyy-MM-dd"));
        string tradePath = ResolverCaminho(dayDir, date, "trades");
        string bookPath  = ResolverCaminho(dayDir, date, "book");

        if (!VerificarArquivo(tradePath))
            throw new InvalidDataException(
                $"Integridade inválida ou arquivo ausente: {tradePath}");
        if (!VerificarArquivo(bookPath))
            throw new InvalidDataException(
                $"Integridade inválida ou arquivo ausente: {bookPath}");

        var trades = LerTrades(tradePath);
        var books  = LerBook(bookPath);

        return MergeSort(trades, books);
    }

    /// <summary>Lê todos os trades do arquivo binário. Registros de tamanho variável.</summary>
    public IEnumerable<RawTradeEvent> LerTrades(string filePath)
    {
        using var fs     = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

        if (fs.Length <= HEADER_SIZE) yield break;
        reader.ReadBytes(HEADER_SIZE);  // pular header de 64 bytes

        while (fs.Position < fs.Length)
        {
            // Layout: Ticks(8) + SeqNum(8) + Price_decimal(16) + Volume(4) + Aggressor(1) + BrokerLen(1) + Broker(var)
            long    timestamp = reader.ReadInt64();
            long    seqNum    = reader.ReadInt64();
            decimal price     = reader.ReadDecimal();   // 16 bytes (BinaryWriter.Write(decimal))
            int     volume    = reader.ReadInt32();
            byte    aggressor = reader.ReadByte();
            byte    brokerLen = reader.ReadByte();
            string  broker    = brokerLen > 0
                ? Encoding.UTF8.GetString(reader.ReadBytes(brokerLen))
                : string.Empty;

            yield return new RawTradeEvent
            {
                Timestamp      = timestamp,
                SequenceNumber = seqNum,
                Price          = price,
                Volume         = volume,
                Aggressor      = aggressor,
                Broker         = broker
            };
        }
    }

    /// <summary>Lê todos os book updates. Registros fixos de 272 bytes.</summary>
    public IEnumerable<RawBookEvent> LerBook(string filePath)
    {
        using var fs     = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

        if (fs.Length <= HEADER_SIZE) yield break;
        reader.ReadBytes(HEADER_SIZE);  // pular header de 64 bytes

        // Layout fixo 272 bytes: ExchTs(8) + RecvTs(8) + SeqNum(8) + BestBid(8) + 10xBid[P(8)+V(4)] + 10xAsk[P(8)+V(4)]
        while (fs.Position + BOOK_RECORD_SIZE <= fs.Length)
        {
            var ev = new RawBookEvent
            {
                ExchangeTimestamp = reader.ReadInt64(),
                ReceiveTimestamp  = reader.ReadInt64(),
                SequenceNumber    = reader.ReadInt64(),
                Price             = reader.ReadDouble()   // best bid
            };

            for (int i = 0; i < 10; i++)
            {
                ev.BidPrices[i]  = reader.ReadDouble();
                ev.BidVolumes[i] = reader.ReadInt32();
            }
            for (int i = 0; i < 10; i++)
            {
                ev.AskPrices[i]  = reader.ReadDouble();
                ev.AskVolumes[i] = reader.ReadInt32();
            }
            yield return ev;
        }
    }

    /// <summary>
    /// Verifica a integridade do arquivo via CRC32 no header (Fase 2).
    /// Retorna false se o arquivo não existir, estiver vazio ou com CRC inválido.
    /// </summary>
    public bool VerificarArquivo(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;
        try
        {
            return MarketRecorder.VerificarIntegridade(filePath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Conta o número de registros num arquivo de tamanho fixo.
    /// Para trades (tamanho variável) retorna uma estimativa conservadora.
    /// </summary>
    public long ContarEventos(string filePath, int recordSize)
    {
        if (!File.Exists(filePath)) return 0;
        long fileSize = new FileInfo(filePath).Length;
        long bodySize = fileSize - HEADER_SIZE;
        return bodySize > 0 && recordSize > 0
            ? bodySize / recordSize
            : 0;
    }

    public void Dispose() { /* nada a liberar */ }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Merge sort de duas listas já ordenadas por ExchangeTimestamp + SequenceNumber.
    /// Complexidade O(n+m).
    /// </summary>
    private static IReadOnlyList<object> MergeSort(
        IEnumerable<RawTradeEvent> trades,
        IEnumerable<RawBookEvent>  books)
    {
        // Materializar em memória (ambas as sequências já estão ordenadas cronologicamente)
        var tList = new List<RawTradeEvent>(trades);
        var bList = new List<RawBookEvent>(books);

        var result = new List<object>(tList.Count + bList.Count);

        int ti = 0, bi = 0;
        while (ti < tList.Count && bi < bList.Count)
        {
            // Comparar por ExchangeTimestamp; SequenceNumber como tiebreaker
            RawTradeEvent t = tList[ti];
            RawBookEvent  b = bList[bi];

            if (t.Timestamp < b.ExchangeTimestamp
                || (t.Timestamp == b.ExchangeTimestamp
                    && t.SequenceNumber <= b.SequenceNumber))
            {
                result.Add(t);
                ti++;
            }
            else
            {
                result.Add(b);
                bi++;
            }
        }

        // Adicionar o que sobrou
        while (ti < tList.Count) result.Add(tList[ti++]);
        while (bi < bList.Count) result.Add(bList[bi++]);

        return result;
    }

    /// <summary>
    /// Resolve o caminho do arquivo binário.
    /// Tenta primeiro o padrão por ativo ({ativo}_{tipo}.bin) e depois o padrão com data.
    /// </summary>
    private static string ResolverCaminho(string dayDir, DateTime date, string tipo)
    {
        // Padrão real do MarketRecorder: WIN_trades.bin
        string primaryPath = Path.Combine(dayDir, $"WIN_{tipo}.bin");
        if (File.Exists(primaryPath)) return primaryPath;

        // Padrão alternativo com data no nome
        string altPath = Path.Combine(dayDir, $"WIN_{date:yyyyMMdd}_{tipo}.bin");
        if (File.Exists(altPath)) return altPath;

        // Retorna o caminho primário (será validado em LerEventos)
        return primaryPath;
    }
}
