using System;
using System.Collections.Generic;
using System.Linq;
using MarketCore.Models;

namespace MarketCore.Engine.Detectors
{
    /// <summary>
    /// Detecta padrão de Exhaustion — agressão continuada sem avanço de preço.
    /// Indica absorção institucional e possível reversão.
    /// </summary>
    public class ExhaustionDetector
    {
        // Configurações públicas — ajustáveis em tempo de execução
        public int MinTradesParaDeteccao { get; set; }
        public int MinVolumeParaDeteccao { get; set; }
        public decimal MaxVariacaoPreco { get; set; }
        public TimeSpan JanelaTemporal { get; set; }

        // Estado por ticker
        private readonly Dictionary<string, SequenciaExhaustion> _sequenciasPorTicker = new();

        public event Action<ExhaustionEvent>? OnExhaustionDetected;

        /// <summary>
        /// Construtor padrão (1 minuto).
        /// </summary>
        public ExhaustionDetector()
        {
            AplicarPreset(ExhaustionTimeframe.M1);
        }

        /// <summary>
        /// Construtor com timeframe específico.
        /// </summary>
        public ExhaustionDetector(ExhaustionTimeframe timeframe)
        {
            AplicarPreset(timeframe);
        }

        /// <summary>
        /// Construtor com parâmetros totalmente customizados.
        /// </summary>
        public ExhaustionDetector(int minTrades, int minVolume, decimal maxVariacao, int janelaSegundos)
        {
            MinTradesParaDeteccao = minTrades;
            MinVolumeParaDeteccao = minVolume;
            MaxVariacaoPreco = maxVariacao;
            JanelaTemporal = TimeSpan.FromSeconds(janelaSegundos);
        }

        /// <summary>
        /// Aplica preset calibrado para um timeframe específico.
        /// </summary>
        public void AplicarPreset(ExhaustionTimeframe timeframe)
        {
            switch (timeframe)
            {
                case ExhaustionTimeframe.S30:
                    MinTradesParaDeteccao = 3;
                    MinVolumeParaDeteccao = 150;
                    MaxVariacaoPreco = 40;
                    JanelaTemporal = TimeSpan.FromSeconds(30);
                    break;

                case ExhaustionTimeframe.M1:
                    MinTradesParaDeteccao = 5;
                    MinVolumeParaDeteccao = 300;
                    MaxVariacaoPreco = 30;
                    JanelaTemporal = TimeSpan.FromSeconds(60);
                    break;

                case ExhaustionTimeframe.M2:
                    MinTradesParaDeteccao = 7;
                    MinVolumeParaDeteccao = 400;
                    MaxVariacaoPreco = 25;
                    JanelaTemporal = TimeSpan.FromSeconds(120);
                    break;

                case ExhaustionTimeframe.M3:
                    MinTradesParaDeteccao = 8;
                    MinVolumeParaDeteccao = 500;
                    MaxVariacaoPreco = 20;
                    JanelaTemporal = TimeSpan.FromSeconds(180);
                    break;

                case ExhaustionTimeframe.M5:
                    MinTradesParaDeteccao = 10;
                    MinVolumeParaDeteccao = 600;
                    MaxVariacaoPreco = 20;
                    JanelaTemporal = TimeSpan.FromSeconds(300);
                    break;

                case ExhaustionTimeframe.M15:
                    MinTradesParaDeteccao = 15;
                    MinVolumeParaDeteccao = 1000;
                    MaxVariacaoPreco = 15;
                    JanelaTemporal = TimeSpan.FromSeconds(900);
                    break;

                case ExhaustionTimeframe.M30:
                    MinTradesParaDeteccao = 20;
                    MinVolumeParaDeteccao = 1500;
                    MaxVariacaoPreco = 15;
                    JanelaTemporal = TimeSpan.FromSeconds(1800);
                    break;

                case ExhaustionTimeframe.H1:
                    MinTradesParaDeteccao = 30;
                    MinVolumeParaDeteccao = 2000;
                    MaxVariacaoPreco = 10;
                    JanelaTemporal = TimeSpan.FromSeconds(3600);
                    break;

                default:
                    throw new ArgumentException($"Timeframe desconhecido: {timeframe}");
            }
        }

        /// <summary>
        /// Processa um novo trade e verifica se há padrão de exhaustion.
        /// </summary>
        public void ProcessarTrade(TradeEvent trade)
        {
            if (!_sequenciasPorTicker.ContainsKey(trade.Ticker))
            {
                _sequenciasPorTicker[trade.Ticker] = new SequenciaExhaustion();
            }

            var sequencia = _sequenciasPorTicker[trade.Ticker];

            // Se mudou o lado do agressor, reinicia sequência
            if (sequencia.Lado != trade.Aggressor && sequencia.Trades.Count > 0)
            {
                // Antes de reiniciar, verifica se a sequência anterior formou exhaustion
                VerificarExhaustion(trade.Ticker, sequencia);
                sequencia.Reiniciar();
            }

            // Adiciona trade à sequência atual
            sequencia.Lado = trade.Aggressor;
            sequencia.Trades.Add(trade);

            // Remove trades muito antigos (fora da janela temporal)
            var limiteInferior = trade.Time - JanelaTemporal;
            sequencia.Trades.RemoveAll(t => t.Time < limiteInferior);

            // Verifica se formou exhaustion
            VerificarExhaustion(trade.Ticker, sequencia);
        }

        /// <summary>
        /// Verifica se a sequência atual formou um padrão de exhaustion.
        /// </summary>
        private void VerificarExhaustion(string ticker, SequenciaExhaustion sequencia)
        {
            if (sequencia.Trades.Count < MinTradesParaDeteccao)
                return;

            var volumeTotal = sequencia.Trades.Sum(t => t.Volume);
            if (volumeTotal < MinVolumeParaDeteccao)
                return;

            var precoInicial = sequencia.Trades.First().Price;
            var precoFinal = sequencia.Trades.Last().Price;
            var variacao = Math.Abs(precoFinal - precoInicial);

            // Exhaustion: muitos trades, muito volume, pouca variação de preço
            if (variacao <= MaxVariacaoPreco)
            {
                // Determinar direção da reversão esperada
                var direcaoReversao = sequencia.Lado == TradeAggressor.Buy
                    ? "BAIXA"  // Compradores sendo absorvidos → reversão pra baixo
                    : "ALTA";  // Vendedores sendo absorvidos → reversão pra cima

                var exhaustionEvent = new ExhaustionEvent
                {
                    Ticker = ticker,
                    Time = sequencia.Trades.Last().Time,
                    LadoAgressor = sequencia.Lado.ToString(),
                    DirecaoReversao = direcaoReversao,
                    NumTrades = sequencia.Trades.Count,
                    VolumeTotal = volumeTotal,
                    PrecoInicial = precoInicial,
                    PrecoFinal = precoFinal,
                    VariacaoPreco = variacao,
                    DuracaoSegundos = (sequencia.Trades.Last().Time - sequencia.Trades.First().Time).TotalSeconds
                };

                OnExhaustionDetected?.Invoke(exhaustionEvent);

                // Reinicia sequência para não detectar múltiplas vezes
                sequencia.Reiniciar();
            }
        }

        /// <summary>
        /// Retorna estatísticas atuais do detector.
        /// </summary>
        public string GetStatus()
        {
            return $"Exhaustion | MinTrades:{MinTradesParaDeteccao} MinVol:{MinVolumeParaDeteccao} " +
                   $"MaxVar:{MaxVariacaoPreco}pts Janela:{JanelaTemporal.TotalSeconds}s";
        }

        /// <summary>
        /// Estado interno da sequência de trades para um ticker.
        /// </summary>
        private class SequenciaExhaustion
        {
            public TradeAggressor Lado { get; set; } = TradeAggressor.Unknown;
            public List<TradeEvent> Trades { get; set; } = new();

            public void Reiniciar()
            {
                Lado = TradeAggressor.Unknown;
                Trades.Clear();
            }
        }
    }

    /// <summary>
    /// Timeframes disponíveis para calibração do Exhaustion Detector.
    /// </summary>
    public enum ExhaustionTimeframe
    {
        S30,   // 30 segundos - scalp ultra-rápido
        M1,    // 1 minuto - scalp
        M2,    // 2 minutos - scalp
        M3,    // 3 minutos - day trade curto
        M5,    // 5 minutos - day trade
        M15,   // 15 minutos - day trade médio
        M30,   // 30 minutos - swing intraday
        H1     // 1 hora - swing/position
    }

    /// <summary>
    /// Evento de detecção de Exhaustion.
    /// </summary>
    public class ExhaustionEvent
    {
        public string Ticker { get; set; } = string.Empty;
        public DateTime Time { get; set; }
        public string LadoAgressor { get; set; } = string.Empty;
        public string DirecaoReversao { get; set; } = string.Empty;
        public int NumTrades { get; set; }
        public int VolumeTotal { get; set; }
        public decimal PrecoInicial { get; set; }
        public decimal PrecoFinal { get; set; }
        public decimal VariacaoPreco { get; set; }
        public double DuracaoSegundos { get; set; }
    }
}