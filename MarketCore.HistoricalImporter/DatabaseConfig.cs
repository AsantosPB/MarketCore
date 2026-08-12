using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketCore.HistoricalImporter;

public sealed class DatabaseConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "marketcore_historical";
    public string Username { get; set; } = "postgres";
    public string Password { get; set; } = "postgres";
    /// <summary>Banco usado só para CREATE DATABASE (normalmente <c>postgres</c>).</summary>
    public string MaintenanceDatabase { get; set; } = "postgres";
}

public sealed class StorageConfig
{
    public bool UseCustomPath { get; set; }
    /// <summary>Diretório no servidor PostgreSQL para tablespace customizado.</summary>
    public string DataPath { get; set; } = "";
    public string TablespaceName { get; set; } = "win_history_data";

    /// <summary>Evita nomes inválidos (ex.: datas) em <c>CREATE DATABASE ... TABLESPACE</c>.</summary>
    internal void Sanitize()
    {
        if (!UseCustomPath)
        {
            DataPath = "";
            TablespaceName = "";
            return;
        }

        DataPath = (DataPath ?? "").Trim();
        TablespaceName = (TablespaceName ?? "").Trim();

        if (DataPath.Length == 0 || !IsValidPostgresIdentifier(TablespaceName))
        {
            UseCustomPath = false;
            DataPath = "";
            TablespaceName = "";
        }
    }

    private static bool IsValidPostgresIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string trimmed = value.Trim();
        if (trimmed.Length > 63)
            return false;

        if (!char.IsLetter(trimmed[0]) && trimmed[0] != '_')
            return false;

        for (int i = 1; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;
        }

        return true;
    }
}

/// <summary>Credenciais Nelogica para <see cref="ProfitMarketInit"/> (DLL).</summary>
public sealed class ProfitCredentialsConfig
{
    public string ActivationKey { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class AppConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public DatabaseConfig Database { get; set; } = new();
    public StorageConfig Storage { get; set; } = new();
    public ProfitCredentialsConfig Profit { get; set; } = new();

    public static string GetDefaultConfigPath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MarketCore",
            "HistoricalImporter");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "config.json");
    }

    public static AppConfig Load(string? path = null)
    {
        path ??= GetDefaultConfigPath();
        if (!File.Exists(path))
        {
            var fresh = new AppConfig();
            fresh.Save(path);
            return fresh;
        }

        string json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        cfg.Storage.Sanitize();
        return cfg;
    }

    public void Save(string? path = null)
    {
        path ??= GetDefaultConfigPath();
        Storage.Sanitize();
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>Connection string Npgsql para o banco configurado.</summary>
    public string GetConnectionString()
    {
        var b = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = Database.Host,
            Port = Database.Port,
            Database = Database.Database,
            Username = Database.Username,
            Password = Database.Password,
            Pooling = true
        };
        return b.ConnectionString;
    }

    /// <summary>Connection string ao banco de manutenção (criar DB / roles).</summary>
    public string GetMaintenanceConnectionString()
    {
        var b = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = Database.Host,
            Port = Database.Port,
            Database = Database.MaintenanceDatabase,
            Username = Database.Username,
            Password = Database.Password,
            Pooling = false
        };
        return b.ConnectionString;
    }
}
