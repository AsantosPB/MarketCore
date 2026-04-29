using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketCore.FlowSense
{
    /// <summary>
    /// BookAnalyzer — processa snapshots do book bilateral para gerar sinais
    /// Expandido com VWAP distance para o FlowScoreEngine
    /// </summary>
    public class BookAnalyzer
    {
        private List<double> _bidPrices = new List<double>(30);
        private List<double> _bidQtys = new List<double>(30);
        private List<double> _askPrices = new List<double>(30);
        private List<double> _askQtys = new List<double>(30);

        private bool _renewableDetected = false;
        private DateTime _lastBookUpdate = DateTime.UtcNow;
        private double _vwapDistance = 0; // distancia do preco ao VWAP

        public void OnBookSnapshot(
            List<double> bidPrices, List<double> bidQtys,
            List<double> askPrices, List<double> askQtys)
        {
            _bidPrices = bidPrices;
            _bidQtys = bidQtys;
            _askPrices = askPrices;
            _askQtys = askQtys;

            DetectRenewable();
            _lastBookUpdate = DateTime.UtcNow;
        }

        /// <summary>
        /// Pressão bid/ask — se ask está fraco (pouca qty), é comprador
        /// Retorna [-1, +1]: +1 = pressão comprador máxima, -1 = vendedor máximo
        /// </summary>
        public double GetBidAskPressure()
        {
            if (_bidQtys.Count == 0 || _askQtys.Count == 0)
                return 0;

            double bidQty = _bidQtys[0]; // best bid qty
            double askQty = _askQtys[0]; // best ask qty
            double total = bidQty + askQty;

            if (total == 0)
                return 0;

            // Normaliza: se bid > ask, é comprador (+), se ask > bid, é vendedor (-)
            return (bidQty - askQty) / total;
        }

        /// <summary>
        /// Desequilíbrio nos primeiros 5 níveis do book
        /// </summary>
        public double GetLevelImbalance()
        {
            int levels = Math.Min(5, Math.Min(_bidQtys.Count, _askQtys.Count));
            double bidSum = _bidQtys.Take(levels).Sum();
            double askSum = _askQtys.Take(levels).Sum();
            double total = bidSum + askSum;

            if (total == 0)
                return 0;

            return (bidSum - askSum) / total;
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
            return _renewableDetected;
        }

        /// <summary>
        /// Distância do preço ao VWAP em percentual
        /// Positivo = preço acima do VWAP (caro)
        /// Negativo = preço abaixo do VWAP (barato)
        /// Retorna [-0.5, +0.5]: valores normalizados para uso no FlowScore
        /// </summary>
        public double GetVWAPDistance()
        {
            // Esta função precisa receber o VWAP e LastPrice de fora
            // Aqui é um placeholder — será alimentado pelo DeltaEngine
            return _vwapDistance;
        }

        /// <summary>
        /// Atualiza a distância VWAP — chamada pelo DeltaEngine ou fluxo de dados
        /// </summary>
        public void SetVWAPDistance(double currentPrice, double sessionVWAP)
        {
            if (sessionVWAP > 0)
            {
                double distance = (currentPrice - sessionVWAP) / sessionVWAP; // percentual
                _vwapDistance = Math.Max(-0.5, Math.Min(0.5, distance)); // clamp
            }
        }

        /// <summary>
        /// Retorna absorção — preço mantém enquanto volume aumenta
        /// </summary>
        public bool IsAbsorptionActive()
        {
            // Simplificado: se bid qty está crescendo (mais buys entrando), é absorção
            if (_bidQtys.Count > 1)
            {
                return _bidQtys[0] > _bidQtys[1] * 1.2; // 20% maior que nível anterior
            }
            return false;
        }
    }
}
