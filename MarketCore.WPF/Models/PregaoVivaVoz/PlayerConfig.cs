using System;
using System.Text.Json.Serialization;

namespace MarketCore.WPF.Models.PregaoVivaVoz
{
    /// <summary>
    /// Configuração completa de um player (corretora) no Pregão Viva Voz.
    /// Cada player tem: identificação, filtros de book, agressão e rajada.
    /// </summary>
    public class PlayerConfig
    {
        // ============ IDENTIFICAÇÃO ============
        
        /// <summary>
        /// Chave única do player (usado internamente). Ex: "goldman", "jpm"
        /// </summary>
        public string Chave { get; set; } = "";
        
        /// <summary>
        /// Nome que aparece na interface. Ex: "Goldman", "JPM"
        /// </summary>
        public string Nome { get; set; } = "";
        
        /// <summary>
        /// Código curto do player. Ex: "GS", "JPM", "BTG"
        /// </summary>
        public string Codigo { get; set; } = "";
        
        /// <summary>
        /// Pronúncia sugerida pra gravação. Ex: "GÓLD-man"
        /// </summary>
        public string Pronuncia { get; set; } = "";
        
        /// <summary>
        /// País de origem (usa emoji). Ex: "🇺🇸", "🇧🇷"
        /// </summary>
        public string Bandeira { get; set; } = "";
        
        /// <summary>
        /// Categoria: S (Big Player), A (Institucional), B (Varejo forte), C (Monitorar)
        /// </summary>
        public string Tier { get; set; } = "B";
        
        /// <summary>
        /// Perfil do player: "Big Player", "Institucional", "Interdealer", "Varejo"
        /// </summary>
        public string Perfil { get; set; } = "";
        
        // ============ ESTADO DE MONITORAMENTO ============
        
        /// <summary>
        /// Se o player está sendo monitorado hoje. Toggle master.
        /// Quando false: sistema ignora eventos deste player, mas mantém config.
        /// </summary>
        public bool AtivoHoje { get; set; } = false;
        
        /// <summary>
        /// Se áudios foram gravados. Sistema não deixa ativar sem áudios.
        /// </summary>
        public bool AudiosGravados { get; set; } = false;
        
        // ============ FILTROS ============
        
        /// <summary>
        /// Filtros de eventos do book (ordens passivas)
        /// </summary>
        public FiltroBook Book { get; set; } = new FiltroBook();
        
        /// <summary>
        /// Filtros de eventos de agressão (tomou/bateu)
        /// </summary>
        public FiltroAgressao Agressao { get; set; } = new FiltroAgressao();
        
        /// <summary>
        /// Participação no detector de rajada
        /// </summary>
        public FiltroRajada Rajada { get; set; } = new FiltroRajada();
        
        // ============ ESTATÍSTICAS DO DIA (não persistidas) ============
        
        [JsonIgnore]
        public int CantouHoje { get; set; } = 0;
        
        [JsonIgnore]
        public DateTime UltimaCantada { get; set; } = DateTime.MinValue;
    }
}
