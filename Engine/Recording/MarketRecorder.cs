using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MarketCore.Contracts;
using MarketCore.Models;

namespace MarketCore.Engine.Recording;

public sealed class MarketRecorder : IMarketRecorder
{
    private readonly string _diretorioBase;
    private string? _diretorioPregao;
    private DateOnly? _pregaoAtivo = null;

    private readonly ConcurrentQueue<(string ativo, TradeEvent trade)> _filaTrades = new();
    private readonly ConcurrentQueue<(string ativo, BookSnapshot snapshot)> _filaBooks = new();
    private readonly ConcurrentQueue<(string mensagem, DateTime timestamp)> _filaEventos = new();

    private long _totaisTrades = 0;
    private long _totaisBooks = 0;
    private long _bytesGravados = 0;

    private Task? _taskProcessamentoTrades;
    private Task? _taskProcessamentoBooks;
    private Task? _taskProcessamentoEventos;
    private CancellationTokenSource? _cts;

    public event EventHandler<RecorderErrorEventArgs>? ErroGravacao;
    public event EventHandler<RecorderWarningEventArgs>? AvisoGravacao;

    public RecorderStatus Status => new RecorderStatus
    {
        PregaoAtivo = _pregaoAtivo,
        EspacoLivreGB = ObterEspacoLivreGB(),
        FilaTrades = _filaTrades.Count,
        FileBook = _filaBooks.Count,
        TotaisTrades = _totaisTrades,
        TotaisBooks = _totaisBooks,
        BytesGravados = _bytesGravados
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

        _pregaoAtivo = data;
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

            _totaisTrades = 0;
            _totaisBooks = 0;
            _bytesGravados = 0;

            _cts = new CancellationTokenSource();
            _taskProcessamentoTrades = Task.Run(() => ProcessarFilaTrades(_cts.Token));
            _taskProcessamentoBooks  = Task.Run(() => ProcessarFilaBooks(_cts.Token));
            _taskProcessamentoEventos = Task.Run(() => ProcessarFilaEventos(_cts.Token));

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

            _cts?.Cancel();

            Task.WaitAll(
                new[] { _taskProcessamentoTrades, _taskProcessamentoBooks, _taskProcessamentoEventos }
                    .Where(t => t != null).ToArray()!,
                TimeSpan.FromSeconds(10)
            );

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
        if (!_pregaoAtivo.HasValue)
            return Task.FromResult(false);

        _filaTrades.Enqueue((ativo, trade));
        return Task.FromResult(true);
    }

    public Task<bool> GravarBookAsync(string ativo, BookSnapshot snapshot)
    {
        if (!_pregaoAtivo.HasValue)
            return Task.FromResult(false);

        _filaBooks.Enqueue((ativo, snapshot));
        return Task.FromResult(true);
    }

    public Task<bool> GravarEventoAsync(string mensagem, DateTime timestamp)
    {
        if (!_pregaoAtivo.HasValue) return Task.FromResult(false);

        _filaEventos.Enqueue((mensagem, timestamp));
        return Task.FromResult(true);
    }

    private void ProcessarFilaTrades(CancellationToken ct)
    {
        var arquivos = new Dictionary<string, FileStream>();
        var writers  = new Dictionary<string, BinaryWriter>();

        try
        {
            while (!ct.IsCancellationRequested || !_filaTrades.IsEmpty)
            {
                if (_filaTrades.TryDequeue(out var item))
                {
                    var (ativo, trade) = item;

                    if (!writers.ContainsKey(ativo))
                    {
                        var path = Path.Combine(_diretorioPregao!, $"{ativo}_trades.bin");
                        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                        arquivos[ativo] = fs;
                        writers[ativo] = new BinaryWriter(fs);
                    }

                    var w = writers[ativo];

                    // Formato: timestamp(8) + price(16) + volume(4) + aggressor(1) + brokerLen(1) + broker(n)
                    w.Write(trade.Time.Ticks);                          // 8 bytes
                    w.Write(trade.Price);                               // 16 bytes (decimal)
                    w.Write(trade.Volume);                              // 4 bytes
                    w.Write((byte)trade.Aggressor);                     // 1 byte
                    var brokerBytes = Encoding.UTF8.GetBytes(trade.Broker ?? "");
                    w.Write((byte)brokerBytes.Length);                  // 1 byte (tamanho)
                    w.Write(brokerBytes);                               // n bytes

                    Interlocked.Increment(ref _totaisTrades);
                    Interlocked.Add(ref _bytesGravados, 30 + brokerBytes.Length);
                }
                else
                {
                    Thread.Sleep(5);
                }
            }
        }
        finally
        {
            foreach (var w in writers.Values) w?.Dispose();
            foreach (var f in arquivos.Values) f?.Dispose();
        }
    }

    private void ProcessarFilaBooks(CancellationToken ct)
    {
        var arquivos = new Dictionary<string, FileStream>();
        var writers  = new Dictionary<string, BinaryWriter>();
        DateTime? ultimoTimestamp = null;

        try
        {
            while (!ct.IsCancellationRequested || !_filaBooks.IsEmpty)
            {
                if (_filaBooks.TryDequeue(out var item))
                {
                    var (ativo, snapshot) = item;

                    if (!writers.ContainsKey(ativo))
                    {
                        var path = Path.Combine(_diretorioPregao!, $"{ativo}_book.bin");
                        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                        arquivos[ativo] = fs;
                        writers[ativo] = new BinaryWriter(fs);
                    }

                    var w = writers[ativo];

                    // Detectar gap (>2 segundos)
                    if (ultimoTimestamp.HasValue && (snapshot.Time - ultimoTimestamp.Value).TotalSeconds > 2)
                    {
                        var gap = (snapshot.Time - ultimoTimestamp.Value).TotalSeconds;
                        _ = GravarEventoAsync($"GAP_BOOK_{ativo}: {gap:F1}s", snapshot.Time);
                    }

                    ultimoTimestamp = snapshot.Time;

                    // Formato: timestamp(8) + numBids(4) + [price(16)+volume(4)]... + numAsks(4) + [price(16)+volume(4)]...
                    w.Write(snapshot.Time.Ticks);
                    w.Write(snapshot.Bids.Count);
                    foreach (var bid in snapshot.Bids)
                    {
                        w.Write(bid.Price);
                        w.Write(bid.Volume);
                    }
                    w.Write(snapshot.Asks.Count);
                    foreach (var ask in snapshot.Asks)
                    {
                        w.Write(ask.Price);
                        w.Write(ask.Volume);
                    }

                    Interlocked.Increment(ref _totaisBooks);

                    var bytes = 8 + 4 + (snapshot.Bids.Count * 20) + 4 + (snapshot.Asks.Count * 20);
                    Interlocked.Add(ref _bytesGravados, bytes);
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
        }
        finally
        {
            foreach (var w in writers.Values) w?.Dispose();
            foreach (var f in arquivos.Values) f?.Dispose();
        }
    }

    private void ProcessarFilaEventos(CancellationToken ct)
    {
        var path = Path.Combine(_diretorioPregao!, "events.log");

        try
        {
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var w  = new StreamWriter(fs, Encoding.UTF8);

            while (!ct.IsCancellationRequested || !_filaEventos.IsEmpty)
            {
                if (_filaEventos.TryDequeue(out var item))
                {
                    w.WriteLine($"[{item.timestamp:yyyy-MM-dd HH:mm:ss.fff}] {item.mensagem}");
                    w.Flush();
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
        }
        catch (Exception ex)
        {
            DispararErro($"Erro ao processar eventos: {ex.Message}", ex);
        }
    }

    private void SalvarMetadata()
    {
        var metadata = new
        {
            data = _pregaoAtivo!.Value.ToString("yyyy-MM-dd"),
            timestamp_gravacao = DateTime.UtcNow.ToString("o"),
            timezone = "UTC",
            total_trades = _totaisTrades,
            total_books = _totaisBooks,
            bytes_brutos = _bytesGravados,
            ativos = new[] { "WIN", "WDO", "WSP" },
            versao_formato = "FLOWSENSE_V1",
            hashes = new { }
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
            Mensagem = mensagem,
            Excecao = excecao,
            Timestamp = DateTime.UtcNow
        });

    private void DispararAviso(string mensagem)
        => AvisoGravacao?.Invoke(this, new RecorderWarningEventArgs
        {
            Mensagem = mensagem,
            Timestamp = DateTime.UtcNow
        });

    public void Dispose()
    {
        if (_pregaoAtivo.HasValue)
            FinalizarPregaoAsync().Wait();

        _cts?.Dispose();
    }
}