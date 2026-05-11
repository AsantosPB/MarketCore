using Npgsql;

namespace MarketCore.HistoricalImporter;

/// <summary>Criação de tablespace, banco, schema e consultas de tamanho/contagem.</summary>
public sealed class DatabaseSetup
{
    private readonly AppConfig _cfg;

    public DatabaseSetup(AppConfig cfg) => _cfg = cfg;

    /// <summary>Cria tablespace em disco customizado (requer permissões superuser no PostgreSQL).</summary>
    public async Task CreateCustomTablespaceAsync(CancellationToken ct = default)
    {
        if (!_cfg.Storage.UseCustomPath || string.IsNullOrWhiteSpace(_cfg.Storage.DataPath))
            throw new InvalidOperationException("Storage.UseCustomPath e DataPath devem estar configurados.");

        string pathSql = _cfg.Storage.DataPath.Trim().Replace('\\', '/').Replace("'", "''");

        await using var conn = new NpgsqlConnection(_cfg.GetMaintenanceConnectionString());
        await conn.OpenAsync(ct);

        await using (var cmd = new NpgsqlCommand(
            "SELECT 1 FROM pg_tablespace WHERE spcname = @name",
            conn))
        {
            cmd.Parameters.AddWithValue("name", _cfg.Storage.TablespaceName);
            if (await cmd.ExecuteScalarAsync(ct) != null)
                return;
        }

        string tsIdent = _cfg.Storage.TablespaceName.Replace("\"", "\"\"");
        await using (var create = new NpgsqlCommand(
            $"CREATE TABLESPACE \"{tsIdent}\" LOCATION '{pathSql}'",
            conn))
        {
            await create.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Cria o banco de dados configurado (se não existir).</summary>
    public async Task CreateDatabaseAsync(CancellationToken ct = default)
    {
        string dbIdent = _cfg.Database.Database.Replace("\"", "\"\"");
        await using var conn = new NpgsqlConnection(_cfg.GetMaintenanceConnectionString());
        await conn.OpenAsync(ct);

        await using (var cmd = new NpgsqlCommand(
            "SELECT 1 FROM pg_database WHERE datname = @name",
            conn))
        {
            cmd.Parameters.AddWithValue("name", _cfg.Database.Database);
            if (await cmd.ExecuteScalarAsync(ct) != null)
                return;
        }

        string tablespaceClause = "";
        if (_cfg.Storage.UseCustomPath && !string.IsNullOrWhiteSpace(_cfg.Storage.TablespaceName))
        {
            string tsIdent = _cfg.Storage.TablespaceName.Replace("\"", "\"\"");
            tablespaceClause = $" TABLESPACE \"{tsIdent}\"";
        }

        await using var create = new NpgsqlCommand(
            $"CREATE DATABASE \"{dbIdent}\"{tablespaceClause}",
            conn);
        await create.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Cria tabelas e índices do schema de histórico.</summary>
    public async Task CreateSchemaAsync(CancellationToken ct = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS trades (
                id BIGSERIAL PRIMARY KEY,
                timestamp TIMESTAMP NOT NULL,
                price NUMERIC(10,2) NOT NULL,
                quantity INTEGER NOT NULL,
                aggressor CHAR(1) NOT NULL,
                buyer_broker VARCHAR(100),
                seller_broker VARCHAR(100),
                contract VARCHAR(10) NOT NULL,
                session_date DATE NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_timestamp ON trades(timestamp);
            CREATE INDEX IF NOT EXISTS idx_session_date ON trades(session_date);
            CREATE INDEX IF NOT EXISTS idx_buyer_broker ON trades(buyer_broker);
            CREATE INDEX IF NOT EXISTS idx_seller_broker ON trades(seller_broker);
            CREATE INDEX IF NOT EXISTS idx_contract ON trades(contract);
            CREATE INDEX IF NOT EXISTS idx_contract_date ON trades(contract, session_date);

            CREATE TABLE IF NOT EXISTS broker_positions (
                id SERIAL PRIMARY KEY,
                broker VARCHAR(100) NOT NULL,
                contract VARCHAR(10) NOT NULL,
                session_date DATE NOT NULL,
                volume_bought BIGINT DEFAULT 0,
                volume_sold BIGINT DEFAULT 0,
                net_position BIGINT,
                total_trades INTEGER,
                first_trade_time TIMESTAMP,
                last_trade_time TIMESTAMP,
                UNIQUE(broker, contract, session_date)
            );
            CREATE INDEX IF NOT EXISTS idx_broker_date ON broker_positions(broker, session_date);
            CREATE INDEX IF NOT EXISTS idx_net_position ON broker_positions(net_position);
            """;

        await using var conn = new NpgsqlConnection(_cfg.GetConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Retorna tamanho aproximado do banco e contagens de linhas.</summary>
    public async Task<string> ShowStorageInfoAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_cfg.GetConnectionString());
        await conn.OpenAsync(ct);

        long dbBytes = 0;
        await using (var cmd = new NpgsqlCommand(
            "SELECT pg_database_size(current_database())",
            conn))
        {
            var o = await cmd.ExecuteScalarAsync(ct);
            if (o is long l) dbBytes = l;
            else if (o != null) dbBytes = Convert.ToInt64(o);
        }

        long trades = 0;
        await using (var tcmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM trades",
            conn))
        {
            var o = await tcmd.ExecuteScalarAsync(ct);
            if (o != null) trades = Convert.ToInt64(o);
        }

        long pos = 0;
        await using (var pcmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM broker_positions",
            conn))
        {
            var o = await pcmd.ExecuteScalarAsync(ct);
            if (o != null) pos = Convert.ToInt64(o);
        }

        string sizeMb = (dbBytes / (1024.0 * 1024.0)).ToString("N2");
        return $"Banco: {_cfg.Database.Database} | Tamanho ~{sizeMb} MB | trades: {trades:N0} | broker_positions: {pos:N0}";
    }
}
