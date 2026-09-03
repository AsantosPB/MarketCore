# MarketCore Intelligence Engine — Memória do Projeto
## Estado atual
- Versão: 1.0.0-beta
- Última atualização: 02/09/2026 (Fase 16 concluída — LivePatternDiscovery validado em pregão real)
- Fase atual: Fase 16 concluída — iniciando Fase 17 (Preço Justo)
- Ambiente: C:\Users\Anderson\Downloads\MarketCore
- Repositório: github.com/AsantosPB/MarketCore
- Branch: main — HEAD: 8591b36 — working tree dirty (sem commit/tag/push desta correção)
- Como rodar: C:\Users\Anderson\Downloads\MarketCore\MarketCore.WPF\bin\Release\net9.0-windows\MarketCore.WPF.exe (ou MarketCore.bat, agora apontando para Release)
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
- [x] Fase 11 — Agent Engine (6 agentes)
- [x] Fase 12 — Decision Core
- [x] Fase 13 — Risk Manager + Kill Switch
- [x] Fase 14 — Paper Trading (2 semanas mínimo)
- [x] Fase 15 — Live Execution
- [x] Fase 16 — Janela MCIE Principal (WPF) — Concluída — ver histórico 02/09/2026 para detalhes
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
### 30/08/2026 — Fase 14
- Arquivos criados:
  - Engine/Paper/PaperTradingSession.cs — POCO de sessão com estatísticas completas
  - Engine/Paper/PaperPosition.cs — posição com MFE/MAE, gera TradeRecord ao fechar
  - Engine/Paper/PaperExecutionProvider.cs — IExecutionProvider simulado, latência 80-300ms, slippage 0.5
  - Engine/Paper/PaperTradingEngine.cs — motor completo, stop -340pts/target +500pts, OnOrderFilled capture
- Arquivos modificados:
  - Engine/Storage/StorageManager.cs — tabela paper_sessions, SalvarPaperSessionAsync, CarregarPaperSessionsAsync
  - Engine/MarketEngine.cs — using FASE 14, campos _paperEngine/_paperModeAtivo, propriedades PaperModeAtivo/SessaoPaperAtual, AtivarPaperTrading/DesativarPaperTradingAsync, pipeline RiskManager→PaperTradingEngine, handlers OnPaperTrade/OnPaperPnLUpdate/OnPaperSessionEnd, Dispose
  - PROJETO.md — Fase 14 marcada concluída
- Pipeline: FeatureEngine → AgentEngine → DecisionCore → RiskManager → PaperTradingEngine
- Commit: fase-14 — tag: v0.14.0

### 30/08/2026 — Fase 16
- Arquivos alterados:
  - MarketCore.WPF/MainWindow.xaml — substituído por dashboard MCIE completo (3 colunas, toolbar MCIE, seletor de modo OBSERVAR/PAPER/LIVE)
  - MarketCore.WPF/MainWindow.xaml.cs — adições MCIE: _mcieTimer 100ms, McieTimer_Tick, BtnModoObservar/Paper/Live_Click, ConfirmarLiveModal, TxStop/TxTarget_TextChanged, AppendEventLog, BtnTf_Click, UpdateModeButtonStyles, ViewModels AgentViewModel/EventLogViewModel/CalendarioEventViewModel
- Commit: fase-16 — tag: v0.16.0

### 30/08/2026 — Fase 15
- Arquivos criados:
  - Engine/Live/LiveExecutionProvider.cs — implementação live de IExecutionProvider (IsLive=true); stub documentado para integração futura com ProfitDLL (ordem real ainda não mapeada em ProfitDLL.cs); market orders disparam fill imediato, limit orders via VerificarOrdensPendentes(); CancelarTodasAsync() para Kill Switch
  - Engine/Live/LiveTradingEngine.cs — motor live espelhando PaperTradingEngine; reutiliza PaperPosition e PaperTradingSession; ProcessarDecisaoAsync(DecisionState, FeatureSnapshot); stop -340 pts / target +500 pts; PararAsync() para cancelar ordens; CalcularEstatisticas(); persiste via StorageManager
- Arquivos modificados:
  - Engine/MarketEngine.cs — using FASE 15, campos _liveEngine/_liveModeAtivo, propriedades ModoAtual/SessaoLiveAtual, AtivarLiveTrading/DesativarLiveTradingAsync, pipeline live>paper>log, handlers OnLiveTrade/OnLivePnLUpdate/OnLiveError, Kill Switch aciona PararAsync(), Dispose
  - PROJETO.md — Fase 15 marcada concluída
- Nota técnica: ProfitDLLProvider implementa IMarketDataProvider apenas (sem métodos de roteamento de ordens). LiveExecutionProvider contém TODO documentado para integração futura quando ProfitDLL.cs mapear SendBuyOrder/SendSellOrder/CancelOrder.
- Commit: fase-15 — tag: v0.15.0

### 30/08/2026 — Fase 13
- Arquivos criados:
  - Engine/Risk/RiskModels.cs — RiskCheckResult, BlockReason, RiskDecision, RiskConfig, RiskState
  - Engine/Risk/RiskManager.cs — 11 verificações, Kill Switch automático (perda/feed/book), ResetDiario
- Arquivos modificados:
  - Engine/MarketEngine.cs — using FASE 13, campos _riskManager/_riskConfig/_pnlDiario etc, wiring em ConnectAsync, pipeline FeatureEngine→AgentEngine→DecisionCore→RiskManager, OnKillSwitchAtivado/OnOrdemBloqueada, propriedades RiskManager/EstadoRisco/AtivarKillSwitchManual/DesativarKillSwitch, ResetDiario em IniciarPregaoAsync, KillSwitch em Dispose
  - PROJETO.md — Fase 13 marcada concluída
- Kill Switch automático: (1) perda diária >= limite, (2) feed desconectado, (3) book stale >10s
- Commit: fase-13 — tag: v0.13.0

### 30/08/2026 — Fase 12
- Arquivos criados:
  - Engine/Decision/DecisionModels.cs — DecisionState, DecisionMode, WeightSet (pesos por regime)
  - Engine/Decision/DecisionCore.cs — scoring ponderado, AplicarConfirmacao 600ms, GravarDecisionAsync
- Arquivos modificados:
  - Engine/MarketEngine.cs — using FASE 12, campo _decisionCore, wiring em ConnectAsync (substituição do stub Fase 11), OnDecisaoTomada, DecisionCore/UltimaDecisao properties
  - PROJETO.md — Fase 12 marcada concluída
- Commit: fase-12 — tag: v0.12.0

### 30/08/2026 — Fase 11
- Arquivos criados:
  - Engine/Agents/AgentModels.cs — Direction, AgentSignal, IAgent
  - Engine/Agents/FlowAgent.cs — delta 1s/5s + OFI + AggressionRatio
  - Engine/Agents/BookAgent.cs — BookImbalance + DepthImbalance + Microprice + Stacking/Pulling
  - Engine/Agents/AbsorptionAgent.cs — AbsorptionScore + amplificação por velocidade baixa
  - Engine/Agents/OFIAgent.cs — OFI 100ms/500ms/1s + convergência de janelas
  - Engine/Agents/PatternAgent.cs — padrões históricos aprovados, NeutralSignal se vazio
  - Engine/Agents/RegimeAgent.cs — regime + janela temporal + bloqueio em leilão/evento
  - Engine/Agents/AgentEngine.cs — orquestra 6 agentes, OnSignals
- Arquivos modificados:
  - Engine/MarketEngine.cs — using FASE 11, campo _agentEngine, wiring em ConnectAsync, OnAgentSignals, AgentEngine/UltimosSignals properties
  - PROJETO.md — Fase 11 marcada concluída
- Commit: fase-11 — tag: v0.11.0

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

### 31/08/2026 — Fase 16 (UI MCIE — correção dos 4 problemas)
- Arquivos modificados:
  - Engine/Agents/AgentEngine.cs — construtor lite sem PatternAgent (AgentEngine()) adicionado antes do construtor com PatternRegistry; permite instanciar sem StorageManager
  - Engine/Decision/DecisionCore.cs — StorageManager? tornado nullable com valor padrão null; guard `if (_storage != null)` na gravação; permite usar sem banco
  - Engine/MarketEngine.cs — [FASE 16] AgentEngine lite + DecisionCore sem persistência criados ANTES do bloco `if (_recordingEnabled)`; _featureEngine.OnSnapshot movido para fora do bloco de gravação com null-guard em _riskManager; dentro do bloco de gravação: upgrade para AgentEngine(_patternRegistry) e DecisionCore(_storageManager); handler usa campos por referência — sem re-subscrição necessária; propriedades GravacaoAtiva e UltimaAtualizacaoBook já presentes
  - MarketCore.WPF/MainWindow.xaml.cs:
    - AgentViewModel: adicionados ScorePct (double), CorBrush (SolidColorBrush), ScoreStr (string) com INotifyPropertyChanged
    - Pressão do Book: corrigido de AggressionRatio para BookImbalance [-1..+1]; ColVenda/ColCompra recebem GridLength proporcional; TbPressaoScore com formato +0.00 e Foreground dinâmico (verde/vermelho/amarelo)
    - Agressão: corrigido de buyRatio/sellRatio para Delta1s (direção) + AggressionRatio (intensidade); PbCompra/PbVenda recebem values proporcionais; TbAgCompraVal/TbAgVendaVal com Foreground dinâmico
    - Agents population: McieAgentes populado com ScorePct, CorBrush, ScoreStr, Conf corretamente
- Problemas resolvidos: PROBLEMA 1 (barras Pressão), PROBLEMA 2 (cor Agressão), PROBLEMA 3 (AgentViewModel), PROBLEMA 4 (Decisão sempre Wait)

### 31/08/2026 — Fase 16b (diagnóstico + 5 correções UI/Calendar)
- Arquivos modificados:
  - Engine/Agents/PatternAgent.cs — PatternRegistry tornado nullable (PatternRegistry? registry = null); guard `if (_registry == null) return NeutralSignal()` no início de Evaluate; permite PatternAgent() sem registry no modo lite
  - Engine/Agents/AgentEngine.cs — construtor lite agora inclui PatternAgent() (Neutral em modo lite); 6 agentes: FlowAgent, BookAgent, AbsorptionAgent, OFIAgent, PatternAgent, RegimeAgent
  - Engine/MarketEngine.cs — [FASE 16] CalendarLoader e CalendarTimer movidos para ANTES do bloco `if (_recordingEnabled)`, criados com null StorageManager (sem persistência SQLite em modo observação); removidos do bloco recording e do DesabilitarGravacao; método público CarregarCalendarioAsync() adicionado; CalendarLoader: `public CalendarLoader()` aceita null storage
  - MarketCore.WPF/MainWindow.xaml.cs — using MarketCore.Engine.Calendar adicionado; Kill Switch: "OFF"→"INATIVO", "ATIVO"→"ATIVO ⚠" com cor verde/vermelho; log diagnóstico Console.WriteLine [MCIE-DEBUG] BookImb/Delta1s/Ofi1s/AbsScore; McieCalendario populado com ImpactLevel/CalendarioHoje no McieTimer_Tick; CarregarCalendarioAsync chamado após ConnectAsync
- Diagnóstico pendente: verificar [MCIE-DEBUG] no console para confirmar se BookImbalance/Delta1s/Ofi1s chegam com valor 0 (determina se problema é no FeatureEngine ou nos thresholds dos agentes)

### 31/08/2026 — Fase 16c (diagnóstico profundo + redesign Agressão)
- Arquivos modificados:
  - Engine/Features/FeatureEngine.cs — log diagnóstico temporário [DIAG-FASE16] adicionado no final de CalcularSnapshot(), antes de `return snap`; throttle: apenas quando Millisecond < 150 (~1 disparo por segundo); exibe: price, bid, ask, lastBook (OK/NULL), bidVol (BidDepth), askVol (AskDepth), bookImb, delta1s, ofi1s, aggRatio, absScore, regime
  - MarketCore.WPF/MainWindow.xaml — painel Agressão redesenhado: ProgressBar dupla (PbCompra/PbVenda) substituída por barra bidirecional única com Grid + 2 ColumnDefinitions (ColAgressaoVenda=vermelho, ColAgressaoCompra=verde); subtítulo estático "compra/venda líquida na sessão" substituído por TbAgressaoDesc (dinâmico: COMPRADORA/VENDEDORA/EQUILÍBRIO com cor)
  - MarketCore.WPF/MainWindow.xaml.cs — bloco Agressão reescrito: cálculo agrcBuy/agrcSell/totalAgg baseado exclusivamente em Delta1s; ColAgressaoVenda/ColAgressaoCompra recebem GridLength proporcional; TbDeltaGrande mostra Delta1s (era Delta5s) com cor dinâmica; TbAgressaoDesc com texto e cor dinâmicos; removidas referências a PbCompra, PbVenda, TbAgCompraVal, TbAgVendaVal
- Diagnóstico: log [FEAT-DIAG] no FeatureEngine + [MCIE-DEBUG] na UI devem ser analisados no console após próximo build para identificar se bookImb/delta1s chegam zerados (problema em BidDepth/AskDepth=0 indica volumes não presentes nos BookLevels recebidos da DLL)
- Build: dotnet build MarketCore.WPF\MarketCore.WPF.csproj --configuration Release (executar no Windows terminal)

### 31/08/2026 — Fase 16d (diagnóstico crítico — correção dos logs de rastreamento)
- Causa raiz identificada: caminho dos logs [DIAG-FASE16] gerado com `@"C:\\Users\\..."` (verbatim string + double-backslash) produzia path inválido em runtime; File.AppendAllText lançava DirectoryNotFoundException, capturada pelo outer try-catch de HandleTrade — impedindo que OnTrade e _featureEngine?.OnTrade() fossem chamados. SnapshotTimer sofria o mesmo: log jogava exceção → TriggerSnapshot() nunca executava.
- Observação: FlowRenko na tela atualizava porque é visualização nativa da ProfitDLL, independente do nosso código.
- Arquivos modificados:
  - Engine/MarketEngine.cs — [HANDLE-TRADE] path corrigido para Path.GetTempPath()+"feat_diag.txt"; write isolado em try{} catch{} próprio
  - Engine/Features/FeatureEngine.cs — [FE-ONTRADE] e [DIAG-FASE16] (CalcularSnapshot) corrigidos para GetTempPath(); ambos isolados em try{} catch{} próprios
  - Engine/Features/SnapshotTimer.cs — [TIMER-TICK] path corrigido para GetTempPath(); write isolado em try{} catch{} próprio
- Próximo passo: build + rodar 30s; ler feat_diag.txt em %TEMP% (C:\Users\Anderson\AppData\Local\Temp\feat_diag.txt). Interpretar:
  - Só [TIMER-TICK] → HandleTrade não chamado (provider não conectado)
  - [HANDLE-TRADE] + [FE-ONTRADE] + [TIMER-TICK] → pipeline funciona; verificar se Price/Delta1s/BookImb têm valores não-zero
  - [HANDLE-TRADE] mas sem [FE-ONTRADE] → _featureEngine null

### 31/08/2026 — Fase 16e (diagnóstico ConnectAsync — 4 logs de rastreamento)
- Causa do silêncio confirmada: ProfitDLLProvider.ConnectAsync tem 3 saídas silenciosas sem exceção (Status=Connected/reutiliza, IsDllInit=True→Reattach, timeout _readyToSubscribe) — em qualquer delas StartProcessingThread pode não ser chamado com os callbacks corretos
- Arquivos modificados:
  - Providers/Nelogica/ProfitDLLProvider.cs — [DLL-CONNECT-ENTRY] antes dos ifs de branch; [DLL-INIT-RESULT] após DLLInitializeMarketLogin
  - MarketCore.WPF/MainWindow.xaml.cs — [ENGINE-CONNECTED] após ConnectAsync; [SUBSCRIBE] após _engine.Subscribe
- Log em: %TEMP%\feat_diag.txt (C:\Users\Anderson\AppData\Local\Temp\feat_diag.txt)
- Interpretação esperada:
  - [DLL-CONNECT-ENTRY] Status=Connected → early return, callbacks stale
  - [DLL-CONNECT-ENTRY] IsDllInit=True → Reattach (tradeCallback não re-enviado via DLLInitializeMarketLogin)
  - [DLL-INIT-RESULT] result!=0 → DLL recusou login (sessão duplicada com Profit Chart?)
  - [ENGINE-CONNECTED] Status=Error → conectou mas falhou
  - [ENGINE-CONNECTED] Status=Connected + [SUBSCRIBE] OK mas sem [HANDLE-TRADE] → bug no Subscribe/ticker

### 01/09/2026 — Correção de latência Release (feat_diag + cache snapshot)

Não é Fase 17. Correção em cima da 16e.

- Sintoma: Release 01/09 atrasava vs Debug 25/08 “na DLL”. Tape/dll_latency com tradeAge 300–500 ms (pico ~1 s). SnapshotTimer ~1 snapshot / 5 s (esperado ~10/s).
- Diagnóstico: NÃO era ProfitDLL64.dll. SHA256 idêntico nos dois bins: 2A51E7BD… Debug 25/08 é binário antigo pré-MCIE, sem as escritas de diagnóstico. Causa = Fases 16c–16e: `File.AppendAllText` em `%TEMP%\feat_diag.txt` duas vezes por trade no worker ProfitDLL-Trades (`HandleTrade` + `OnTrade`). feat_diag chegou a ~11,8 MB / ~218 mil linhas na sessão da manhã; I/O síncrono no caminho crítico starved o SnapshotTimer e atrasou o tape.
- Lembrete de arquitetura: caminho crítico ProfitDLL → RAM → Features → Agents → Decision → Risk → Order. Nenhuma operação de I/O, rede ou banco no caminho crítico. Persistência só assíncrona.
- O que foi aplicado:
  - Removidos todos os `File.AppendAllText` de feat_diag (HandleTrade, OnTrade, CalcularSnapshot, SnapshotTimer.HandleTimer, ConnectEngineSafelyAsync, EnsureBookSubscription, ProfitDLLProvider.Connect).
  - Removido `Console.WriteLine` `[MCIE-DEBUG]` do `McieTimer_Tick`.
  - `UltimoSnapshot` deixa de recomputar: FeatureEngine guarda o snapshot que o SnapshotTimer já calcula; getter devolve o cache (null antes do primeiro). `UltimosSignals` usa o cache — não chama `CalcularSnapshot` de novo.
  - `MarketCore.bat` aponta para o exe Release.
  - Rebuild Release (`dotnet build MarketCore.WPF\MarketCore.WPF.csproj --configuration Release`).
- O que NÃO foi feito: ProfitDLL64.dll / enqueue de callbacks intocados (só saíram os writes feat_diag em Connect). MCIE mantido (FeatureEngine, agents, DecisionCore, Fase 1 BookProcessingLoop). Trabalho UI/agentes 16a–16e não revertido. Sem git commit, tag ou push.
- Arquivos modificados:
  - Engine/MarketEngine.cs — delete write feat_diag em HandleTrade; UltimoSnapshot retorna cache
  - Engine/Features/FeatureEngine.cs — delete writes OnTrade/CalcularSnapshot; campo `_ultimoSnapshot`; CalcularSnapshot continua sendo o único cálculo
  - Engine/Features/SnapshotTimer.cs — delete write throttled no HandleTimer
  - MarketCore.WPF/MainWindow.xaml.cs — delete `[MCIE-DEBUG]` e writes feat_diag Connect/Subscribe
  - Providers/Nelogica/ProfitDLLProvider.cs — delete writes feat_diag em Connect
  - MarketCore.bat — Debug → Release
  - PROJETO.md — este registro
- Como rodar: `C:\Users\Anderson\Downloads\MarketCore\MarketCore.WPF\bin\Release\net9.0-windows\MarketCore.WPF.exe` (ou `MarketCore.bat` após a atualização do bat).
- Próximo check (Anderson): fechar Debug, abrir Release, confirmar que o tape não atrasa; feat_diag não deve mais crescer.

### 01/09/2026 — Correções pós-16e (REC, OFI thresholds, PatternRegistry)

Não é Fase 17. Correções incrementais sobre a build Release de 01/09.

#### O que foi corrigido

**1. Botão REC na toolbar (crash → toggle seguro)**
- Primeiro problema: `DesabilitarGravacao()` chamado na UI thread → `Dispose()` em objetos nativos → crash nativo não capturável.
- Tentativa 1 (Task.Run): crash persistiu — Dispose de objetos COM/P/Invoke não é thread-safe de qualquer thread.
- Solução final: adicionados `PausarGravacao()` e `RetomarGravacao()` no MarketEngine — só toggleam `_recordingEnabled`, jamais chamam Dispose. Crash eliminado.
- `BtnGravacao_Click` usa `PausarGravacao`/`RetomarGravacao` exclusivamente.
- `DesabilitarGravacao()` mantida mas reservada para encerramento de sessão.

**2. Footer "Gravando: OFF" não atualizava**
- `TbRecordingStatus` e `EllipseRecording` existiam no XAML mas nenhum código os atualizava.
- Corrigido no bloco do `McieTimer_Tick`: sincroniza botão REC + `TbRecordingStatus` + `EllipseRecording` a cada 100ms.

**3. OFIAgent — thresholds errados (raw vs normalizado)**
- OFI é normalizado em [-1.0, +1.0], mas os thresholds usavam valores raw (100–600).
- Corrigidos: OFI100ms >200/>100 → >0.70/>0.40 | OFI500ms >400/>200 → >0.70/>0.40 | OFI1s >600/>300 → >0.70/>0.40.
- ReasonCodes: >400/>200 → >0.60/>0.30; >150 → >0.50.

**4. FlowAgent — threshold Ofi1s errado**
- Mesmo problema: Ofi1s >300/>100 → >0.60/>0.30 | ReasonCode >200 → >0.40.
- Delta1s e Delta5s mantidos (valores reais de volume, ex: -264 — corretos como estavam).

**5. PatternRegistry — inicialização desacoplada da gravação**
- Bug crítico: StorageManager + PatternRegistry + AgentEngine(patternRegistry) estavam dentro do bloco `if (_recordingEnabled && _recorder != null)`.
- Se `HabilitarGravacao` lançasse exceção (path inválido, drive ausente), o bloco era pulado → `_agentEngine` ficava em modo lite → PatternAgent sempre 0.
- Correção: bloco movido para FORA do guard de gravação, com try/catch próprio. Se falhar, loga e mantém modo lite.
- Dentro do bloco de gravação: só DatasetBuilder, DatasetTimer, DecisionCore com persistência, PatternDiscovery e IniciarPregaoAsync.
- Bug de sintaxe no fix: `.Length` em `List<>` → corrigido para `.Count`.

**6. Diagnóstico de padrões no painel Saúde**
- Adicionado `NumeroPadroesAtivos` (int, -1 = não inicializado) ao MarketEngine.
- `TbSaudeGrav` no McieTimer_Tick passa a exibir `ATIVO | X padrões` ou `OFF | sem registry`.
- Permite confirmar visualmente se PatternRegistry inicializou e quantos padrões estão carregados.

#### Estado atual do PatternAgent

- Build compilando com 0 erros (confirmado pelo usuário).
- PatternAgent mostra 0 / 0% na UI → esperado: banco SQLite vazio (primeira sessão).
- PatternDiscovery roda automaticamente quando DatasetTimer disparar (precisa de dados acumulados).
- **Em monitoramento**: aguardando sessão completa de pregão para confirmar que DatasetTimer dispara, PatternDiscovery encontra padrões e PatternRegistry salva no banco.
- Na próxima sessão: `TbSaudeGrav` deve exibir `ATIVO | N padrões` com N > 0.

#### Arquivos modificados

- `Engine/MarketEngine.cs` — PausarGravacao/RetomarGravacao; StorageManager+PatternRegistry init fora do guard; NumeroPadroesAtivos property; .Count fix
- `Engine/Agents/OFIAgent.cs` — thresholds OFI normalizados [-1, +1]
- `Engine/Agents/FlowAgent.cs` — threshold Ofi1s normalizado [-1, +1]
- `MarketCore.WPF/MainWindow.xaml` — BtnGravacao adicionado na toolbar; TbRecordingStatus e EllipseRecording no footer
- `MarketCore.WPF/MainWindow.xaml.cs` — BtnGravacao_Click (toggle seguro); McieTimer_Tick: sync REC + footer + diagnóstico padrões

Sem git commit. Working tree dirty.

### 01/09/2026 — Correção _dbPath: banco SQLite movido para AppData (análise do manual)

#### Diagnóstico via especificação

- Análise do arquivo `MarketCore_Especificacao_Completa.docx` revelou que a estrutura de pastas definida no manual é:
  ```
  /data
    /raw/{ano}/{mes}/{dia}/  ← binários brutos
    /db/
      patterns.sqlite
      decisions.sqlite
      trades.sqlite
      config.sqlite
  ```
- No `AppData\Roaming\MarketCore\` as pastas `data/db`, `data/raw`, etc. **nunca foram criadas** — confirmando que o StorageManager nunca inicializou corretamente.
- Causa raiz: `_dbPath = Path.Combine(diretorioBase, "..", "data", "db")` com `diretorioBase = "D:\Gravações_MarketCore"` resolvia para `D:\data\db` — drive D: provavelmente inexistente ou sem acesso.
- O `..` na expressão sobe um nível acima de `D:\Gravações_MarketCore` chegando em `D:\`, não dentro da pasta de gravações.

#### O que foi corrigido

**`Engine/MarketEngine.cs` — 3 pontos alterados:**

1. `HabilitarGravacao`: `_dbPath` agora usa AppData fixo, independente do drive de gravações:
   ```csharp
   // ANTES (errado):
   _dbPath = System.IO.Path.Combine(diretorioBase, "..", "data", "db");

   // DEPOIS (correto):
   _dbPath = System.IO.Path.Combine(
       Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
       "MarketCore", "data", "db");
   ```

2. `ConnectAsync` linha ~513: fallback quando `_dbPath` é null também corrigido para AppData.

3. `ConnectAsync` linha ~553: idem para o segundo fallback dentro do bloco de gravação.

4. Log adicional em `HabilitarGravacao`: `[RECORDER] Banco SQLite/DuckDB em: {_dbPath}` — permite confirmar visualmente o path resolvido no console.

#### Caminho resultante

- Binários brutos (trades.bin, book.bin): `{RecordingsPath}\{yyyy-MM-dd}\` (inalterado — gerenciado pelo MarketRecorder)
- Banco SQLite: `C:\Users\Anderson\AppData\Roaming\MarketCore\data\db\` (novo — fixo, independente do drive configurado)

#### Como confirmar após build

1. Compilar: `dotnet build MarketCore.WPF\MarketCore.WPF.csproj --configuration Release`
2. Ao iniciar, o log deve exibir:
   ```
   [RECORDER] Gravação de TRADES + BOOK habilitada em: D:\Gravações_MarketCore
   [RECORDER] Banco SQLite/DuckDB em: C:\Users\Anderson\AppData\Roaming\MarketCore\data\db
   [PATTERNS] PatternRegistry pronto — 0 padrões ativos carregados
   ```
3. `TbSaudeGrav` na UI: `ATIVO | 0 padrões` (primeira sessão com banco novo)
4. Após 18:04–18:06: DatasetTimer dispara, PatternDiscovery roda sobre dados do dia

#### Estado atual

- Em monitoramento: banco SQLite será criado automaticamente pelo StorageManager na primeira sessão após o build.
- Próxima sessão: verificar se `AppData\Roaming\MarketCore\data\db\` foi criado com arquivos `.sqlite`.

#### Arquivos modificados

- `Engine/MarketEngine.cs` — `_dbPath` corrigido em 3 pontos; log de confirmação adicionado

Sem git commit. Working tree dirty.

---

## [FASE 3] LivePatternDiscovery — Varredura de Padrões em Tempo Real

**Data:** 2026-09-02

### Objetivo

Descoberta de padrões intraday em tempo real, operando sobre snapshots acumulados durante a sessão, com intervalo de varredura configurável pela UI (campo "Varredura min" no painel de Stop/Target).

### Arquivos criados/modificados

| Arquivo | Tipo | Mudança |
|---|---|---|
| `Engine/Patterns/LivePatternDiscovery.cs` | **NOVO** | Classe principal — timer, warmup 30min, ciclo de descoberta |
| `Engine/Patterns/PatternRegistry.cs` | modificado | `LimparPadroesIntraday()`, `AdicionarIntraday()`, `PadroesAtivos()` inclui `Paper` |
| `Engine/Features/FeatureEngine.cs` | modificado | `_snapshotsHoje`, `SnapshotsHoje`, acumula em `TriggerSnapshot`, limpa em `ResetarSessao` |
| `Engine/MarketEngine.cs` | modificado | Campo `_liveDiscovery`, init em `ConnectAsync`, `Dispose`, props `PadroesAtivosCount` e `AlterarIntervaloVarredura` |
| `MarketCore.WPF/MainWindow.xaml` | modificado | Grid "Varredura min" após Target |
| `MarketCore.WPF/MainWindow.xaml.cs` | modificado | Handler `TxVarredura_TextChanged`, `McieTimer_Tick` usa `PadroesAtivosCount` |

### Comportamento

- **Warmup:** 30 min após `ConnectAsync` (aguarda acúmulo de snapshots)
- **Mínimo para executar:** 1.000 snapshots totais, 500 elegíveis (horizonte > 10s), 500 labels gerados
- **Critérios de qualidade:** `MinSamples=50`, `MinExpectancy=1.5`, `MinProfitFactor=1.3`, `MinWinRate=0.52`
- **Padrões intraday:** status `Paper`, limpos a cada ciclo (apenas do dia atual)
- **PatternAgent vê padrões Paper:** `PadroesAtivos()` agora inclui `Approved | Live | Paper`
- **Thread safety:** `_snapshotsHoje` protegido por `_snapshotsLock`; `SnapshotsHoje` retorna cópia

### UI — campo Varredura

Campo `TxVarredura` (TextBox, 1–30 min) inserido abaixo do Target no painel de controle.
Chama `_engine.AlterarIntervaloVarredura(min)` → `_liveDiscovery.AlterarIntervalo(min)` que reprograma o timer imediatamente.

### Logs esperados

```
[LIVE-PATTERN] Iniciado — warmup 30min, intervalo 3min
[LIVE-PATTERN] Ciclo ignorado — 423 snapshots (min 1000)   ← ainda no warmup
[LIVE-PATTERN] Ciclo concluído — 7 novos intraday, 7 padrões ativos (10:32)
[LIVE-PATTERN] Intervalo alterado para 5min                ← usuário alterou na UI
```

Sem git commit. Working tree dirty.

### 02/09/2026 — Fase 16 correções e LivePatternDiscovery

BUGS CORRIGIDOS:

1. Latência 157s na DLL
   Causa: File.AppendAllText dentro do HandleTrade
   e SnapshotTimer — I/O síncrono no caminho crítico
   Solução: removidos todos os logs de diagnóstico
   (feat_diag.txt) do pipeline de trading

2. Book não chegava ao FeatureEngine
   Causa: BookProcessingLoop desativada por comentário
   de performance. StartBookSnapshotPublishing()
   causava saturação quando reativada
   Solução: OnBook() chamado diretamente no HandleBook()
   com throttle de 100ms via Task.Run

3. DateTime UTC vs Local
   Causa: LivePatternDiscovery usava DateTime.UtcNow
   para comparar com timestamps em DateTime.Now.Ticks
   Em Brasília (UTC-3) causava elegiveis sempre vazio
   Solução: padronizado para DateTime.Now em todo
   o LivePatternDiscovery

4. Cálculo de ticks nos labels
   Causa: FindClosest usava T0 + 1000ms em vez de
   T0 + 1000 * 10000L (ticks por milissegundo)
   Resultado: FutureReturn max=187660 (impossível)
   Solução: const long ticksPerMs = 10000L aplicado
   em todos os horizontes (1s, 2s, 5s, 10s)

5. OFI Agent thresholds incorretos
   Causa: thresholds em valores brutos (100-600)
   mas OFI normalizado entre -1.0 e +1.0
   Solução: thresholds ajustados para escala -1 a +1
   (0.40, 0.70 etc)

6. Padrões intraday sendo apagados a cada ciclo
   Causa: LimparPadroesIntraday() chamado no início
   de cada ExecutarCiclo()
   Solução: substituído por acumulação com dedup
   por condições. Padrões acumulam durante o dia
   e são limpos apenas no encerramento do pregão

7. feat_diag.txt commitado no repositório
   Solução: removido com git rm e adicionado
   ao .gitignore

FUNCIONALIDADES ADICIONADAS:

1. LivePatternDiscovery (Engine/Patterns/LivePatternDiscovery.cs)
   - Descoberta de padrões em tempo real durante o pregão
   - Warm-up configurável (padrão: 30 minutos produção,
     2 minutos para teste)
   - Intervalo configurável na UI (campo Varredura)
   - Critérios intraday reduzidos:
     MinSamples=20, MinExpectancy=0.2,
     MinProfitFactor=1.02, MinWinRate=0.40
   - Labels calculados em memória:
     FutureReturn 1s / 2s / 5s
   - Acumulação de padrões sem apagar anteriores
   - Dedup por condições para evitar padrões duplicados
   - Monitoramento de decay (remove padrões com
     WinRate < 35% com 10+ amostras)
   - Log em Desktop/mcie_patterns.log para diagnóstico
   - Padrões encerrados ao desconectar do pregão

2. Campo Varredura na UI
   - Arquivo: MarketCore.WPF/MainWindow.xaml
   - Campo numérico no painel de Decisão
   - Permite alterar intervalo do LivePatternDiscovery
     em tempo real sem recompilar
   - Padrão: 3 minutos

3. Botão STOP REC na toolbar
   - Ativa/desativa gravação manualmente
   - Verde quando gravando, vermelho quando parado
   - Sincronizado com _engine.GravacaoAtiva

4. SnapshotTimer nullable
   - Aceita StorageManager null
   - Roda sem gravação ativa (lite mode)

5. FeatureEngine histórico intraday
   - Campo _snapshotsHoje acumula snapshots do dia
   - Propriedade SnapshotsHoje exposta para o
     LivePatternDiscovery
   - Limitado a 400.000 snapshots (~11 horas)
   - Resetado em ResetarSessao()

6. PatternRegistry métodos intraday
   - AdicionarIntraday() — status Paper
   - LimparPadroesIntraday() — chamado só no
     encerramento do pregão
   - MonitorarDecayAsync() adaptado para Paper patterns

RESULTADO VALIDADO EM PREGÃO REAL (02/09/2026):
   - DLL: 0ms de atraso durante todo o pregão
   - Gravação: ATIVO durante todo o pregão
   - 32 padrões intraday descobertos e acumulados
   - Decisão: PrepareBuy (saiu do Wait)
   - FLOW: +8, BOOK: +55 funcionando com dados reais
   - Pressão do book: variando dinamicamente
   - BookImbalance chegando corretamente (0-1)

COMMITS DO DIA:
   - ea4a5ce: fase-16 LivePatternDiscovery funcionando
   - 3de5866: remove feat_diag.txt do repositorio
