# Handoff — Histórico WIN, ProfitDLL e MarketCore WPF

Última atualização: 2026-05-12 — fixes ao loop de espera do `GetHistoryTrades` (dados incompletos).

---

## Objetivo do utilizador

1. **Dom / livro de ordens**: tinha problemas (preços errados, vazios, nomes de corretora); foram feitas correções no motor e no provider.
2. **Histórico WIN**:
   - Biblioteca **`MarketCore.HistoricalImporter`** com importação para **PostgreSQL** (BINARY COPY, Npgsql) e opção **CSV**.
   - Consola **`HistoricalImporterTool`** para correr o import fora do WPF.
   - **WPF (FlowSense)**: janela **in-app** para baixar histórico WIN — escolher **intervalo de datas** e **pasta** do CSV; opção **abrir janela ao iniciar** após ligar ao mercado.

---

## O que já foi feito (resumo técnico)

### `MarketCore.HistoricalImporter`

- Tipos: `ContractGenerator`, `ContractPeriod`, `TradeRecord`, `TradeRecordFactory`.
- **`HistoricalImporter`**: escrita em PostgreSQL via `BeginBinaryImport`; implementa `IProfitHistoryTradeSink` e `FlushPendingExports`.
- **`CsvTradeExportBuffer`**: exporta negócios para CSV na pasta escolhida (separador `;`); mesma interface de sink.
- **`ProfitHistoryService`**:
  - Resolução dinâmica de export legacy (`DLLNewHistoryTypedTradesByPeriod`-style); se a DLL **não** tiver o símbolo, **fallback** para **`GetHistoryTrades`** com ticker + bolsa **F** e depois **B**, datas como `dd/MM/yyyy HH:mm:ss.fff`.
  - **`ProviderHistoryBridge`**: liga-se por reflexão ao **`ProfitDLLProvider.OnNativeHistoryTrade`** para receber callbacks de histórico.
- **`ProfitMarketInit`**: `DLLInitializeMarketLogin` + `TryEnsureMarketForHistoryAsync(sessionAlreadyConnected)`.
- `DatabaseConfig`, `DatabaseSetup`, etc.

### `Providers/Nelogica/ProfitDLLProvider.cs`

- Evento estático **`OnNativeHistoryTrade`**: disparado a partir de `OnHistoryCallback` com ticker, data em string, número do negócio, preço, **vol**, qtd, agente compra/venda, tipo — para o importador poder usar **`vol` quando `qtd` é 0**.

### `MarketCore.WPF`

- **`DownloadHistoryWindow`**: datas, pasta, botão Baixar; preferências em `%AppData%\MarketCore\flowsense_ui.json` (incl. “mostrar ao abrir”).
- **`MainWindow`**: botão de histórico (mercado real); abre histórico após **primeiro `Connected`** se a opção estiver ativa.
- **Correção importante**: passar **`Func<bool>`** para a sessão ao vivaço — `() => _profitDllConnected` — para não ficar “sem sessão” com valor **stal** da abertura da janela.

### `MarketCore.csproj`

- **`Compile Remove`** para pastas **`MarketCore.HistoricalImporter/**`** e **`HistoricalImporterTool/**`** para não compilarem glob no exe errado.

### Erros que apareceram e como foram tratados

| Sintoma | Causa provável / fix |
|--------|----------------------|
| `EntryPointNotFound` em `DLLNewHistoryTypedTradesByPeriod` | Deixou de depender só desse nome; **NativeLibrary** + candidatos; falta no DLL → **GetHistoryTrades**. Ver também **exe antigo** → `dotnet clean` / `dotnet run` no projeto certo. |
| **0 negócios** | Mapeamento qty: usar **vol se qtd ≤ 0**; tentar bolsa **F** e **B**; bridge aos callbacks de histórico. |
| “Não logado” estando ligado | **Snapshot** do bool na abertura da janela — corrigido com **`Func<bool>`** no clique. |

---

## Sessão 2026-05-12 — Downloads incompletos do dia (fix)

### Sintoma reportado pelo utilizador
Ao baixar o histórico de um dia para o PostgreSQL, às vezes vinham menos negócios do que o esperado.

### Causa-raiz
O `ProfitHistoryService` decidia que `GetHistoryTrades` tinha acabado por **heurística de silêncio**:
`HistoryQuietMs = 2_000` e `HistoryWaitMaxMs = 90_000`. Em dias WIN com volume alto, a ProfitDLL faz pacing e
pode pausar 3–8s entre rajadas. Resultado: o serviço dava por terminado cedo e desinscrevia ticker/book antes
de a DLL acabar de despachar os callbacks → trades perdidos no fim do dia.

### Fixes aplicados

`MarketCore.HistoricalImporter/ProfitHistoryService.cs`
- `HistoryQuietMs` 2s → **12s**; `HistoryWaitMaxMs` 90s → **600s**; `NativeGetHistoryTradesCallTimeout` 2min → **10min** (clamp 30s..20min).
- **Progress=100 como sinal de fim**: campo `s_lastProgressPct` atualizado pelo handler `HistoryProgress`. `WaitHistoryQuietAsync` sai cedo 2s após Progress=100 (e não engole rajadas que cheguem dentro desse drain).
- **Drain de 500ms** antes de `Unsubscribe*` por exchange e **drain de 5s** (até 1.5s sem novos ticks) no `finally` de `RequestHistoricalDataCore` antes de desligar o `ProviderHistoryBridge`.
- `OnDllNewHistoryTypedTrade` (callback do export `DLLNewHistoryTypedTradesByPeriod`) agora **incrementa `s_historyTicks`** e força `qty>=1` quando a DLL devolve 0 — simétrico ao bridge `OnHistory` que já mapeava `vol→qty`.

`Providers/Nelogica/ProfitDLLProvider.cs`
- `OnHistoryCallback`: invocações de `OnNativeHistoryTrade` e `ProfitHistoryRelay.Raise` em **try/catch independentes** — se o legacy event lançar, o relay (que alimenta o sink) já não é pulado.

`MarketCore.HistoricalImporter/HistoricalImporter.cs`
- Propriedades **`TotalAccepted`** / **`TotalRejected`** (contadores `Interlocked`) para distinguir callbacks recebidos vs descartados pelo `TradeRecordFactory`.
- `WaitForPendingFlushes` simplificado: só espera `_flushScheduled == 0` (a antiga condição `_buffer.Count < BufferCapacity` era redundante).

`MarketCore.WPF/DownloadHistoryWindow.xaml.cs`
- Janela mostra agora em tempo real **Recebidos / Buffer / Gravados** e, se houver, **Rejeitados pelo factory**. No "Concluído" idem.

### Como validar
1. Baixar um dia conhecido com muito volume.
2. Acompanhar pela janela: `Recebidos` deve continuar a subir mesmo com pausas longas.
3. No fim: `Recebidos ≈ Gravados` (a diferença, se houver, está explicada por `Rejeitados pelo factory`).
4. Log `%AppData%\MarketCore\history_dll.log` agora traz `[Progress] WIN... 100%`, `[WaitQuiet] Progress=100 recebido aos …ms; drain 2000ms` e linhas tipo `WIN…/F: drain capturou N callbacks adicionais antes do unsubscribe`. Se aparecer `[WaitQuiet] saiu por maxWait=600000ms`, foi o teto de 10min — basta aumentar `HistoryWaitMaxMs` ou `MaxHistoryChunkDays`.

---

## Onde paramos / o que ainda NÃO está fechado

1. **Fluxo histórico WIN → CSV** não está **validado end-to-end** na tua máquina: ainda pode sair **0 linhas** (comportamento da DLL, subscrições, símbolo, datas).
2. **Deploy**: se voltar o erro de **EntryPoint**, confirmar que estás a correr o **build atual** (não atalho para `bin` antigo).
3. **DLL Nelogica**: versões diferentes podem exigir **`ticker:bolsa`** ou **subscribe** antes de `GetHistoryTrades` (documentação da tua build).
4. **Validar com um dia real** que `Recebidos == Gravados` após os fixes de 2026-05-12. Se ainda houver delta, ver `Rejeitados pelo factory` e o `history_dll.log`.

---

## O que falta fazer (checklist para retomar)

- [x] **Logging / diagnóstico**: contagem de aceitos/rejeitados visível no UI e log (2026-05-12).
- [x] **Não terminar cedo** o `GetHistoryTrades` durante pausas longas da DLL (2026-05-12).
- [ ] **Verificar no dispositivo**: baixar um dia conhecido pós-fix e confirmar `Recebidos == Gravados`.
- [ ] Se callbacks = 0: avaliar **`SubscribeAdjustHistory`** ou subscribe do ativo **antes** de pedir histórico (manual da DLL).
- [ ] Opcional: **versão do build** visível na janela de download (confirmar binário correto).
- [ ] Opcional: referência direta de **`MarketCore.HistoricalImporter`** ao assembly principal para o evento de histórico (menos reflexão), se a estrutura de projetos permitir.
- [ ] Validar **intervalo de datas** e formato **WIN + mês + ano** conforme contratos gerados.

---

## Como correr o app com o código certo

```powershell
Set-Location "C:\Users\Anderson\Downloads\MarketCore"
dotnet clean ".\MarketCore.WPF\MarketCore.WPF.csproj"
dotnet build ".\MarketCore.WPF\MarketCore.WPF.csproj"
dotnet run --project ".\MarketCore.WPF\MarketCore.WPF.csproj"
```

Garantir **`ProfitDLL64.dll`** junto ao exe de saída.

---

## Ficheiros principais desta linha de trabalho

- `MarketCore.HistoricalImporter/ProfitHistoryService.cs`
- `MarketCore.HistoricalImporter/CsvTradeExportBuffer.cs`
- `MarketCore.HistoricalImporter/HistoricalImporter.cs`
- `Providers/Nelogica/ProfitDLLProvider.cs` (`OnNativeHistoryTrade`, `OnHistoryCallback`)
- `MarketCore.WPF/DownloadHistoryWindow.xaml(.cs)`
- `MarketCore.WPF/MainWindow.xaml.cs`
- `Contracts/IMarketDataProvider.cs`, `Engine/MarketEngine.cs` — se estiveres a retomar também o lado **DOM/mercado**, rever estes diffs no Git.

---

## Consola do importador (PostgreSQL / testes)

```powershell
dotnet run --project ".\HistoricalImporterTool\HistoricalImporterTool.csproj"
```

(Ajustar argumentos/connection string conforme `Program.cs` e ambiente.)

---

*Atualiza este ficheiro quando fechares bugs ou mudares o fluxo.*
