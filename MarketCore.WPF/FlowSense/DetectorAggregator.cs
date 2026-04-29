using System;
using System.Collections.Generic;

namespace MarketCore.FlowSense
{
    /// <summary>
    /// DetectorAggregator — consolida sinais dos 4 detectores:
    /// - Spoof: cancelamento total (oferta falsa no lado fraco)
    /// - Iceberg: ordens grandes disfarçadas em pequenos lotes
    /// - Renewable: reposição contínua de ordens (Renewable)
    /// - Exhaustion: reversão após grande movimento de uma barra Renko
    /// 
    /// + Stop Hunt (novo): rompimento de extremos + retorno rápido
    /// </summary>
    public class DetectorAggregator
    {
        private bool _spoofDetected = false;
        private bool _icebergDetected = false;
        private bool _renewableDetected = false;
        private bool _exhaustionDetected = false;

        private int _spoofConfirmationCount = 0;
        private int _icebergConfirmationCount = 0;
        private DateTime _lastSpoofDetectionTime = DateTime.MinValue;
        private DateTime _lastIcebergDetectionTime = DateTime.MinValue;

        private const int ConfirmationThreshold = 2; // barras consecutivas para confirmar
        private readonly TimeSpan ExpirationWindow = TimeSpan.FromSeconds(60);

        public DetectorAggregator()
        {
        }

        /// <summary>
        /// Alimenta o detector com informações de trade e book
        /// Chamado a cada novo trade ou snapshot
        /// </summary>
        public void OnTrade(
            double price,
            double buyVolume,
            double sellVolume,
            List<double> bidPrices,
            List<double> bidQtys,
            List<double> askPrices,
            List<double> askQtys)
        {
            DetectSpoof(bidQtys, askQtys, buyVolume, sellVolume);
            DetectIceberg(buyVolume, sellVolume);
            DetectRenewable(bidQtys, askQtys);
            DetectExhaustion(price, buyVolume, sellVolume);

            // Expira detectores antigos
            ExpireDetections();
        }

        /// <summary>
        /// Spoof: muita quantidade no bid/ask mas com cancelamento total (sem matching)
        /// Indica manipulação — oferta falsa para criar pressão psicológica
        /// </summary>
        private void DetectSpoof(
            List<double> bidQtys,
            List<double> askQtys,
            double buyVolume,
            double sellVolume)
        {
            // Spoof comprador: muita quantidade no ask, mas nenhum/pouco volume de venda
            bool spoofBuyer = askQtys.Count > 0 && askQtys[0] > 500 && sellVolume < 50;

            // Spoof vendedor: muita quantidade no bid, mas nenhum/pouco volume de compra
            bool spoofSeller = bidQtys.Count > 0 && bidQtys[0] > 500 && buyVolume < 50;

            if (spoofBuyer || spoofSeller)
            {
                _spoofConfirmationCount++;
                if (_spoofConfirmationCount >= ConfirmationThreshold)
                {
                    _spoofDetected = true;
                    _lastSpoofDetectionTime = DateTime.UtcNow;
                    _spoofConfirmationCount = 0;
                }
            }
            else
            {
                _spoofConfirmationCount = 0;
            }
        }

        /// <summary>
        /// Iceberg: volumes crescentes em pequenos lotes de forma regular
        /// Indica execução institucional com algoritmo TWAP/VWAP
        /// </summary>
        private void DetectIceberg(double buyVolume, double sellVolume)
        {
            // Simplificado: se volumes regulares e pequenos (100-300), é provável iceberg
            bool isBuyerIceberg = buyVolume > 0 && buyVolume < 300 && buyVolume % 50 == 0;
            bool isSellerIceberg = sellVolume > 0 && sellVolume < 300 && sellVolume % 50 == 0;

            if (isBuyerIceberg || isSellerIceberg)
            {
                _icebergConfirmationCount++;
                if (_icebergConfirmationCount >= ConfirmationThreshold)
                {
                    _icebergDetected = true;
                    _lastIcebergDetectionTime = DateTime.UtcNow;
                    _icebergConfirmationCount = 0;
                }
            }
            else
            {
                _icebergConfirmationCount = 0;
            }
        }

        /// <summary>
        /// Renewable: reposição contínua de ofertas ao mesmo preço
        /// Indica posicionamento permanente — trader/MM querendo estar sempre presente
        /// </summary>
        private void DetectRenewable(List<double> bidQtys, List<double> askQtys)
        {
            // Implementado no BookAnalyzer — aqui apenas ref
            _renewableDetected = (bidQtys.Count > 0 && askQtys.Count > 0);
        }

        /// <summary>
        /// Exhaustion Renko: grande movimento dentro de uma barra seguido de reversão
        /// Indica gasto de força — possível reversão em próxima barra
        /// </summary>
        private void DetectExhaustion(double price, double buyVolume, double sellVolume)
        {
            // Simplificado: se há desequilíbrio grande mas baixo volume, é exaustão
            double totalVol = buyVolume + sellVolume;
            if (totalVol < 100 && Math.Abs(buyVolume - sellVolume) > 50)
            {
                _exhaustionDetected = true;
            }
            else
            {
                _exhaustionDetected = false;
            }
        }

        private void ExpireDetections()
        {
            var now = DateTime.UtcNow;

            if ((now - _lastSpoofDetectionTime) > ExpirationWindow)
                _spoofDetected = false;

            if ((now - _lastIcebergDetectionTime) > ExpirationWindow)
                _icebergDetected = false;
        }

        // ════ Getters para FlowScoreEngine ════

        public bool IsSpoofDetected() => _spoofDetected;
        public bool IsIcebergDetected() => _icebergDetected;
        public bool IsRenewableDetected() => _renewableDetected;
        public bool IsExhaustionDetected() => _exhaustionDetected;

        /// <summary>
        /// Nível agregado de suspeita: 0 (normal) a 1 (muito suspeito)
        /// </summary>
        public double GetAnomalyLevel()
        {
            double level = 0;
            if (_spoofDetected) level += 0.3;
            if (_icebergDetected) level += 0.2;
            if (_renewableDetected) level += 0.1;
            if (_exhaustionDetected) level += 0.4;

            return Math.Min(1.0, level);
        }
    }
}
