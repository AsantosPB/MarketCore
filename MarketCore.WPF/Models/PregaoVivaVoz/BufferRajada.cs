using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketCore.WPF.Models.PregaoVivaVoz
{
    /// <summary>
    /// Buffer circular pra rastrear agressões de um player em milissegundos.
    /// Usado pelo DetectorRajadaService pra identificar sequências rápidas.
    /// </summary>
    public class BufferRajada
    {
        /// <summary>Chave do player deste buffer.</summary>
        public string PlayerChave { get; set; } = "";
        
        /// <summary>
        /// Lado da rajada em andamento: "compra", "venda" ou null se parado.
        /// </summary>
        public string LadoAtivo { get; set; } = null!;
        
        /// <summary>
        /// Agressões recentes (mantém apenas as dentro da janela ms).
        /// </summary>
        public List<AgressaoBuffer> Agressoes { get; set; } = new List<AgressaoBuffer>();
        
        /// <summary>
        /// Se já detectou rajada em andamento (evita disparar "início" 2x).
        /// </summary>
        public bool RajadaEmAndamento { get; set; } = false;
        
        /// <summary>
        /// Timestamp da última agressão registrada.
        /// </summary>
        public DateTime UltimaAgressao { get; set; } = DateTime.MinValue;
        
        /// <summary>
        /// Volume acumulado da rajada em andamento.
        /// </summary>
        public int VolumeAcumulado { get; set; } = 0;
        
        /// <summary>
        /// Remove agressões antigas fora da janela de tempo.
        /// </summary>
        public void LimparAntigas(int janelaMs)
        {
            var limite = DateTime.Now.AddMilliseconds(-janelaMs);
            Agressoes.RemoveAll(a => a.Timestamp < limite);
        }
        
        /// <summary>
        /// Adiciona nova agressão ao buffer.
        /// </summary>
        public void AdicionarAgressao(int quantidade, string lado, DateTime timestamp)
        {
            Agressoes.Add(new AgressaoBuffer 
            { 
                Quantidade = quantidade, 
                Lado = lado, 
                Timestamp = timestamp 
            });
            UltimaAgressao = timestamp;
        }
        
        /// <summary>
        /// Calcula volume total das agressões de determinado lado.
        /// </summary>
        public int VolumeTotal(string lado)
        {
            return Agressoes.Where(a => a.Lado == lado).Sum(a => a.Quantidade);
        }
        
        /// <summary>
        /// Conta quantas agressões de determinado lado estão no buffer.
        /// </summary>
        public int ContarAgressoes(string lado)
        {
            return Agressoes.Count(a => a.Lado == lado);
        }
        
        /// <summary>
        /// Reset completo do buffer (após rajada parar).
        /// </summary>
        public void Reset()
        {
            Agressoes.Clear();
            LadoAtivo = null!;
            RajadaEmAndamento = false;
            VolumeAcumulado = 0;
        }
    }
    
    /// <summary>
    /// Representa uma agressão individual no buffer.
    /// </summary>
    public class AgressaoBuffer
    {
        public DateTime Timestamp { get; set; }
        public int Quantidade { get; set; }
        
        /// <summary>"compra" ou "venda"</summary>
        public string Lado { get; set; } = "";
    }
}
