namespace MarketCore.AgentPanel.Detectors
{
    public interface IDetector
    {
        string Nome      { get; }
        string Categoria { get; }
        ResultadoDeteccao Analisar(MarketContext ctx);
    }

    public class ResultadoDeteccao
    {
        public bool     Detectado    { get; set; }
        public double   Confianca    { get; set; }
        public string   Descricao    { get; set; } = "";
        public Direcao  Direcao      { get; set; }
        public string   Categoria    { get; set; } = "";
        public string   NomeDetector { get; set; } = "";
        public double?  Stop         { get; set; }
        public double?  Alvo         { get; set; }

        public static ResultadoDeteccao Nenhum => new ResultadoDeteccao { Detectado = false };
    }

    public enum Direcao { Neutro, Compra, Venda }
}
