using MarketCore.Contracts;
using MarketCore.Engine;
using MarketCore.Engine.Detectors;
using MarketCore.Models;
using MarketCore.Providers.Simulator;

var provider = new SimulatorProvider();
var engine   = new MarketEngine(provider);

engine.Spoof.OnSpoofDetected += e =>
    Console.WriteLine($"[SPOOF] {e.Time:HH:mm:ss.fff} {e.Ticker} {e.Side} {e.Broker} " +
                      $"P={e.Price} Vol:{e.VolumeBefore}→{e.VolumeAfter} Dist:{e.PriceDistance}pts");

engine.Iceberg.OnIcebergDetected += e =>
    Console.WriteLine($"[ICEBERG] {e.Time:HH:mm:ss.fff} {e.Ticker} {e.Side} {e.Broker} " +
                      $"{e.FromPrice}→{e.ToPrice} Vol:{e.Volume} {e.Direction}");

engine.Renewable.OnRenewableDetected += e =>
    Console.WriteLine($"[RENOVÁVEL] {e.Time:HH:mm:ss.fff} {e.Ticker} {e.Side} {e.Broker} " +
                      $"P={e.Price} Lote:{e.VolumePerCycle} Renov:{e.Renewals}x Total:{e.TotalExecuted}");

engine.OnConnectionChanged += e => Console.WriteLine($"[{e.Status}] {e.Message}");

await engine.ConnectAsync(new ProviderCredentials("","",""));
engine.Subscribe("WINFUT");

Console.WriteLine("Detectores rodando... Enter para parar");
Console.ReadLine();

await engine.DisconnectAsync();
engine.Dispose();