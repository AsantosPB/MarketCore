using System;
using System.Collections.Generic;
using System.Linq;
using MarketCore.AgentPanel.Detectors;

namespace MarketCore.AgentPanel
{
    public class PatternHistory
    {
        private readonly List<PatternRecord> _historico = new();
        private const int MAX_HISTORICO = 100;

        public void Adicionar(ResultadoAgregado sinal)
        {
            _historico.Add(new PatternRecord
            {
                Timestamp    = DateTime.Now,
                Direcao      = sinal.Direcao,
                Padrao       = sinal.Descricao,
                Confianca    = sinal.Confianca,
                Stop         = sinal.Stop,
                Alvo         = sinal.Alvo,
                PrecoEntrada = 0
            });

            if (_historico.Count > MAX_HISTORICO)
                _historico.RemoveAt(0);
        }

        public void MarcarResultado(PatternRecord record, bool sucesso, int pontos)
        {
            record.Sucesso = sucesso;
            record.Pontos  = pontos;
        }

        public List<PatternRecord> ObterUltimos(int count)
            => _historico.OrderByDescending(p => p.Timestamp).Take(count).ToList();

        public int    ContarSucessos() => _historico.Count(p => p.Sucesso);
        public int    ContarFalhas()   => _historico.Count(p => !p.Sucesso && p.Pontos != 0);
        public double WinRate()
        {
            var total = _historico.Count(p => p.Pontos != 0);
            return total == 0 ? 0.65 : ContarSucessos() / (double)total;
        }

        public List<PatternRecord> ObterTodos() => _historico;
    }

    public class PatternRecord
    {
        public DateTime Timestamp    { get; set; }
        public Direcao  Direcao      { get; set; }
        public string   Padrao       { get; set; } = "";
        public double   Confianca    { get; set; }
        public double   Stop         { get; set; }
        public double   Alvo         { get; set; }
        public double   PrecoEntrada { get; set; }
        public bool     Sucesso      { get; set; }
        public int      Pontos       { get; set; }
    }
}
