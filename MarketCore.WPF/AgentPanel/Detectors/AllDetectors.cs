// Devido ao limite de espaço, vou criar um arquivo consolidado com os detectores mais importantes
// Você pode expandir os demais seguindo esses templates

using System;
using System.Linq;

namespace MarketCore.AgentPanel.Detectors
{
    // ═══════════════════════════════════════════════════════
    // TAPE DETECTORS
    // ═══════════════════════════════════════════════════════

    public class RafadaVendaDetector : IDetector
    {
        public string Nome => "Rafada Vendedora";
        public string Categoria => "TAPE";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            if (ctx.AgressaoVenda60s < 300 || ctx.AgressaoVenda60s < ctx.AgressaoCompra60s * 2)
                return ResultadoDeteccao.Nenhum;

            var conf = 0.65;
            if (ctx.BookImbalance < 0.4) conf += 0.1;
            if (ctx.CVDAceleracao5s < -10) conf += 0.1;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = Math.Clamp(conf, 0, 1),
                Descricao = $"Rafada vendedora detectada. {ctx.AgressaoVenda60s} lotes.",
                Direcao   = Direcao.Venda,
                Stop      = ctx.PrecoAtual + 3,
                Alvo      = ctx.ProximoSuporteLiquidez
            };
        }
    }

    public class StopHuntDetector : IDetector
    {
        public string Nome => "Stop Hunt";
        public string Categoria => "TAPE";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            // Verifica movimento rápido seguido de reversão
            // Na prática você precisa do histórico tick-a-tick
            if (ctx.UltimosPrecos.Count < 5)
                return ResultadoDeteccao.Nenhum;

            var max = ctx.UltimosPrecos.Max();
            var min = ctx.UltimosPrecos.Min();
            var range = max - min;

            // Movimento rápido > 5 pontos e voltou
            var houvePico = range > 5 && Math.Abs(ctx.PrecoAtual - ctx.UltimosPrecos[0]) < 2;

            if (!houvePico)
                return ResultadoDeteccao.Nenhum;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = 0.68,
                Descricao = "Stop hunt detectado. Preço perfurou e voltou rapidamente.",
                Direcao   = ctx.PrecoAtual > ctx.UltimosPrecos[0] ? Direcao.Compra : Direcao.Venda
            };
        }
    }

    // ═══════════════════════════════════════════════════════
    // BOOK DETECTORS
    // ═══════════════════════════════════════════════════════

    public class IcebergDetector : IDetector
    {
        public string Nome => "Iceberg";
        public string Categoria => "BOOK";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            if (!ctx.IcebergDetectadoBid && !ctx.IcebergDetectadoAsk)
                return ResultadoDeteccao.Nenhum;

            var lado = ctx.IcebergDetectadoBid ? "compra" : "venda";
            var direcao = ctx.IcebergDetectadoBid ? Direcao.Compra : Direcao.Venda;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = 0.72,
                Descricao = $"Iceberg identificado no lado {lado}. Ordem grande sendo fatiada.",
                Direcao   = direcao
            };
        }
    }

    public class SpoofingDetector : IDetector
    {
        public string Nome => "Spoofing";
        public string Categoria => "BOOK";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            if (!ctx.SpoofingDetectado)
                return ResultadoDeteccao.Nenhum;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = 0.60,
                Descricao = "Spoofing detectado. Muro fantasma cancelado antes de execução.",
                Direcao   = Direcao.Neutro  // direção oposta ao muro
            };
        }
    }

    public class VacuoLiquidezDetector : IDetector
    {
        public string Nome => "Vácuo de Liquidez";
        public string Categoria => "BOOK";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            // Verifica se há pouca liquidez acima/abaixo do preço
            var distanciaResistencia = Math.Abs(ctx.ProximaResistenciaLiquidez - ctx.PrecoAtual);
            var distanciaSuporte     = Math.Abs(ctx.PrecoAtual - ctx.ProximoSuporteLiquidez);

            var vacuoCima  = distanciaResistencia > 10;  // 10 pontos sem resistência
            var vacuoBaixo = distanciaSuporte > 10;

            if (!vacuoCima && !vacuoBaixo)
                return ResultadoDeteccao.Nenhum;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = 0.70,
                Descricao = vacuoCima 
                    ? "Vácuo de liquidez acima. Preço pode subir rápido se romper."
                    : "Vácuo de liquidez abaixo. Preço pode cair rápido se romper.",
                Direcao   = vacuoCima ? Direcao.Compra : Direcao.Venda
            };
        }
    }

    public class ThinMarketDetector : IDetector
    {
        public string Nome => "Thin Market";
        public string Categoria => "BOOK";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            if (!ctx.ThinMarket)
                return ResultadoDeteccao.Nenhum;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = 0.0,  // reduz confiança de outros sinais
                Descricao = "Mercado fino detectado. Volume total do book baixo. Cautela.",
                Direcao   = Direcao.Neutro
            };
        }
    }

    // ═══════════════════════════════════════════════════════
    // PREÇO VS VOLUME DETECTORS
    // ═══════════════════════════════════════════════════════

    public class AbsorcaoSuporteDetector : IDetector
    {
        public string Nome => "Absorção em Suporte";
        public string Categoria => "PADRÃO";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            // Volume vendedor alto mas preço não cai
            var volumeVendaAlto = ctx.AgressaoVenda60s > ctx.AgressaoCompra60s * 1.5;
            var precoLateral    = ctx.UltimosPrecos.Count > 3 &&
                                  ctx.UltimosPrecos.Max() - ctx.UltimosPrecos.Min() < 3;

            if (!volumeVendaAlto || !precoLateral)
                return ResultadoDeteccao.Nenhum;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = 0.78,
                Descricao = "Absorção em suporte detectada. Comprador passivo segurando o nível.",
                Direcao   = Direcao.Compra,
                Stop      = ctx.ProximoSuporteLiquidez - 2,
                Alvo      = ctx.ProximaResistenciaLiquidez
            };
        }
    }

    public class AbsorcaoResistenciaDetector : IDetector
    {
        public string Nome => "Absorção em Resistência";
        public string Categoria => "PADRÃO";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            var volumeCompraAlto = ctx.AgressaoCompra60s > ctx.AgressaoVenda60s * 1.5;
            var precoLateral     = ctx.UltimosPrecos.Count > 3 &&
                                   ctx.UltimosPrecos.Max() - ctx.UltimosPrecos.Min() < 3;

            if (!volumeCompraAlto || !precoLateral)
                return ResultadoDeteccao.Nenhum;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = 0.78,
                Descricao = "Absorção em resistência detectada. Vendedor passivo segurando.",
                Direcao   = Direcao.Venda,
                Stop      = ctx.ProximaResistenciaLiquidez + 2,
                Alvo      = ctx.ProximoSuporteLiquidez
            };
        }
    }

    public class DivergenciaCVDDetector : IDetector
    {
        public string Nome => "Divergência CVD";
        public string Categoria => "PADRÃO";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            if (ctx.UltimosCVD.Count < 5 || ctx.UltimosPrecos.Count < 5)
                return ResultadoDeteccao.Nenhum;

            var precoSubindo = ctx.UltimosPrecos.Last() > ctx.UltimosPrecos.First();
            var cvdCaindo    = ctx.UltimosCVD.Last() < ctx.UltimosCVD.First();

            var precoCaindo  = ctx.UltimosPrecos.Last() < ctx.UltimosPrecos.First();
            var cvdSubindo   = ctx.UltimosCVD.Last() > ctx.UltimosCVD.First();

            var divergenciaBaixista = precoSubindo && cvdCaindo;
            var divergenciaAltista  = precoCaindo && cvdSubindo;

            if (!divergenciaBaixista && !divergenciaAltista)
                return ResultadoDeteccao.Nenhum;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = 0.75,
                Descricao = divergenciaBaixista
                    ? "Divergência baixista CVD. Preço subindo mas fluxo caindo."
                    : "Divergência altista CVD. Preço caindo mas fluxo subindo.",
                Direcao   = divergenciaBaixista ? Direcao.Venda : Direcao.Compra
            };
        }
    }

    public class ExaustaoMovimentoDetector : IDetector
    {
        public string Nome => "Exaustão de Movimento";
        public string Categoria => "PADRÃO";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            // CVD acelerando mas preço parando
            var cvdAcelerando = Math.Abs(ctx.CVDAceleracao5s) > 15;
            var precoLateral  = ctx.UltimosPrecos.Count > 3 &&
                                ctx.UltimosPrecos.Max() - ctx.UltimosPrecos.Min() < 2;

            if (!cvdAcelerando || !precoLateral)
                return ResultadoDeteccao.Nenhum;

            var direcao = ctx.CVDAceleracao5s > 0 ? Direcao.Venda : Direcao.Compra;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = 0.71,
                Descricao = "Exaustão detectada. CVD acelerando mas preço não acompanha.",
                Direcao   = direcao  // reversão
            };
        }
    }

    public class ClimaxVolumeDetector : IDetector
    {
        public string Nome => "Climax de Volume";
        public string Categoria => "PADRÃO";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            // Volume muito acima da média mas preço não move proporcionalmente
            var volumeTotal = ctx.AgressaoCompra60s + ctx.AgressaoVenda60s;
            var climax      = ctx.RVOL > 2.0;  // 2x a média

            if (!climax)
                return ResultadoDeteccao.Nenhum;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = 0.69,
                Descricao = "Climax de volume detectado. Possível reversão ou pausa.",
                Direcao   = Direcao.Neutro
            };
        }
    }

    // ═══════════════════════════════════════════════════════
    // PRESSÃO DETECTORS
    // ═══════════════════════════════════════════════════════

    public class BookImbalanceDetector : IDetector
    {
        public string Nome => "Book Imbalance Forte";
        public string Categoria => "BOOK";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            var imbalanceForte = ctx.BookImbalance > 0.75 || ctx.BookImbalance < 0.25;

            if (!imbalanceForte)
                return ResultadoDeteccao.Nenhum;

            var direcao = ctx.BookImbalance > 0.5 ? Direcao.Compra : Direcao.Venda;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = 0.68,
                Descricao = $"Book com {ctx.BookImbalance * 100:0}% de desequilíbrio.",
                Direcao   = direcao
            };
        }
    }

    public class TickImbalanceDetector : IDetector
    {
        public string Nome => "Tick Imbalance";
        public string Categoria => "TAPE";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            var imbalance = Math.Abs(ctx.TickImbalance);
            var forte     = imbalance > 35;  // dos 50 ticks, 35+ num lado

            if (!forte)
                return ResultadoDeteccao.Nenhum;

            var direcao = ctx.TickImbalance > 0 ? Direcao.Compra : Direcao.Venda;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = 0.66,
                Descricao = $"Tick imbalance: {imbalance}/50 ticks em um lado. Momentum definido.",
                Direcao   = direcao
            };
        }
    }

    // ═══════════════════════════════════════════════════════
    // PLAYER DETECTORS
    // ═══════════════════════════════════════════════════════

    public class PlayerDominanteDetector : IDetector
    {
        public string Nome => "Player Dominante";
        public string Categoria => "PLAYER";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            var concentrado = ctx.ConcentracaoFlow > 0.6;  // top 2 brokers > 60%

            if (!concentrado)
                return ResultadoDeteccao.Nenhum;

            // Verifica perfil do broker dominante
            var topBroker = ctx.TopCompradores.FirstOrDefault() ?? ctx.TopVendedores.FirstOrDefault();
            if (topBroker == null)
                return ResultadoDeteccao.Nenhum;

            var conf = topBroker.Perfil == PerfilBroker.Iniciador ? 0.80 : 0.65;

            var lado = ctx.TopCompradores.Any() && ctx.TopCompradores.First().Volume > 
                       (ctx.TopVendedores.FirstOrDefault()?.Volume ?? 0)
                ? Direcao.Compra : Direcao.Venda;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = conf,
                Descricao = $"{topBroker.Nome} dominando o flow. Concentração {ctx.ConcentracaoFlow * 100:0}%.",
                Direcao   = lado
            };
        }
    }

    public class SincronizacaoBrokersDetector : IDetector
    {
        public string Nome => "Sincronização de Brokers";
        public string Categoria => "PLAYER";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            // 3+ brokers iniciadores no mesmo lado
            var iniciadoresCompra = ctx.TopCompradores.Count(b => b.Perfil == PerfilBroker.Iniciador);
            var iniciadoresVenda  = ctx.TopVendedores.Count(b => b.Perfil == PerfilBroker.Iniciador);

            var sincronizado = iniciadoresCompra >= 3 || iniciadoresVenda >= 3;

            if (!sincronizado)
                return ResultadoDeteccao.Nenhum;

            var direcao = iniciadoresCompra >= 3 ? Direcao.Compra : Direcao.Venda;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = 0.85,  // alta confiança
                Descricao = $"Sincronização detectada. {Math.Max(iniciadoresCompra, iniciadoresVenda)} iniciadores alinhados.",
                Direcao   = direcao
            };
        }
    }

    public class PlayerMudouLadoDetector : IDetector
    {
        public string Nome => "Player Mudou de Lado";
        public string Categoria => "PLAYER";

        // Este detector precisa de histórico do broker ao longo da sessão
        // Implementação simplificada
        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            // Na prática você manteria histórico de posicionamento por broker
            // e detectaria quando um broker influente inverte
            return ResultadoDeteccao.Nenhum;
        }
    }

    // ═══════════════════════════════════════════════════════
    // CORRELAÇÃO DETECTORS
    // ═══════════════════════════════════════════════════════

    public class ConvergenciaTotalDetector : IDetector
    {
        public string Nome => "Convergência Total";
        public string Categoria => "CORRELAÇÃO";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            // WSP subindo + WDO caindo + WIN com padrão de compra
            var convergenciaAltista = ctx.WSP_Variacao > 0.2 &&
                                      ctx.WDO_Variacao < -0.1 &&
                                      ctx.BookImbalance > 0.6;

            // WSP caindo + WDO subindo + WIN com padrão de venda
            var convergenciaBaixista = ctx.WSP_Variacao < -0.2 &&
                                       ctx.WDO_Variacao > 0.1 &&
                                       ctx.BookImbalance < 0.4;

            if (!convergenciaAltista && !convergenciaBaixista)
                return ResultadoDeteccao.Nenhum;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = 0.88,  // altíssima confiança
                Descricao = convergenciaAltista
                    ? "Convergência total altista. WSP↑ + WDO↓ + WIN alinhado."
                    : "Convergência total baixista. WSP↓ + WDO↑ + WIN alinhado.",
                Direcao   = convergenciaAltista ? Direcao.Compra : Direcao.Venda
            };
        }
    }

    public class DivergenciaWSPDetector : IDetector
    {
        public string Nome => "Divergência WSP";
        public string Categoria => "CORRELAÇÃO";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            // WSP moveu mas WIN não reagiu ainda — janela de antecipação
            var gapSignificativo = Math.Abs(ctx.GapWinWsp) > 0.15;
            var lagNormal        = ctx.LagWinWsp > 15 && ctx.LagWinWsp < 45;

            if (!gapSignificativo || !lagNormal)
                return ResultadoDeteccao.Nenhum;

            var direcao = ctx.GapWinWsp > 0 ? Direcao.Compra : Direcao.Venda;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = 0.76,
                Descricao = $"WSP moveu {ctx.WSP_Variacao:+0.00;-0.00}%. WIN com lag de {ctx.LagWinWsp}s. Antecipação possível.",
                Direcao   = direcao
            };
        }
    }

    public class ConflitoWDODetector : IDetector
    {
        public string Nome => "Conflito WDO";
        public string Categoria => "CORRELAÇÃO";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            // WIN querendo subir (book comprador) mas WDO também subindo (conflito)
            var conflitoAltista = ctx.BookImbalance > 0.65 && ctx.WDO_Variacao > 0.2;

            // WIN querendo cair mas WDO caindo (deveria ajudar WIN a subir)
            var conflitoBaixista = ctx.BookImbalance < 0.35 && ctx.WDO_Variacao < -0.2;

            if (!conflitoAltista && !conflitoBaixista)
                return ResultadoDeteccao.Nenhum;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = 0.40,  // reduz confiança de outros sinais
                Descricao = "Conflito com WDO detectado. Correlação negativa sendo testada.",
                Direcao   = Direcao.Neutro
            };
        }
    }

    // ═══════════════════════════════════════════════════════
    // CONTEXTO TEMPORAL DETECTORS
    // ═══════════════════════════════════════════════════════

    public class HorarioFavoravelDetector : IDetector
    {
        public string Nome => "Horário Favorável";
        public string Categoria => "CONTEXTO";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            var favoravel = ctx.Fase == FaseSessao.AltaLiquidez;

            return new ResultadoDeteccao
            {
                Detectado = favoravel,
                Confianca = favoravel ? 1.0 : 0.5,  // multiplica confiança de outros
                Descricao = favoravel
                    ? "Horário de alta liquidez. Padrões mais confiáveis."
                    : "Horário de baixa liquidez. Cautela com sinais.",
                Direcao   = Direcao.Neutro
            };
        }
    }

    public class EventoMacroProximoDetector : IDetector
    {
        public string Nome => "Evento Macro Próximo";
        public string Categoria => "CONTEXTO";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            var temEvento = !string.IsNullOrEmpty(ctx.ProximoEvento);

            if (!temEvento)
                return ResultadoDeteccao.Nenhum;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = 0.3,  // reduz drasticamente
                Descricao = $"Evento próximo: {ctx.ProximoEvento}. Cautela — mercado pode estar nervoso.",
                Direcao   = Direcao.Neutro
            };
        }
    }

    public class RafadaCompraDetector : IDetector
    {
        public string Nome => "Rafada Compradora";
        public string Categoria => "TAPE";

        public ResultadoDeteccao Analisar(MarketContext ctx)
        {
            var agressaoConcentrada = ctx.AgressaoCompra60s > 300;
            var volumeAcimaNormal   = ctx.AgressaoCompra60s > ctx.AgressaoVenda60s * 2;

            if (!agressaoConcentrada || !volumeAcimaNormal)
                return ResultadoDeteccao.Nenhum;

            var conf = 0.65;
            if (ctx.BookImbalance > 0.6)  conf += 0.1;
            if (ctx.CVDAceleracao5s > 10) conf += 0.1;
            if (ctx.WDO_Variacao > 0.2)   conf -= 0.15;

            return new ResultadoDeteccao
            {
                Detectado = true,
                Confianca = Math.Clamp(conf, 0, 1),
                Descricao = $"Rafada compradora. {ctx.AgressaoCompra60s} lotes em curto per�odo.",
                Direcao   = Direcao.Compra,
                Stop      = ctx.ProximoSuporteLiquidez - 2,
                Alvo      = ctx.ProximaResistenciaLiquidez
            };
        }
    }
}
