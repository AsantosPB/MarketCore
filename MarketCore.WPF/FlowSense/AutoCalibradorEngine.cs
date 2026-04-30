using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace MarketCore.FlowSense
{
    /// <summary>
    /// Resultado da calibração automática de um componente.
    /// </summary>
    public class ComponenteCalibrado
    {
        public string Nome          { get; set; } = "";
        public double AcertoPct     { get; set; }   // 0.0 a 1.0
        public double Ratio         { get; set; }   // pts preço por 10pts score
        public double PesoAtual     { get; set; }   // 0.0 a 1.0
        public double PesoSugerido  { get; set; }   // 0.0 a 1.0
        public string Tendencia     { get; set; } = "→";  // ↑ ↓ →
    }

    /// <summary>
    /// Resultado completo da calibração automática.
    /// </summary>
    public class AutoCalibracaoResultado
    {
        public List<ComponenteCalibrado> Componentes { get; set; } = new();
        public int    JanelasAnalisadas  { get; set; }
        public int    MovimentosValidos  { get; set; }
        public int    PeriodoMinutos     { get; set; }
        public int    MovMinPontos       { get; set; }
        public bool   DadosInsuficientes { get; set; }
        public string Mensagem          { get; set; } = "";
    }

    /// <summary>
    /// Motor de calibração automática do FlowScore.
    /// Analisa snapshots históricos e calcula novos pesos baseados em correlação com preço real.
    /// </summary>
    public static class AutoCalibradorEngine
    {
        /// <summary>
        /// Analisa snapshots e retorna novos pesos sugeridos.
        /// </summary>
        /// <param name="snapshots">Lista de snapshots ordenados por timestamp</param>
        /// <param name="periodoMinutos">Quantos minutos analisar (ex: 30)</param>
        /// <param name="movMinPontos">Movimento mínimo de preço para contar como sinal (ex: 25)</param>
        /// <param name="configAtual">Configuração atual para comparação</param>
        public static AutoCalibracaoResultado Analisar(
            IReadOnlyList<FlowScoreSnapshot> snapshots,
            int periodoMinutos,
            int movMinPontos,
            FlowScoreConfig configAtual)
        {
            var resultado = new AutoCalibracaoResultado
            {
                PeriodoMinutos = periodoMinutos,
                MovMinPontos   = movMinPontos
            };

            if (snapshots.Count < 60)
            {
                resultado.DadosInsuficientes = true;
                resultado.Mensagem = $"Dados insuficientes: {snapshots.Count} snapshots. Mínimo: 60 (1 minuto).";
                return resultado;
            }

            // Filtrar pelo período solicitado
            var cutoff = snapshots[snapshots.Count - 1].Timestamp.AddMinutes(-periodoMinutos);
            var janela = snapshots.Where(s => s.Timestamp >= cutoff).ToList();

            if (janela.Count < 60)
            {
                resultado.DadosInsuficientes = true;
                resultado.Mensagem = $"Dados insuficientes no período de {periodoMinutos}min: {janela.Count} snapshots.";
                return resultado;
            }

            // Dividir em janelas de 1 minuto
            var janelasMins = DividirEmJanelas(janela, 60);
            resultado.JanelasAnalisadas = janelasMins.Count - 1; // última não tem "próxima"

            if (resultado.JanelasAnalisadas < 5)
            {
                resultado.DadosInsuficientes = true;
                resultado.Mensagem = $"Janelas insuficientes: {resultado.JanelasAnalisadas}. Mínimo: 5.";
                return resultado;
            }

            // Analisar cada componente
            var stats = new Dictionary<string, (int acertos, int total, List<double> ratios)>
            {
                ["BrokerFlow"]  = (0, 0, new List<double>()),
                ["FluxoDireto"] = (0, 0, new List<double>()),
                ["Book"]        = (0, 0, new List<double>()),
                ["Detectores"]  = (0, 0, new List<double>()),
            };

            for (int i = 0; i < janelasMins.Count - 1; i++)
            {
                var atual   = janelasMins[i];
                var proxima = janelasMins[i + 1];

                if (atual.Count == 0 || proxima.Count == 0) continue;

                // Preço no início da próxima janela vs final desta janela
                double precoFinal  = atual[atual.Count - 1].Preco;
                double precoProx   = proxima[proxima.Count - 1].Preco;
                double variacaoReal = precoProx - precoFinal;

                // Só conta se movimento >= mínimo configurado
                if (Math.Abs(variacaoReal) < movMinPontos) continue;

                resultado.MovimentosValidos++;
                bool precoSubiu = variacaoReal > 0;

                // Score médio de cada componente na janela atual
                double mediaBroker  = atual.Average(s => s.BrokerFlow);
                double mediaFluxo   = atual.Average(s => s.FluxoDireto);
                double mediaBook    = atual.Average(s => s.Book);
                double mediaDetect  = atual.Average(s => s.Detectores);

                // Calcular acerto e ratio para cada componente
                AnalisarComponente("BrokerFlow",  mediaBroker,  precoSubiu, variacaoReal, stats);
                AnalisarComponente("FluxoDireto", mediaFluxo,   precoSubiu, variacaoReal, stats);
                AnalisarComponente("Book",        mediaBook,    precoSubiu, variacaoReal, stats);
                AnalisarComponente("Detectores",  mediaDetect,  precoSubiu, variacaoReal, stats);
            }

            if (resultado.MovimentosValidos < 3)
            {
                resultado.DadosInsuficientes = true;
                resultado.Mensagem = $"Movimentos válidos insuficientes: {resultado.MovimentosValidos}. " +
                                     $"Reduza o movimento mínimo ou aumente o período.";
                return resultado;
            }

            // Calcular novos pesos baseados em acerto × ratio
            var scores = new Dictionary<string, double>();
            foreach (var kv in stats)
            {
                var (acertos, total, ratios) = kv.Value;
                double acertoPct = total > 0 ? (double)acertos / total : 0;
                double ratioMed  = ratios.Count > 0 ? ratios.Average() : 0;
                scores[kv.Key]   = Math.Max(0.01, acertoPct * Math.Max(0.1, ratioMed));
            }

            double totalScore = scores.Values.Sum();

            // Pesos atuais
            var pesosAtuais = new Dictionary<string, double>
            {
                ["BrokerFlow"]  = configAtual.WeightBrokerFlow,
                ["FluxoDireto"] = configAtual.WeightFluxoDireto,
                ["Book"]        = configAtual.WeightBook,
                ["Detectores"]  = configAtual.WeightDetectores,
            };

            // Montar resultado
            foreach (var kv in stats)
            {
                var (acertos, total, ratios) = kv.Value;
                double acertoPct    = total > 0 ? (double)acertos / total : 0;
                double ratioMed     = ratios.Count > 0 ? ratios.Average() : 0;
                double pesoSugerido = totalScore > 0 ? scores[kv.Key] / totalScore : 0.25;
                double pesoAtual    = pesosAtuais[kv.Key];

                string tendencia = "→";
                if (pesoSugerido > pesoAtual + 0.02) tendencia = "↑";
                else if (pesoSugerido < pesoAtual - 0.02) tendencia = "↓";

                resultado.Componentes.Add(new ComponenteCalibrado
                {
                    Nome         = kv.Key,
                    AcertoPct    = acertoPct,
                    Ratio        = ratioMed,
                    PesoAtual    = pesoAtual,
                    PesoSugerido = pesoSugerido,
                    Tendencia    = tendencia
                });
            }

            resultado.Mensagem = $"Calibração concluída. {resultado.JanelasAnalisadas} janelas, " +
                                  $"{resultado.MovimentosValidos} movimentos válidos ≥ {movMinPontos}pts.";
            return resultado;
        }

        /// <summary>
        /// Lê snapshots de um arquivo .bin para análise de dias anteriores.
        /// </summary>
        public static List<FlowScoreSnapshot> LerArquivo(string caminhoArquivo)
        {
            var lista = new List<FlowScoreSnapshot>();
            if (!File.Exists(caminhoArquivo)) return lista;

            try
            {
                using var fs = new FileStream(caminhoArquivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var br = new BinaryReader(fs);

                while (fs.Position + FlowScoreSnapshot.TamanhoBytes <= fs.Length)
                {
                    lista.Add(new FlowScoreSnapshot
                    {
                        Timestamp  = new DateTime(br.ReadInt64()),
                        Preco      = br.ReadDouble(),
                        ScoreTotal = br.ReadDouble(),
                        BrokerFlow = br.ReadDouble(),
                        FluxoDireto= br.ReadDouble(),
                        Book       = br.ReadDouble(),
                        Detectores = br.ReadDouble(),
                    });
                }
            }
            catch { }

            return lista;
        }

        /// <summary>
        /// Lê snapshots de múltiplos dias para análise de período longo.
        /// </summary>
        public static List<FlowScoreSnapshot> LerArquivos(string diretorioBase, int diasAtras)
        {
            var todos = new List<FlowScoreSnapshot>();
            var hoje  = DateOnly.FromDateTime(DateTime.Now);

            for (int d = diasAtras; d >= 0; d--)
            {
                var data    = hoje.AddDays(-d);
                var pasta   = Path.Combine(diretorioBase, data.ToString("yyyy-MM-dd"));
                var arquivo = Path.Combine(pasta, "WIN_flowscore.bin");
                todos.AddRange(LerArquivo(arquivo));
            }

            return todos.OrderBy(s => s.Timestamp).ToList();
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static void AnalisarComponente(
            string nome, double scoreComponente, bool precoSubiu,
            double variacaoReal,
            Dictionary<string, (int acertos, int total, List<double> ratios)> stats)
        {
            var (acertos, total, ratios) = stats[nome];
            total++;

            bool componentePositivo = scoreComponente > 5;
            bool componenteNegativo = scoreComponente < -5;

            bool acertou = (componentePositivo && precoSubiu) ||
                           (componenteNegativo && !precoSubiu);

            if (acertou)
            {
                acertos++;
                // Ratio: pontos de preço por 10pts de score
                double absScore = Math.Abs(scoreComponente);
                if (absScore > 5)
                {
                    double ratio = Math.Abs(variacaoReal) / (absScore / 10.0);
                    ratios.Add(Math.Min(ratio, 10.0)); // cap em 10x
                }
            }

            stats[nome] = (acertos, total, ratios);
        }

        private static List<List<FlowScoreSnapshot>> DividirEmJanelas(
            List<FlowScoreSnapshot> snapshots, int tamanho)
        {
            var resultado = new List<List<FlowScoreSnapshot>>();
            for (int i = 0; i < snapshots.Count; i += tamanho)
            {
                var janela = snapshots.Skip(i).Take(tamanho).ToList();
                if (janela.Count > 0)
                    resultado.Add(janela);
            }
            return resultado;
        }
    }
}
