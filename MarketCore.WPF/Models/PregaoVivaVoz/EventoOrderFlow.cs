using System;

namespace MarketCore.WPF.Models.PregaoVivaVoz
{
    /// <summary>
    /// Tipos de evento que o motor detecta no order flow.
    /// </summary>
    public enum TipoEvento
    {
        /// <summary>Player COLOCOU ordem passiva de COMPRA no book.</summary>
        BookCompra,
        
        /// <summary>Player COLOCOU ordem passiva de VENDA no book.</summary>
        BookVenda,
        
        /// <summary>Player agrediu comprando (tomou no ask).</summary>
        AgressaoCompra,
        
        /// <summary>Player agrediu vendendo (bateu no bid).</summary>
        AgressaoVenda,
        
        /// <summary>Player INICIOU rajada compradora.</summary>
        RajadaInicioCompra,
        
        /// <summary>Player INICIOU rajada vendedora.</summary>
        RajadaInicioVenda,
        
        /// <summary>Player PAROU rajada compradora.</summary>
        RajadaPararCompra,
        
        /// <summary>Player PAROU rajada vendedora.</summary>
        RajadaPararVenda
    }
    
    /// <summary>
    /// Evento de order flow detectado pelo motor.
    /// Contém tudo que o FraseBuilder precisa pra montar a narração.
    /// </summary>
    public class EventoOrderFlow
    {
        /// <summary>
        /// Timestamp preciso do evento (com milissegundos).
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;
        
        /// <summary>
        /// Chave do player que originou o evento.
        /// </summary>
        public string PlayerChave { get; set; } = "";
        
        /// <summary>
        /// Nome do player pra exibição/log.
        /// </summary>
        public string PlayerNome { get; set; } = "";
        
        /// <summary>
        /// Tipo do evento (define qual frase montar).
        /// </summary>
        public TipoEvento Tipo { get; set; }
        
        /// <summary>
        /// Quantidade envolvida no evento (contratos).
        /// Nas rajadas: volume acumulado.
        /// </summary>
        public int Quantidade { get; set; }
        
        /// <summary>
        /// Nível do book onde o evento ocorreu (1 a 5).
        /// 1 = "na boca", 2-5 = "no dois/três/quatro/cinco".
        /// Só relevante em eventos passivos (BookCompra/BookVenda).
        /// </summary>
        public int Nivel { get; set; } = 1;
        
        /// <summary>
        /// Preço no momento do evento (opcional, pra log).
        /// </summary>
        public double Preco { get; set; }
        
        /// <summary>
        /// Ticker relacionado (ex: WINQ26).
        /// </summary>
        public string Ticker { get; set; } = "";
    }
}
