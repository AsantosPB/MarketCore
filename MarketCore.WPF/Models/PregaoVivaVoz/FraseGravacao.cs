using System;
using System.Text.Json.Serialization;

namespace MarketCore.WPF.Models.PregaoVivaVoz
{
    /// <summary>
    /// Categoria de frase pra organização no Studio.
    /// </summary>
    public enum CategoriaFrase
    {
        Numero,
        Nivel,
        Complemento,
        AlertaRajada,
        PlayerBook,
        PlayerAgressao,
        PlayerRajada
    }
    
    /// <summary>
    /// Representa uma frase (clip) que precisa ser gravada no Studio.
    /// </summary>
    public class FraseGravacao
    {
        /// <summary>
        /// Identificador único da frase. Ex: "gs_tomou", "500", "na_boca".
        /// </summary>
        public string Id { get; set; } = "";
        
        /// <summary>
        /// Categoria pra agrupar no Studio.
        /// </summary>
        public CategoriaFrase Categoria { get; set; }
        
        /// <summary>
        /// Chave do player relacionado (só se PlayerXxx). Ex: "goldman".
        /// </summary>
        public string PlayerChave { get; set; } = "";
        
        /// <summary>
        /// Texto que aparece pra ler durante a gravação.
        /// Ex: "Goldman tomou", "quinhentos", "na boca".
        /// </summary>
        public string TextoParaFalar { get; set; } = "";
        
        /// <summary>
        /// Dica curta de contexto/entonação.
        /// Ex: "agressão de compra", "tom aberto no final".
        /// </summary>
        public string DicaContexto { get; set; } = "";
        
        /// <summary>
        /// Nome do arquivo .wav (relativo à pasta da categoria).
        /// Ex: "gs_tomou.wav", "500.wav".
        /// </summary>
        public string NomeArquivo { get; set; } = "";
        
        /// <summary>
        /// Caminho completo do arquivo (montado dinamicamente).
        /// </summary>
        public string CaminhoCompleto { get; set; } = "";
        
        // ============ ESTADO ============
        
        /// <summary>
        /// Se o clip já foi gravado.
        /// </summary>
        public bool Gravado { get; set; } = false;
        
        /// <summary>
        /// Duração do clip em segundos (após gravar).
        /// </summary>
        public double DuracaoSegundos { get; set; } = 0;
        
        /// <summary>
        /// Data/hora da gravação.
        /// </summary>
        public DateTime? DataGravacao { get; set; } = null;
        
        /// <summary>
        /// Se o clip tem algum problema detectado.
        /// </summary>
        public bool TemErro { get; set; } = false;
        
        /// <summary>
        /// Descrição do erro se TemErro=true.
        /// Ex: "Volume muito baixo (-25dB)".
        /// </summary>
        public string MensagemErro { get; set; } = "";
        
        /// <summary>
        /// Se é uma frase prioritária (marcador visual "🎯").
        /// Ex: números muito comuns como 500, 1000.
        /// </summary>
        public bool Prioritaria { get; set; } = false;
    }
}
