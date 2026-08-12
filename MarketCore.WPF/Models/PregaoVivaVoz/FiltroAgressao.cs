namespace MarketCore.WPF.Models.PregaoVivaVoz
{
    /// <summary>
    /// Filtros para eventos de agressão (ordens agressivas).
    /// Quando player AGRIDE, sistema canta se qty >= limite.
    /// - "Tomou" = agressão de compra (comprou no ask)
    /// - "Bateu" = agressão de venda (vendeu no bid)
    /// </summary>
    public class FiltroAgressao
    {
        /// <summary>
        /// Se o filtro de agressão está ativo para este player.
        /// </summary>
        public bool Ativo { get; set; } = true;
        
        /// <summary>
        /// Quantidade mínima para narrar agressão de compra (tomou).
        /// Exemplo: 50 = "Goldman tomou 50" só narra se qty >= 50.
        /// </summary>
        public int TomouMinimo { get; set; } = 100;
        
        /// <summary>
        /// Quantidade mínima para narrar agressão de venda (bateu).
        /// </summary>
        public int BateuMinimo { get; set; } = 100;
    }
}
