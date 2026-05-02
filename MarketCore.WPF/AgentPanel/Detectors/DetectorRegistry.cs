using System.Collections.Generic;
using System.Linq;

namespace MarketCore.AgentPanel.Detectors
{
    public class DetectorRegistry
    {
        private readonly List<IDetector> _detectores = new();

        public DetectorRegistry()
        {
            RegistrarDetectores();
        }

        private void RegistrarDetectores()
        {
            // TAPE
            _detectores.Add(new RafadaCompraDetector());
            _detectores.Add(new RafadaVendaDetector());
            _detectores.Add(new StopHuntDetector());
            _detectores.Add(new TickImbalanceDetector());

            // BOOK
            _detectores.Add(new IcebergDetector());
            _detectores.Add(new SpoofingDetector());
            _detectores.Add(new VacuoLiquidezDetector());
            _detectores.Add(new ThinMarketDetector());
            _detectores.Add(new BookImbalanceDetector());

            // PADRÃO PREÇO/VOLUME
            _detectores.Add(new AbsorcaoSuporteDetector());
            _detectores.Add(new AbsorcaoResistenciaDetector());
            _detectores.Add(new DivergenciaCVDDetector());
            _detectores.Add(new ExaustaoMovimentoDetector());
            _detectores.Add(new ClimaxVolumeDetector());

            // PLAYERS
            _detectores.Add(new PlayerDominanteDetector());
            _detectores.Add(new SincronizacaoBrokersDetector());
            _detectores.Add(new PlayerMudouLadoDetector());

            // CORRELAÇÕES
            _detectores.Add(new ConvergenciaTotalDetector());
            _detectores.Add(new DivergenciaWSPDetector());
            _detectores.Add(new ConflitoWDODetector());

            // CONTEXTO
            _detectores.Add(new HorarioFavoravelDetector());
            _detectores.Add(new EventoMacroProximoDetector());

            // ═══════════════════════════════════════════════════════
            // Para adicionar novo detector:
            // 1. Crie a classe implementando IDetector em AllDetectors.cs
            // 2. Adicione: _detectores.Add(new SeuDetector());
            // ═══════════════════════════════════════════════════════
        }

        public List<ResultadoDeteccao> ExecutarTodos(MarketContext ctx)
        {
            var resultados = new List<ResultadoDeteccao>();
            foreach (var detector in _detectores)
            {
                var resultado = detector.Analisar(ctx);
                if (resultado.Detectado)
                {
                    resultado.NomeDetector = detector.Nome;
                    resultado.Categoria    = detector.Categoria;
                    resultados.Add(resultado);
                }
            }
            return resultados;
        }

        public IReadOnlyList<IDetector> ObterTodos() => _detectores;
    }
}
