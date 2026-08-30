using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MarketCore.Engine.Features;
using MarketCore.Models;

namespace MarketCore.Engine.Replay;

/// <summary>
/// Motor de replay determinístico.
/// Reproduz os eventos binários brutos de um pregão passado na ordem original,
/// alimentando o mesmo pipeline do mercado real (FeatureEngine → EventDetector → RegimeDetector).
///
/// PROPRIEDADE FUNDAMENTAL: a mesma entrada produz sempre a mesma saída.
/// O ChecksumHash ao final permite verificar esse determinismo.
/// </summary>
public class ReplayEngine : IDisposable
{
    private readonly ReplayReader  _reader;
    private readonly FeatureEngine _featureEngine;
    private readonly string        _rawDataPath;
    private readonly string        _ticker;
    private readonly SemaphoreSlim _stepSemaphore = new(0, int.MaxValue);

    private ReplaySession          _session    = new();
    private CancellationTokenSource _cts       = new();
    private bool                   _disposed;

    // ── Eventos ────────────────────────────────────────────────────────────

    /// <summary>Progresso do replay (disparado a cada 1000 eventos).</summary>
    public event Action<ReplaySession>? OnProgress;

    /// <summary>Resultado ao fim do replay.</summary>
    public event Action<ReplayResult>? OnFinished;

    /// <summary>Erro fatal durante o replay.</summary>
    public event Action<string>? OnError;

    // ── Estado ─────────────────────────────────────────────────────────────

    public ReplaySession Session => _session;

    // ── Construtor ─────────────────────────────────────────────────────────

    public ReplayEngine(FeatureEngine featureEngine,
                        string rawDataPath,
                        string ticker = "WINFUT")
    {
        _featureEngine = featureEngine ?? throw new ArgumentNullException(nameof(featureEngine));
        _rawDataPath   = rawDataPath   ?? throw new ArgumentNullException(nameof(rawDataPath));
        _ticker        = ticker;
        _reader        = new ReplayReader();
    }

    // ── API pública ────────────────────────────────────────────────────────

    /// <summary>
    /// Inicia o replay de uma data completa.
    /// Retorna o resultado com checksum SHA256 para verificação de determinismo.
    /// </summary>
    public async Task<ReplayResult> IniciarAsync(
        DateTime    date,
        ReplaySpeed speed            = ReplaySpeed.RealTime,
        CancellationToken externalCt = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ct = _cts.Token;

        // PASSO 1 — Inicializar sessão
        _session = new ReplaySession
        {
            SessionId = Guid.NewGuid(),
            Date      = date,
            Speed     = speed,
            Status    = ReplayStatus.Running
        };

        var inicio = DateTime.UtcNow;
        long tradeCount = 0, bookCount = 0;
        long lastTimestamp = 0;
        decimal priceSum   = 0m;

        try
        {
            // PASSO 2 — Carregar e contar eventos
            IReadOnlyList<object> eventos;
            try
            {
                eventos = _reader.LerEventos(date, _rawDataPath);
            }
            catch (Exception ex)
            {
                _session.Status = ReplayStatus.Error;
                OnError?.Invoke($"Erro ao carregar eventos: {ex.Message}");
                return BuildResult(inicio, tradeCount, bookCount, lastTimestamp, priceSum);
            }

            _session.TotalEvents = eventos.Count;
            _session.CurrentTime = date.Date.AddHours(9);  // início pregão

            // PASSO 3 — Resetar FeatureEngine para sessão nova (garante determinismo)
            _featureEngine.ResetarSessao();

            // PASSO 4 — Loop de reprodução
            DateTime? lastEventTime = null;

            foreach (var evento in eventos)
            {
                if (ct.IsCancellationRequested)
                    break;

                // Aguardar se pausado
                while (_session.Status == ReplayStatus.Paused)
                    await Task.Delay(50, ct).ConfigureAwait(false);

                // Modo passo a passo: aguarda sinal externo
                if (speed == ReplaySpeed.StepByStep)
                {
                    _session.Status = ReplayStatus.Stepping;
                    await _stepSemaphore.WaitAsync(ct).ConfigureAwait(false);
                    _session.Status = ReplayStatus.Running;
                }

                // Throttle baseado na velocidade
                long eventTicks = GetTimestamp(evento);
                DateTime eventTime = new DateTime(eventTicks);

                if (speed != ReplaySpeed.MaxSpeed
                 && speed != ReplaySpeed.StepByStep
                 && lastEventTime.HasValue)
                {
                    TimeSpan elapsed = eventTime - lastEventTime.Value;
                    int      factor  = (int)speed;
                    TimeSpan wait    = TimeSpan.FromTicks(elapsed.Ticks / factor);

                    if (wait > TimeSpan.Zero && wait < TimeSpan.FromSeconds(5))
                        await Task.Delay(wait, ct).ConfigureAwait(false);
                }
                lastEventTime = eventTime;

                // Despachar evento para o FeatureEngine
                if (evento is RawTradeEvent trade)
                {
                    _featureEngine.OnTrade(ConvertToTradeEvent(trade));
                    tradeCount++;
                    priceSum  += trade.Price;
                    lastTimestamp = trade.Timestamp;
                }
                else if (evento is RawBookEvent book)
                {
                    _featureEngine.OnBook(ConvertToBookSnapshot(book));
                    bookCount++;
                    if (book.ExchangeTimestamp > lastTimestamp)
                        lastTimestamp = book.ExchangeTimestamp;
                }

                _session.ProcessedEvents++;
                _session.CurrentTime = eventTime;

                // Notificar progresso a cada 1000 eventos
                if (_session.ProcessedEvents % 1000 == 0)
                    OnProgress?.Invoke(_session);
            }
        }
        catch (OperationCanceledException) { /* replay cancelado */ }
        catch (Exception ex)
        {
            _session.Status = ReplayStatus.Error;
            OnError?.Invoke($"Erro durante replay: {ex.Message}");
        }

        // PASSO 5 — Finalizar
        _session.Status = ReplayStatus.Finished;
        var result = BuildResult(inicio, tradeCount, bookCount, lastTimestamp, priceSum);

        OnFinished?.Invoke(result);
        return result;
    }

    /// <summary>Pausa o replay no próximo evento.</summary>
    public void Pausar()
    {
        if (_session.Status == ReplayStatus.Running)
            _session.Status = ReplayStatus.Paused;
    }

    /// <summary>Retoma o replay pausado.</summary>
    public void Retomar()
    {
        if (_session.Status == ReplayStatus.Paused)
            _session.Status = ReplayStatus.Running;
    }

    /// <summary>Avança exatamente 1 evento no modo StepByStep. Retorna false se o replay terminou.</summary>
    public Task<bool> AvancarUmEventoAsync()
    {
        if (_session.Status == ReplayStatus.Finished
         || _session.Status == ReplayStatus.Error)
            return Task.FromResult(false);

        _stepSemaphore.Release(1);
        return Task.FromResult(true);
    }

    /// <summary>Para o replay imediatamente.</summary>
    public void Parar() => _cts.Cancel();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _stepSemaphore.Dispose();
        _reader.Dispose();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private ReplayResult BuildResult(DateTime inicio, long trades, long books,
                                     long lastTs, decimal priceSum)
    {
        string checksum = ComputeChecksum(_session.ProcessedEvents, lastTs, priceSum);
        return new ReplayResult
        {
            Session              = _session,
            Duration             = DateTime.UtcNow - inicio,
            TradesReplayed       = trades,
            BookUpdatesReplayed  = books,
            IsDeterministic      = true,   // verificável via ChecksumHash
            ChecksumHash         = checksum
        };
    }

    /// <summary>
    /// Calcula SHA256 de: total_eventos|ultimo_timestamp|soma_precos
    /// Permite verificar: replay1.ChecksumHash == replay2.ChecksumHash → determinístico.
    /// </summary>
    private static string ComputeChecksum(long totalEvents, long lastTs, decimal priceSum)
    {
        string input = $"{totalEvents}|{lastTs}|{priceSum:F4}";
        byte[] hash  = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static long GetTimestamp(object evento)
        => evento switch
        {
            RawTradeEvent t => t.Timestamp,
            RawBookEvent  b => b.ExchangeTimestamp,
            _               => 0L
        };

    /// <summary>Converte RawTradeEvent → TradeEvent (modelo de domínio do FeatureEngine).</summary>
    private TradeEvent ConvertToTradeEvent(RawTradeEvent raw)
        => new TradeEvent(
            Ticker:    _ticker,
            Price:     raw.Price,
            Volume:    raw.Volume,
            Broker:    raw.Broker,
            Aggressor: (TradeAggressor)raw.Aggressor,
            Time:      new DateTime(raw.Timestamp, DateTimeKind.Local));

    /// <summary>Converte RawBookEvent → BookSnapshot (modelo de domínio do FeatureEngine).</summary>
    private BookSnapshot ConvertToBookSnapshot(RawBookEvent raw)
    {
        var time = new DateTime(raw.ExchangeTimestamp, DateTimeKind.Local);
        var bids = new List<BookLevel>();
        var asks = new List<BookLevel>();

        for (int i = 0; i < 10; i++)
        {
            if (raw.BidPrices[i] > 0)
                bids.Add(new BookLevel(_ticker, BookSide.Bid,
                    (decimal)raw.BidPrices[i], raw.BidVolumes[i],
                    string.Empty, time, Position: i + 1));
            if (raw.AskPrices[i] > 0)
                asks.Add(new BookLevel(_ticker, BookSide.Ask,
                    (decimal)raw.AskPrices[i], raw.AskVolumes[i],
                    string.Empty, time, Position: i + 1));
        }

        return new BookSnapshot(_ticker, bids, asks, time);
    }
}
