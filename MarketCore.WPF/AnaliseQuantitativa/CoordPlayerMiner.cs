using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MarketCore.Models;
using Npgsql;

namespace MarketCore.WPF.AnaliseQuantitativa;

/// <summary>
/// Motor de mineração e detecção de clusters coordenados de corretoras.
///
/// MINERAÇÃO (a cada MiningIntervalSec):
///   Lê trades_intraday e agrupa segundo a segundo.
///   Em cada segundo, identifica quais corretoras agressiram na mesma ponta.
///   Mede se o preço se moveu nos ImpactWindowSec seguintes.
///   Grava o padrão: "toda vez que XP+BTG compraram no mesmo segundo → preço subiu X pts em Y% dos casos".
///
/// DETECÇÃO (a cada 1 segundo):
///   Olha os trades que chegaram no último segundo ao vivo.
///   Se o conjunto de corretoras bate com um padrão aprendido → sinal imediato.
///   Sinal fica ativo por SignalPersistSec segundos.
/// </summary>
public sealed class CoordPlayerMiner : IDisposable
{
    // ── configuração ──────────────────────────────────────────────────
    private const int MiningIntervalSec   = 30;    // minerar a cada 30s — atualização quase contínua
    private const int ImpactWindowSec     = 20;    // mede impacto 20s após o cluster
    private const int ImpactMinTicks      = 2;     // impacto mínimo para "hit" (pts de WIN = R$1)
    private const int MinBrokersCluster   = 2;     // mínimo de corretoras no cluster
    private const int MinObsForSignal     = 30;    // exige amostra estatisticamente relevante
    /// <summary>Win rate mínimo para emitir sinal (ajustável em tempo real pela UI).</summary>
    public double MinWinRateSignal { get; set; } = 0.51;  // padrão 51%
    private const double MinScoreSignal   = 450.0; // filtro composto: obs × WR × impacto — só clusters "fortes"
    private const double MinSideDominance = 1.20;  // lado vencedor precisa ter score 20% > lado perdedor
    private const int SignalPersistSec    = 45;    // sinal fica ativo por 45s após detecção
    private const int MaxLiveQueue       = 50_000;
    private const int MaxPatterns        = 5_000; // capacidade grande; piores são descartados automaticamente

    // ── estado interno ────────────────────────────────────────────────
    private readonly string  _connStr;
    private readonly string  _symbol;
    private readonly ConcurrentQueue<TradeEvent> _liveQueue = new();

    // padrões aprendidos: chave = "B|XP,BTG" (lado + brokers ordenados)
    private readonly Dictionary<string, ClusterPattern> _patterns = new(StringComparer.Ordinal);
    private readonly object _patternsLock = new();

    private CoordSignal _lastSignal      = new();
    private CoordSignal _persistedSignal = new();
    private DateTime       _lastRenewalAt  = DateTime.MinValue;
    private CoordSignalDir _lastRenewalDir = CoordSignalDir.Aguardar;
    private const int      RenewalBadgeSec = 5;
    private DateTime    _lastMiningRun   = DateTime.MinValue;
    // Timestamp do trade mais recente já processado. Na próxima mineração, só lê trades mais recentes que isto.
    // Evita re-contar os mesmos trades a cada ciclo (bug do "OBS inflado" quando MiningIntervalSec é curto).
    // Sobreposição de 25s para pegar clusters cortados na borda do ciclo anterior.
    private DateTime    _lastMinedTradeTs = DateTime.MinValue;
    private int         _dbTradesLastMining;
    private bool        _miningRunning;

    private CancellationTokenSource? _cts;
    private Thread? _mineThread;
    private Thread? _detectThread;

    // ── eventos ───────────────────────────────────────────────────────
    public event Action<CoordSignal>?          OnSignal;
    public event Action<List<ClusterPattern>>? OnPatternsUpdated;

    // ── leitura pública ───────────────────────────────────────────────
    public CoordSignal LastSignal         => _lastSignal;
    public DateTime    LastMiningRun      => _lastMiningRun;
    public int         DbTradesLastMining => _dbTradesLastMining;
    public int         PatternCount       { get { lock (_patternsLock) return _patterns.Count; } }
    public bool        IsMiningRunning    => _miningRunning;
    public int         LiveQueueCount     { get { lock (_windowLock) return _recentWindow.Count; } }
    public string      LastDetectInfo     { get; private set; } = "aguardando...";

    /// <summary>Cursor atual de mineração (último trade processado).</summary>
    public DateTime LastMinedTradeTs => _lastMinedTradeTs;

    /// <summary>Diagnóstico: estado atual do banco.</summary>
    public long     DbTotalRows { get; private set; }
    public DateTime DbMinTs     { get; private set; } = DateTime.MinValue;
    public DateTime DbMaxTs     { get; private set; } = DateTime.MinValue;

    /// <summary>Padrões NOVOS criados no último ciclo de mineração.</summary>
    public int NewPatternsLastCycle    { get; private set; }
    /// <summary>Padrões EXISTENTES que ganharam novas observações no último ciclo.</summary>
    public int UpdatedPatternsLastCycle { get; private set; }

    /// <summary>Segundos até a próxima mineração (0 se está minerando agora).</summary>
    public int NextMiningInSec =>
        _lastMiningRun == DateTime.MinValue
            ? 0
            : Math.Max(0, MiningIntervalSec - (int)(DateTime.Now - _lastMiningRun).TotalSeconds);

    public int SignalSecondsLeft =>
        _persistedSignal.Direction != CoordSignalDir.Aguardar
            ? Math.Max(0, SignalPersistSec - (int)(DateTime.Now - _persistedSignal.GeneratedAt).TotalSeconds)
            : 0;

    /// <summary>Segundos restantes do badge de renovação (0 se não há renovo recente).</summary>
    public int RenewalSecondsLeft =>
        _lastRenewalDir != CoordSignalDir.Aguardar
            ? Math.Max(0, RenewalBadgeSec - (int)(DateTime.Now - _lastRenewalAt).TotalSeconds)
            : 0;

    public CoordSignalDir LastRenewalDir => _lastRenewalDir;

    public CoordPlayerMiner(string connectionString, string symbol = "")
    {
        _connStr = connectionString;
        _symbol  = symbol;
    }

    // ─────────────────────────────────────────────────────────────────
    //  API PÚBLICA
    // ─────────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_cts != null) return;

        // Carrega padrões salvos de execuções anteriores (acumula histórico dia após dia)
        LoadPatternsFromDisk();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _mineThread = new Thread(() => MineLoop(ct))
        {
            IsBackground = true,
            Name         = "CoordMiner-Mine",
            Priority     = ThreadPriority.BelowNormal
        };
        _detectThread = new Thread(() => DetectLoop(ct))
        {
            IsBackground = true,
            Name         = "CoordMiner-Detect",
            Priority     = ThreadPriority.AboveNormal   // detecção é crítica para latência
        };

        _mineThread.Start();
        _detectThread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _mineThread?.Join(3000);
        _detectThread?.Join(1000);
        _cts?.Dispose();
        _cts = null;

        // Salva ao fechar — garante que nada seja perdido
        SavePatternsToDisk();
    }

    // Throttling event-driven: se muitos trades chegarem, evita rodar RunDetectCycle mais
    // de uma vez a cada InstantDetectMinMs (o cluster ativo não muda a cada trade individual).
    private long _lastInstantDetectMs;
    private const int InstantDetectMinMs = 50; // no máximo 20 detecções/s por trades novos

    /// <summary>Alimenta trade ao vivo (chamado pelo hot path de trades).
    /// IMPORTANTE:
    ///   1. Sobrescreve trade.Time com DateTime.Now (DLL manda datas erradas às vezes)
    ///   2. Dispara detecção IMEDIATA se passou InstantDetectMinMs desde a última —
    ///      latência trade→sinal fica ~50ms em vez de esperar o polling.</summary>
    public void PushLiveTrade(TradeEvent trade)
    {
        var t = trade with { Time = DateTime.Now };
        lock (_windowLock)
        {
            _recentWindow.Add(t);
            // limite duro: se passar de MaxLiveQueue, remove os primeiros (mais antigos)
            if (_recentWindow.Count > MaxLiveQueue)
                _recentWindow.RemoveRange(0, _recentWindow.Count - MaxLiveQueue);
        }

        // Detecção instantânea com throttling: se muitos trades chegarem, dispara
        // no máximo 1x por InstantDetectMinMs (20/s) em vez de 1x por trade.
        long nowMs = Environment.TickCount64;
        long lastMs = System.Threading.Interlocked.Read(ref _lastInstantDetectMs);
        if (nowMs - lastMs >= InstantDetectMinMs &&
            System.Threading.Interlocked.CompareExchange(ref _lastInstantDetectMs, nowMs, lastMs) == lastMs)
        {
            // Roda a detecção em ThreadPool pra não bloquear o hot path da DLL
            System.Threading.Tasks.Task.Run(() =>
            {
                try { TriggerDetectionOnce(); } catch { /* silent */ }
            });
        }
    }

    /// <summary>Executa 1 ciclo de detecção fora do polling (usado pelo event-driven).</summary>
    private void TriggerDetectionOnce()
    {
        var fresh = RunDetectCycle();
        if (fresh.Direction != CoordSignalDir.Aguardar)
        {
            bool isRenewal = _persistedSignal.Direction == fresh.Direction &&
                             (DateTime.Now - _persistedSignal.GeneratedAt).TotalSeconds <= SignalPersistSec;
            _persistedSignal = fresh;
            if (isRenewal)
            {
                _lastRenewalAt  = DateTime.Now;
                _lastRenewalDir = fresh.Direction;
            }
            _lastSignal = _persistedSignal;
            OnSignal?.Invoke(_persistedSignal);
        }
    }

    public System.Threading.Tasks.Task ForceMineCycleAsync()
        => System.Threading.Tasks.Task.Run(RunMineCycle);

    /// <summary>Apaga todos os padrões aprendidos e reseta o cursor de mineração.
    /// A próxima mineração vai rebuild do zero lendo as últimas 8h do DB.</summary>
    public void ClearPatterns()
    {
        lock (_patternsLock) _patterns.Clear();
        _lastMinedTradeTs = DateTime.MinValue;
        _persistedSignal  = new CoordSignal();
        _lastSignal       = new CoordSignal();
        try { if (File.Exists(PatternsFilePath)) File.Delete(PatternsFilePath); } catch { /* ignore */ }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  PERSISTÊNCIA — padrões sobrevivem entre execuções do programa
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Caminho do arquivo JSON com padrões aprendidos.</summary>
    private static string PatternsFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarketCore",
            "coord_patterns.json");

    /// <summary>DTO serializável — evita expor props computadas.</summary>
    private sealed class PatternsSnapshot
    {
        public DateTime           SavedAt         { get; set; }
        public DateTime           LastMinedTradeTs { get; set; }
        public List<PatternDto>?  Patterns        { get; set; }
    }
    private sealed class PatternDto
    {
        public string   Key            { get; set; } = "";
        public string   Side           { get; set; } = "";
        public string[] Brokers        { get; set; } = Array.Empty<string>();
        public int      Observations   { get; set; }
        public int      Hits           { get; set; }
        public double   AvgImpactTicks { get; set; }
        public DateTime LastSeen       { get; set; }
    }

    /// <summary>Salva padrões atuais em disco (chamado ao fim de cada ciclo).</summary>
    private void SavePatternsToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(PatternsFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            PatternsSnapshot snap;
            lock (_patternsLock)
            {
                snap = new PatternsSnapshot
                {
                    SavedAt         = DateTime.Now,
                    LastMinedTradeTs = _lastMinedTradeTs,
                    Patterns        = _patterns.Values.Select(p => new PatternDto
                    {
                        Key            = p.Key,
                        Side           = p.Side,
                        Brokers        = p.Brokers,
                        Observations   = p.Observations,
                        Hits           = p.Hits,
                        AvgImpactTicks = p.AvgImpactTicks,
                        LastSeen       = p.LastSeen,
                    }).ToList(),
                };
            }

            // Escreve em arquivo temporário e move (evita corrupção se travar durante o save)
            var tmp = PatternsFilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snap));
            File.Move(tmp, PatternsFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CoordMiner] Erro ao salvar padrões: {ex.Message}");
        }
    }

    /// <summary>Carrega padrões do disco (chamado uma vez no Start()).</summary>
    private void LoadPatternsFromDisk()
    {
        try
        {
            if (!File.Exists(PatternsFilePath)) return;

            var json = File.ReadAllText(PatternsFilePath);
            var snap = JsonSerializer.Deserialize<PatternsSnapshot>(json);
            if (snap?.Patterns == null) return;

            // Retenção de ~5 dias de pregão (7 dias corridos cobrem fim de semana).
            // Padrões cujo último "avistamento" foi há mais de 7 dias são descartados.
            var cutoff = DateTime.Now.AddDays(-7);
            int skipped = 0;

            lock (_patternsLock)
            {
                _patterns.Clear();
                foreach (var d in snap.Patterns)
                {
                    if (d.LastSeen < cutoff)
                    {
                        skipped++;
                        continue;
                    }
                    _patterns[d.Key] = new ClusterPattern
                    {
                        Key            = d.Key,
                        Side           = d.Side,
                        Brokers        = d.Brokers,
                        Observations   = d.Observations,
                        Hits           = d.Hits,
                        AvgImpactTicks = d.AvgImpactTicks,
                        LastSeen       = d.LastSeen,
                    };
                }
            }

            if (skipped > 0)
                Console.WriteLine($"[CoordMiner] Descartados {skipped} padrões com mais de 7 dias.");
            // Retoma o cursor onde parou — só busca trades NOVOS desde a última execução
            _lastMinedTradeTs = snap.LastMinedTradeTs;

            Console.WriteLine($"[CoordMiner] Carregou {snap.Patterns.Count} padrões salvos em " +
                              $"{snap.SavedAt:dd/MM HH:mm:ss} (cursor: {_lastMinedTradeTs:dd/MM HH:mm:ss})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CoordMiner] Erro ao carregar padrões: {ex.Message}");
        }
    }

    public List<ClusterPattern> GetTopPatterns(int n = 30)
    {
        lock (_patternsLock)
            return _patterns.Values
                .OrderByDescending(p => p.Score)
                .Take(n)
                .ToList();
    }

    public void Dispose() => Stop();

    // ─────────────────────────────────────────────────────────────────
    //  THREAD DE MINERAÇÃO
    // ─────────────────────────────────────────────────────────────────

    private void MineLoop(CancellationToken ct)
    {
        // aguarda 15s para o DB acumular trades antes da primeira mineração
        for (int i = 0; i < 150 && !ct.IsCancellationRequested; i++)
            Thread.Sleep(100);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _miningRunning = true;
                RunMineCycle();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CoordMiner] Erro mineração: {ex.Message}");
            }
            finally
            {
                _miningRunning = false;
            }

            for (int i = 0; i < MiningIntervalSec * 10 && !ct.IsCancellationRequested; i++)
                Thread.Sleep(100);
        }
    }

    private void RunMineCycle()
    {
        // Mineração incremental: só processa trades NOVOS desde o último ciclo.
        // Assim padrões antigos são preservados (observações acumuladas) e novos trades
        // adicionam observações incrementalmente — nada é apagado, nada é re-contado.
        //
        // Se o cursor for de um dia anterior (ex.: JSON salvo ontem), faz bootstrap forçado
        // desde 00h de hoje. Isso evita o cenário "cursor congelado" quando o dia muda.
        if (_lastMinedTradeTs != DateTime.MinValue && _lastMinedTradeTs.Date < DateTime.Today)
        {
            Console.WriteLine($"[CoordMiner] Cursor {_lastMinedTradeTs:dd/MM HH:mm} é de outro dia — bootstrap forçado hoje");
            _lastMinedTradeTs = DateTime.MinValue;
        }

        var trades = LoadTradesFromDb(_lastMinedTradeTs);
        _dbTradesLastMining = trades.Count;

        if (trades.Count < 10)
        {
            _lastMiningRun = DateTime.Now;
            NewPatternsLastCycle     = 0;
            UpdatedPatternsLastCycle = 0;
            // avança o cursor mesmo com poucos trades (evita re-consulta do mesmo intervalo)
            if (trades.Count > 0)
                _lastMinedTradeTs = trades.Max(t => t.Time);
            // redispara evento de atualização pra UI ficar viva (mesmo sem novos padrões)
            OnPatternsUpdated?.Invoke(GetTopPatterns(50));
            SavePatternsToDisk();
            return;
        }

        // ordena por tempo
        trades.Sort((a, b) => a.Time.CompareTo(b.Time));

        // indexa por segundo para acesso O(1)
        // chave = segundo truncado, valor = lista de trades naquele segundo
        var bySecond = new Dictionary<long, List<DbTrade>>();
        foreach (var t in trades)
        {
            long key = new DateTimeOffset(t.Time).ToUnixTimeSeconds();
            if (!bySecond.TryGetValue(key, out var list))
            {
                list = new List<DbTrade>();
                bySecond[key] = list;
            }
            list.Add(t);
        }

        var sessionPatterns = new Dictionary<string, ClusterPattern>(StringComparer.Ordinal);
        var seconds = bySecond.Keys.OrderBy(k => k).ToList();

        foreach (long sec in seconds)
        {
            var window = bySecond[sec];

            // preço de referência: último trade deste segundo
            decimal priceRef = window[^1].Price;

            // busca o preço ImpactWindowSec segundos depois
            long futureSec = sec + ImpactWindowSec;
            // procura o segundo mais próximo disponível após futureSec
            decimal priceFuture = -1;
            for (int delta = 0; delta <= 5; delta++)
            {
                if (bySecond.TryGetValue(futureSec + delta, out var futureList))
                {
                    priceFuture = futureList[0].Price;
                    break;
                }
            }

            if (priceFuture < 0) continue; // não temos dados futuros suficientes, pula

            decimal priceDelta = priceFuture - priceRef; // em pts (WIN: 1 pt = R$1)

            // separa por lado agressor ("B" = compra, "S" = venda)
            foreach (var sideKey in new[] { "B", "S" })
            {
                var sideTrades = window
                    .Where(t => !string.IsNullOrEmpty(t.BrokerName)
                             && IsMatchingSide(t.Side, t.Aggressor, sideKey))
                    .ToList();

                if (sideTrades.Count == 0) continue;

                var brokers = sideTrades
                    .Select(t => NormBroker(t.BrokerName!))
                    .Where(b => b.Length > 0)
                    .Distinct()
                    .OrderBy(b => b)
                    .ToList();

                if (brokers.Count < MinBrokersCluster) continue;

                // "hit" = preço moveu na direção esperada pelo menos ImpactMinTicks pts
                bool hit = sideKey == "B"
                    ? priceDelta >= (decimal)ImpactMinTicks
                    : priceDelta <= -(decimal)ImpactMinTicks;

                double absDelta = (double)Math.Abs(priceDelta);

                // gera todos os clusters de 2 e 3 brokers
                foreach (var cluster in GenerateClusters(brokers))
                {
                    string key = $"{sideKey}|{string.Join(",", cluster)}";
                    if (!sessionPatterns.TryGetValue(key, out var pat))
                    {
                        pat = new ClusterPattern
                        {
                            Key     = key,
                            Side    = sideKey,
                            Brokers = cluster.ToArray(),
                        };
                        sessionPatterns[key] = pat;
                    }

                    pat.Observations++;
                    if (hit) pat.Hits++;
                    pat.AvgImpactTicks = (pat.AvgImpactTicks * (pat.Observations - 1) + absDelta) / pat.Observations;
                    pat.LastSeen       = new DateTime(1970, 1, 1).AddSeconds(sec).ToLocalTime();
                }
            }
        }

        // mescla com padrões globais acumulados — conta quantos foram criados/atualizados
        int newCount = 0, updatedCount = 0;
        lock (_patternsLock)
        {
            foreach (var kv in sessionPatterns)
            {
                if (!_patterns.TryGetValue(kv.Key, out var existing))
                {
                    _patterns[kv.Key] = kv.Value;
                    newCount++;
                }
                else
                {
                    updatedCount++;
                    int total = existing.Observations + kv.Value.Observations;
                    existing.AvgImpactTicks =
                        (existing.AvgImpactTicks * existing.Observations + kv.Value.AvgImpactTicks * kv.Value.Observations) / total;
                    existing.Observations = total;
                    existing.Hits        += kv.Value.Hits;
                    if (kv.Value.LastSeen > existing.LastSeen)
                        existing.LastSeen = kv.Value.LastSeen;
                }
            }

            // mantém só os MaxPatterns melhores
            if (_patterns.Count > MaxPatterns)
            {
                var toRemove = _patterns.Values
                    .OrderBy(p => p.Score)
                    .Take(_patterns.Count - MaxPatterns)
                    .Select(p => p.Key)
                    .ToList();
                foreach (var k in toRemove)
                    _patterns.Remove(k);
            }
        }

        _lastMiningRun = DateTime.Now;
        NewPatternsLastCycle     = newCount;
        UpdatedPatternsLastCycle = updatedCount;
        // Avança o cursor incremental para o timestamp do último trade processado neste ciclo.
        // No próximo ciclo, só trades ESTRITAMENTE mais novos que isto serão lidos.
        if (trades.Count > 0)
            _lastMinedTradeTs = trades[^1].Time;
        OnPatternsUpdated?.Invoke(GetTopPatterns(50));

        // Persiste em disco a cada ciclo — se travar o programa, nada é perdido
        SavePatternsToDisk();

        Console.WriteLine($"[CoordMiner] Mineração: {_dbTradesLastMining} trades novos, " +
                          $"{sessionPatterns.Count} clusters neste ciclo, {PatternCount} padrões totais " +
                          $"(cursor: {_lastMinedTradeTs:HH:mm:ss}).");
    }

    // ─────────────────────────────────────────────────────────────────
    //  THREAD DE DETECÇÃO — roda a cada 1 segundo
    // ─────────────────────────────────────────────────────────────────

    // janela deslizante de trades ao vivo (somente últimos 120s)
    private readonly List<TradeEvent> _recentWindow = new(1024);
    private readonly object           _windowLock   = new();

    private void DetectLoop(CancellationToken ct)
    {
        int drainTick = 0;
        while (!ct.IsCancellationRequested)
        {
            var tStart = DateTime.Now;
            try
            {
                // Trades já entram direto em _recentWindow via PushLiveTrade (event-driven).
                // Este loop só faz backup periódico (limpeza + safety-net de detecção).

                // a cada 10s (100 × 100ms) remove itens antigos (> 120s) da janela
                if (++drainTick % 100 == 0)
                {
                    var cutOld = DateTime.Now.AddSeconds(-120);
                    lock (_windowLock)
                        _recentWindow.RemoveAll(t => t.Time < cutOld);
                }

                var fresh = RunDetectCycle();

                // se detectou algo novo:
                //   - substitui o sinal ativo (reseta o timer para 45s)
                //   - se for MESMA direção do sinal já ativo → também dispara o badge de 5s
                if (fresh.Direction != CoordSignalDir.Aguardar)
                {
                    bool isRenewal = _persistedSignal.Direction == fresh.Direction &&
                                     (DateTime.Now - _persistedSignal.GeneratedAt).TotalSeconds <= SignalPersistSec;

                    // Sempre substitui — reseta o timer principal para 45s
                    _persistedSignal = fresh;

                    // Se foi renovação da mesma direção, aciona badge por 5s
                    if (isRenewal)
                    {
                        _lastRenewalAt  = DateTime.Now;
                        _lastRenewalDir = fresh.Direction;
                    }
                }

                // decide o que emitir
                CoordSignal toEmit;
                if (_persistedSignal.Direction != CoordSignalDir.Aguardar &&
                    (DateTime.Now - _persistedSignal.GeneratedAt).TotalSeconds <= SignalPersistSec)
                {
                    toEmit = _persistedSignal;
                }
                else
                {
                    _persistedSignal = new CoordSignal();
                    toEmit = new CoordSignal { Direction = CoordSignalDir.Aguardar };
                }

                _lastSignal = toEmit;
                OnSignal?.Invoke(toEmit);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CoordMiner] Erro detecção: {ex.Message}");
            }

            // Safety-net a 100ms — detecção principal agora é event-driven em PushLiveTrade.
            // Este polling só garante que mesmo sem trades novos, ciclos periódicos rodem.
            int elapsed = (int)(DateTime.Now - tStart).TotalMilliseconds;
            int remaining = 100 - elapsed;
            if (remaining > 0 && !ct.IsCancellationRequested)
                Thread.Sleep(remaining);
        }
    }

    private CoordSignal RunDetectCycle()
    {
        // janela de 10s: cobre mercado lento (sessão noturna) e picos de volume
        var cutoff = DateTime.Now.AddSeconds(-10.0);
        List<TradeEvent> recent;
        int windowSize;
        DateTime newestInWindow = DateTime.MinValue;
        lock (_windowLock)
        {
            windowSize = _recentWindow.Count;
            if (windowSize > 0)
                newestInWindow = _recentWindow.Max(t => t.Time);
            recent = _recentWindow.Where(t => t.Time >= cutoff).ToList();
        }

        if (recent.Count == 0)
        {
            if (windowSize == 0)
                LastDetectInfo = "janela vazia — nenhum trade chegou ao miner";
            else
            {
                var ageSec = (DateTime.Now - newestInWindow).TotalSeconds;
                LastDetectInfo = $"janela tem {windowSize} trades mas o + recente é de {ageSec:F0}s atrás " +
                                 $"(now={DateTime.Now:HH:mm:ss} newest={newestInWindow:HH:mm:ss})";
            }
            return new CoordSignal { Direction = CoordSignalDir.Aguardar };
        }

        var diagParts = new System.Text.StringBuilder();
        diagParts.Append($"recentes={recent.Count}");

        // Coleta melhor match de cada lado
        (ClusterPattern? top, string[] brokers, CoordSignalDir dir)? bestB = null;
        (ClusterPattern? top, string[] brokers, CoordSignalDir dir)? bestS = null;

        foreach (var (sideKey, dir, aggressor) in new[]
        {
            ("B", CoordSignalDir.Comprar, TradeAggressor.Buy),
            ("S", CoordSignalDir.Vender,  TradeAggressor.Sell),
        })
        {
            var sideTrades = recent
                .Where(t => t.Aggressor == aggressor && !string.IsNullOrEmpty(t.Broker))
                .ToList();

            diagParts.Append($" | {sideKey}:{sideTrades.Count}trades");

            if (sideTrades.Count == 0) continue;

            var activeBrokers = sideTrades
                .Select(t => NormBroker(t.Broker))
                .Where(b => b.Length > 0)
                .Distinct()
                .OrderBy(b => b)
                .ToList();

            diagParts.Append($"[{string.Join(",", activeBrokers.Take(4))}]");

            if (activeBrokers.Count < MinBrokersCluster) continue;

            List<ClusterPattern> matches;
            int clustersChecked;
            lock (_patternsLock)
            {
                var allClusters = GenerateClusters(activeBrokers);
                clustersChecked = allClusters.Count;
                matches = allClusters
                    .Select(c =>
                    {
                        string k = $"{sideKey}|{string.Join(",", c)}";
                        _patterns.TryGetValue(k, out var p);
                        return p;
                    })
                    .Where(p => p != null
                             && p.Observations >= MinObsForSignal
                             && p.WinRate       >= MinWinRateSignal
                             && p.Score         >= MinScoreSignal)
                    .Select(p => p!)
                    .OrderByDescending(p => p.Score)
                    .ToList();
            }

            diagParts.Append($" clusters={clustersChecked} matches={matches.Count}");

            if (matches.Count == 0) continue;

            var top = matches[0];
            var entry = (top: (ClusterPattern?)top, brokers: activeBrokers.ToArray(), dir);
            if (sideKey == "B") bestB = entry; else bestS = entry;
        }

        // Se nenhum lado tem match qualificado → AGUARDAR
        if (bestB is null && bestS is null)
        {
            LastDetectInfo = diagParts + " → sem padrões qualificados";
            return new CoordSignal { Direction = CoordSignalDir.Aguardar };
        }

        // Ambos os lados têm sinal — verifica dominância
        double scoreB = bestB?.top?.Score ?? 0;
        double scoreS = bestS?.top?.Score ?? 0;

        if (bestB is not null && bestS is not null)
        {
            double ratio = Math.Max(scoreB, scoreS) / Math.Max(1, Math.Min(scoreB, scoreS));
            if (ratio < MinSideDominance)
            {
                LastDetectInfo = diagParts + $" → INDECISÃO (B={scoreB:F0} vs S={scoreS:F0} ratio={ratio:F2}) → AGUARDAR";
                return new CoordSignal { Direction = CoordSignalDir.Aguardar };
            }
        }

        // Lado vencedor
        var winner = scoreB >= scoreS ? bestB!.Value : bestS!.Value;
        var winTop = winner.top!;
        double conf = Math.Min(0.99, winTop.WinRate * Math.Min(1.0, winTop.Observations / 50.0));

        diagParts.Append($" → SINAL {(winner.dir == CoordSignalDir.Comprar ? "B" : "S")} " +
                         $"conf={conf:P0} score={winTop.Score:F0}");
        LastDetectInfo = diagParts.ToString();

        return new CoordSignal
        {
            Direction      = winner.dir,
            Confidence     = conf,
            AvgImpactTicks = (int)Math.Round(winTop.AvgImpactTicks),
            ActiveBrokers  = winner.brokers,
            PatternKey     = winTop.Key,
            GeneratedAt    = DateTime.Now,
        };
    }

    // ─────────────────────────────────────────────────────────────────
    //  LEITURA DO BANCO
    // ─────────────────────────────────────────────────────────────────

    private List<DbTrade> LoadTradesFromDb(DateTime sinceExclusive)
    {
        var result = new List<DbTrade>(5000);
        try
        {
            using var conn = new NpgsqlConnection(_connStr);
            conn.Open();

            // Se nunca minerou (sinceExclusive = MinValue): pega últimas 8h para bootstrap.
            // Se já minerou: pega só trades ESTRITAMENTE MAIS NOVOS que o último timestamp processado.
            // (com sobreposição de 25s para não perder clusters partidos entre ciclos)
            bool isFirstRun = sinceExclusive == DateTime.MinValue;
            string sql;
            if (isFirstRun)
            {
                sql = string.IsNullOrEmpty(_symbol)
                    ? @"SELECT timestamp, price, quantity, side, aggressor, broker_name
                        FROM   trades_intraday
                        WHERE  timestamp >= DATE_TRUNC('day', NOW()) + INTERVAL '9 hours'
                        ORDER  BY timestamp ASC LIMIT 10000000"
                    : @"SELECT timestamp, price, quantity, side, aggressor, broker_name
                        FROM   trades_intraday
                        WHERE  timestamp >= DATE_TRUNC('day', NOW()) + INTERVAL '9 hours'
                          AND  (symbol = @sym OR symbol LIKE @symP)
                        ORDER  BY timestamp ASC LIMIT 10000000";
            }
            else
            {
                sql = string.IsNullOrEmpty(_symbol)
                    ? @"SELECT timestamp, price, quantity, side, aggressor, broker_name
                        FROM   trades_intraday
                        WHERE  timestamp > @since AND timestamp::date = CURRENT_DATE
                        ORDER  BY timestamp ASC LIMIT 10000000"
                    : @"SELECT timestamp, price, quantity, side, aggressor, broker_name
                        FROM   trades_intraday
                        WHERE  timestamp > @since AND timestamp::date = CURRENT_DATE
                          AND  (symbol = @sym OR symbol LIKE @symP)
                        ORDER  BY timestamp ASC LIMIT 10000000";
            }

            // diagnóstico: MIN, MAX e COUNT — mostra faixa real dos dados no DB
            using (var diagCmd = new NpgsqlCommand(
                "SELECT COUNT(*), MIN(timestamp), MAX(timestamp) FROM trades_intraday", conn))
            using (var diagR = diagCmd.ExecuteReader())
            {
                if (diagR.Read())
                {
                    long total = diagR.GetInt64(0);
                    DateTime? dbMin = diagR.IsDBNull(1) ? null : diagR.GetDateTime(1);
                    DateTime? dbMax = diagR.IsDBNull(2) ? null : diagR.GetDateTime(2);
                    DbTotalRows = total;
                    DbMinTs     = dbMin ?? DateTime.MinValue;
                    DbMaxTs     = dbMax ?? DateTime.MinValue;
                    Console.WriteLine($"[CoordMiner-DB] {total} linhas | {dbMin:dd/MM HH:mm} → {dbMax:dd/MM HH:mm}");
                }
            }

            using var cmd = new NpgsqlCommand(sql, conn);
            if (!isFirstRun)
            {
                var since = DateTime.SpecifyKind(sinceExclusive, DateTimeKind.Unspecified);
                cmd.Parameters.AddWithValue("since", since);
            }
            if (!string.IsNullOrEmpty(_symbol))
            {
                cmd.Parameters.AddWithValue("sym",  _symbol);
                cmd.Parameters.AddWithValue("symP", _symbol + "%");
            }

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                result.Add(new DbTrade
                {
                    Time       = rdr.GetDateTime(0),
                    Price      = rdr.GetDecimal(1),
                    Qty        = rdr.GetInt32(2),
                    Side       = rdr.IsDBNull(3) ? "" : rdr.GetString(3),
                    Aggressor  = rdr.IsDBNull(4) ? 0  : rdr.GetInt32(4),
                    BrokerName = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CoordMiner] Erro ao ler DB: {ex.Message}");
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────
    //  UTILITÁRIOS
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Determina se um trade do DB é do lado esperado.
    /// Aceita: aggressor int (2=compra/3=venda) ou side string (B/C/S/V).
    /// </summary>
    private static bool IsMatchingSide(string side, int aggressor, string sideKey)
    {
        if (sideKey == "B")
            return aggressor == 2
                || string.Equals(side, "B", StringComparison.OrdinalIgnoreCase)
                || string.Equals(side, "C", StringComparison.OrdinalIgnoreCase)
                || string.Equals(side, "compra", StringComparison.OrdinalIgnoreCase)
                || string.Equals(side, "buy",    StringComparison.OrdinalIgnoreCase);

        return aggressor == 3
            || string.Equals(side, "S", StringComparison.OrdinalIgnoreCase)
            || string.Equals(side, "V", StringComparison.OrdinalIgnoreCase)
            || string.Equals(side, "venda", StringComparison.OrdinalIgnoreCase)
            || string.Equals(side, "sell",  StringComparison.OrdinalIgnoreCase);
    }

    private static List<List<string>> GenerateClusters(List<string> brokers)
    {
        var result = new List<List<string>>();
        int n = Math.Min(brokers.Count, 6);

        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                result.Add(new List<string> { brokers[i], brokers[j] });

        if (n >= 3)
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    for (int k = j + 1; k < n; k++)
                        result.Add(new List<string> { brokers[i], brokers[j], brokers[k] });

        return result;
    }

    private static string NormBroker(string? broker)
    {
        if (string.IsNullOrWhiteSpace(broker)) return "";
        var s = broker.Trim().ToUpperInvariant();
        var space = s.IndexOf(' ');
        return space > 0 ? s[..space] : s;
    }

    // ─────────────────────────────────────────────────────────────────
    //  MODELO INTERNO (DB)
    // ─────────────────────────────────────────────────────────────────

    private sealed class DbTrade
    {
        public DateTime Time       { get; set; }
        public decimal  Price      { get; set; }   // WIN: valores como 180065.0
        public int      Qty        { get; set; }
        public string   Side       { get; set; } = "";
        public int      Aggressor  { get; set; }  // 2=compra, 3=venda
        public string?  BrokerName { get; set; }
    }
}
