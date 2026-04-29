using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketCore.FlowSense
{
    /// <summary>
    /// Modelo de dados por corretora — acumula volume comprador/vendedor no dia
    /// e mantém janela deslizante de 60 segundos para detectar atividade em tempo real
    /// </summary>
    public class BrokerStats
    {
        public string BrokerName { get; set; }

        // ════ Acumulado no dia ════
        public double BuyVolume { get; private set; }      // volume total comprador acumulado
        public double SellVolume { get; private set; }     // volume total vendedor acumulado
        public double NetDelta { get; private set; }       // BuyVolume - SellVolume

        // ════ Janela deslizante 60 segundos ════
        private Queue<TradeEventRecord> _recentEvents;     // eventos dos ultimos 60s
        private readonly int _windowSeconds = 60;
        private DateTime _lastCleanup = DateTime.UtcNow;

        public double ActiveBuyVol60s { get; private set; }  // volume comprador nos ultimos 60s
        public double ActiveSellVol60s { get; private set; } // volume vendedor nos ultimos 60s
        public int TradesPerMinute { get; private set; }     // quantidade de trades nos ultimos 60s

        public BrokerStats(string brokerName)
        {
            BrokerName = brokerName;
            BuyVolume = 0;
            SellVolume = 0;
            NetDelta = 0;
            _recentEvents = new Queue<TradeEventRecord>(256);
            ActiveBuyVol60s = 0;
            ActiveSellVol60s = 0;
            TradesPerMinute = 0;
        }

        /// <summary>
        /// Registra um trade nesta corretora — atualiza acumulado do dia e janela 60s
        /// </summary>
        public void RecordTrade(double volume, bool isBuyAggressor, DateTime timestamp)
        {
            // Atualiza acumulado do dia
            if (isBuyAggressor)
            {
                BuyVolume += volume;
            }
            else
            {
                SellVolume += volume;
            }
            NetDelta = BuyVolume - SellVolume;

            // Enqueue no RecentEvents
            _recentEvents.Enqueue(new TradeEventRecord 
            { 
                Volume = volume, 
                IsBuyAggressor = isBuyAggressor, 
                Timestamp = timestamp 
            });

            // Cleanup: remove eventos com mais de 60s
            CleanupExpiredEvents(timestamp);

            // Recalcula atividade 60s
            RecalculateWindow60s();
        }

        private void CleanupExpiredEvents(DateTime now)
        {
            // Para evitar cleanup em cada trade, só faz a cada 5 segundos
            if ((now - _lastCleanup).TotalSeconds < 5)
                return;

            var cutoffTime = now.AddSeconds(-_windowSeconds);
            while (_recentEvents.Count > 0 && _recentEvents.Peek().Timestamp < cutoffTime)
            {
                _recentEvents.Dequeue();
            }
            _lastCleanup = now;
        }

        private void RecalculateWindow60s()
        {
            ActiveBuyVol60s = 0;
            ActiveSellVol60s = 0;
            TradesPerMinute = 0;

            foreach (var evt in _recentEvents)
            {
                if (evt.IsBuyAggressor)
                    ActiveBuyVol60s += evt.Volume;
                else
                    ActiveSellVol60s += evt.Volume;
                TradesPerMinute++;
            }
        }

        /// <summary>
        /// Reset à meia-noite — chamado pelo DispatcherTimer do BrokerAccumulator
        /// </summary>
        public void ResetDaily()
        {
            BuyVolume = 0;
            SellVolume = 0;
            NetDelta = 0;
            // Mantém os eventos recentes (nao limpa a janela 60s)
        }

        private class TradeEventRecord
        {
            public double Volume { get; set; }
            public bool IsBuyAggressor { get; set; }
            public DateTime Timestamp { get; set; }
        }
    }
}
