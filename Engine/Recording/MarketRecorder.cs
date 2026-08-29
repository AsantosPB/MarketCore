using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MarketCore.Contracts;
using MarketCore.Models;

namespace MarketCore.Engine.Recording;

public sealed class MarketRecorder : IMarketRecorder
{
    private readonly string _diretorioBase;
    private string? _diretorioPregao;
    private DateOnly? _pregaoAtivo = null;

    // [FASE 2] Channel<T> substituindo ConcurrentQueue + Thread.Sleep polling
    // Recriados a cada IniciarPregaoAsync para suportar múltiplos pregões por instância
    private Channel<(string ativo, TradeEvent trade)>?       _tradeChannel;
    private Channel<(string ativo, BookSnapshot snapshot)>?  _bookChannel;
    private Channel<(string mensagem, DateTime timestamp)>?  _eventosChannel;
    private Channel<FlowScoreRecord>?                        _flowScoreChannel;

    private long _totaisTrades   = 0;
    private long _totaisBooks    = 0;
    private long _bytesGravados  = 0;
    private static long _tradeSequence = 0; // [FASE 1] contador monotônico por processo
    private static long _bookSequence  = 0; // [FASE 1] contador monotônico por processo

    private Task? _taskProcessamentoTrades;
    private Task? _taskProcessamentoBooks;
    private Task? _taskProcessamentoEventos;
    private Task? _taskProcessamentoFlowScore;
    private CancellationTokenSource? _cts;

    // [FASE 2] Rastreia arquivos binários abertos para finalização do CRC32
    private readonly List<string> _arquivosBinarios     = new();
    private readonly object       _arquivosBinariosLock  = new();

    public event EventHandler<RecorderErrorEventArgs>?   ErroGravacao;
    public event EventHandler<RecorderWarningEventArgs>? AvisoGravacao;

    // ── Modelo interno FlowScore ─────────────────────────────────────────
    private readonly record struct FlowScoreRecord(
        string   Ativo,
        DateTime Timestamp,
        double   Preco,
        double   ScoreTotal,
        double   BrokerFlow,
        double   FluxoDireto,
        double   Book,
        double   Detectores);

    public RecorderStatus Status => new RecorderStatus
    {
        PregaoAtivo    = _pregaoAtivo,
        EspacoLivreGB  = ObterEspacoLivreGB(),
        FilaTrades     = _tradeChannel?.Reader.Count ?? 0,   // [FASE 2]
        FileBook       = _bookChannel?.Reader.Count ?? 0,    // [FASE 2]
        TotaisTrades   = _totaisTrades,
        TotaisBooks    = _totaisBooks,
        BytesGravados  = _bytesGravados
    };

    public MarketRecorder(string diretorioBase)
    {
        _diretorioBase = diretorioBase;
        Directory.CreateDirectory(_diretorioBase);
    }

    public Task<bool> IniciarPregaoAsync(DateOnly data)
    {
        if (_pregaoAtivo.HasValue)
        {
            DispararErro("Já existe um pregão ativo. Finalize antes de iniciar outro.", null);
            return Task.FromResult(false);
        }

        _pregaoAtivo     = data;
        _diretorioPregao = Path.Combine(_diretorioBase, data.ToString("yyyy-MM-dd"));

        try
        {
            Directory.CreateDirectory(_diretorioPregao);

            var espacoLivre = ObterEspacoLivreGB();
            if (espacoLivre < 1)
            {
                DispararErro($"Espaço em disco insuficiente: {espacoLivre:F2} GB", null);
                return Task.FromResult(false);
            }

            if (espacoLivre < 10)
                DispararAviso($"Espaço em disco baixo: {espacoLivre:F2} GB");

            _totaisTrades  = 0;
            _totaisBooks   = 0;
            _bytesGravados = 0;
            lock (_arquivosBinariosLock) _arquivosBinarios.Clear();

            // [FASE 2] Novos channels a cada pregão (channels anteriores foram Complete'd)
            var opts = new UnboundedChannelOptions { SingleReader = true, SingleWriter = false };
            _tradeChannel     = Channel.CreateUnbounded<(string, TradeEvent)>(opts);
            _bookChannel      = Channel.CreateUnbounded<(string, BookSnapshot)>(opts);
            _eventosChannel   = Channel.CreateUnbounded<(string, DateTime)>(opts);
            _flowScoreChannel = Channel.CreateUnbounded<FlowScoreRecord>(opts);

            _cts = new CancellationTokenSource();

            // [FASE 2] Workers async com await foreach ReadAllAsync — sem Thread.Sleep
            _taskProcessamentoTrades    = Task.Run(async () => await ProcessarFilaTrades(_cts.Token));
            _taskProcessamentoBooks     = Task.Run(async () => await ProcessarFilaBooks(_cts.Token));
            _taskProcessamentoEventos   = Task.Run(async () => await ProcessarFilaEventos(_cts.Token));
            _taskProcessamentoFlowScore = Task.Run(async () => await ProcessarFilaFlowScore(_cts.Token));

            GravarEventoAsync("PREGAO_INICIADO", DateTime.UtcNow).Wait();
            DispararAviso($"Pregão {data:yyyy-MM-dd} iniciado. Espaço livre: {espacoLivre:F2} GB");

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            DispararErro($"Erro ao iniciar pregão: {ex.Message}", ex);
            return Task.FromResult(false);
        }
    }

    public Task<bool> FinalizarPregaoAsync()
    {
        if (!_pregaoAtivo.HasValue)
        {
            DispararAviso("Nenhum pregão ativo para finalizar.");
            return Task.FromResult(false);
        }

        try
        {
            GravarEventoAsync("PREGAO_FINALIZANDO", DateTime.UtcNow).Wait();

            // [FASE 2] Sinaliza fim de escrita; workers drenam itens restantes e encerram
            _tradeChannel!.Writer.Complete();
            _bookChannel!.Writer.Complete();
            _eventosChannel!.Writer.Complete();
            _flowScoreChannel!.Writer.Complete();

            Task.WaitAll(
                new[] {
                    _taskProcessamentoTrades,
                    _taskProcessamentoBooks,
                    _taskProcessamentoEventos,
                    _taskProcessamentoFlowScore
                }.Where(t => t != null).ToArray()!,
                TimeSpan.FromSeconds(10)
            );

            // [FASE 2] Finaliza CRC32 em cada arquivo binário gravado neste pregão
            List<string> binarios;
            lock (_arquivosBinariosLock) binarios = new List<string>(_arquivosBinarios);
            foreach (var path in binarios)
                FinalizarCRC32(path);

            SalvarMetadata();

            var dataFinalizada = _pregaoAtivo.Value;
            _pregaoAtivo = null;
            _cts?.Dispose();

            DispararAviso($"Pregão {dataFinalizada:yyyy-MM-dd} finalizado. " +
                          $"Trades: {_totaisTrades} Books: {_totaisBooks} " +
                          $"Bytes: {_bytesGravados}");

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            DispararErro($"Erro ao finalizar pregão: {ex.Message}", ex);
            return Task.FromResult(false);
        }
    }

    public Task<bool> GravarTradeAsync(string ativo, TradeEvent trade)
    {
        if (!_pregaoAtivo.HasValue) return Task.FromResult(false);
        _tradeChannel!.Writer.TryWrite((ativo, trade));    // [FASE 2]
        return Task.FromResult(true);
    }

    public Task<bool> GravarBookAsync(string ativo, BookSnapshot snapshot)
    {
        if (!_pregaoAtivo.HasValue) return Task.FromResult(false);
        _bookChannel!.Writer.TryWrite((ativo, snapshot));  // [FASE 2]
        return Task.FromResult(true);
    }

    public Task<bool> GravarEventoAsync(string mensagem, DateTime timestamp)
    {
        if (!_pregaoAtivo.HasValue) return Task.FromResult(false);
        _eventosChannel!.Writer.TryWrite((mensagem, timestamp)); // [FASE 2]
        return Task.FromResult(true);
    }

    /// <summary>
    /// Grava um snapshot do FlowScore no arquivo WIN_flowscore.bin.
    /// Chamado a cada 1 segundo pelo timer do FlowScoreEngine.
    /// 56 bytes por registro (+ 64 bytes de header na Fase 2).
    /// </summary>
    public Task<bool> GravarFlowScoreAsync(
        string ativo, double preco, double scoreTotal,
        double brokerFlow, double fluxoDireto, double book, double detectores)
    {
        if (!_pregaoAtivo.HasValue) return Task.FromResult(false);

        _flowScoreChannel!.Writer.TryWrite(new FlowScoreRecord( // [FASE 2]
            ativo, DateTime.UtcNow, preco, scoreTotal,
            brokerFlow, fluxoDireto, book, detectores));

        return Task.FromResult(true);
    }

    // ── Processamento de channels ──────────────────────────────────────────

    private async Task ProcessarFilaTrades(CancellationToken ct)
    {
        var arquivos = new Dictionary<string, FileStream>();
        var writers  = new Dictionary<string, BinaryWriter>();

        try
        {
            // [FASE 2] await foreach drena o channel sem Thread.Sleep
            await foreach (var (ativo, trade) in _tradeChannel!.Reader.ReadAllAsync(ct))
            {
                if (!writers.ContainsKey(ativo))
                {
                    var path = Path.Combine(_diretorioPregao!, $"{ativo}_trades.bin");
                    var fs   = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                    if (fs.Length == 0)
                    {
                        // [FASE 2] Header de 64 bytes — bytes 60-63 reservados para CRC32
                        fs.Write(new byte[64]);
                    }
                    else
                    {
                        fs.Seek(0, SeekOrigin.End);
                    }
                    arquivos[ativo] = fs;
                    writers[ativo]  = new BinaryWriter(fs);
                    lock (_arquivosBinariosLock) _arquivosBinarios.Add(path);
                }

                var w = writers[ativo];
                w.Write(trade.Time.Ticks);
                w.Write(Interlocked.Increment(ref _tradeSequence)); // [FASE 1] SequenceNumber Int64
                w.Write(trade.Price);
                w.Write(trade.Volume);
                w.Write((byte)trade.Aggressor);
                var brokerBytes = Encoding.UTF8.GetBytes(trade.Broker ?? "");
                w.Write((byte)brokerBytes.Length);
                w.Write(brokerBytes);

                Interlocked.Increment(ref _totaisTrades);
                Interlocked.Add(ref _bytesGravados, 30 + brokerBytes.Length);
            }
        }
        finally
        {
            foreach (var w in writers.Values) w?.Dispose();
            foreach (var f in arquivos.Values) f?.Dispose();
        }
    }

    private async Task ProcessarFilaBooks(CancellationToken ct)
    {
        var arquivos = new Dictionary<string, FileStream>();
        var writers  = new Dictionary<string, BinaryWriter>();
        DateTime? ultimoTimestamp = null;

        try
        {
            // [FASE 2] await foreach drena o channel sem Thread.Sleep
            await foreach (var (ativo, snapshot) in _bookChannel!.Reader.ReadAllAsync(ct))
            {
                if (!writers.ContainsKey(ativo))
                {
                    var path = Path.Combine(_diretorioPregao!, $"{ativo}_book.bin");
                    var fs   = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                    if (fs.Length == 0)
                    {
                        // [FASE 2] Header de 64 bytes — bytes 60-63 reservados para CRC32
                        fs.Write(new byte[64]);
                    }
                    else
                    {
                        fs.Seek(0, SeekOrigin.End);
                    }
                    arquivos[ativo] = fs;
                    writers[ativo]  = new BinaryWriter(fs);
                    lock (_arquivosBinariosLock) _arquivosBinarios.Add(path);
                }

                var w = writers[ativo];

                if (ultimoTimestamp.HasValue && (snapshot.Time - ultimoTimestamp.Value).TotalSeconds > 2)
                {
                    var gap = (snapshot.Time - ultimoTimestamp.Value).TotalSeconds;
                    _ = GravarEventoAsync($"GAP_BOOK_{ativo}: {gap:F1}s", snapshot.Time);
                }

                ultimoTimestamp = snapshot.Time;

                // [FASE 1] Formato fixo 272 bytes por registro (seek direto por índice para Replay Engine).
                // Layout: ExchangeTimestamp(8) + ReceiveTimestamp(8) + SequenceNumber(8) + Price(8)
                //       + 10xBid[Price(8)+Qty(4)] + 10xAsk[Price(8)+Qty(4)] = 272 bytes
                const int BOOK_LEVELS = 10;
                w.Write(snapshot.Time.Ticks);                              // ExchangeTimestamp  8
                w.Write(DateTime.UtcNow.Ticks);                            // ReceiveTimestamp   8
                w.Write(Interlocked.Increment(ref _bookSequence));         // SequenceNumber     8
                w.Write(snapshot.Bids.Count > 0
                    ? (double)snapshot.Bids[0].Price : 0.0);              // Price (best bid)   8

                for (int bi = 0; bi < BOOK_LEVELS; bi++)                  // 10 Bid levels     120
                {
                    if (bi < snapshot.Bids.Count)
                    { w.Write((double)snapshot.Bids[bi].Price); w.Write(snapshot.Bids[bi].Volume); }
                    else
                    { w.Write(0.0); w.Write(0); }
                }

                for (int ai = 0; ai < BOOK_LEVELS; ai++)                  // 10 Ask levels     120
                {
                    if (ai < snapshot.Asks.Count)
                    { w.Write((double)snapshot.Asks[ai].Price); w.Write(snapshot.Asks[ai].Volume); }
                    else
                    { w.Write(0.0); w.Write(0); }
                }

                Interlocked.Increment(ref _totaisBooks);
                Interlocked.Add(ref _bytesGravados, 272);
            }
        }
        finally
        {
            foreach (var w in writers.Values) w?.Dispose();
            foreach (var f in arquivos.Values) f?.Dispose();
        }
    }

    private async Task ProcessarFilaEventos(CancellationToken ct)
    {
        var path = Path.Combine(_diretorioPregao!, "events.log");

        try
        {
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var w  = new StreamWriter(fs, Encoding.UTF8);

            // [FASE 2] await foreach drena o channel sem Thread.Sleep
            await foreach (var (mensagem, timestamp) in _eventosChannel!.Reader.ReadAllAsync(ct))
            {
                w.WriteLine($"[{timestamp:yyyy-MM-dd HH:mm:ss.fff}] {mensagem}");
                w.Flush();
            }
        }
        catch (Exception ex)
        {
            DispararErro($"Erro ao processar eventos: {ex.Message}", ex);
        }
    }

    private async Task ProcessarFilaFlowScore(CancellationToken ct)
    {
        var arquivos = new Dictionary<string, FileStream>();
        var writers  = new Dictionary<string, BinaryWriter>();

        try
        {
            // [FASE 2] await foreach drena o channel sem Thread.Sleep
            await foreach (var item in _flowScoreChannel!.Reader.ReadAllAsync(ct))
            {
                if (!writers.ContainsKey(item.Ativo))
                {
                    var path = Path.Combine(_diretorioPregao!, $"{item.Ativo}_flowscore.bin");
                    var fs   = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                    if (fs.Length == 0)
                    {
                        // [FASE 2] Header de 64 bytes — bytes 60-63 reservados para CRC32
                        fs.Write(new byte[64]);
                    }
                    else
                    {
                        fs.Seek(0, SeekOrigin.End);
                    }
                    arquivos[item.Ativo] = fs;
                    writers[item.Ativo]  = new BinaryWriter(fs);
                    lock (_arquivosBinariosLock) _arquivosBinarios.Add(path);
                }

                var w = writers[item.Ativo];
                // 56 bytes por registro
                w.Write(item.Timestamp.Ticks);  // 8
                w.Write(item.Preco);             // 8
                w.Write(item.ScoreTotal);        // 8
                w.Write(item.BrokerFlow);        // 8
                w.Write(item.FluxoDireto);       // 8
                w.Write(item.Book);              // 8
                w.Write(item.Detectores);        // 8

                Interlocked.Add(ref _bytesGravados, 56);
            }
        }
        finally
        {
            foreach (var w in writers.Values) w?.Dispose();
            foreach (var f in arquivos.Values) f?.Dispose();
        }
    }

    // ── CRC32 [FASE 2] ────────────────────────────────────────────────────

    /// <summary>
    /// Calcula o CRC32 do corpo do arquivo (bytes 64 em diante) e grava
    /// nos bytes 60-63 do header de 64 bytes. Chamado após os workers encerrarem.
    /// </summary>
    private static void FinalizarCRC32(string caminhoArquivo)
    {
        try
        {
            using var fs = new FileStream(caminhoArquivo, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (fs.Length < 64) return;

            fs.Seek(64, SeekOrigin.Begin);
            var body = new byte[fs.Length - 64];
            _ = fs.Read(body, 0, body.Length);

            var hashBytes = Crc32.Hash(body);   // 4 bytes little-endian
            fs.Seek(60, SeekOrigin.Begin);
            fs.Write(hashBytes, 0, 4);
        }
        catch { /* arquivo pode não ter sido criado se ativo não teve dados no pregão */ }
    }

    /// <summary>
    /// Verifica a integridade de um arquivo binário comparando o CRC32 armazenado
    /// no header (bytes 60-63) com o CRC32 calculado sobre o corpo (bytes 64 ate EOF).
    /// </summary>
    /// <returns>true se checksum bate; false se arquivo invalido ou checksum diverge.</returns>
    public static bool VerificarIntegridade(string caminhoArquivo)
    {
        try
        {
            using var fs = new FileStream(caminhoArquivo, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 64) return false;

            fs.Seek(60, SeekOrigin.Begin);
            var storedBytes = new byte[4];
            _ = fs.Read(storedBytes, 0, 4);
            uint storedCrc = BitConverter.ToUInt32(storedBytes, 0);

            fs.Seek(64, SeekOrigin.Begin);
            var body = new byte[fs.Length - 64];
            _ = fs.Read(body, 0, body.Length);

            uint computedCrc = BitConverter.ToUInt32(Crc32.Hash(body), 0);

            return storedCrc == computedCrc;
        }
        catch { return false; }
    }

    // ─────────────────────────────────────────────────────────────────────

    private void SalvarMetadata()
    {
        var metadata = new
        {
            data                = _pregaoAtivo!.Value.ToString("yyyy-MM-dd"),
            timestamp_gravacao  = DateTime.UtcNow.ToString("o"),
            timezone            = "UTC",
            total_trades        = _totaisTrades,
            total_books         = _totaisBooks,
            bytes_brutos        = _bytesGravados,
            ativos              = new[] { "WIN", "WDO", "WSP" },
            versao_formato      = "FLOWSENSE_V2",
            hashes              = new { }
        };

        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(_diretorioPregao!, "metadata.json"), json);
    }

    private double ObterEspacoLivreGB()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(_diretorioBase)!);
            return drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
        }
        catch { return double.MaxValue; }
    }

    private void DispararErro(string mensagem, Exception? excecao)
        => ErroGravacao?.Invoke(this, new RecorderErrorEventArgs
        {
            Mensagem   = mensagem,
            Excecao    = excecao,
            Timestamp  = DateTime.UtcNow
        });

    private void DispararAviso(string mensagem)
        => AvisoGravacao?.Invoke(this, new RecorderWarningEventArgs
        {
            Mensagem  = mensagem,
            Timestamp = DateTime.UtcNow
        });

    public void Dispose()
    {
        if (_pregaoAtivo.HasValue)
            FinalizarPregaoAsync().Wait();
        _cts?.Dispose();
    }
}
