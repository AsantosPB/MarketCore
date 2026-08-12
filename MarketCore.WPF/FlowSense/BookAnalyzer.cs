using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketCore.FlowSense
{
    /// <summary>
    /// BookAnalyzer - processa snapshots do book bilateral para gerar sinais
    /// Expandido com VWAP distance para o FlowScoreEngine
    /// </summary>
    public class BookAnalyzer
    {
        private readonly FlowScoreConfig? _flowScoreConfig;

        /// <summary>Snapshot do livro arrive na MarketEngine UiDispatch; leituras na UI timer.</summary>
        private readonly object _sync = new();

        private List<double> _bidPrices = new List<double>(30);
        private List<double> _bidQtys = new List<double>(30);
        private List<double> _askPrices = new List<double>(30);
        private List<double> _askQtys = new List<double>(30);

        private bool _renewableDetected = false;
        private DateTime _lastBookUpdate = DateTime.UtcNow;
        private double _vwapDistance = 0; // distancia do preco ao VWAP

        public BookAnalyzer(FlowScoreConfig? flowScoreConfig = null)
        {
            _flowScoreConfig = flowScoreConfig;
        }

        public void OnBookSnapshot(
            List<double> bidPrices, List<double> bidQtys,
            List<double> askPrices, List<double> askQtys)
        {
            lock (_sync)
            {
                _bidPrices = bidPrices;
                _bidQtys = bidQtys;
                _askPrices = askPrices;
                _askQtys = askQtys;

                if (_flowScoreConfig?.PreferAggregatedBookSignals != true)
                    DetectRenewable();
                _lastBookUpdate = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Pressão bid/ask - se ask está fraco (pouca qty), é comprador
        /// Retorna [-1, +1]: +1 = pressão comprador máxima, -1 = vendedor máximo
        /// </summary>
        public double GetBidAskPressure()
        {
            lock (_sync)
            {
                if (_bidQtys.Count == 0 || _askQtys.Count == 0)
                    return 0;

                double bidQty = _bidQtys[0];
                double askQty = _askQtys[0];
                double total = bidQty + askQty;

                if (total == 0)
                    return 0;

                return (bidQty - askQty) / total;
            }
        }

        /// <summary>
        /// Desequilíbrio nos primeiros 5 níveis do book
        /// </summary>
        public double GetLevelImbalance()
        {
            lock (_sync)
            {
                int levels = Math.Min(5, Math.Min(_bidQtys.Count, _askQtys.Count));
                double bidSum = _bidQtys.Take(levels).Sum();
                double askSum = _askQtys.Take(levels).Sum();
                double total = bidSum + askSum;

                if (total == 0)
                    return 0;

                return (bidSum - askSum) / total;
            }
        }

        /// <summary>
        /// Renewable: ofertas que desaparecem e voltam ao mesmo nível (reposição contínua)
        /// Indica interesse institucional em manter presença no book
        /// </summary>
        private void DetectRenewable()
        {
            // Simplificado: se ask qty está sempre ao redor do mesmo valor, é renewable
            if (_askQtys.Count > 0)
            {
                var recentAsks = _askQtys.Take(3);
                double avgAsk = recentAsks.Average();
                _renewableDetected = recentAsks.All(q => Math.Abs(q - avgAsk) < avgAsk * 0.3);
            }
        }

        public bool IsRenewableActive()
        {
            lock (_sync) return _renewableDetected;
        }

        /// <summary>
        /// Distância do preço ao VWAP em percentual
        /// Positivo = preço acima do VWAP (caro)
        /// Negativo = preço abaixo do VWAP (barato)
        /// Retorna [-0.5, +0.5]: valores normalizados para uso no FlowScore
        /// </summary>
        public double GetVWAPDistance()
        {
            lock (_sync) return _vwapDistance;
        }

        /// <summary>
        /// Atualiza a distância VWAP - chamada pelo DeltaEngine ou fluxo de dados
        /// </summary>
        public void SetVWAPDistance(double currentPrice, double sessionVWAP)
        {
            lock (_sync)
            {
                if (sessionVWAP > 0)
                {
                    double distance = (currentPrice - sessionVWAP) / sessionVWAP;
                    _vwapDistance = Math.Max(-0.5, Math.Min(0.5, distance));
                }
            }
        }

        /// <summary>
        /// Retorna absorção - preço mantém enquanto volume aumenta
        /// </summary>
        public bool IsAbsorptionActive()
        {
            lock (_sync)
            {
                if (_bidQtys.Count > 1)
                    return _bidQtys[0] > _bidQtys[1] * 1.2;
                return false;
            }
        }

        public void ClearBookState()
        {
            lock (_sync)
            {
                _bidPrices.Clear();
                _bidQtys.Clear();
                _askPrices.Clear();
                _askQtys.Clear();
                _renewableDetected = false;
                _vwapDistance = 0;
            }
        }
    }
}
