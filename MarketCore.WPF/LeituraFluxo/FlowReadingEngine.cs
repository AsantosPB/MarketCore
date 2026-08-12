using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketCore.WPF.LeituraFluxo
{
    /// <summary>
    /// Motor em memória da "Leitura de Fluxo": recebe cada <c>TradeEvent</c> ao vivo (mesmo fluxo que já
    /// alimenta o <c>MarketDataManager</c>/Postgres em <c>MainWindow.Engine_OnTrade</c>) e mantém, por
    /// corretora, os dados necessários para os 3 padrões de execução acordados com o usuário:
    /// Segundo fixo, Intervalo regular e Impacto no preço. Também mantém um buffer global de negócios
    /// usado pelas janelas de "agressão por quantidade executada" e pelo mini-tape da janela.
    ///
    /// Não grava nada em disco — a persistência em Postgres (trades_intraday) já é feita
    /// independentemente pelo <c>MarketDataManager</c>. Isto é apenas um segundo consumidor,
    /// só em memória, do mesmo evento de trade.
    ///
    /// Thread-safety: <see cref="OnTrade"/> é chamado pela thread do motor de mercado; os métodos
    /// de leitura (Get*) são chamados pela UI (via DispatcherTimer). Tudo protegido por um único lock;
    /// as seções críticas são O(1) em escrita e limitadas (poucos milhares de amostras) em leitura.
    /// </summary>
    public sealed class FlowReadingEngine
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, List<FlowTradeSample>> _byBroker = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<FlowPatternMatch>> _historyByBroker = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<FlowTradeSample> _global = new();

        // 300.000 era baseado numa estimativa errada de volume diário. Você reportou 6+ milhões de negócios
        // no dia — bem maior. Subi para 15.000.000, com folga confortável mesmo em pregão recorde. Cada
        // amostra (struct pequena) é guardada 2x (uma vez em _global, uma vez na lista da própria corretora),
        // ~48 bytes cada cópia: no pior caso teórico (os 15M inteiros preenchidos) isso é ~1,4 GB de RAM — mas
        // esse pior caso só existiria se TODOS os negócios do dia fossem de uma única corretora, o que nunca
        // acontece na prática (6 milhões se distribuem entre várias corretoras, então cada lista por
        // corretora fica bem menor que o total global). Tranquilo pra um PC atual.
        //
        // Sobre performance: os detectores (Segundo Fixo, Intervalo Regular) rodam a cada 1s e variam com o
        // tamanho da lista DAQUELA corretora específica (não do total global) — como nenhuma corretora
        // sozinha chega perto de 6 milhões, o custo por tick continua baixo. Se algum dia uma corretora muito
        // concentrada ficar pesada e a tela travar visivelmente perto do fechamento, o próximo passo certo é
        // trocar os detectores de "recalcula tudo a cada tick" para "mantém contador incremental" — não fiz
        // isso agora pra não mexer em mais coisa do que o necessário sem ver o efeito na prática primeiro.
        //
        // Isso cobre o dia inteiro DENTRO de uma mesma execução do programa; não há reset automático de
        // virada de dia, então continua valendo reiniciar o MarketCore a cada novo pregão (como já é o fluxo
        // hoje).
        private const int MaxSamplesPerBroker = 15_000_000;
        private const int MaxGlobalSamples = 15_000_000;
        private const int MaxHistoryPerBroker = 5;

        private const int MinSamplesForDetection = 20;
        private const double EventGroupingSeconds = 2.0;
        private const int MinEventsForInterval = 6;
        private const double ImpactWindowSeconds = 20.0;
        private const double ImpactMultiplierThreshold = 1.5;

        // Rajada de Volume: "vinha executando aos poucos e de repente solta um volume bem maior" — compara
        // o volume executado numa janela curta (BurstWindowSeconds) com o volume médio da PRÓPRIA corretora
        // na mesma duração de janela ao longo do dia (linha de base própria, não um número fixo pra todo
        // mundo). BurstMultiplierThreshold=5 exige que a rajada seja pelo menos 5x o normal dela mesma;
        // MinBurstVolume é um piso absoluto pra não contar como "rajada" um pico pequeno só porque a
        // corretora é normalmente muito quieta.
        private const double BurstWindowSeconds = 30.0;
        private const double BurstMultiplierThreshold = 5.0;
        private const int MinBurstVolume = 500;

        // Antes, Segundo Fixo/Intervalo Regular eram calculados sobre o dia inteiro acumulado — então, uma
        // vez que um segundo "vencia" a disputa (ex.: :45, por ter sido dominante lá pelas 10h), ele continuava
        // "vencendo" o recálculo pro resto do dia mesmo que a corretora tivesse parado de executar nesse
        // segundo há horas, porque a contagem histórica já acumulada nunca diminui. Resultado: o card ficava
        // sendo "reconfirmado" a cada tick (LastConfirmedAt sempre "agora") citando como prova só execuções
        // de 3h atrás — parecia estar acontecendo agora, mas não estava. RecencyWindowMinutes é o corte: só
        // conta como padrão ATIVO se a execução mais recente que sustenta ele aconteceu dentro dessa janela;
        // caso contrário o padrão para de ser encontrado (e acaba expirando do histórico, ver MergeIntoHistory).
        // Reduzido de 60 para 7 min a pedido do usuário: prioriza cortar rápido um padrão que parou de
        // ocorrer, mesmo sabendo do efeito colateral em Intervalo Regular — um padrão desse tipo com
        // intervalo real maior que 7 min (o campo aceita até 3600s/1h entre execuções) vai "piscar"
        // (expira e reaparece) entre uma confirmação e outra, já que o gap normal dele pode passar dos 7 min.
        private const double RecencyWindowMinutes = 7.0;

        private volatile string _backfillStatus = "";

        /// <summary>Texto de status do carregamento do histórico de hoje (ex.: "Carregando histórico de hoje… 42.318
        /// negócios"). Vazio quando não há carregamento em andamento. Lido pela UI a cada tick.</summary>
        public string BackfillStatus => _backfillStatus;

        /// <summary>Chamado pelo backfill (<c>FlowReadingHistorySink</c>) para reportar progresso à UI.</summary>
        public void SetBackfillStatus(string status) => _backfillStatus = status ?? "";

        /// <summary>Alimenta o motor com um negócio ao vivo. Seguro para chamar de fora da UI thread.</summary>
        public void OnTrade(string? broker, DateTime time, decimal price, int volume, bool isBuy)
        {
            if (volume <= 0 || price <= 0)
                return;

            string key = string.IsNullOrWhiteSpace(broker) ? "N/D" : broker.Trim().ToUpperInvariant();
            var sample = new FlowTradeSample(time, price, volume, isBuy, key);

            lock (_sync)
            {
                InsertSorted(_global, sample);
                if (_global.Count > MaxGlobalSamples)
                    _global.RemoveRange(0, _global.Count - MaxGlobalSamples);

                if (!_byBroker.TryGetValue(key, out var list))
                {
                    list = new List<FlowTradeSample>();
                    _byBroker[key] = list;
                }
                InsertSorted(list, sample);
                if (list.Count > MaxSamplesPerBroker)
                    list.RemoveRange(0, list.Count - MaxSamplesPerBroker);
            }
        }

        /// <summary>Insere mantendo a lista sempre ordenada por Time — vários detectores (Rajada de Volume,
        /// Intervalo Regular) assumem ordem cronológica estrita pra calcular janelas/deltas corretamente.
        /// Um simples Add no fim quebrava essa premissa: o backfill "hoje desde a abertura" roda em paralelo
        /// com a captura ao vivo (fire-and-forget, ver StartTodayHistoryBackfillIfNeededAsync), então um
        /// negócio ao vivo (recente) podia ser inserido ANTES de um negócio histórico mais antigo que o
        /// backfill ainda estava processando — resultado: a lista ficava com timestamps fora de ordem, e a
        /// janela deslizante da Rajada de Volume (que soma volume "desde o início até 30s depois" avançando
        /// dois ponteiros) explodia pra somar um trecho do dia inteiro sempre que topava com essa inversão,
        /// gerando números como "1 milhão de lotes em 30s". Caminho comum (negócio ao vivo, o mais recente de
        /// todos) continua O(1) — só cai pra busca binária + Insert (O(n) no pior caso) na rara inserção fora
        /// de ordem durante a janela de corrida do backfill.</summary>
        private static void InsertSorted(List<FlowTradeSample> list, FlowTradeSample sample)
        {
            if (list.Count == 0 || sample.Time >= list[^1].Time)
            {
                // O Postgres já ignora negócio duplicado (trava única por timestamp+preço+qtd+lado); o motor
                // em memória nunca teve essa proteção. Como o backfill roda em paralelo com a captura ao vivo
                // na inicialização, o MESMO negócio pode chegar pelos dois caminhos — sem checar isso aqui,
                // ele conta 2x no volume (foi o que inflou a Rajada de Volume pra números impossíveis, tipo
                // 18 mil lotes em 30s: nenhum negócio de verdade, só o mesmo contado duas vezes).
                if (list.Count > 0 && IsDuplicate(list[^1], sample))
                    return;
                list.Add(sample);
                return;
            }

            int lo = 0, hi = list.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (list[mid].Time <= sample.Time) lo = mid + 1;
                else hi = mid;
            }

            if ((lo > 0 && IsDuplicate(list[lo - 1], sample)) || (lo < list.Count && IsDuplicate(list[lo], sample)))
                return;

            list.Insert(lo, sample);
        }

        /// <summary>Mesmo negócio, contado duas vezes: mesmo horário, preço, quantidade e lado. Não usa
        /// broker no comparativo porque a checagem já acontece dentro da lista de UMA corretora/lado só.</summary>
        private static bool IsDuplicate(FlowTradeSample a, FlowTradeSample b) =>
            a.Time == b.Time && a.Price == b.Price && a.Volume == b.Volume && a.IsBuy == b.IsBuy;

        /// <summary>Roda os detectores para uma corretora e devolve uma foto pronta para bind.</summary>
        public BrokerFlowSnapshot GetBrokerSnapshot(string? broker)
        {
            if (string.IsNullOrWhiteSpace(broker))
                return BrokerFlowSnapshot.Empty(broker ?? "");

            string key = broker.Trim().ToUpperInvariant();

            lock (_sync)
            {
                if (!_byBroker.TryGetValue(key, out var list) || list.Count == 0)
                    return BrokerFlowSnapshot.Empty(key);

                long buy = 0, sell = 0;
                foreach (var s in list)
                {
                    if (s.IsBuy) buy += s.Volume; else sell += s.Volume;
                }

                var found = DetectPatterns(list, _global);
                // Chama sempre, mesmo com found vazio: MergeIntoHistory também expira do histórico os
                // padrões que pararam de ser reconfirmados (ver RecencyWindowMinutes). Se só chamasse quando
                // found.Count > 0, uma corretora que parou de ter QUALQUER padrão ativo nunca teria seu
                // histórico velho limpo — a expiração precisa rodar mesmo sem nada novo pra fundir.
                MergeIntoHistory(key, found, DateTime.Now);

                var historySnapshot = _historyByBroker.TryGetValue(key, out var hist)
                    ? CloneReversed(hist)
                    : new List<FlowPatternMatch>();

                return new BrokerFlowSnapshot
                {
                    Broker = key,
                    BuyVolume = buy,
                    SellVolume = sell,
                    TradeCount = list.Count,
                    LastPatterns = historySnapshot
                };
            }
        }

        /// <summary>Calcula a janela de agressão por quantidade executada (últimos N lotes negociados, qualquer corretora).</summary>
        public AggressionWindowResult GetAggressionWindow(int targetQty)
        {
            if (targetQty <= 0) targetQty = 1000;
            lock (_sync)
            {
                return ComputeWindow(_global, targetQty);
            }
        }

        /// <summary>Últimas N linhas para o mini-tape "Times &amp; Trades — captura ao vivo".</summary>
        public IReadOnlyList<FlowTapeRow> GetRecentTape(int count)
        {
            lock (_sync)
            {
                int take = Math.Min(count, _global.Count);
                var result = new List<FlowTapeRow>(take);
                for (int i = _global.Count - 1; i >= _global.Count - take; i--)
                {
                    var s = _global[i];
                    result.Add(new FlowTapeRow
                    {
                        Time = s.Time,
                        Price = s.Price,
                        Volume = s.Volume,
                        Broker = s.Broker,
                        IsBuy = s.IsBuy
                    });
                }
                return result;
            }
        }

        /// <summary>Corretoras com mais negócios observados nesta sessão — útil para pré-selecionar as colunas.</summary>
        public IReadOnlyList<string> GetTopActiveBrokers(int n)
        {
            lock (_sync)
            {
                return _byBroker
                    .OrderByDescending(kv => kv.Value.Count)
                    .Take(n)
                    .Select(kv => kv.Key)
                    .ToList();
            }
        }

        public int TotalTradeCount
        {
            get { lock (_sync) return _global.Count; }
        }

        // ══════════════════════════════════════════════════════════
        // Detectores
        // ══════════════════════════════════════════════════════════

        private static List<FlowPatternMatch> DetectPatterns(List<FlowTradeSample> samples, List<FlowTradeSample> global)
        {
            var found = new List<FlowPatternMatch>();
            // Compra e venda são detectadas separadamente: uma corretora pode ter um comportamento recorrente
            // só na ponta compradora (ou só na vendedora), e misturar as duas amostras mascarava/diluía padrões
            // reais além de deixar o usuário sem saber qual lado está executando o padrão mostrado na tela.
            DetectForSide(samples, global, isBuy: true, found);
            DetectForSide(samples, global, isBuy: false, found);
            return found;
        }

        private static void DetectForSide(List<FlowTradeSample> samples, List<FlowTradeSample> global, bool isBuy, List<FlowPatternMatch> found)
        {
            var sideSamples = samples.Where(s => s.IsBuy == isBuy).ToList();
            string sidePrefix = isBuy ? "C" : "V";

            var segundo = DetectSegundoFixo(sideSamples);
            if (segundo != null)
            {
                segundo.IsBuySide = isBuy;
                segundo.BucketKey = $"{sidePrefix}:{segundo.BucketKey}";
                found.Add(segundo);
            }

            var ciclo = DetectCicloFixo(sideSamples);
            if (ciclo != null)
            {
                ciclo.IsBuySide = isBuy;
                ciclo.BucketKey = $"{sidePrefix}:{ciclo.BucketKey}";
                found.Add(ciclo);
            }

            var rajada = DetectRajadaVolume(sideSamples);
            if (rajada != null)
            {
                rajada.IsBuySide = isBuy;
                rajada.BucketKey = $"{sidePrefix}:{rajada.BucketKey}";
                found.Add(rajada);
            }

            var events = BuildExecutionEvents(sideSamples);
            var intervalo = DetectIntervaloRegular(events, sideSamples);
            if (intervalo != null)
            {
                intervalo.IsBuySide = isBuy;
                intervalo.BucketKey = $"{sidePrefix}:{intervalo.BucketKey}";
                found.Add(intervalo);
            }

            string? parentKey = intervalo?.BucketKey ?? segundo?.BucketKey;
            if (parentKey != null)
            {
                var impacto = DetectImpactoPreco(events, global, parentKey);
                if (impacto != null)
                {
                    impacto.IsBuySide = isBuy;
                    found.Add(impacto);
                }
            }
        }

        /// <summary>Padrão 1 — corretora concentra execuções sempre no mesmo segundo do minuto.</summary>
        private static FlowPatternMatch? DetectSegundoFixo(List<FlowTradeSample> samples)
        {
            if (samples.Count < MinSamplesForDetection)
                return null;

            var counts = new int[60];
            foreach (var s in samples)
                counts[s.Time.Second]++;

            int total = samples.Count;
            int bestSecond = 0, bestCount = 0;
            for (int sec = 0; sec < 60; sec++)
            {
                if (counts[sec] > bestCount)
                {
                    bestCount = counts[sec];
                    bestSecond = sec;
                }
            }

            double expected = total / 60.0;
            // Reduzido de 3x para 2x o esperado ao acaso — a pedido do usuário, pra pegar padrões reais que
            // ainda são estatisticamente fortes (o dobro do normal num único segundo não é coincidência),
            // mas que a barra antiga (3x) deixava passar batido. A confiança exibida (abaixo) já reflete
            // esse afrouxamento: um caso "raspando" o novo mínimo aparece com confiança baixa (~50%), um caso
            // muito concentrado continua chegando perto de 97% — o usuário vê a força real de cada achado.
            if (bestCount < 5 || bestCount < expected * 2.0)
                return null;

            double ratio = bestCount / Math.Max(expected, 0.01);
            double confidence = Math.Clamp(35 + ratio * 7.0, 50, 97);

            // Até 5 OCORRÊNCIAS (não negócios crus) desse segundo, mais recentes primeiro — cada uma já com o
            // volume somado de todos os fills que caíram naquele minuto/segundo, não só o do primeiro negócio.
            var examples = BuildOccurrenceExamples(
                samples,
                s => s.Time.Second == bestSecond,
                t => new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0).Ticks);

            // Corte de atualidade: se a execução mais recente que sustenta esse segundo já passou da janela
            // de recência, a corretora parou de fazer isso — não é "padrão acontecendo agora", é passado.
            if (examples.Count == 0 || (DateTime.Now - examples[0].Time).TotalMinutes > RecencyWindowMinutes)
                return null;

            DateTime nextExpectedAt = PredictNextPhase(bestSecond, cicloSegundos: 60);

            return new FlowPatternMatch
            {
                Type = FlowPatternType.SegundoFixo,
                BucketKey = $"seg:{bestSecond}",
                ConfidencePct = confidence,
                Examples = examples,
                NextExpectedAt = nextExpectedAt,
                ExpectedVolume = AverageVolume(examples),
                Detail = $"Executa recorrentemente no segundo :{bestSecond:00} de cada minuto — " +
                          $"{bestCount} de {total} negócios ({bestCount * 100.0 / total:0.0}%)."
            };
        }

        /// <summary>Média de lotes das execuções de exemplo — usada como estimativa de tamanho da próxima
        /// execução prevista (mostrada no resumo "Próximas Execuções Previstas").</summary>
        private static int? AverageVolume(List<FlowPatternExample> examples) =>
            examples.Count > 0 ? (int)Math.Round(examples.Average(e => e.Volume)) : null;

        /// <summary>Agrupa as amostras que batem no padrão por OCORRÊNCIA (ex.: mesmo minuto pro Segundo Fixo,
        /// mesmo ciclo pro Ciclo Fixo) e soma o volume de cada ocorrência inteira — uma execução recorrente
        /// quase sempre tem mais de um negócio (fills parciais no mesmo instante), então o volume esperado da
        /// PRÓXIMA execução precisa refletir a soma de uma ocorrência completa, não o tamanho de um negócio
        /// isolado dentro dela (que é bem menor e some enganosamente pequeno no resumo).</summary>
        private static List<FlowPatternExample> BuildOccurrenceExamples(
            List<FlowTradeSample> samples, Func<FlowTradeSample, bool> matches, Func<DateTime, long> occurrenceKey, int maxOccurrences = 5)
        {
            var result = new List<FlowPatternExample>();
            long? currentKey = null;
            DateTime occTime = default;
            decimal occPrice = 0;
            int occVolume = 0;

            for (int i = samples.Count - 1; i >= 0 && result.Count < maxOccurrences; i--)
            {
                if (!matches(samples[i])) continue;
                long key = occurrenceKey(samples[i].Time);

                if (currentKey == null)
                {
                    currentKey = key;
                    occTime = samples[i].Time;
                    occPrice = samples[i].Price;
                    occVolume = samples[i].Volume;
                }
                else if (key == currentKey)
                {
                    occVolume += samples[i].Volume;
                }
                else
                {
                    result.Add(new FlowPatternExample(occTime, occPrice, occVolume));
                    currentKey = key;
                    occTime = samples[i].Time;
                    occPrice = samples[i].Price;
                    occVolume = samples[i].Volume;
                }
            }
            if (currentKey != null)
                result.Add(new FlowPatternExample(occTime, occPrice, occVolume));

            return result;
        }

        /// <summary>Prevê o próximo horário em que a fase <paramref name="targetPhaseSeconds"/> (segundos
        /// desde o início do ciclo) vai se repetir, a partir de agora, num ciclo de <paramref
        /// name="cicloSegundos"/> segundos. Usado tanto pelo Segundo Fixo (ciclo de 60s) quanto pelo Ciclo
        /// Fixo (ciclos maiores) — é a mesma matemática de fase, só muda o tamanho do ciclo.</summary>
        private static DateTime PredictNextPhase(double targetPhaseSeconds, int cicloSegundos)
        {
            DateTime now = DateTime.Now;
            double currentPhase = now.TimeOfDay.TotalSeconds % cicloSegundos;
            double secondsUntilNext = ((targetPhaseSeconds - currentPhase) % cicloSegundos + cicloSegundos) % cicloSegundos;
            return now.AddSeconds(secondsUntilNext);
        }

        /// <summary>Ciclos (em segundos) testados pelo Ciclo Fixo: 1,5 / 2 / 3 / 4 / 5 / 10 minutos. Cobre os
        /// ritmos "redondos" mais comuns de robôs de execução. Um ciclo de 60s não entra aqui de propósito —
        /// isso já é o próprio Segundo Fixo.</summary>
        private static readonly int[] _cicloCandidatesSeconds = { 90, 120, 180, 240, 300, 600 };

        /// <summary>Padrão "Segundo Fixo" generalizado para ciclos maiores que 1 minuto (ex.: a cada 3 minutos).
        /// Olha a FASE de cada execução dentro do ciclo (segundos desde a meia-noite, módulo o tamanho do
        /// ciclo) em vez da distância crua até a próxima execução — por isso, ao contrário do Intervalo
        /// Regular, aguenta uma ordem picotada em vários disparos dentro da mesma "rodada" sem que o ruído
        /// interno do disparo quebre a detecção do ritmo de fundo.</summary>
        private static FlowPatternMatch? DetectCicloFixo(List<FlowTradeSample> samples)
        {
            if (samples.Count < MinSamplesForDetection)
                return null;

            int total = samples.Count;
            FlowPatternMatch? best = null;
            double bestRatio = 0;

            foreach (int cicloSegundos in _cicloCandidatesSeconds)
            {
                const int binWidthSeconds = 3; // mesma granularidade usada nos exemplos/segundo fixo
                int numBins = cicloSegundos / binWidthSeconds;
                var counts = new int[numBins];
                foreach (var s in samples)
                {
                    int offset = (int)s.Time.TimeOfDay.TotalSeconds % cicloSegundos;
                    counts[offset / binWidthSeconds]++;
                }

                int bestBin = 0, bestCount = 0;
                for (int b = 0; b < numBins; b++)
                {
                    if (counts[b] > bestCount)
                    {
                        bestCount = counts[b];
                        bestBin = b;
                    }
                }

                double expected = (double)total / numBins;
                if (bestCount < 5 || bestCount < expected * 2.0)
                    continue; // esse ciclo não mostrou concentração — tenta o próximo candidato

                double ratio = bestCount / Math.Max(expected, 0.01);
                if (ratio <= bestRatio)
                    continue; // já achamos um ciclo candidato mais forte que este — fica com o melhor

                // Até 5 OCORRÊNCIAS do ciclo (não negócios crus), cada uma com o volume somado de todos os
                // fills que caíram naquela mesma passagem do ciclo — mesmo raciocínio do Segundo Fixo.
                int cicloSegundosLocal = cicloSegundos; // captura por valor pra usar dentro das lambdas abaixo
                var examples = BuildOccurrenceExamples(
                    samples,
                    s => (int)s.Time.TimeOfDay.TotalSeconds % cicloSegundosLocal / binWidthSeconds == bestBin,
                    t => (long)(t.TimeOfDay.TotalSeconds / cicloSegundosLocal));

                // Mesmo corte de atualidade dos outros detectores: sem execução recente sustentando o ciclo,
                // não conta como "acontecendo agora".
                if (examples.Count == 0 || (DateTime.Now - examples[0].Time).TotalMinutes > RecencyWindowMinutes)
                    continue;

                double confidence = Math.Clamp(35 + ratio * 7.0, 50, 97);
                string cicloTxt = cicloSegundos % 60 == 0 ? $"{cicloSegundos / 60} min" : $"{cicloSegundos / 60.0:0.#} min";

                // Prevê a próxima ocorrência usando o MEIO da janela de ~3s (mais preciso que a borda inicial).
                double targetPhase = bestBin * binWidthSeconds + binWidthSeconds / 2.0;
                DateTime nextExpectedAt = PredictNextPhase(targetPhase, cicloSegundos);

                bestRatio = ratio;
                best = new FlowPatternMatch
                {
                    Type = FlowPatternType.CicloFixo,
                    BucketKey = $"ciclo:{cicloSegundos}:{bestBin}",
                    ConfidencePct = confidence,
                    Examples = examples,
                    NextExpectedAt = nextExpectedAt,
                    ExpectedVolume = AverageVolume(examples),
                    Detail = $"Executa recorrentemente a cada {cicloTxt}, sempre na mesma janela de ~{binWidthSeconds}s " +
                              $"dentro do ciclo — {bestCount} de {total} negócios ({bestCount * 100.0 / total:0.0}%)."
                };
            }

            return best;
        }

        /// <summary>Mínimo de rajadas já vistas hoje pra dar um veredito sobre regularidade de horário.
        /// Com menos que isso, o card mostra a rajada mas avisa que ainda não dá pra dizer se é padronizada.</summary>
        private const int MinBurstsForRegularity = 3;

        /// <summary>Padrão "rajada de volume" — a corretora vinha executando num ritmo, e de repente solta um
        /// volume bem maior que o normal DELA MESMA numa janela curta (ex.: "vinha nos 550 lotes e disparou
        /// 3.000"). Varre o dia inteiro UMA vez (dois ponteiros, O(n)) marcando cada "episódio" de rajada
        /// (trecho contínuo acima do limiar), guardando o PICO de cada um. Além de reportar a rajada mais
        /// recente (quantidade e lado), compara os horários entre as rajadas já vistas hoje pra responder
        /// diretamente o que o usuário pediu: além de "quanto" e "compra ou venda", também "está numa
        /// cadência de tempo padronizada, ou é esporádica?".</summary>
        private static FlowPatternMatch? DetectRajadaVolume(List<FlowTradeSample> samples)
        {
            if (samples.Count < MinSamplesForDetection)
                return null;

            double totalSeconds = (samples[^1].Time - samples[0].Time).TotalSeconds;
            if (totalSeconds < BurstWindowSeconds)
                return null; // histórico curto demais pra calibrar o que é "normal" pra essa corretora

            long totalVolume = 0;
            foreach (var s in samples) totalVolume += s.Volume;
            double normalVolumePerWindow = totalVolume / totalSeconds * BurstWindowSeconds;
            double burstThreshold = Math.Max(MinBurstVolume, normalVolumePerWindow * BurstMultiplierThreshold);

            // Uma passada só: acompanha o volume da janela de BurstWindowSeconds terminando em cada índice
            // (dois ponteiros) e marca cada trecho contínuo acima do limiar como um "episódio" de rajada,
            // guardando o pico (maior volume) de cada um. Isso dá a lista de TODAS as rajadas do dia, não só
            // a mais forte — necessário pra avaliar se elas se repetem num intervalo de tempo regular.
            var episodes = new List<(int startIdx, int endIdx, long volume)>();
            int left = 0;
            long windowVolume = 0;
            bool inBurst = false;
            long peakVolume = 0;
            int peakStartIdx = -1, peakEndIdx = -1;

            for (int right = 0; right < samples.Count; right++)
            {
                windowVolume += samples[right].Volume;
                while (samples[right].Time - samples[left].Time > TimeSpan.FromSeconds(BurstWindowSeconds))
                {
                    windowVolume -= samples[left].Volume;
                    left++;
                }

                if (windowVolume >= burstThreshold)
                {
                    if (!inBurst || windowVolume > peakVolume)
                    {
                        peakVolume = windowVolume;
                        peakStartIdx = left;
                        peakEndIdx = right;
                    }
                    inBurst = true;
                }
                else if (inBurst)
                {
                    episodes.Add((peakStartIdx, peakEndIdx, peakVolume));
                    inBurst = false;
                }
            }
            if (inBurst)
                episodes.Add((peakStartIdx, peakEndIdx, peakVolume));

            if (episodes.Count == 0)
                return null;

            var latest = episodes[^1];

            // Corte de atualidade: só reporta se a rajada mais recente aconteceu dentro da janela de recência.
            if ((DateTime.Now - samples[latest.endIdx].Time).TotalMinutes > RecencyWindowMinutes)
                return null;

            double ratio = latest.volume / Math.Max(normalVolumePerWindow, 0.01);
            double confidence = Math.Clamp(35 + ratio * 3.0, 50, 97);

            // Regularidade: compara o horário entre as rajadas já vistas hoje (mesma lógica de coeficiente
            // de variação do Intervalo Regular, só que aplicada a EVENTOS DE RAJADA em vez de negócio a negócio.
            string regularidadeTxt;
            DateTime? nextExpectedAt = null;
            if (episodes.Count >= MinBurstsForRegularity)
            {
                var deltas = new List<double>();
                for (int i = 1; i < episodes.Count; i++)
                    deltas.Add((samples[episodes[i].endIdx].Time - samples[episodes[i - 1].endIdx].Time).TotalSeconds);

                double mean = deltas.Average();
                double variance = deltas.Select(d => (d - mean) * (d - mean)).Average();
                double cv = mean > 0 ? Math.Sqrt(variance) / mean : 1.0;

                if (cv < 0.45)
                {
                    string intervaloTxt = mean < 90 ? $"{mean:0}s" : $"{(int)(mean / 60)}min{(int)(mean % 60):00}s";
                    regularidadeTxt = $"Sim — se repete a cada ~{intervaloTxt} (variação {cv * 100:0}%), com base em {episodes.Count} rajadas hoje.";
                    nextExpectedAt = samples[latest.endIdx].Time.AddSeconds(mean);
                }
                else
                {
                    regularidadeTxt = $"Não — {episodes.Count} rajadas hoje, mas sem intervalo de tempo regular entre elas.";
                }
            }
            else
            {
                regularidadeTxt = $"Ainda sem dados suficientes ({episodes.Count} de {MinBurstsForRegularity} rajadas necessárias pra avaliar regularidade).";
            }

            // Execuções dentro da própria janela da rajada mais recente — a "prova" de quais negócios formaram o pico.
            var examples = new List<FlowPatternExample>();
            for (int i = latest.endIdx; i >= latest.startIdx && examples.Count < 5; i--)
                examples.Add(new FlowPatternExample(samples[i].Time, samples[i].Price, samples[i].Volume));

            // BucketKey pelo minuto da rajada mais recente: reconfirmações da MESMA rajada (tick a tick,
            // enquanto ela ainda estiver dentro da janela de recência) atualizam um único card; uma rajada
            // de fato distinta, em outro minuto, vira uma entrada nova.
            string bucketKey = $"burst:{samples[latest.endIdx].Time:yyyyMMddHHmm}";

            return new FlowPatternMatch
            {
                Type = FlowPatternType.RajadaVolume,
                BucketKey = bucketKey,
                ConfidencePct = confidence,
                Examples = examples,
                NextExpectedAt = nextExpectedAt,
                ExpectedVolume = (int)Math.Min(latest.volume, int.MaxValue),
                Detail = $"Rajada de {latest.volume:N0} lotes em {BurstWindowSeconds:0}s — {ratio:0.0}x o volume normal " +
                          $"da corretora nessa janela (~{normalVolumePerWindow:N0} lotes). Tempo padronizado? {regularidadeTxt}"
            };
        }

        /// <summary>Um "evento de execução": um ou mais negócios (fills parciais) a menos de <see
        /// cref="EventGroupingSeconds"/> um do outro, tratados como uma única ocorrência. Volume é a SOMA de
        /// todos os fills do evento — sem isso, o volume "esperado" da próxima execução refletia só o
        /// primeiro fill parcial, não o total real que costuma ser executado naquele instante.</summary>
        private readonly record struct ExecutionEvent(DateTime Time, int Volume);

        /// <summary>Agrupa prints crus em "eventos de execução" (prints a menos de 2s um do outro = 1 evento),
        /// somando o volume de todos os fills de cada evento.</summary>
        private static List<ExecutionEvent> BuildExecutionEvents(List<FlowTradeSample> samples)
        {
            var events = new List<ExecutionEvent>();
            DateTime? last = null;
            DateTime eventStart = default;
            int eventVolume = 0;
            bool hasEvent = false;

            foreach (var s in samples)
            {
                if (last == null || (s.Time - last.Value).TotalSeconds > EventGroupingSeconds)
                {
                    if (hasEvent) events.Add(new ExecutionEvent(eventStart, eventVolume));
                    eventStart = s.Time;
                    eventVolume = 0;
                    hasEvent = true;
                }
                eventVolume += s.Volume;
                last = s.Time;
            }
            if (hasEvent) events.Add(new ExecutionEvent(eventStart, eventVolume));

            return events;
        }

        /// <summary>Padrão 2 — corretora executa em intervalo aproximadamente constante desde a execução anterior.</summary>
        private static FlowPatternMatch? DetectIntervaloRegular(List<ExecutionEvent> events, List<FlowTradeSample> samples)
        {
            if (events.Count < MinEventsForInterval)
                return null;

            var deltas = new List<double>();
            for (int i = 1; i < events.Count; i++)
            {
                double d = (events[i].Time - events[i - 1].Time).TotalSeconds;
                if (d >= 3 && d <= 3600) // faixa plausível: 3s a 1h
                    deltas.Add(d);
            }
            if (deltas.Count < MinEventsForInterval - 1)
                return null;

            double mean = deltas.Average();
            if (mean <= 0)
                return null;

            double variance = deltas.Select(d => (d - mean) * (d - mean)).Average();
            double stdev = Math.Sqrt(variance);
            double cv = stdev / mean;

            // Afrouxado de 0.30 para 0.40 de variação aceita — mesmo raciocínio do Segundo Fixo: pega
            // cadências reais que são "bem regulares, mas não perfeitas", e a confiança exibida abaixo
            // (100*(1-cv)) já cai naturalmente quando a regularidade é mais fraca, então o usuário enxerga
            // a diferença entre um ritmo muito preciso e um só razoavelmente constante.
            if (cv >= 0.40) // muito irregular para chamar de "regular"
                return null;

            // Corte de atualidade: se o último evento já passou da janela de recência, a corretora não está
            // mais nesse ritmo — sem isso um intervalo que rodou de manhã continuava "confirmado" a tarde toda.
            if ((DateTime.Now - events[^1].Time).TotalMinutes > RecencyWindowMinutes)
                return null;

            double confidence = Math.Clamp(100 * (1 - cv), 50, 97);
            string intervaloTxt = mean < 90
                ? $"{mean:0}s"
                : $"{(int)(mean / 60)}min{(int)(mean % 60):00}s";

            int bucket = mean < 90
                ? (int)(Math.Round(mean / 5.0) * 5)
                : (int)(Math.Round(mean / 60.0) * 60);

            // Últimos até 5 eventos que formam o intervalo — Volume já vem SOMADO de todos os fills daquele
            // evento (ver ExecutionEvent/BuildExecutionEvents); Preço é só ilustrativo, pega do primeiro fill.
            var examples = new List<FlowPatternExample>();
            for (int i = events.Count - 1; i >= 0 && examples.Count < 5; i--)
            {
                var evt = events[i];
                decimal price = 0;
                for (int j = samples.Count - 1; j >= 0; j--)
                {
                    if (samples[j].Time == evt.Time) { price = samples[j].Price; break; }
                }
                examples.Add(new FlowPatternExample(evt.Time, price, evt.Volume));
            }

            // Aqui não é fase de ciclo (a corretora não está travada num horário do relógio) — é uma folga
            // fixa desde a ÚLTIMA execução, então a previsão é simplesmente "último evento + intervalo médio".
            DateTime nextExpectedAt = events[^1].Time.AddSeconds(mean);

            return new FlowPatternMatch
            {
                Type = FlowPatternType.IntervaloRegular,
                BucketKey = $"int:{bucket}",
                ConfidencePct = confidence,
                Examples = examples,
                NextExpectedAt = nextExpectedAt,
                ExpectedVolume = AverageVolume(examples),
                Detail = $"Executa a cada ~{intervaloTxt} desde a execução anterior (variação {cv * 100:0}%), " +
                          $"{deltas.Count + 1} execuções observadas."
            };
        }

        /// <summary>Padrão 3 — quando o padrão acima repete, o preço se move mais que o normal do dia logo após.</summary>
        private static FlowPatternMatch? DetectImpactoPreco(List<ExecutionEvent> events, List<FlowTradeSample> global, string parentBucketKey)
        {
            if (global.Count < 30 || events.Count < 3)
                return null;

            // Mesmo corte de atualidade dos outros dois — depende do padrão "pai" (segundo/intervalo), mas
            // reforça aqui também: sem execução recente, não é impacto acontecendo agora.
            if ((DateTime.Now - events[^1].Time).TotalMinutes > RecencyWindowMinutes)
                return null;

            double baseline = ComputeBaselineMovement(global, TimeSpan.FromSeconds(ImpactWindowSeconds));
            if (baseline <= 0)
                return null;

            int take = Math.Min(10, events.Count);
            var movements = new List<double>();
            for (int i = events.Count - take; i < events.Count; i++)
            {
                var t0 = events[i].Time;
                decimal? p0 = FindPriceAtOrAfter(global, t0);
                decimal? p1 = FindPriceAtOrAfter(global, t0.AddSeconds(ImpactWindowSeconds));
                if (p0.HasValue && p1.HasValue)
                    movements.Add((double)(p1.Value - p0.Value));
            }
            if (movements.Count < 3)
                return null;

            double avgAbs = movements.Select(Math.Abs).Average();
            if (avgAbs < baseline * ImpactMultiplierThreshold)
                return null;

            double avgSigned = movements.Average();
            int pts = (int)Math.Round(avgSigned, MidpointRounding.AwayFromZero);
            int magnitudeBucket = (int)Math.Round(avgAbs);

            return new FlowPatternMatch
            {
                Type = FlowPatternType.ImpactoPreco,
                BucketKey = $"imp:{parentBucketKey}:{magnitudeBucket}",
                PointsMoved = pts,
                Detail = $"Nas últimas {movements.Count} execuções do padrão, o preço se moveu em média " +
                          $"{Math.Abs(pts)} pts em {ImpactWindowSeconds:0}s — " +
                          $"{avgAbs / Math.Max(baseline, 0.01):0.0}x o movimento normal do dia."
            };
        }

        /// <summary>Primeira amostra do buffer global com Time >= <paramref name="time"/> (busca binária; buffer é cronológico).</summary>
        private static decimal? FindPriceAtOrAfter(List<FlowTradeSample> global, DateTime time)
        {
            int lo = 0, hi = global.Count - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (global[mid].Time >= time) { ans = mid; hi = mid - 1; }
                else lo = mid + 1;
            }
            return ans >= 0 ? global[ans].Price : null;
        }

        /// <summary>Movimento médio de preço "normal" do dia numa janela de tempo, amostrado ao longo do buffer global.</summary>
        private static double ComputeBaselineMovement(List<FlowTradeSample> global, TimeSpan window)
        {
            if (global.Count < 20)
                return 0;

            int sampleEvery = Math.Max(1, global.Count / 150);
            double sum = 0;
            int n = 0;
            for (int i = 0; i < global.Count; i += sampleEvery)
            {
                var t0 = global[i];
                var p1 = FindPriceAtOrAfter(global, t0.Time + window);
                if (p1.HasValue)
                {
                    sum += Math.Abs((double)(p1.Value - t0.Price));
                    n++;
                }
            }
            return n > 0 ? sum / n : 0;
        }

        // ══════════════════════════════════════════════════════════
        // Histórico "últimos padrões encontrados" (5 por corretora)
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Funde as detecções desta rodada no histórico da corretora: se já existe uma entrada do mesmo
        /// tipo+faixa (BucketKey), ela é atualizada em memória (o padrão "evolui"/"se confirma");
        /// caso contrário uma entrada nova é criada, empurrando a mais antiga para fora quando passa de 5.
        /// </summary>
        private void MergeIntoHistory(string broker, List<FlowPatternMatch> newFinds, DateTime now)
        {
            if (!_historyByBroker.TryGetValue(broker, out var hist))
            {
                hist = new List<FlowPatternMatch>();
                _historyByBroker[broker] = hist;
            }

            foreach (var found in newFinds)
            {
                FlowPatternMatch? existing = null;
                for (int i = hist.Count - 1; i >= 0; i--)
                {
                    if (hist[i].Type == found.Type && hist[i].BucketKey == found.BucketKey)
                    {
                        existing = hist[i];
                        break;
                    }
                }

                if (existing != null)
                {
                    existing.Detail = found.Detail;
                    existing.IsBuySide = found.IsBuySide;
                    existing.ConfidencePct = found.ConfidencePct;
                    existing.PointsMoved = found.PointsMoved;
                    existing.Examples = found.Examples;
                    existing.NextExpectedAt = found.NextExpectedAt;
                    existing.ExpectedVolume = found.ExpectedVolume;
                    existing.LastConfirmedAt = now;
                }
                else
                {
                    found.FoundAt = now;
                    found.LastConfirmedAt = now;
                    hist.Add(found);
                    if (hist.Count > MaxHistoryPerBroker)
                        hist.RemoveAt(0);
                }
            }

            // Expira do histórico qualquer padrão que não foi reconfirmado dentro da janela de recência —
            // sem isso, uma vez que o detector para de encontrar um padrão (porque a corretora parou de
            // fazer aquilo), a entrada antiga simplesmente ficava lá parada pra sempre, com o horário
            // congelado, até ser empurrada por 5 padrões novos (o que podia nunca acontecer). Assim ela
            // desaparece da tela sozinha quando realmente parou de acontecer — como pedido.
            hist.RemoveAll(h => (now - h.LastConfirmedAt).TotalMinutes > RecencyWindowMinutes);
        }

        private static List<FlowPatternMatch> CloneReversed(List<FlowPatternMatch> hist)
        {
            var result = new List<FlowPatternMatch>(hist.Count);
            for (int i = hist.Count - 1; i >= 0; i--)
            {
                var h = hist[i];
                result.Add(new FlowPatternMatch
                {
                    FoundAt = h.FoundAt,
                    LastConfirmedAt = h.LastConfirmedAt,
                    Type = h.Type,
                    IsBuySide = h.IsBuySide,
                    Detail = h.Detail,
                    ConfidencePct = h.ConfidencePct,
                    PointsMoved = h.PointsMoved,
                    Examples = h.Examples,
                    NextExpectedAt = h.NextExpectedAt,
                    ExpectedVolume = h.ExpectedVolume,
                    BucketKey = h.BucketKey
                });
            }
            return result;
        }

        // ══════════════════════════════════════════════════════════
        // Janela de agressão por quantidade executada
        // ══════════════════════════════════════════════════════════

        private static AggressionWindowResult ComputeWindow(List<FlowTradeSample> global, int targetQty)
        {
            if (global.Count == 0)
                return new AggressionWindowResult { TargetQty = targetQty, ActualQty = 0, BuyPct = 50, PointsMoved = 0 };

            long acc = 0, buy = 0, sell = 0;
            int startIdx = global.Count - 1;
            for (int i = global.Count - 1; i >= 0; i--)
            {
                var s = global[i];
                acc += s.Volume;
                if (s.IsBuy) buy += s.Volume; else sell += s.Volume;
                startIdx = i;
                if (acc >= targetQty) break;
            }

            decimal endPrice = global[^1].Price;
            decimal startPrice = global[startIdx].Price;
            int pts = (int)Math.Round(endPrice - startPrice, MidpointRounding.AwayFromZero);
            double buyPct = (buy + sell) > 0 ? buy * 100.0 / (buy + sell) : 50.0;

            return new AggressionWindowResult
            {
                TargetQty = targetQty,
                ActualQty = acc,
                BuyPct = buyPct,
                PointsMoved = pts
            };
        }
    }
}
