using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Npgsql;

namespace MarketCore.WPF.Data
{
    /// <summary>
    /// Gerencia salvamento de trades em PostgreSQL em tempo real.
    /// Escritas são enfileiradas e processadas numa task dedicada — nunca bloqueia a thread de mercado.
    /// </summary>
    public sealed class MarketDataManager : IDisposable
    {
        private readonly string _connectionString;
        private NpgsqlConnection? _connection;
        private bool _isConnected;

        private Channel<PendingTrade>? _tradeChannel;
        private CancellationTokenSource? _writerCts;
        private Task? _writerTask;

        private readonly record struct PendingTrade(
            DateTime Timestamp,
            string Symbol,
            int Price,
            int Quantity,
            string Side,
            int Aggressor,
            int BrokerCode,
            string? BrokerName,
            string Source);

        public bool IsConnected => _isConnected;

        /// <summary>Construtor - configura connection string</summary>
        public MarketDataManager(string host = "localhost", int port = 5432,
            string database = "marketcore_historical", string username = "postgres",
            string password = "sua_senha_aqui")
        {
            _connectionString = $"Host={host};Port={port};Database={database};Username={username};Password={password}";
        }

        /// <summary>Conecta ao PostgreSQL e inicia o writer em background.</summary>
        public async Task<bool> ConnectAsync()
        {
            try
            {
                StopWriter();

                _connection = new NpgsqlConnection(_connectionString);
                await _connection.OpenAsync();
                _isConnected = true;

                // Garante que a tabela e o índice único existem.
                // Seguro de rodar toda vez (usa IF NOT EXISTS).
                await EnsureSchemaAsync(_connection);

                // Fila de 500k para cobrir picos (WIN chega a 6M trades/dia = 100+ trades/s em bursts).
                // Combinada com INSERT em lote no drain loop, permite acompanhar o fluxo sem descartar.
                _tradeChannel = Channel.CreateBounded<PendingTrade>(new BoundedChannelOptions(500_000)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });

                _writerCts = new CancellationTokenSource();
                var token = _writerCts.Token;
                var conn = _connection;
                var ch = _tradeChannel!;
                _writerTask = Task.Run(() => DrainTradesLoopAsync(conn, ch.Reader, token), token);

                Console.WriteLine("✓ MarketDataManager: Conectado ao PostgreSQL");

                // Retenção rolante: mantém só os últimos N dias em trades_intraday, senão a tabela cresce
                // pra sempre. Roda uma vez por conexão (ou seja, uma vez por abertura do programa) — não
                // precisa de job/agendador separado, e o custo é só um DELETE indexado pela coluna timestamp.
                // Retenção de 1 dia: ao abrir o programa num novo dia, os trades do dia anterior
                // são apagados. Padrões aprendidos são preservados no JSON do CoordPlayerMiner.
                _ = PurgeOldTradesAsync(daysToKeep: 1);

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ MarketDataManager: Erro ao conectar - {ex.Message}");
                _isConnected = false;
                StopWriter();
                return false;
            }
        }

        /// <summary>Enfileira trade para persistência (não bloqueia; descarta os mais antigos se a fila estourar).</summary>
        public void EnqueueRealtimeTrade(
            DateTime timestamp,
            string symbol,
            int price,
            int quantity,
            string side,
            int aggressor,
            int brokerCode,
            string? brokerName = null,
            string source = "realtime")
        {
            if (!_isConnected || _tradeChannel == null)
                return;

            // Corrige trades com data absurda (bug da ProfitDLL que às vezes manda datas
            // de 30 dias atrás ou horários no futuro). Se o timestamp for suspeito,
            // usa DateTime.Now — não perde o trade, só ajusta a data.
            var now = DateTime.Now;
            if (timestamp < now.Date.AddDays(-1) || timestamp > now.AddMinutes(5))
                timestamp = now;

            var p = new PendingTrade(timestamp, symbol, price, quantity, side, aggressor, brokerCode, brokerName, source);
            _tradeChannel.Writer.TryWrite(p);
        }

        /// <summary>
        /// Compat legado: não bloqueia mais a thread de callbacks.
        /// </summary>
        public bool InsertTrade(
            DateTime timestamp,
            string symbol,
            int price,
            int quantity,
            string side,
            int aggressor,
            int brokerCode,
            string? brokerName = null,
            string source = "realtime")
        {
            EnqueueRealtimeTrade(timestamp, symbol, price, quantity, side, aggressor, brokerCode, brokerName, source);
            return true;
        }

        /// <summary>Cria a tabela e o índice único se não existirem.</summary>
        private static async Task EnsureSchemaAsync(NpgsqlConnection conn)
        {
            const string ddl = @"
                CREATE TABLE IF NOT EXISTS trades_intraday (
                    id          BIGSERIAL PRIMARY KEY,
                    timestamp   TIMESTAMP NOT NULL,
                    symbol      VARCHAR(10) NOT NULL,
                    price       INTEGER NOT NULL,
                    quantity    INTEGER NOT NULL,
                    side        VARCHAR(10),
                    aggressor   INTEGER,
                    broker_code INTEGER,
                    broker_name VARCHAR(100),
                    source      VARCHAR(20)
                );
                -- Índice funcional para deduplicação via ON CONFLICT
                CREATE UNIQUE INDEX IF NOT EXISTS idx_trades_intraday_dedup
                    ON trades_intraday (timestamp, symbol, price, quantity,
                                        COALESCE(side,''), COALESCE(broker_code, 0));
                -- Índice para queries por timestamp (mineração)
                CREATE INDEX IF NOT EXISTS idx_trades_intraday_ts
                    ON trades_intraday (timestamp);";

            await using var cmd = new NpgsqlCommand(ddl, conn);
            await cmd.ExecuteNonQueryAsync();
            Console.WriteLine("✓ MarketDataManager: Schema OK");
        }

        private static async Task DrainTradesLoopAsync(
            NpgsqlConnection connection,
            ChannelReader<PendingTrade> reader,
            CancellationToken ct)
        {
            const int BatchSize = 500;
            var batch = new List<PendingTrade>(BatchSize);
            try
            {
                while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    // Drena tudo que estiver disponível no momento (até BatchSize)
                    batch.Clear();
                    while (batch.Count < BatchSize && reader.TryRead(out var t))
                        batch.Add(t);

                    if (batch.Count == 0) continue;

                    try
                    {
                        await InsertBatchAsync(connection, batch, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"✗ MarketDataManager: Erro ao inserir lote de {batch.Count} - {ex.Message}");
                        // fallback: insere um por um pra pelo menos salvar os que dá
                        foreach (var t in batch)
                        {
                            try { await InsertOneAsync(connection, t, ct).ConfigureAwait(false); }
                            catch { /* trade individual perdido */ }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // encerramento
            }
        }

        /// <summary>INSERT em lote via UNNEST — dezenas a centenas de vezes mais rápido que 1-por-1.</summary>
        private static async Task InsertBatchAsync(NpgsqlConnection connection, List<PendingTrade> batch, CancellationToken ct)
        {
            const string sql = @"
                INSERT INTO trades_intraday
                    (timestamp, symbol, price, quantity, side, aggressor, broker_code, broker_name, source)
                SELECT * FROM UNNEST(
                    @ts::timestamp[],
                    @sym::varchar[],
                    @px::int[],
                    @qty::int[],
                    @sd::varchar[],
                    @ag::int[],
                    @bc::int[],
                    @bn::varchar[],
                    @src::varchar[])
                ON CONFLICT (timestamp, symbol, price, quantity,
                             COALESCE(side,''), COALESCE(broker_code, 0))
                DO NOTHING";

            int n = batch.Count;
            var ts   = new DateTime[n];
            var sym  = new string[n];
            var px   = new int[n];
            var qty  = new int[n];
            var sd   = new string[n];
            var ag   = new int[n];
            var bc   = new int[n];
            var bn   = new string?[n];
            var src  = new string[n];

            for (int i = 0; i < n; i++)
            {
                var t = batch[i];
                ts[i]  = DateTime.SpecifyKind(t.Timestamp, DateTimeKind.Unspecified);
                sym[i] = t.Symbol;
                px[i]  = t.Price;
                qty[i] = t.Quantity;
                sd[i]  = t.Side ?? "";
                ag[i]  = t.Aggressor;
                bc[i]  = t.BrokerCode;
                bn[i]  = t.BrokerName;
                src[i] = t.Source;
            }

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("ts",  ts);
            cmd.Parameters.AddWithValue("sym", sym);
            cmd.Parameters.AddWithValue("px",  px);
            cmd.Parameters.AddWithValue("qty", qty);
            cmd.Parameters.AddWithValue("sd",  sd);
            cmd.Parameters.AddWithValue("ag",  ag);
            cmd.Parameters.AddWithValue("bc",  bc);
            cmd.Parameters.AddWithValue("bn",  (object?)bn ?? DBNull.Value);
            cmd.Parameters.AddWithValue("src", src);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        private static async Task InsertOneAsync(NpgsqlConnection connection, PendingTrade t, CancellationToken ct)
        {
            const string sql = @"
                    INSERT INTO trades_intraday
                    (timestamp, symbol, price, quantity, side, aggressor, broker_code, broker_name, source)
                    VALUES
                    (@timestamp, @symbol, @price, @quantity, @side, @aggressor, @broker_code, @broker_name, @source)
                    ON CONFLICT (timestamp, symbol, price, quantity,
                                 COALESCE(side,''), COALESCE(broker_code, 0))
                    DO NOTHING";

            await using var cmd = new NpgsqlCommand(sql, connection);
            // Npgsql 6+ exige Kind=Unspecified para colunas TIMESTAMP (sem timezone).
            // DateTime.Now tem Kind=Local — strip para Unspecified antes de inserir.
            var ts = DateTime.SpecifyKind(t.Timestamp, DateTimeKind.Unspecified);
            cmd.Parameters.AddWithValue("timestamp", ts);
            cmd.Parameters.AddWithValue("symbol", t.Symbol);
            cmd.Parameters.AddWithValue("price", t.Price);
            cmd.Parameters.AddWithValue("quantity", t.Quantity);
            cmd.Parameters.AddWithValue("side", t.Side);
            cmd.Parameters.AddWithValue("aggressor", t.Aggressor);
            cmd.Parameters.AddWithValue("broker_code", t.BrokerCode);
            cmd.Parameters.AddWithValue("broker_name", t.BrokerName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("source", t.Source);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        /// <summary>Insere um trade (chamada direta — útil para scripts; não usar no hot path).</summary>
        public async Task<bool> InsertTradeAsync(
            DateTime timestamp,
            string symbol,
            int price,
            int quantity,
            string side,
            int aggressor,
            int brokerCode,
            string? brokerName = null,
            string source = "realtime")
        {
            if (!_isConnected || _connection == null)
            {
                Console.WriteLine("✗ MarketDataManager: Não conectado ao PostgreSQL");
                return false;
            }

            try
            {
                await InsertOneAsync(_connection,
                    new PendingTrade(timestamp, symbol, price, quantity, side, aggressor, brokerCode, brokerName, source),
                    CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ MarketDataManager: Erro ao inserir trade - {ex.Message}");
                return false;
            }
        }

        /// <summary>Conta quantos trades estão salvos na tabela (não no buffer).</summary>
        public async Task<int> GetTodayTradeCountAsync()
        {
            if (!_isConnected || _connection == null) return 0;

            try
            {
                const string sql = "SELECT COUNT(*) FROM trades_intraday";
                await using var cmd = new NpgsqlCommand(sql, _connection);
                var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                return Convert.ToInt32(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ MarketDataManager: Erro ao contar trades - {ex.Message}");
                return 0;
            }
        }

        /// <summary>Apaga de trades_intraday tudo com mais de <paramref name="daysToKeep"/> dias — janela de
        /// retenção rolante (padrão 10 dias). Diferente de <see cref="ClearTodayTradesAsync"/> (que zera a
        /// tabela inteira e é para uso manual), este roda automaticamente a cada conexão, então o dia mais
        /// antigo vai "caindo" da janela sozinho conforme dias novos entram — sem crescer pra sempre e sem
        /// precisar de manutenção manual.</summary>
        public async Task<int> PurgeOldTradesAsync(int daysToKeep = 10)
        {
            if (!_isConnected || _connection == null) return 0;

            try
            {
                const string sql = "DELETE FROM trades_intraday WHERE timestamp < (NOW() - make_interval(days => @days))";
                await using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("days", daysToKeep);
                int affected = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                if (affected > 0)
                    Console.WriteLine($"✓ MarketDataManager: retenção de {daysToKeep} dias removeu {affected} negócios antigos de trades_intraday");
                return affected;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ MarketDataManager: Erro ao purgar negócios antigos - {ex.Message}");
                return 0;
            }
        }

        public async Task<bool> ClearTodayTradesAsync()
        {
            if (!_isConnected || _connection == null) return false;

            try
            {
                const string sql = "TRUNCATE TABLE trades_intraday";
                await using var cmd = new NpgsqlCommand(sql, _connection);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                Console.WriteLine("✓ MarketDataManager: Tabela trades_intraday limpa");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ MarketDataManager: Erro ao limpar trades - {ex.Message}");
                return false;
            }
        }

        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                if (!_isConnected || _connection == null)
                    await ConnectAsync().ConfigureAwait(false);

                if (!_isConnected || _connection == null)
                    return false;

                int count = await GetTodayTradeCountAsync().ConfigureAwait(false);
                Console.WriteLine($"✓ MarketDataManager: Conexão OK - {count} trades na tabela");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ MarketDataManager: Teste falhou - {ex.Message}");
                return false;
            }
        }

        private void StopWriter()
        {
            try
            {
                _tradeChannel?.Writer.TryComplete();
            }
            catch { /* ignore */ }

            try
            {
                _writerCts?.Cancel();
            }
            catch { /* ignore */ }

            try
            {
                if (_writerTask != null)
                    _writerTask.Wait(TimeSpan.FromSeconds(4));
            }
            catch { /* ignore */ }

            _writerTask = null;
            _writerCts?.Dispose();
            _writerCts = null;
            _tradeChannel = null;
        }

        public void Dispose()
        {
            StopWriter();

            try
            {
                _connection?.Close();
                _connection?.Dispose();
            }
            catch { /* ignore */ }

            _connection = null;
            _isConnected = false;
            Console.WriteLine("✓ MarketDataManager: Conexão fechada");
        }
    }
}
