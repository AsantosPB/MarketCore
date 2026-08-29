using System.Text.Json.Serialization;

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

        /// <summary>
        /// Limiar para NARRAÇÃO PRIORITÁRIA no book: quando a oferta atinge
        /// esse volume (nível 1..4), a narração interrompe o áudio atual,
        /// descarta a fila pendente e é reproduzida imediatamente.
        ///
        /// Valor <c>null</c> → prioridade desligada para este player
        /// (comportamento 100% igual ao anterior). Valor inteiro > 0 → ativa.
        ///
        /// É INDEPENDENTE de <see cref="CompraMinima"/>/<see cref="VendaMinima"/>:
        /// uma oferta pode acionar só o filtro normal, só o de prioridade, ambos
        /// (nesse caso só a prioridade narra) ou nenhum.
        /// </summary>
        public int? PrioridadeMinima { get; set; } = null;

        /// <summary>
        /// Wrapper de string para binding no TextBox do XAML (WPF não converte
        /// bem <c>""</c> ↔ <c>int?</c> nativamente). Não é persistido no JSON
        /// (<c>[JsonIgnore]</c>). Vazio ou não-numérico → <c>PrioridadeMinima = null</c>;
        /// número positivo → <c>PrioridadeMinima = valor</c>.
        /// </summary>
        [JsonIgnore]
        public string PrioridadeMinimaTexto
        {
            get => PrioridadeMinima?.ToString() ?? string.Empty;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    PrioridadeMinima = null;
                    return;
                }
                if (int.TryParse(value.Trim(), out var n) && n > 0)
                    PrioridadeMinima = n;
                else
                    PrioridadeMinima = null;
            }
        }
    }
}
