using System;
using System.IO;
using System.Threading.Tasks;
using MarketCore.Contracts;
using MarketCore.Engine;
using MarketCore.Engine.Detectors;
using MarketCore.Models;
using MarketCore.Providers.Simulator;
using MarketCore.Providers.Replay;

Console.WriteLine("╔════════════════════════════════════════════════════════════════╗");
Console.WriteLine("║    MarketCore / FlowSense — Engine + Recorder + Replay        ║");
Console.WriteLine("╚════════════════════════════════════════════════════════════════╝\n");

Console.WriteLine("Escolha o modo de operação:\n");
Console.WriteLine("  1 - Simulador (dados sintéticos em tempo real)");
Console.WriteLine("  2 - Replay (rever pregão gravado)\n");

Console.Write("Opção (1 ou 2): ");
var opcao = Console.ReadLine()?.Trim();

if (opcao == "2")
{
    // ═══════════════════════════════════════════════════════════════════════════════
    // MODO REPLAY
    // ═══════════════════════════════════════════════════════════════════════════════
    Console.WriteLine("\n▶ MODO REPLAY");
    Console.WriteLine("─────────────────────────────────────────────────────────────────\n");

    var diretorioGravacoes = Path.Combine(Path.GetTempPath(), "MarketCore_Recordings");
    
    if (!Directory.Exists(diretorioGravacoes))
    {
        Console.WriteLine("❌ Nenhum pregão gravado encontrado!");
        Console.WriteLine($"   Diretório: {diretorioGravacoes}\n");
        return;
    }

    // Listar pregões disponíveis
    var pregoes = Directory.GetDirectories(diretorioGravacoes)
        .Select(d => Path.GetFileName(d))
        .OrderByDescending(d => d)
        .ToList();

    if (pregoes.Count == 0)
    {
        Console.WriteLine("❌ Nenhum pregão gravado encontrado!\n");
        return;
    }

    Console.WriteLine("📁 Pregões disponíveis para replay:\n");
    for (int i = 0; i < pregoes.Count; i++)
    {
        var metadataPath = Path.Combine(diretorioGravacoes, pregoes[i], "metadata.json");
        var info = "";
        
        if (File.Exists(metadataPath))
        {
            try
            {
                var json = File.ReadAllText(metadataPath);
                var metadata = System.Text.Json.JsonSerializer.Deserialize<ReplayMetadata>(json);
                info = $" | Books: {metadata?.total_books ?? 0}";
            }
            catch { }
        }
        
        Console.WriteLine($"  {i + 1}) {pregoes[i]}{info}");
    }

    Console.Write($"\nEscolha o pregão (1-{pregoes.Count}): ");
    if (!int.TryParse(Console.ReadLine(), out var escolha) || escolha < 1 || escolha > pregoes.Count)
    {
        Console.WriteLine("❌ Opção inválida!\n");
        return;
    }

    var dataReplay = pregoes[escolha - 1];
    
    Console.Write("\n⏱ Escolha a velocidade (0.5, 1, 2, 5, 10): ");
    if (!float.TryParse(Console.ReadLine(), out var velocidade))
    {
        velocidade = 1.0f;
    }

    Console.Write("\n⏱ Escolha o timeframe para Exhaustion (S30, M1, M2, M3, M5, M15): ");
    var tfEscolha = Console.ReadLine()?.ToUpper().Trim() ?? "M1";

    ExhaustionTimeframe timeframe;
    try
    {
        timeframe = Enum.Parse<ExhaustionTimeframe>(tfEscolha);
    }
    catch
    {
        Console.WriteLine("⚠ Timeframe inválido, usando M1");
        timeframe = ExhaustionTimeframe.M1;
    }

    // Criar provider de replay
    var replayProvider = new ReplayProvider(diretorioGravacoes);
    var engine = new MarketEngine(replayProvider);

    // Configurar Exhaustion
    engine.Exhaustion.AplicarPreset(timeframe);

    // Configurar velocidade
    replayProvider.SetVelocidade(velocidade);

    Console.WriteLine($"\n✓ Replay configurado:");
    Console.WriteLine($"  Data: {dataReplay}");
    Console.WriteLine($"  Velocidade: {velocidade}x");
    Console.WriteLine($"  Exhaustion: {timeframe}");
    Console.WriteLine($"  {engine.Exhaustion.GetStatus()}\n");

    // Contadores
    var contagemSpoof = 0;
    var contagemIceberg = 0;
    var contagemRenewable = 0;
    var contagemExhaustion = 0;

    engine.Spoof.OnSpoofDetected += e =>
    {
        contagemSpoof++;
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"[SPOOF #{contagemSpoof}] {e.Time:HH:mm:ss.fff} {e.Side} {e.Broker} P={e.Price}");
        Console.ResetColor();
    };

    engine.Iceberg.OnIcebergDetected += e =>
    {
        contagemIceberg++;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[ICEBERG #{contagemIceberg}] {e.Time:HH:mm:ss.fff} {e.Side} {e.Broker} {e.FromPrice}→{e.ToPrice}");
        Console.ResetColor();
    };

    engine.Renewable.OnRenewableDetected += e =>
    {
        contagemRenewable++;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[RENEWABLE #{contagemRenewable}] {e.Time:HH:mm:ss.fff} {e.Side} {e.Broker} Renov:{e.Renewals}x");
        Console.ResetColor();
    };

    engine.Exhaustion.OnExhaustionDetected += e =>
    {
        contagemExhaustion++;
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"[EXHAUSTION #{contagemExhaustion}] {e.Time:HH:mm:ss.fff} → {e.DirecaoReversao} | T:{e.NumTrades} V:{e.VolumeTotal}");
        Console.ResetColor();
    };

    engine.OnConnectionChanged += e =>
    {
        Console.ForegroundColor = e.Status == ConnectionStatus.Connected ? ConsoleColor.Green : ConsoleColor.Gray;
        Console.WriteLine($"[{e.Status}] {e.Message}");
        Console.ResetColor();
    };

    // Conectar (passar data como username)
    await engine.ConnectAsync(new ProviderCredentials(dataReplay, "", ""));
    engine.Subscribe("WINFUT");

    Console.WriteLine("\n╔════════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║                  🎬 REPLAY EM ANDAMENTO                       ║");
    Console.WriteLine("║         Comandos: P = Pause/Resume | Q = Sair                ║");
    Console.WriteLine("╚════════════════════════════════════════════════════════════════╝\n");

    // Loop de comandos
    while (true)
    {
        if (Console.KeyAvailable)
        {
            var key = Console.ReadKey(true);
            
            if (key.Key == ConsoleKey.P)
            {
                replayProvider.TogglePause();
            }
            else if (key.Key == ConsoleKey.Q)
            {
                break;
            }
        }

        await Task.Delay(100);
    }

    await engine.DisconnectAsync();
    engine.Dispose();

    // Estatísticas
    Console.WriteLine("\n╔════════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║                    ESTATÍSTICAS DO REPLAY                     ║");
    Console.WriteLine("╚════════════════════════════════════════════════════════════════╝\n");

    Console.WriteLine($"  🟡 Spoof:      {contagemSpoof} detecções");
    Console.WriteLine($"  🔵 Iceberg:    {contagemIceberg} detecções");
    Console.WriteLine($"  🟢 Renewable:  {contagemRenewable} detecções");
    Console.WriteLine($"  🟣 Exhaustion: {contagemExhaustion} detecções ({timeframe})\n");
}
else
{
    // ═══════════════════════════════════════════════════════════════════════════════
    // MODO SIMULADOR (código original)
    // ═══════════════════════════════════════════════════════════════════════════════
    Console.WriteLine("\n▶ MODO SIMULADOR");
    Console.WriteLine("─────────────────────────────────────────────────────────────────\n");

    var diretorioGravacao = Path.Combine(Path.GetTempPath(), "MarketCore_Recordings");
    Console.WriteLine($"📁 Diretório de gravação: {diretorioGravacao}\n");

    var provider = new SimulatorProvider();
    var engine   = new MarketEngine(provider);

    Console.Write("🔴 Deseja HABILITAR gravação automática? (S/N): ");
    var resposta = Console.ReadLine()?.ToUpper().Trim();

    if (resposta == "S")
    {
        engine.HabilitarGravacao(diretorioGravacao);
        Console.WriteLine();
    }
    else
    {
        Console.WriteLine("⚪ Gravação desabilitada - apenas visualização\n");
    }

    Console.Write("⏱ Escolha o timeframe para Exhaustion (S30, M1, M2, M3, M5, M15): ");
    var tfEscolha = Console.ReadLine()?.ToUpper().Trim() ?? "M1";

    ExhaustionTimeframe timeframe;
    try
    {
        timeframe = Enum.Parse<ExhaustionTimeframe>(tfEscolha);
    }
    catch
    {
        Console.WriteLine("⚠ Timeframe inválido, usando M1");
        timeframe = ExhaustionTimeframe.M1;
    }

    engine.Exhaustion.AplicarPreset(timeframe);
    Console.WriteLine($"✓ Exhaustion configurado: {timeframe}");
    Console.WriteLine($"  {engine.Exhaustion.GetStatus()}\n");

    var contagemSpoof = 0;
    var contagemIceberg = 0;
    var contagemRenewable = 0;
    var contagemExhaustion = 0;

    engine.Spoof.OnSpoofDetected += e =>
    {
        contagemSpoof++;
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"[SPOOF #{contagemSpoof}] {e.Time:HH:mm:ss.fff} {e.Side} {e.Broker} P={e.Price}");
        Console.ResetColor();
    };

    engine.Iceberg.OnIcebergDetected += e =>
    {
        contagemIceberg++;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[ICEBERG #{contagemIceberg}] {e.Time:HH:mm:ss.fff} {e.Side} {e.Broker} {e.FromPrice}→{e.ToPrice}");
        Console.ResetColor();
    };

    engine.Renewable.OnRenewableDetected += e =>
    {
        contagemRenewable++;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[RENEWABLE #{contagemRenewable}] {e.Time:HH:mm:ss.fff} {e.Side} {e.Broker} Renov:{e.Renewals}x");
        Console.ResetColor();
    };

    engine.Exhaustion.OnExhaustionDetected += e =>
    {
        contagemExhaustion++;
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"[EXHAUSTION #{contagemExhaustion}] {e.Time:HH:mm:ss.fff} → {e.DirecaoReversao} | T:{e.NumTrades} V:{e.VolumeTotal}");
        Console.ResetColor();
    };

    engine.OnConnectionChanged += e => 
    {
        Console.ForegroundColor = e.Status == ConnectionStatus.Connected ? ConsoleColor.Green : ConsoleColor.Gray;
        Console.WriteLine($"[{e.Status}] {e.Message}");
        Console.ResetColor();
    };

    await engine.ConnectAsync(new ProviderCredentials("","",""));
    engine.Subscribe("WINFUT");

    Console.WriteLine("\n╔════════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║              🔴 SIMULADOR EM ANDAMENTO                        ║");
    Console.WriteLine("║                   (Enter para parar)                          ║");
    Console.WriteLine("╚════════════════════════════════════════════════════════════════╝\n");

    Console.ReadLine();

    await engine.DisconnectAsync();
    engine.Dispose();

    Console.WriteLine("\n╔════════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║                    ESTATÍSTICAS DA SESSÃO                     ║");
    Console.WriteLine("╚════════════════════════════════════════════════════════════════╝\n");

    Console.WriteLine($"  🟡 Spoof:      {contagemSpoof} detecções");
    Console.WriteLine($"  🔵 Iceberg:    {contagemIceberg} detecções");
    Console.WriteLine($"  🟢 Renewable:  {contagemRenewable} detecções");
    Console.WriteLine($"  🟣 Exhaustion: {contagemExhaustion} detecções ({timeframe})\n");
}

Console.WriteLine("╔════════════════════════════════════════════════════════════════╗");
Console.WriteLine("║                  ✅ SESSÃO FINALIZADA                         ║");
Console.WriteLine("╚════════════════════════════════════════════════════════════════╝\n");