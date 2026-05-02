using System;
using System.Collections.Generic;
using System.Linq;
using MarketCore.AgentPanel.Detectors;

namespace MarketCore.AgentPanel
{
    public class SignalAggregator
    {
        private const double CONFIANCA_MINIMA = 0.60;
        private const double RR_MINIMO        = 1.5;

        public ResultadoAgregado Agregar(List<ResultadoDeteccao> deteccoes, MarketContext ctx)
        {
            if (!deteccoes.Any()) return ResultadoAgregado.Neutro;

            var sinaisCompra = deteccoes.Where(d => d.Direcao == Direcao.Compra).ToList();
            var sinaisVenda  = deteccoes.Where(d => d.Direcao == Direcao.Venda).ToList();

            var forcaCompra = CalcularForca(sinaisCompra);
            var forcaVenda  = CalcularForca(sinaisVenda);

            var direcao = forcaCompra > forcaVenda && forcaCompra > CONFIANCA_MINIMA ? Direcao.Compra :
                          forcaVenda > forcaCompra && forcaVenda > CONFIANCA_MINIMA ? Direcao.Venda  :
                          Direcao.Neutro;

            if (direcao == Direcao.Neutro) return ResultadoAgregado.Neutro;

            var confianca = Math.Max(forcaCompra, forcaVenda);
            var (stop, alvo) = CalcularStopAlvo(direcao, deteccoes, ctx);

            var risco   = Math.Abs(ctx.PrecoAtual - stop);
            var retorno = Math.Abs(alvo - ctx.PrecoAtual);
            var rr      = risco > 0 ? retorno / risco : 0;

            if (rr < RR_MINIMO) return ResultadoAgregado.Neutro;

            return new ResultadoAgregado
            {
                Detectado = true,
                Direcao   = direcao,
                Confianca = confianca,
                Stop      = stop,
                Alvo      = alvo,
                RR        = rr,
                Lote      = CalcularLote(confianca),
                Descricao = MontarDescricao(deteccoes, direcao)
            };
        }

        private double CalcularForca(List<ResultadoDeteccao> sinais)
        {
            if (!sinais.Any()) return 0;
            var pesos     = sinais.Select(s => s.Categoria == "CORRELAÇÃO" ? 1.5 : 1.0).ToList();
            var somaConf  = sinais.Select(s => s.Confianca).Zip(pesos, (c, p) => c * p).Sum();
            return somaConf / pesos.Sum();
        }

        private (double stop, double alvo) CalcularStopAlvo(
            Direcao direcao, List<ResultadoDeteccao> deteccoes, MarketContext ctx)
        {
            var stops = deteccoes.Where(d => d.Stop.HasValue).Select(d => d.Stop!.Value).ToList();
            var alvos = deteccoes.Where(d => d.Alvo.HasValue).Select(d => d.Alvo!.Value).ToList();

            if (direcao == Direcao.Compra)
                return (stops.Any() ? stops.Min() : ctx.ProximoSuporteLiquidez - 2,
                        alvos.Any() ? alvos.Max() : ctx.ProximaResistenciaLiquidez);
            else
                return (stops.Any() ? stops.Max() : ctx.ProximaResistenciaLiquidez + 2,
                        alvos.Any() ? alvos.Min() : ctx.ProximoSuporteLiquidez);
        }

        private int CalcularLote(double confianca) =>
            confianca >= 0.85 ? 3 : confianca >= 0.70 ? 2 : 1;

        private string MontarDescricao(List<ResultadoDeteccao> deteccoes, Direcao direcao)
        {
            var principais = deteccoes
                .Where(d => d.Direcao == direcao)
                .OrderByDescending(d => d.Confianca)
                .Take(3)
                .Select(d => d.Descricao);
            return string.Join(" | ", principais);
        }
    }

    public class ResultadoAgregado
    {
        public bool    Detectado  { get; set; }
        public Direcao Direcao    { get; set; }
        public double  Confianca  { get; set; }
        public double  Stop       { get; set; }
        public double  Alvo       { get; set; }
        public double  RR         { get; set; }
        public int     Lote       { get; set; }
        public string  Descricao  { get; set; } = "";

        public static ResultadoAgregado Neutro => new ResultadoAgregado
        {
            Detectado = false,
            Direcao   = Direcao.Neutro,
            Lote      = 0
        };
    }
}
