using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MarketCore.Contracts;
using MarketCore.Models;

namespace MarketCore.Providers.Replay;

/// <summary>
/// Provider que reproduz pregões gravados, permitindo replay com controles de velocidade.
/// </summary>
public sealed class ReplayProvider : IMarketDataProvider
{
    public string ProviderName => "Replay";
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

    public event Action<TradeEvent>? OnTrade;
    public event Action<BookLevel>? OnBook;
    public event Action<QuoteEvent>? OnQuote;
    public event Action<ConnectionChangedEvent>? OnConnectionChanged;

    public IReadOnlyList<string> SubscribedTickers => _subscriptions.ToList();

    private readonly string _diretorioBase;
    private DateOnly _dataReplay;
    private Thread? _replayThread;
    private CancellationTokenSource? _cts;
    private readonly HashSet<string> _subscriptions = new();

    // Controles de replay
    private bool _isPaused = false;
    private float _velocidade = 1.0f; // 1x = tempo real, 2x = 2x mais rápido, etc

    // Dados carregados
    private List<BookSnapshot> _bookSnapshots = new();
    private ReplayMetadata? _metadata;

    public ReplayProvider(string diretorioBase)
    {
        _diretorioBase = diretorioBase;
    }

    public Task ConnectAsync(ProviderCredentials credentials)
    {
        // Credentials não são usadas no replay, mas podemos usar para passar a data
        // Por exemplo: credentials.Username = "2026-04-23"
        
        if (!DateOnly.TryParse(credentials.Username, out _dataReplay))
        {
            _dataReplay = DateOnly.FromDateTime(DateTime.Now);
        }

        var diretorioPregao = Path.Combine(_diretorioBase, _dataReplay.ToString("yyyy-MM-dd"));

        if (!Directory.Exists(diretorioPregao))
        {
            Status = ConnectionStatus.Error;
            OnConnectionChanged?.Invoke(new ConnectionChangedEvent(
                Status,
                $"Pregão {_dataReplay:yyyy-MM-dd} não encontrado em {_diretorioBase}"
            ));
            return Task.CompletedTask;
        }

        // Carregar metadata
        var metadataPath = Path.Combine(diretorioPregao, "metadata.json");
        if (File.Exists(metadataPath))
        {
            var json = File.ReadAllText(metadataPath);
            _metadata = JsonSerializer.Deserialize<ReplayMetadata>(json);
        }

        Status = ConnectionStatus.Connected;
        OnConnectionChanged?.Invoke(new ConnectionChangedEvent(
            Status,
            $"Replay carregado: {_dataReplay:yyyy-MM-dd} | Books: {_metadata?.total_books ?? 0}"
        ));

        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _cts?.Cancel();
        _replayThread?.Join();
        _cts?.Dispose();

        Status = ConnectionStatus.Disconnected;
        OnConnectionChanged?.Invoke(new ConnectionChangedEvent(
            Status,
            "Replay finalizado"
        ));

        return Task.CompletedTask;
    }

    public void Subscribe(string ticker)
    {
        if (_subscriptions.Add(ticker))
        {
            // Carregar book snapshots para este ticker
            var ativo = ExtrairAtivo(ticker);
            var diretorioPregao = Path.Combine(_diretorioBase, _dataReplay.ToString("yyyy-MM-dd"));
            var bookPath = Path.Combine(diretorioPregao, $"{ativo}_book.bin");

            if (File.Exists(bookPath))
            {
                _bookSnapshots = CarregarBookSnapshots(bookPath, ticker);
                IniciarReplay();
            }
        }
    }

    public void Unsubscribe(string ticker)
    {
        _subscriptions.Remove(ticker);
    }

    /// <summary>
    /// Pausa ou resume o replay.
    /// </summary>
    public void TogglePause()
    {
        _isPaused = !_isPaused;
        Console.WriteLine(_isPaused ? "[REPLAY] ⏸ PAUSADO" : "[REPLAY] ▶ RESUMIDO");
    }

    /// <summary>
    /// Ajusta a velocidade do replay.
    /// </summary>
    /// <param name="velocidade">1.0 = tempo real, 2.0 = 2x mais rápido, 0.5 = câmera lenta</param>
    public void SetVelocidade(float velocidade)
    {
        _velocidade = Math.Max(0.1f, Math.Min(100f, velocidade));
        Console.WriteLine($"[REPLAY] ⏩ Velocidade: {_velocidade:F1}x");
    }

    private void IniciarReplay()
    {
        if (_replayThread != null) return; // Já está rodando

        _cts = new CancellationTokenSource();
        _replayThread = new Thread(() =>
        {
            Console.WriteLine($"[REPLAY] ▶ Iniciando replay de {_bookSnapshots.Count} snapshots");

            DateTime? timestampAnterior = null;

            foreach (var snapshot in _bookSnapshots)
            {
                if (_cts.Token.IsCancellationRequested) break;

                // Aguardar se estiver pausado
                while (_isPaused && !_cts.Token.IsCancellationRequested)
                {
                    Thread.Sleep(100);
                }

                if (_cts.Token.IsCancellationRequested) break;

                // Calcular delay baseado no timestamp real
                if (timestampAnterior.HasValue)
                {
                    var intervalo = snapshot.Time - timestampAnterior.Value;
                    var delayMs = (int)(intervalo.TotalMilliseconds / _velocidade);
                    
                    if (delayMs > 0)
                    {
                        Thread.Sleep(delayMs);
                    }
                }

                timestampAnterior = snapshot.Time;

                // Disparar eventos de book (nível por nível)
                foreach (var bid in snapshot.Bids)
                {
                    OnBook?.Invoke(bid);
                }

                foreach (var ask in snapshot.Asks)
                {
                    OnBook?.Invoke(ask);
                }
            }

            Console.WriteLine("[REPLAY] ✅ Replay finalizado");
        })
        {
            IsBackground = true,
            Name = "ReplayProvider-Playback"
        };

        _replayThread.Start();
    }

    private List<BookSnapshot> CarregarBookSnapshots(string path, string ticker)
    {
        var snapshots = new List<BookSnapshot>();

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs);

            while (fs.Position < fs.Length)
            {
                // Ler timestamp (8 bytes - long ticks)
                var ticks = br.ReadInt64();
                var timestamp = new DateTime(ticks, DateTimeKind.Utc);

                // Ler quantidade de bids
                var numBids = br.ReadInt32();
                var bids = new List<BookLevel>();

                for (int i = 0; i < numBids; i++)
                {
                    var price = br.ReadDecimal();
                    var volume = br.ReadInt32();
                    bids.Add(new BookLevel(ticker, BookSide.Bid, price, volume, "", timestamp));
                }

                // Ler quantidade de asks
                var numAsks = br.ReadInt32();
                var asks = new List<BookLevel>();

                for (int i = 0; i < numAsks; i++)
                {
                    var price = br.ReadDecimal();
                    var volume = br.ReadInt32();
                    asks.Add(new BookLevel(ticker, BookSide.Ask, price, volume, "", timestamp));
                }

                snapshots.Add(new BookSnapshot(ticker, bids, asks, timestamp));
            }

            Console.WriteLine($"[REPLAY] ✅ Carregados {snapshots.Count} snapshots de {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[REPLAY] ❌ Erro ao carregar book: {ex.Message}");
        }

        return snapshots;
    }

    private string ExtrairAtivo(string ticker)
    {
        if (ticker.StartsWith("WIN")) return "WIN";
        if (ticker.StartsWith("WDO")) return "WDO";
        if (ticker.StartsWith("WSP")) return "WSP";
        return ticker;
    }

    public void Dispose()
    {
        DisconnectAsync().Wait();
    }
}

/// <summary>
/// Metadata do pregão gravado.
/// </summary>
public class ReplayMetadata
{
    public string data { get; set; } = string.Empty;
    public string timestamp_gravacao { get; set; } = string.Empty;
    public string timezone { get; set; } = string.Empty;
    public int total_trades { get; set; }
    public int total_books { get; set; }
    public long bytes_brutos { get; set; }
    public List<string> ativos { get; set; } = new();
    public string versao_formato { get; set; } = string.Empty;
}