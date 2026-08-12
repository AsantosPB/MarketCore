using System;
using System.Collections.Generic;

namespace MarketCore.WPF.LeituraFluxo
{
    /// <summary>
    /// Os 3 tipos de padrão de execução detectáveis a partir de um único dia de dados
    /// (definição final acordada com o usuário — "Horário do dia" foi descartado por não
    /// ser verificável em uma única sessão).
    /// </summary>
    public enum FlowPatternType
    {
        /// <summary>Corretora executa recorrentemente no mesmo segundo do minuto, independente da hora.</summary>
        SegundoFixo,
        /// <summary>Corretora executa em um intervalo aproximadamente constante desde a execução anterior.</summary>
        IntervaloRegular,
        /// <summary>Quando o padrão acima se repete, o preço se move mais que o normal do dia logo em seguida.</summary>
        ImpactoPreco,
        /// <summary>Corretora concentra execuções sempre na mesma janela de um ciclo maior que 1 minuto
        /// (ex.: sempre perto da marca de a cada 3 minutos) — como o Segundo Fixo, mas pra ciclos longos.
        /// Diferente do Intervalo Regular, aguenta ordens picotadas em vários disparos por ciclo, porque olha
        /// a FASE dentro do ciclo (módulo), não a distância crua entre uma execução e a próxima.</summary>
        CicloFixo,
        /// <summary>Volume executado numa janela curta muito acima do normal da própria corretora — ex.:
        /// vinha executando aos poucos e de repente solta um volume bem maior num intervalo curto (uma
        /// "rajada"). Compara a corretora com ela mesma (linha de base é o volume médio dela na mesma janela
        /// de tempo), não com um número fixo — então se ajusta a corretoras de perfis de volume diferentes.</summary>
        RajadaVolume
    }

    /// <summary>
    /// Uma amostra crua de negócio usada internamente pelo motor (buffer por corretora + buffer global).
    /// </summary>
    internal readonly struct FlowTradeSample
    {
        public FlowTradeSample(DateTime time, decimal price, int volume, bool isBuy, string broker)
        {
            Time = time;
            Price = price;
            Volume = volume;
            IsBuy = isBuy;
            Broker = broker;
        }

        public DateTime Time { get; }
        public decimal Price { get; }
        public int Volume { get; }
        public bool IsBuy { get; }
        public string Broker { get; }
    }

    /// <summary>Uma execução concreta usada como "prova" de um padrão detectado — permite ao usuário conferir
    /// na fita/Profit Chart se o negócio realmente aconteceu, em vez de confiar só na estatística resumida.</summary>
    public readonly struct FlowPatternExample
    {
        public FlowPatternExample(DateTime time, decimal price, int volume)
        {
            Time = time;
            Price = price;
            Volume = volume;
        }

        public DateTime Time { get; }
        public decimal Price { get; }
        public int Volume { get; }
    }

    /// <summary>Uma linha para o mini-tape "Times &amp; Trades — captura ao vivo" da janela.</summary>
    public sealed class FlowTapeRow
    {
        public DateTime Time { get; init; }
        public decimal Price { get; init; }
        public int Volume { get; init; }
        public string Broker { get; init; } = "";
        public bool IsBuy { get; init; }
    }

    /// <summary>
    /// Uma entrada no histórico "últimos padrões encontrados" de uma corretora.
    /// É mutável de propósito: enquanto o mesmo padrão continua ativo, a entrada existente é
    /// atualizada (Detail/Confiança evoluem) em vez de criar uma entrada nova a cada tick.
    /// </summary>
    public sealed class FlowPatternMatch
    {
        public DateTime FoundAt { get; set; }
        public DateTime LastConfirmedAt { get; set; }
        public FlowPatternType Type { get; set; }
        /// <summary>Lado da execução que gerou o padrão: true = compra (a corretora era o agressor comprador),
        /// false = venda. Os 3 detectores rodam separadamente para compra e venda — um padrão nunca mistura
        /// os dois lados, porque uma corretora pode ter um comportamento recorrente distinto em cada ponta
        /// (ex.: compra sempre no segundo :31, mas venda sem padrão nenhum).</summary>
        public bool IsBuySide { get; set; }
        public string Detail { get; set; } = "";
        /// <summary>Percentual de confiança (0-100). Nulo para entradas do tipo Impacto — essas mostram PointsMoved.</summary>
        public double? ConfidencePct { get; set; }
        /// <summary>Pontos de preço movidos. Usado somente em entradas do tipo ImpactoPreco.</summary>
        public int? PointsMoved { get; set; }

        /// <summary>Previsão de quando a próxima execução deste padrão deve acontecer — Segundo Fixo, Ciclo
        /// Fixo e Intervalo Regular sempre calculam; Rajada de Volume só quando já identificou repetição
        /// regular no tempo entre rajadas; Impacto no Preço nunca (não tem "próximo horário" previsível).
        /// Recalculada do zero a cada tick (a partir do agora atual), então sempre reflete a próxima
        /// ocorrência a partir deste instante.</summary>
        public DateTime? NextExpectedAt { get; set; }

        /// <summary>Estimativa de quantos lotes a próxima execução deve ter, baseada no histórico recente
        /// deste padrão. Nulo quando não há como estimar (ex.: Impacto no Preço).</summary>
        public int? ExpectedVolume { get; set; }

        /// <summary>Até 5 execuções reais que compõem este padrão (mais recentes primeiro) — hora, preço e
        /// quantidade exatos, pra o usuário conferir na fita/Profit Chart se a execução realmente aconteceu.
        /// Sem isso a tela só mostra uma estatística resumida ("41 de 331 negócios"), sem nenhum jeito de
        /// verificar manualmente qual negócio concreto está por trás do número.</summary>
        public IReadOnlyList<FlowPatternExample> Examples { get; set; } = Array.Empty<FlowPatternExample>();

        /// <summary>Chave interna usada para decidir se uma nova detecção é "a mesma" ocorrência continuando
        /// (atualiza esta entrada) ou uma ocorrência genuinamente nova (cria entrada nova).</summary>
        internal string BucketKey { get; set; } = "";
    }

    /// <summary>Foto do estado de uma corretora em um instante, pronta para bind na UI.</summary>
    public sealed class BrokerFlowSnapshot
    {
        public string Broker { get; init; } = "";
        public long BuyVolume { get; init; }
        public long SellVolume { get; init; }
        public int TradeCount { get; init; }
        public double BuyPct => (BuyVolume + SellVolume) > 0 ? BuyVolume * 100.0 / (BuyVolume + SellVolume) : 50.0;
        public double SellPct => 100.0 - BuyPct;
        /// <summary>Últimos até 5 padrões encontrados, mais recente primeiro.</summary>
        public IReadOnlyList<FlowPatternMatch> LastPatterns { get; init; } = Array.Empty<FlowPatternMatch>();

        public static BrokerFlowSnapshot Empty(string broker) => new() { Broker = broker };
    }

    /// <summary>Resultado de uma janela de "agressão por quantidade executada".</summary>
    public sealed class AggressionWindowResult
    {
        public int TargetQty { get; init; }
        public long ActualQty { get; init; }
        public double BuyPct { get; init; } = 50.0;
        public double SellPct => 100.0 - BuyPct;
        public int PointsMoved { get; init; }
    }
}
