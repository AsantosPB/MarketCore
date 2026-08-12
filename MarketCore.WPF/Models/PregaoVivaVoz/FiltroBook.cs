namespace MarketCore.WPF.Models.PregaoVivaVoz
{
    /// <summary>
    /// Filtros para eventos passivos do book (ordens colocadas).
    /// Quando player COLOCA ordem no book, sistema canta se qty >= limite.
    /// </summary>
    public class FiltroBook
    {
        /// <summary>
        /// Se o filtro do book está ativo para este player.
        /// </summary>
        public bool Ativo { get; set; } = true;
        
        /// <summary>
        /// Quantidade mínima para narrar ordem passiva de COMPRA.
        /// Exemplo: 100 = "Goldman compra 100 na boca" só narra se qty >= 100.
        /// </summary>
        public int CompraMinima { get; set; } = 100;
        
        /// <summary>
        /// Quantidade mínima para narrar ordem passiva de VENDA.
        /// </summary>
        public int VendaMinima { get; set; } = 100;
    }
}
