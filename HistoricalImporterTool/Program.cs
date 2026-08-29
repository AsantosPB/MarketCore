using System.Globalization;
using MarketCore.HistoricalImporter;

static void PrintMenu()
{
    Console.WriteLine();
    Console.WriteLine("=== Importador histórico WIN → PostgreSQL ===");
    Console.WriteLine(" 1 - Configurar (JSON: DB, storage, Profit)");
    Console.WriteLine(" 2 - Setup database (tablespace opcional, CREATE DB, schema)");
    Console.WriteLine(" 3 - Importar (datas → contratos → DLL + COPY)");
    Console.WriteLine(" 4 - Info armazenamento");
    Console.WriteLine(" 5 - Sair");
    Console.Write("Opção: ");
}

static string Prompt(string label, string? defaultValue = null)
{
    if (!string.IsNullOrEmpty(defaultValue))
        Console.Write($"{label} [{defaultValue}]: ");
    else
        Console.Write($"{label}: ");
    string? line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line) && defaultValue != null)
        return defaultValue;
    return line ?? "";
}

static bool PromptDate(string label, out DateTime dt)
{
    Console.Write($"{label} (dd/MM/yyyy): ");
    string? s = Console.ReadLine();
    return DateTime.TryParse(s, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out dt);
}

var cfgPath = AppConfig.GetDefaultConfigPath();
AppConfig cfg = AppConfig.Load(cfgPath);

while (true)
{
    PrintMenu();
    string? op = Console.ReadLine();
    Console.WriteLine();

    try
    {
        switch (op?.Trim())
        {
            case "1":
                Console.WriteLine($"Arquivo: {cfgPath}");
                cfg.Database.Host = Prompt("Host PostgreSQL", cfg.Database.Host);
                if (int.TryParse(Prompt("Porta", cfg.Database.Port.ToString()), out int p)) cfg.Database.Port = p;
                cfg.Database.Database = Prompt("Nome do banco", cfg.Database.Database);
                cfg.Database.Username = Prompt("Usuário", cfg.Database.Username);
                cfg.Database.Password = Prompt("Senha", cfg.Database.Password);
                cfg.Database.MaintenanceDatabase = Prompt("Banco manutenção", cfg.Database.MaintenanceDatabase);
                cfg.Storage.UseCustomPath = Prompt("Usar path customizado (s/N)", cfg.Storage.UseCustomPath ? "s" : "N").Trim().Equals("s", StringComparison.OrdinalIgnoreCase);
                if (cfg.Storage.UseCustomPath)
                {
                    cfg.Storage.DataPath = Prompt("DataPath (servidor PG)", cfg.Storage.DataPath);
                    cfg.Storage.TablespaceName = Prompt("Nome tablespace", cfg.Storage.TablespaceName);
                }
                cfg.Profit.ActivationKey = Prompt("Profit activation key", cfg.Profit.ActivationKey);
                cfg.Profit.Username = Prompt("Profit usuário", cfg.Profit.Username);
                cfg.Profit.Password = Prompt("Profit senha", cfg.Profit.Password);
                cfg.Save(cfgPath);
                Console.WriteLine("config.json salvo.");
                break;

            case "2":
                var setup = new DatabaseSetup(cfg);
                if (cfg.Storage.UseCustomPath)
                {
                    Console.WriteLine("Criando tablespace (se necessário)...");
                    await setup.CreateCustomTablespaceAsync();
                }
                Console.WriteLine("Criando banco (se necessário)...");
                await setup.CreateDatabaseAsync();
                Console.WriteLine("Criando schema...");
                await setup.CreateSchemaAsync();
                Console.WriteLine("Setup concluído.");
                break;

            case "3":
                {
                    if (!PromptDate("Data inicial", out DateTime start))
                    {
                        Console.WriteLine("Data inicial inválida.");
                        break;
                    }
                    if (!PromptDate("Data final", out DateTime end))
                    {
                        Console.WriteLine("Data final inválida.");
                        break;
                    }

                    Console.WriteLine("Conectando mercado (DLL)...");
                    bool ok = await ProfitMarketInit.TryInitializeAsync(cfg.Profit, TimeSpan.FromSeconds(60));
                    if (!ok)
                    {
                        Console.WriteLine("Falha ao aguardar conexão de mercado. Verifique credenciais Profit em config.");
                        break;
                    }

                    const string futTicker = "WINFUT";
                    DateTime r0 = start.Date;
                    DateTime r1 = end.Date;
                    Console.WriteLine($"Histórico {futTicker}: {r0:yyyy-MM-dd} .. {r1:yyyy-MM-dd}");

                    var importer = new HistoricalImporter(cfg);
                    using (var history = new ProfitHistoryService(importer))
                    {
                        int rc = await history.RequestHistoricalDataAsync(futTicker, r0, r1);
                        if (rc != 0)
                            Console.WriteLine($"DLL retornou código {rc} (ver manual Nelogica).");

                        long total = importer.TotalFlushed + importer.TotalBuffered;
                        Console.WriteLine($"Registros gravados (buffer + já enviados): {total:N0}");
                    }

                    importer.FlushToDatabase();
                    Console.WriteLine($"Importação finalizada. Total copiado: {importer.TotalFlushed:N0} negócios.");
                    break;
                }

            case "4":
                var dbi = new DatabaseSetup(cfg);
                string info = await dbi.ShowStorageInfoAsync();
                Console.WriteLine(info);
                break;

            case "5":
                return;

            default:
                Console.WriteLine("Opção inválida.");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Erro: {ex.Message}");
    }
}
