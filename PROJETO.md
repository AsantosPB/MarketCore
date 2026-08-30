# MarketCore Intelligence Engine — Memória do Projeto
## Estado atual
- Versão: 1.0.0-alpha
- Última atualização: 29/08/2026 (Fase 6)
- Fase atual: Fase 10 concluída — iniciando Fase 11
- Ambiente: C:\Users\Anderson\Downloads\MarketCore
- Repositório: github.com/AsantosPB/MarketCore
- Branch: main — commit atual: 388a82f — tag: v-pre-mcie-20260829
---
## Decisões arquiteturais tomadas
### Interface
- Janela MCIE substitui completamente o FlowSense v4 (tela atual sem valor operacional)
- Book visual REMOVIDO da tela — capturado internamente para Feature Engine
- No lugar do book: medidor de pressão + medidor de agressão
- Janela MCIE: independente, mesmo padrão do Pregão Viva Voz
- Janela Preço Justo: independente, fase posterior ao MCIE
### Armazenamento
- Stack: RAM + binário bruto + DuckDB + SQLite
- SEM ClickHouse, SEM PostgreSQL, SEM servidor externo
- Retenção: 10 pregões — rotação automática
- Volume estimado: ~7 GB para 10 pregões completos
### Arquitetura
- Caminho crítico: ProfitDLL → RAM → Features → Agents → Decision → Risk → Order
- Nenhuma operação de I/O, rede ou banco no caminho crítico
- Persistência 100% assíncrona via workers em background
- Calendário econômico: carregado automaticamente às 08h30 via API Investing.com
### Janelas do sistema
| Janela | Status | Descrição |
|--------|--------|-----------|
| MCIE Principal | Em implementação | Dashboard principal de trading |
| Preço Justo | Planejada (fase posterior) | Ibovespa teórico via DDE das ações |
---
## O que já existe e funciona — NÃO MEXER
Identificado na auditoria de 29/08/2026:
- ConcurrentQueue lock-free em todos os callbacks (OnTrade, OnBook)
- ManualResetEventSlim para wake-up sub-milissegundo no trade
- FileStream aberto continuamente durante o pregão (sem custo de open/close)
- Filtro TTL 15s no OnTradeCallbackCore (proteção contra replay histórico)
- Cache de formato de data (_lastMatchedFormatIdx) — evita 6 TryParseExact por callback
- Workers Task.Run em background (conceito correto)
- _trades.bin, _book.bin, _flowscore.bin já existem
- _flowscore.bin com registro fixo de 56 bytes (seek direto por índice)
- Estrutura de pastas {diretorioBase}/{yyyy-MM-dd}/ já implementada
---
## O que precisa ser corrigido
- [x] BookProcessingLoop desativada — reativar independente do PVV
- [x] OnOfferBookCallbackCore filtra apenas rank 1-4 — gravar todos os 10 níveis
- [x] _book.bin formato variável — mudar para registro fixo de 272 bytes (seek direto)
- [x] Workers com Thread.Sleep polling — substituir por Channel<T>
- [x] Sem checksum nos arquivos binários — adicionar no header
- [ ] Parse de data dentro do callback do trade — mover para thread de processamento
- [ ] TNewDailyCallback não gravada — dado rico (OHLCV + agressão) sendo descartado
- [ ] Retorno GravarTradeAsync descartado com _ = — tratar erro silencioso
---
## O que será adicionado (novo)
- [ ] SequenceNumber nas structs RawTradeEvent e RawBookEvent
- [ ] DuckDB — tabelas: market_snapshots, features, labels
- [ ] SQLite — tabelas: patterns, decisions, trades, orders, config
- [ ] Calendar Loader — download automático agenda econômica (08h30 diário)
- [ ] Detecção automática horário verão/inverno USA
- [ ] Feature Engine — imbalance, OFI, microprice, delta, absorção, velocidade, aceleração
- [ ] Event Detector — 10 tipos de eventos relevantes
- [ ] Regime Detector — TREND_UP, TREND_DOWN, RANGE, HIGH_VOL, BREAKOUT
- [ ] Pattern Engine — descoberta estatística sem look-ahead bias
- [ ] Pattern Registry — lifecycle: DISCOVERED → VALIDATING → APPROVED → LIVE → DEPRECATED
- [ ] Agent Engine — FlowAgent, BookAgent, AbsorptionAgent, OFIAgent, PatternAgent, RegimeAgent
- [ ] Decision Core — combinação de agentes com modo confirmado (600ms)
- [ ] Risk Manager — 11 verificações + Kill Switch automático
- [ ] Order Manager — state machine completa de ordens
- [ ] Execution Log — latência e slippage por operação
- [ ] Rotação automática — deletar pregão mais antigo após 10 dias
- [ ] Janela MCIE Principal (WPF) — substitui FlowSense v4
- [ ] Janela Preço Justo (WPF) — fase posterior
---
## Roadmap de fases
- [x] Fase 0 — Backup + snapshot git (29/08/2026)
- [x] Fase 1 — Corrigir BookProcessingLoop + formato fixo _book.bin
- [x] Fase 2 — SequenceNumber + Channel<T> nos workers
- [x] Fase 3 — DuckDB + SQLite (novas camadas, sem tocar no binário)
- [x] Fase 4 — Calendar Loader (agenda econômica automática)
- [x] Fase 5 — Feature Engine
- [x] Fase 6 — Event Detector + Regime Detector
- [x] Fase 7 — Dataset automático (features + labels no DuckDB)
- [x] Fase 8 — Pattern Engine + Pattern Registry
- [x] Fase 9 — Replay determinístico
- [x] Fase 10 — Backtest com simulador de execução
- [ ] Fase 11 — Agent Engine (6 agentes)
- [ ] Fase 12 — Decision Core
- [ ] Fase 13 — Risk Manager + Kill Switch
- [ ] Fase 14 — Paper Trading (2 semanas mínimo)
- [ ] Fase 15 — Live Execution
- [ ] Fase 16 — Janela MCIE Principal (WPF)
- [ ] Fase 17 — Janela Preço Justo (WPF)
---
## Horários e contexto de mercado
### Pregão WINFUT
- Pré-abertura (leilão): 09:00 → 09:05 — NÃO OPERAR
- Abertura: 09:05
- Encerramento: 18:00 (call de fechamento 17:55 → 18:00 — NÃO OPERAR)
### NYSE/NASDAQ em horário de Brasília
- Horário de verão USA (mar → nov): abre 10h30, fecha 17h00
- Horário de inverno USA (nov → mar): abre 11h30, fecha 17h00
- DST 2026: 8 março → 1 novembro
### Dados econômicos — horário Brasília
| Dado | Verão USA | Inverno USA | Impacto |
|------|-----------|-------------|---------|
| Payroll, CPI, PPI | 09h30 | 10h30 | CRÍTICO |
| ISM, Confiança | 11h00 | 12h00 | ALTO |
| FOMC/Fed | 15h00 | 16h00 | CRÍTICO |
| Copom | Variável | Variável | CRÍTICO |
| Focus BCB | 08h25 | 08h25 | MÉDIO |
### Bloqueios automáticos por impacto
| Impacto | Bloquear antes | Aguardar após |
|---------|---------------|---------------|
| CRÍTICO | 30 minutos | 5 segundos |
| ALTO | 15 minutos | 3 segundos |
| MÉDIO | 5 minutos | 2 segundos |
---
## Configurações do sistema
```json
{
  "instrument": "WIN",
  "storage": {
    "retention_days": 10,
    "raw_path": "./data/raw",
    "db_path": "./data/db"
  },
  "calendar": {
    "load_time": "08:30:00",
    "countries": ["US", "BR"]
  },
  "decision": {
    "mode": "confirmed",
    "confirmation_ms": 600,
    "buy_threshold": 65,
    "sell_threshold": -65
  },
  "risk": {
    "max_position": 1,
    "max_daily_loss_brl": 1000,
    "max_trades_per_day": 20,
    "block_minutes_before_critical": 30,
    "wait_seconds_after_critical": 5
  }
}
```
---
## Histórico de mudanças
### 30/08/2026 — Fase 10
- Arquivos criados:
  - Engine/Backtest/ExecutionProvider.cs — interface IExecutionProvider, OrderType, OrderFill
  - Engine/Backtest/BacktestExecutionProvider.cs — simulador de execução com latência e slippage assimétrico (Ask+slip / Bid-slip)
  - Engine/Backtest/BacktestPosition.cs — gerenciamento de posição com MFE/MAE e criação de TradeRecord
  - Engine/Backtest/BacktestModels.cs — BacktestResult (StrategyAlpha/ExecutionAlpha separados), BacktestConfig
  - Engine/Backtest/BacktestEngine.cs — motor principal: replay + padrões + execução + stop/target + CalcularResultado
- Arquivos modificados:
  - Engine/MarketEngine.cs — using FASE 10, campo _backtestEngine, método ExecutarBacktestAsync
  - PROJETO.md — Fase 10 marcada concluída
- Commit: fase-10 — tag: v0.10.0

### 30/08/2026 — Fase 9
- Arquivos criados:
  - Engine/Replay/ReplayModels.cs — ReplaySpeed, ReplayStatus, RawTradeEvent, RawBookEvent, ReplaySession, ReplayResult
  - Engine/Replay/ReplayReader.cs — leitura de trades (var-length) e book (272 bytes), MergeSort cronológico, fallback de nome de arquivo
  - Engine/Replay/ReplayEngine.cs — loop de replay com throttle, pause/resume, step-by-step, SHA256 checksum de determinismo
- Arquivos modificados:
  - Engine/MarketEngine.cs — using FASE 9, campos _diretorioBase/_replayEngine, IniciarReplayAsync, PausarReplay, RetomarReplay, PararReplay, ReplayAtual, Dispose
  - PROJETO.md — Fase 9 marcada concluída
- Commit: fase-9 — tag: v0.9.0

### 29/08/2026 — Fase 1
- Arquivos modificados:
  - Providers/Nelogica/ProfitDLLProvider.cs — BookProcessingLoop reativada (StartProcessingThread); filtros rank/agente/PVV removidos do OnOfferBookCallbackCore; todos os 10 níveis enfileirados em _bookQueue
  - Engine/Recording/MarketRecorder.cs — _book.bin reescrito com formato fixo 272 bytes (ExchangeTimestamp+ReceiveTimestamp+SequenceNumber+Price+10Bids+10Asks); SequenceNumber Int64 adicionado ao _trades.bin; campos estáticos _tradeSequence e _bookSequence adicionados
  - PROJETO.md — fase 1 marcada concluída
- Commit: fase-1 — tag: v0.1.0

### 29/08/2026
- Auditoria completa do MarketCore existente via Cowork (somente leitura)
- Backup criado: C:\Users\Anderson\Downloads\Backup\MarketCore_20260829_pre-MCIE\
- Git commit: 388a82f — tag v-pre-mcie-20260829 criada
- Push confirmado: github.com/AsantosPB/MarketCore (branch main)
- Especificação técnica completa gerada (MarketCore_Especificacao_Completa.docx)
- Layout MCIE aprovado: pressão + agressão + agentes + decisão + posição + event log
- Decisão: book visual removido da tela (capturado internamente)
- Decisão: preço justo como janela independente (Fase 17)
- Decisão: fontes grandes, alto contraste, grade de preço no gráfico
- PROJETO.md criado
---
## Instruções para o Cowork
### Ao iniciar qualquer sessão de trabalho:
1. Leia este arquivo PRIMEIRO antes de qualquer ação
2. Identifique a fase atual e o que está pendente
3. Nunca refaça o que já está marcado como [x]
4. Nunca altere decisões arquiteturais sem consultar o Anderson
### Ao finalizar qualquer implementação:
1. Marque a fase concluída com [x] no Roadmap
2. Adicione entrada no Histórico de mudanças com a data
3. Liste os arquivos criados e modificados
4. Atualize o campo "Fase atual" no Estado atual
5. Faça git commit com mensagem descritiva
6. Atualize a tag ou crie nova tag de versão
### Formato do commit ao concluir uma fase:
git commit -m "fase-X: descrição do que foi implementado"
git tag -a v0.X.0 -m "Fase X concluída — descrição"
git push origin main --tags
---
*Arquivo mantido automaticamente pelo Cowork a cada implementação.*
*Não editar manualmente exceto em caso de decisão arquitetural nova.*

### 29/08/2026 — Fase 2
- Arquivos modificados:
  - Engine/Recording/MarketRecorder.cs — 4 ConcurrentQueue + Thread.Sleep workers substituídos por Channel<T> (UnboundedChannelOptions SingleReader=true, SingleWriter=false; await foreach ReadAllAsync); Writer.Complete() em todos os 4 channels no FinalizarPregaoAsync; header de 64 bytes adicionado em todos os arquivos binários (trades.bin, book.bin, flowscore.bin); CRC32 gravado nos bytes 60-63 do header via System.IO.Hashing.Crc32; método estático VerificarIntegridade(string) adicionado
  - PROJETO.md — fase 2 marcada concluída
- Commit: fase-2 — tag: v0.2.0

### 29/08/2026 — Fase 3
- Arquivos criados:
  - Engine/Storage/StorageModels.cs — MarketSnapshot (29 campos), DecisionRecord, TradeRecord
  - Engine/Storage/StorageManager.cs — InicializarAsync, schemas DuckDB (market_snapshots + labels) e SQLite (decisions, trades, patterns, config), GravarSnapshotAsync, GravarDecisionAsync, GravarTradeOperacionalAsync, ConsultarSnapshotsAsync, ConsultarDecisionsAsync
- Arquivos modificados:
  - MarketCore.csproj — PackageReference DuckDB.NET.Data.Full 1.2.0, Microsoft.Data.Sqlite 9.0.0
  - Engine/MarketEngine.cs — campo _storageManager + _dbPath; InicializarAsync em ConnectAsync; Dispose em DisconnectAsync e Dispose()
  - .gitignore — data/raw/, data/db/, data/features/, data/labels/, data/patterns/ adicionados
  - PROJETO.md — fase 3 marcada concluída
- Pastas criadas: data/db/, data/raw/, data/features/, data/labels/, data/patterns/
- Commit: fase-3 — tag: v0.3.0

### 29/08/2026 — Fase 6
- Arquivos criados:
  - Engine/Detectors/EventDetector.cs — 7 detectores: AggressionSpike (p90 histórico + streak 5 snapshots), BookImbalance (|imb|>0.60), Absorption (|score|>60), PriceAcceleration (|accel|>5 pts/s²), VolumeSpike (p90 histórico), DeltaDivergence (delta vs velocidade), TradeRateSpike (2× média); RingBuffer de histórico 300 amostras
  - Engine/Detectors/RegimeDetector.cs — 6 classificadores: TrendUp/TrendDown (streak 10+ snapshots + Delta5s + Vwap), Range (streak 20+ + |Delta5s|<200), HighVol/LowVol (p90/p10 volatilidade), Breakout (DistanceHigh/Low < 5 pts + VolumeRate p80); eleição por confiança máxima; detecção de Transition (regime mudou < 30s)
- Arquivos modificados:
  - Engine/Detectors/DetectorModels.cs — adicionados enum MarketRegime (8 valores), enum MarketEventType (12 valores), class MarketEvent, class RegimeState
  - Engine/Features/FeatureSnapshot.cs — campo Confidence adicionado (confiança do RegimeDetector 0-100)
  - Engine/Features/FeatureEngine.cs — integração: campos _eventDetector/_regimeDetector; eventos OnMarketEvent/OnRegimeChange; propriedade RegimeAtual; init em Inicializar(); chamada dos detectores em CalcularSnapshot()
  - Engine/MarketEngine.cs — handlers OnMarketEventDetectado/OnRegimeAlterado; wiring em ConnectAsync(); propriedade RegimeAtual
  - PROJETO.md — fase 6 marcada concluída
- Commit: fase-6 — tag: v0.6.0

### 29/08/2026 — Fase 5
- Arquivos criados:
  - Engine/Features/RingBuffer.cs — buffer circular pré-alocado, thread-safe, capacidades: trades=10000, bookSnaps=3600, prices=10000
  - Engine/Features/FeatureSnapshot.cs — 37 campos de microestrutura; método ToMarketSnapshot() para persistência no DuckDB
  - Engine/Features/FeatureEngine.cs — motor de features incremental em memória: BookImbalance, Microprice, Delta(100ms/500ms/1s/2s/5s), OFI(100ms/500ms/1s), TradeRate, VolumeRate, AggressionRatio, Velocity, Acceleration, Volatility30s, VWAP, DistanceVwap/High/Low, AbsorptionScore, Stacking/Pulling, TimeWindow, Regime
  - Engine/Features/SnapshotTimer.cs — timer 100ms; escrita fire-and-forget no DuckDB via ContinueWith; contador SnapshotCount
- Arquivos modificados:
  - Engine/MarketEngine.cs — integração FeatureEngine (_featureEngine, _snapshotTimer, UltimoSnapshot); OnTrade→_featureEngine.OnTrade; PublishDirtyBookSnapshots→_featureEngine.OnBook; Inicializar/Parar/Dispose
  - PROJETO.md — fase 5 marcada concluída
- Commit: fase-5 — tag: v0.5.0

### 29/08/2026 — Fase 4
- Arquivos criados:
  - Engine/Calendar/CalendarModels.cs — ImpactLevel enum (Low/Medium/High/Critical), EconomicEvent (EventId, TimeBrasilia, Impact, BlockMinutesBefore, WaitSecondsAfter, BloqueioInicio/Fim), CalendarDay
  - Engine/Calendar/CalendarLoader.cs — download Investing.com via HtmlAgilityPack; detecção DST USA (2º dom. março → 1º dom. novembro); conversão NY→Brasília (+1h/+2h); classificação de impacto por país+keywords; persistência SQLite; EstaBloqueado, ProximoEvento, MinutosAteProximoBloqueio, VerificarBloqueios
  - Engine/Calendar/CalendarTimer.cs — Timer carga às 08:30 + loop monitoramento 30s
- Arquivos modificados:
  - Engine/Storage/StorageManager.cs — tabela economic_events no SQLite; SalvarEventosAsync(CalendarDay); CarregarEventosSalvosAsync(DateTime)
  - Engine/MarketEngine.cs — campos _calendarLoader/_calendarTimer/_calendarioHoje; propriedades públicas BloqueadoPorEventoEconomico, CalendarioHoje, ProximoEvento, MinutosAteProximoBloqueio; init em ConnectAsync; handlers OnCalendarLoaded/OnBlockApproaching/OnBlockStart/OnBlockEnd; Parar/Dispose em DisconnectAsync e Dispose()
  - MarketCore.csproj — PackageReference HtmlAgilityPack 1.11.67
  - PROJETO.md — fase 4 marcada concluída
- Commit: fase-4 — tag: v0.4.0

### 29/08/2026 — Fase 7
- Arquivos criados:
  - Engine/Dataset/DatasetModels.cs — LabelRecord (FutureReturn 100ms/250ms/500ms/1s/2s/5s/10s, MFE/MAE 5s, TimeTo20Pts, TimeToStop), DatasetRecord, DatasetStats
  - Engine/Dataset/DatasetBuilder.cs — BuildAsync (labels sem look-ahead bias: j > i estrito), BuildRangeAsync; cálculo RetornoFuturo por interpolação de snapshots; MFE/MAE via janela 5s; TimeTo20Pts/TimeToStop via loop forward
  - Engine/Dataset/DatasetTimer.cs — timer 60s; disparo automático entre 18h04–18h06; flags _rodouHoje/_ultimaData para evitar duplicata diária; DispararManualAsync para testes
- Arquivos modificados:
  - Engine/Storage/StorageManager.cs — tabela dataset_stats no DuckDB; SalvarLabelsAsync, ConsultarDatasetAsync (JOIN market_snapshots+labels), SalvarDatasetStatsAsync, DiaTemLabelsAsync
  - Engine/MarketEngine.cs — campos _datasetBuilder/_datasetTimer; init em ConnectAsync; handler OnDatasetPronto; propriedade DatasetBuilder; ConstruirDatasetManualAsync; Dispose
  - PROJETO.md — fase 7 marcada concluída
- REGRA ABSOLUTA LOOK-AHEAD BIAS: labels usam apenas index j > i (timestamp > T0). Comentado no código.
- Commit: fase-7 — tag: v0.7.0

### 29/08/2026 — Fase 8
- Arquivos criados:
  - Engine/Patterns/PatternModels.cs — PatternStatus (9 estados), PatternCondition, PatternStats (12 métricas + WinRateByRegime), DiscoveredPattern (InDecay = RecentWinRate < DiscoveryWinRate * 0.85)
  - Engine/Patterns/PatternEvaluator.cs — Satisfaz() (all-conditions match), CalcularStats() (WinRate/LossRate/Expectancy/ProfitFactor/Sharpe/MFE/MAE/WinRateByRegime), AvaliarPadrao(); GetFeatureValue() via switch sobre 15 features
  - Engine/Patterns/PatternDiscovery.cs — DescubrirAsync() com divisão 70/15/15; busca 2-3 condições; validação >= 70% performance em validação e out-of-sample; EliminarRedundantes() (correlação > 0.85); 13 features candidatas; critérios: MinSamples=200, MinExpectancy=2.0, MinProfitFactor=1.5, MinWinRate=0.55
  - Engine/Patterns/PatternRegistry.cs — InicializarAsync, AdicionarAsync, AtualizarStatusAsync, PadroesAtivos, MonitorarDecayAsync (threshold InDecay: RecentWinRate < DiscoveryWinRate * 0.85)
- Arquivos modificados:
  - Engine/Storage/StorageManager.cs — SalvarPadraoAsync (SQLite, conditions em JSON), AtualizarStatusPadraoAsync, CarregarPadroesAsync, ConsultarDatasetComLabelsAsync (alias para ConsultarDatasetAsync)
  - Engine/MarketEngine.cs — campos _patternRegistry/_patternDiscovery; init em ConnectAsync; wiring OnDatasetPronto (discovery + decay automáticos); handler OnPatternEmDecay; propriedades PatternRegistry e PadroesAtivos()
  - PROJETO.md — fase 8 marcada concluída
- Commit: fase-8 — tag: v0.8.0
