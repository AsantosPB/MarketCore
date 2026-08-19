using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace MarketCore.FlowSense
{
    /// <summary>
    /// Acumula e ranqueia fluxo por corretora.
    /// Alimentado por cada TradeEvent do tape - mantém dicionario de BrokerStats,
    /// expõe GetTop4Buyers/Sellers ordenados por volume do dia ou atividade 60s
    /// </summary>
    public class BrokerAccumulator
    {
        private readonly object _sync = new();
        private Dictionary<string, BrokerStats> _brokers = new Dictionary<string, BrokerStats>(64);
        private DispatcherTimer? _resetTimer;

        public BrokerAccumulator()
        {
            InitializeResetTimer();
        }

        /// <summary>
        /// Processa um trade - cria ou atualiza BrokerStats para o broker
        /// </summary>
        public void OnTrade(string brokerName, double volume, bool isBuyAggressor, DateTime timestamp)
        {
            if (string.IsNullOrEmpty(brokerName))
                return;

            lock (_sync)
            {
                if (!_brokers.TryGetValue(brokerName, out var stats))
                {
                    stats = new BrokerStats(brokerName);
                    _brokers[brokerName] = stats;
                }
                stats.RecordTrade(volume, isBuyAggressor, timestamp);
            }
        }

        /// <summary>
        /// Retorna os 4 maiores compradores do dia, ordenados por BuyVolume
        /// </summary>
        public List<BrokerStats> GetTop4Buyers()
        {
            lock (_sync)
                return _brokers.Values
                    .OrderByDescending(b => b.BuyVolume)
                    .Take(4)
                    .ToList();
        }

        /// <summary>
        /// Retorna os 4 maiores vendedores do dia, ordenados por SellVolume
        /// </summary>
        public List<BrokerStats> GetTop4Sellers()
        {
            lock (_sync)
                return _brokers.Values
                    .OrderByDescending(b => b.SellVolume)
                    .Take(4)
                    .ToList();
        }

        /// <summary>
        /// Retorna os brokers mais ativos nos ultimos 60 segundos (por ActiveBuyVol60s)
        /// Estes são os que entram no FlowScore com peso 35% (BrokerFlow)
        /// </summary>
        public List<BrokerStats> GetActiveBuyers60s()
        {
            lock (_sync)
                return _brokers.Values
                    .Where(b => b.ActiveBuyVol60s > 0)
                    .OrderByDescending(b => b.ActiveBuyVol60s)
                    .Take(4)
                    .ToList();
        }

        /// <summary>
        /// Retorna os brokers mais ativos nos ultimos 60 segundos (por ActiveSellVol60s)
        /// </summary>
        public List<BrokerStats> GetActiveSellers60s()
        {
            lock (_sync)
                return _brokers.Values
                    .Where(b => b.ActiveSellVol60s > 0)
                    .OrderByDescending(b => b.ActiveSellVol60s)
                    .Take(4)
                    .ToList();
        }

        /// <summary>
        /// Retorna um broker especifico por nome
        /// </summary>
        public BrokerStats? GetBroker(string brokerName)
        {
            lock (_sync)
                return _brokers.ContainsKey(brokerName) ? _brokers[brokerName] : null;
        }

        /// <summary>
        /// Retorna o dicionario inteiro (cuidado - apenas para debug)
        /// </summary>
        public IReadOnlyDictionary<string, BrokerStats> GetAllBrokers()
        {
            lock (_sync)
                return new Dictionary<string, BrokerStats>(_brokers);
        }

        private void InitializeResetTimer()
        {
            _resetTimer = new DispatcherTimer();
            _resetTimer.Interval = TimeSpan.FromSeconds(60); // verifica a cada 1 min se é meia-noite

            _resetTimer.Tick += (s, e) =>
            {
                var now = DateTime.Now;
                // Se passar das 00:00, reseta
                if (now.Hour == 0 && now.Minute == 0)
                {
                    ResetDaily();
                }
            };

            _resetTimer.Start();
        }

        private void ResetDaily()
        {
            lock (_sync)
            {
                foreach (var broker in _brokers.Values)
                    broker.ResetDaily();
            }
        }

        /// <summary>
        /// Para o timer de reset (chamar ao desligar a aplicacao)
        /// </summary>
        public void Stop()
        {
            _resetTimer?.Stop();
        }

        /// <summary>Limpa volumes por corretora ao mudar o ativo na UI principal.</summary>
        public void ClearAllBrokers()
        {
            lock (_sync) _brokers.Clear();
        }
    }
}
