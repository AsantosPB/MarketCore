using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using MarketCore.Engine.Calendar;

namespace MarketCore.Engine.Storage;

/// <summary>
/// Gerencia os dois bancos analíticos da Fase 3:
///   • DuckDB  — market_snapshots + labels  (dados processados, consultáveis via SQL)
///   • SQLite  — decisions, trades, patterns, config  (metadados e registros operacionais)
///
/// Completamente independente do armazenamento binário bruto (Fases 1 e 2).
/// Alimentação real começa na Fase 5 (Feature Engine) e Fase 12 (Decision Core).
/// </summary>
public sealed class StorageManager : IDisposable
{
    private DuckDBConnection? _duckDb;
    private SqliteConnection? _sqlite;

    private bool _disposed;

    // ── Inicialização ─────────────────────────────────────────────────────

    /// <summary>
    /// Cria os bancos de dados (se não existirem), aplica os schemas e deixa
    /// as conexões abertas prontas para uso. Idempotente — seguro chamar a cada pregão.
    /// </summary>
    public async Task InicializarAsync(string dataPath)
    {
        Directory.CreateDirectory(dataPath);

        await InicializarDuckDbAsync(dataPath);
        await InicializarSqliteAsync(dataPath);
    }

    // ── DuckDB ────────────────────────────────────────────────────────────

    private Task InicializarDuckDbAsync(string dataPath)
    {
        var duckDbPath = Path.Combine(dataPath, "market.duckdb");
        _duckDb = new DuckDBConnection($"Data Source={duckDbPath}");
        _duckDb.Open();

        using var cmd = _duckDb.CreateCommand();

        // [FASE 3] Tabela de snapshots de mercado com features derivadas
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS market_snapshots (
    timestamp           BIGINT NOT NULL,
    session_date        VARCHAR,
    price               DOUBLE,
    bid                 DOUBLE,
    ask                 DOUBLE,
    spread              DOUBLE,
    book_imbalance      DOUBLE,
    microprice          DOUBLE,
    delta_100ms         BIGINT,
    delta_500ms         BIGINT,
    delta_1s            BIGINT,
    delta_5s            BIGINT,
    ofi_100ms           DOUBLE,
    ofi_500ms           DOUBLE,
    ofi_1s              DOUBLE,
    trade_rate          DOUBLE,
    volume_rate         DOUBLE,
    volatility_30s      DOUBLE,
    vwap                DOUBLE,
    distance_vwap       DOUBLE,
    absorption_score    DOUBLE,
    stacking_score      DOUBLE,
    pulling_score       DOUBLE,
    velocity            DOUBLE,
    acceleration        DOUBLE,
    regime              VARCHAR,
    time_window         VARCHAR,
    has_economic_event  BOOLEAN,
    event_impact        INTEGER
);";
        cmd.ExecuteNonQuery();

        // [FASE 3] Tabela de labels (retornos futuros) — preenchida pelo Dataset Builder (Fase 7)
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS labels (
    timestamp               BIGINT NOT NULL,
    future_return_100ms     DOUBLE,
    future_return_250ms     DOUBLE,
    future_return_500ms     DOUBLE,
    future_return_1s        DOUBLE,
    future_return_2s        DOUBLE,
    future_return_5s        DOUBLE,
    future_return_10s       DOUBLE,
    mfe_5s                  DOUBLE,
    mae_5s                  DOUBLE,
    time_to_20pts           INTEGER,
    time_to_stop            INTEGER
);";
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    /// <summary>Grava um snapshot de mercado no DuckDB.</summary>
    public async Task GravarSnapshotAsync(MarketSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var cmd = _duckDb!.CreateCommand();
        cmd.CommandText = @"
INSERT INTO market_snapshots VALUES (
    $ts, $sd, $price, $bid, $ask, $spread, $bi, $mp,
    $d100, $d500, $d1s, $d5s,
    $ofi100, $ofi500, $ofi1s,
    $tr, $vr, $vol30, $vwap, $dvwap,
    $abs, $stk, $pll, $vel, $acc,
    $regime, $tw, $eco, $impact
)";
        cmd.Parameters.Add(new DuckDBParameter("ts",     snapshot.Timestamp));
        cmd.Parameters.Add(new DuckDBParameter("sd",     snapshot.SessionDate));
        cmd.Parameters.Add(new DuckDBParameter("price",  snapshot.Price));
        cmd.Parameters.Add(new DuckDBParameter("bid",    snapshot.Bid));
        cmd.Parameters.Add(new DuckDBParameter("ask",    snapshot.Ask));
        cmd.Parameters.Add(new DuckDBParameter("spread", snapshot.Spread));
        cmd.Parameters.Add(new DuckDBParameter("bi",     snapshot.BookImbalance));
        cmd.Parameters.Add(new DuckDBParameter("mp",     snapshot.Microprice));
        cmd.Parameters.Add(new DuckDBParameter("d100",   snapshot.Delta100ms));
        cmd.Parameters.Add(new DuckDBParameter("d500",   snapshot.Delta500ms));
        cmd.Parameters.Add(new DuckDBParameter("d1s",    snapshot.Delta1s));
        cmd.Parameters.Add(new DuckDBParameter("d5s",    snapshot.Delta5s));
        cmd.Parameters.Add(new DuckDBParameter("ofi100", snapshot.Ofi100ms));
        cmd.Parameters.Add(new DuckDBParameter("ofi500", snapshot.Ofi500ms));
        cmd.Parameters.Add(new DuckDBParameter("ofi1s",  snapshot.Ofi1s));
        cmd.Parameters.Add(new DuckDBParameter("tr",     snapshot.TradeRate));
        cmd.Parameters.Add(new DuckDBParameter("vr",     snapshot.VolumeRate));
        cmd.Parameters.Add(new DuckDBParameter("vol30",  snapshot.Volatility30s));
        cmd.Parameters.Add(new DuckDBParameter("vwap",   snapshot.Vwap));
        cmd.Parameters.Add(new DuckDBParameter("dvwap",  snapshot.DistanceVwap));
        cmd.Parameters.Add(new DuckDBParameter("abs",    snapshot.AbsorptionScore));
        cmd.Parameters.Add(new DuckDBParameter("stk",    snapshot.StackingScore));
        cmd.Parameters.Add(new DuckDBParameter("pll",    snapshot.PullingScore));
        cmd.Parameters.Add(new DuckDBParameter("vel",    snapshot.Velocity));
        cmd.Parameters.Add(new DuckDBParameter("acc",    snapshot.Acceleration));
        cmd.Parameters.Add(new DuckDBParameter("regime", snapshot.Regime));
        cmd.Parameters.Add(new DuckDBParameter("tw",     snapshot.TimeWindow));
        cmd.Parameters.Add(new DuckDBParameter("eco",    snapshot.HasEconomicEvent));
        cmd.Parameters.Add(new DuckDBParameter("impact", snapshot.EventImpact));

        await Task.Run(() => cmd.ExecuteNonQuery());
    }

    /// <summary>Consulta snapshots de mercado em um intervalo de tempo (timestamps em ticks).</summary>
    public async Task<List<MarketSnapshot>> ConsultarSnapshotsAsync(DateTime inicio, DateTime fim)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = new List<MarketSnapshot>();

        using var cmd = _duckDb!.CreateCommand();
        cmd.CommandText = "SELECT * FROM market_snapshots WHERE timestamp >= $ini AND timestamp <= $fim ORDER BY timestamp";
        cmd.Parameters.Add(new DuckDBParameter("ini", inicio.Ticks));
        cmd.Parameters.Add(new DuckDBParameter("fim", fim.Ticks));

        await Task.Run(() =>
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new MarketSnapshot
                {
                    Timestamp        = reader.GetInt64(0),
                    SessionDate      = reader.GetString(1),
                    Price            = reader.GetDouble(2),
                    Bid              = reader.GetDouble(3),
                    Ask              = reader.GetDouble(4),
                    Spread           = reader.GetDouble(5),
                    BookImbalance    = reader.GetDouble(6),
                    Microprice       = reader.GetDouble(7),
                    Delta100ms       = reader.GetInt64(8),
                    Delta500ms       = reader.GetInt64(9),
                    Delta1s          = reader.GetInt64(10),
                    Delta5s          = reader.GetInt64(11),
                    Ofi100ms         = reader.GetDouble(12),
                    Ofi500ms         = reader.GetDouble(13),
                    Ofi1s            = reader.GetDouble(14),
                    TradeRate        = reader.GetDouble(15),
                    VolumeRate       = reader.GetDouble(16),
                    Volatility30s    = reader.GetDouble(17),
                    Vwap             = reader.GetDouble(18),
                    DistanceVwap     = reader.GetDouble(19),
                    AbsorptionScore  = reader.GetDouble(20),
                    StackingScore    = reader.GetDouble(21),
                    PullingScore     = reader.GetDouble(22),
                    Velocity         = reader.GetDouble(23),
                    Acceleration     = reader.GetDouble(24),
                    Regime           = reader.GetString(25),
                    TimeWindow       = reader.GetString(26),
                    HasEconomicEvent = reader.GetBoolean(27),
                    EventImpact      = reader.GetInt32(28),
                });
            }
        });

        return result;
    }

    // ── SQLite ────────────────────────────────────────────────────────────

    private async Task InicializarSqliteAsync(string dataPath)
    {
        var sqlitePath = Path.Combine(dataPath, "marketcore.db");
        _sqlite = new SqliteConnection($"Data Source={sqlitePath}");
        _sqlite.Open();

        using var cmd = _sqlite.CreateCommand();

        // [FASE 3] Decisões do Decision Core
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS decisions (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp      INTEGER NOT NULL,
    final_score    REAL,
    direction      TEXT,
    decision_state TEXT,
    agent_scores   TEXT,
    regime         TEXT,
    time_window    TEXT,
    risk_approved  INTEGER,
    entry_taken    INTEGER,
    block_reason   TEXT
);";
        await cmd.ExecuteNonQueryAsync();

        // [FASE 3] Operações executadas (entrada + saída)
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS trades (
    trade_id         TEXT PRIMARY KEY,
    entry_time       INTEGER,
    entry_price      REAL,
    side             TEXT,
    quantity         INTEGER,
    exit_time        INTEGER,
    exit_price       REAL,
    gross_pnl        REAL,
    slippage         REAL,
    net_pnl          REAL,
    mfe              REAL,
    mae              REAL,
    exit_reason      TEXT,
    pattern_id       INTEGER,
    strategy_version TEXT
);";
        await cmd.ExecuteNonQueryAsync();

        // [FASE 3] Padrões descobertos pelo Pattern Engine (Fase 8)
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS patterns (
    pattern_id        INTEGER PRIMARY KEY,
    version           INTEGER,
    created_at        INTEGER,
    conditions        TEXT,
    sample_count      INTEGER,
    win_rate          REAL,
    expectancy        REAL,
    profit_factor     REAL,
    mfe_avg           REAL,
    mae_avg           REAL,
    drawdown          REAL,
    regime            TEXT,
    training_period   TEXT,
    validation_period TEXT,
    status            TEXT
);";
        await cmd.ExecuteNonQueryAsync();

        // [FASE 3] Configurações e metadados do sistema
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS config (
    key        TEXT PRIMARY KEY,
    value      TEXT,
    updated_at INTEGER
);";
        await cmd.ExecuteNonQueryAsync();

        // Semente de configuração — versão da estratégia
        cmd.CommandText = @"
INSERT OR IGNORE INTO config (key, value, updated_at)
VALUES ('strategy_version', 'FLOWSENSE_V2', $ts);";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.Ticks);
        await cmd.ExecuteNonQueryAsync();

        // [FASE 4] Calendário econômico — eventos importados do Investing.com
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS economic_events (
    event_id             TEXT    NOT NULL,
    event_date           TEXT    NOT NULL,
    time_brasilia        INTEGER NOT NULL,
    name                 TEXT,
    country              TEXT,
    impact               INTEGER,
    forecast             REAL,
    previous             REAL,
    block_minutes_before INTEGER,
    wait_seconds_after   INTEGER,
    is_active            INTEGER,
    PRIMARY KEY (event_id, event_date)
);";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Grava uma decisão do Decision Core no SQLite.</summary>
    public async Task GravarDecisionAsync(DecisionRecord decision)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var cmd = _sqlite!.CreateCommand();
        cmd.CommandText = @"
INSERT INTO decisions
    (timestamp, final_score, direction, decision_state, agent_scores,
     regime, time_window, risk_approved, entry_taken, block_reason)
VALUES
    ($ts, $score, $dir, $state, $agents,
     $regime, $tw, $risk, $entry, $block)";
        cmd.Parameters.AddWithValue("$ts",     decision.Timestamp);
        cmd.Parameters.AddWithValue("$score",  decision.FinalScore);
        cmd.Parameters.AddWithValue("$dir",    decision.Direction);
        cmd.Parameters.AddWithValue("$state",  decision.DecisionState);
        cmd.Parameters.AddWithValue("$agents", decision.AgentScores);
        cmd.Parameters.AddWithValue("$regime", decision.Regime);
        cmd.Parameters.AddWithValue("$tw",     decision.TimeWindow);
        cmd.Parameters.AddWithValue("$risk",   decision.RiskApproved ? 1 : 0);
        cmd.Parameters.AddWithValue("$entry",  decision.EntryTaken   ? 1 : 0);
        cmd.Parameters.AddWithValue("$block",  decision.BlockReason);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Grava uma operação completa (entrada + saída) no SQLite.</summary>
    public async Task GravarTradeOperacionalAsync(TradeRecord trade)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var cmd = _sqlite!.CreateCommand();
        cmd.CommandText = @"
INSERT OR REPLACE INTO trades
    (trade_id, entry_time, entry_price, side, quantity,
     exit_time, exit_price, gross_pnl, slippage, net_pnl,
     mfe, mae, exit_reason, pattern_id, strategy_version)
VALUES
    ($id, $et, $ep, $side, $qty,
     $xt, $xp, $gpnl, $slip, $npnl,
     $mfe, $mae, $xr, $pid, $sv)";
        cmd.Parameters.AddWithValue("$id",   trade.TradeId);
        cmd.Parameters.AddWithValue("$et",   trade.EntryTime);
        cmd.Parameters.AddWithValue("$ep",   trade.EntryPrice);
        cmd.Parameters.AddWithValue("$side", trade.Side);
        cmd.Parameters.AddWithValue("$qty",  trade.Quantity);
        cmd.Parameters.AddWithValue("$xt",   trade.ExitTime);
        cmd.Parameters.AddWithValue("$xp",   trade.ExitPrice);
        cmd.Parameters.AddWithValue("$gpnl", trade.GrossPnl);
        cmd.Parameters.AddWithValue("$slip", trade.Slippage);
        cmd.Parameters.AddWithValue("$npnl", trade.NetPnl);
        cmd.Parameters.AddWithValue("$mfe",  trade.Mfe);
        cmd.Parameters.AddWithValue("$mae",  trade.Mae);
        cmd.Parameters.AddWithValue("$xr",   trade.ExitReason);
        cmd.Parameters.AddWithValue("$pid",  trade.PatternId);
        cmd.Parameters.AddWithValue("$sv",   trade.StrategyVersion);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Consulta decisões em um intervalo de tempo (timestamps em ticks).</summary>
    public async Task<List<DecisionRecord>> ConsultarDecisionsAsync(DateTime inicio, DateTime fim)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = new List<DecisionRecord>();
        using var cmd = _sqlite!.CreateCommand();
        cmd.CommandText = "SELECT timestamp, final_score, direction, decision_state, agent_scores, regime, time_window, risk_approved, entry_taken, block_reason FROM decisions WHERE timestamp >= $ini AND timestamp <= $fim ORDER BY timestamp";
        cmd.Parameters.AddWithValue("$ini", inicio.Ticks);
        cmd.Parameters.AddWithValue("$fim", fim.Ticks);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new DecisionRecord
            {
                Timestamp     = reader.GetInt64(0),
                FinalScore    = reader.GetDouble(1),
                Direction     = reader.GetString(2),
                DecisionState = reader.GetString(3),
                AgentScores   = reader.GetString(4),
                Regime        = reader.GetString(5),
                TimeWindow    = reader.GetString(6),
                RiskApproved  = reader.GetInt32(7) != 0,
                EntryTaken    = reader.GetInt32(8) != 0,
                BlockReason   = reader.GetString(9),
            });
        }

        return result;
    }

    // ── Calendário econômico ──────────────────────────────────────────────

    /// <summary>
    /// Persiste todos os eventos de um CalendarDay no SQLite.
    /// Substitui eventos existentes para a mesma data (DELETE + INSERT).
    /// </summary>
    public async Task SalvarEventosAsync(CalendarDay day)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (day.Events.Count == 0) return;

        var dateStr = day.Date.ToString("yyyy-MM-dd");

        using var transaction = _sqlite!.BeginTransaction();
        using var cmd = _sqlite.CreateCommand();
        cmd.Transaction = transaction;

        cmd.CommandText = "DELETE FROM economic_events WHERE event_date = $date";
        cmd.Parameters.AddWithValue("$date", dateStr);
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = @"
INSERT INTO economic_events
    (event_id, event_date, time_brasilia, name, country, impact,
     forecast, previous, block_minutes_before, wait_seconds_after, is_active)
VALUES
    ($id, $date, $tb, $name, $country, $impact,
     $forecast, $previous, $bmb, $wsa, $active)";

        foreach (var ev in day.Events)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id",       ev.EventId);
            cmd.Parameters.AddWithValue("$date",     dateStr);
            cmd.Parameters.AddWithValue("$tb",       ev.TimeBrasilia.Ticks);
            cmd.Parameters.AddWithValue("$name",     ev.Name);
            cmd.Parameters.AddWithValue("$country",  ev.Country);
            cmd.Parameters.AddWithValue("$impact",   (int)ev.Impact);
            cmd.Parameters.AddWithValue("$forecast", (object?)ev.Forecast ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$previous", (object?)ev.Previous ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bmb",      ev.BlockMinutesBefore);
            cmd.Parameters.AddWithValue("$wsa",      ev.WaitSecondsAfter);
            cmd.Parameters.AddWithValue("$active",   ev.IsActive ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    /// <summary>
    /// Carrega eventos econômicos salvos para uma data específica.
    /// Retorna um CalendarDay vazio se não houver dados persistidos.
    /// </summary>
    public async Task<CalendarDay> CarregarEventosSalvosAsync(DateTime date)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var day     = new CalendarDay { Date = date.Date };
        var dateStr = date.ToString("yyyy-MM-dd");

        using var cmd = _sqlite!.CreateCommand();
        cmd.CommandText = @"
SELECT event_id, time_brasilia, name, country, impact,
       forecast, previous, block_minutes_before, wait_seconds_after, is_active
FROM   economic_events
WHERE  event_date = $date
ORDER  BY time_brasilia";
        cmd.Parameters.AddWithValue("$date", dateStr);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            day.Events.Add(new EconomicEvent
            {
                EventId            = reader.GetString(0),
                TimeBrasilia       = new DateTime(reader.GetInt64(1), DateTimeKind.Local),
                Name               = reader.GetString(2),
                Country            = reader.GetString(3),
                Impact             = (ImpactLevel)reader.GetInt32(4),
                Forecast           = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                Previous           = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                BlockMinutesBefore = reader.GetInt32(7),
                WaitSecondsAfter   = reader.GetInt32(8),
                IsActive           = reader.GetInt32(9) != 0,
            });
        }

        return day;
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _duckDb?.Close();
        _duckDb?.Dispose();
        _sqlite?.Close();
        _sqlite?.Dispose();
    }
}
