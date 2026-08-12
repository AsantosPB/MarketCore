namespace MarketCore.WPF.Models.PregaoVivaVoz
{
    /// <summary>
    /// Configuração de participação do player no detector de rajada.
    /// Parâmetros globais da rajada estão em ConfigRajadaGlobal.
    /// </summary>
    public class FiltroRajada
    {
        /// <summary>
        /// Se este player participa da detecção de rajadas.
        /// </summary>
        public bool Participa { get; set; } = false;
    }
    
    /// <summary>
    /// Parâmetros globais do detector de rajada.
    /// Aplicam-se a TODOS os players que participam.
    /// </summary>
    public class ConfigRajadaGlobal
    {
        /// <summary>
        /// Quantas agressões seguidas para considerar rajada.
        /// Padrão: 3 agressões.
        /// </summary>
        public int SequenciaMinima { get; set; } = 3;
        
        /// <summary>
        /// Janela de tempo em MILISSEGUNDOS para as agressões ocorrerem.
        /// Padrão: 2000ms (2 segundos) - captura rajadas rápidas do WIN.
        /// </summary>
        public int JanelaMilissegundos { get; set; } = 2000;
        
        /// <summary>
        /// Volume mínimo acumulado (soma das agressões) para disparar alerta.
        /// Padrão: 200 contratos.
        /// </summary>
        public int VolumeMinimo { get; set; } = 200;
        
        /// <summary>
        /// Tempo em MILISSEGUNDOS sem nova agressão para considerar que PAROU.
        /// Padrão: 3000ms (3 segundos).
        /// Este é o gatilho mais valioso do sistema.
        /// </summary>
        public int SilencioParouMilissegundos { get; set; } = 3000;
    }
}
