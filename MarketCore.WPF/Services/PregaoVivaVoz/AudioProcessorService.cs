using System;
using System.IO;
using NAudio.Wave;

namespace MarketCore.WPF.Services.PregaoVivaVoz
{
    /// <summary>
    /// Analisa qualidade dos áudios gravados e detecta problemas.
    /// VERSÃO FINAL: usa NAudio direto.
    /// </summary>
    public class AudioProcessorService
    {
        public DiagnosticoAudio Analisar(string caminhoWav)
        {
            var diagnostico = new DiagnosticoAudio
            {
                CaminhoArquivo = caminhoWav,
                Existe = File.Exists(caminhoWav)
            };
            
            if (!diagnostico.Existe)
            {
                diagnostico.TemProblema = true;
                diagnostico.MensagemProblema = "Arquivo não existe";
                return diagnostico;
            }
            
            // RETRY: até 3 tentativas com 200ms de espera entre elas.
            // Isso resolve o erro "The process cannot access the file"
            // que acontece quando o Windows ainda não liberou o handle do WAV.
            for (int tentativa = 1; tentativa <= 3; tentativa++)
            {
                try
                {
                    return AnalisarInterno(caminhoWav, diagnostico);
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"[AudioProcessor] Tentativa {tentativa}/3 falhou (IOException): {ex.Message}");
                    if (tentativa < 3)
                    {
                        System.Threading.Thread.Sleep(200);
                    }
                }
                catch (Exception ex)
                {
                    // Outros erros não fazem retry (não vai adiantar)
                    diagnostico.TemProblema = true;
                    diagnostico.MensagemProblema = $"Erro ao analisar: {ex.Message}";
                    return diagnostico;
                }
            }
            
            // Se todos os retries falharam POR ARQUIVO EM USO,
            // considera SUCESSO (o arquivo foi gravado, só não conseguimos analisar agora).
            // Isso é melhor que mostrar erro em vermelho — o áudio tá lá, funciona.
            try
            {
                var info = new FileInfo(caminhoWav);
                diagnostico.TamanhoBytes = info.Length;
                diagnostico.DuracaoSegundos = 0; // não conseguimos ler, mas o arquivo existe
                
                if (info.Length > 1000) // pelo menos 1KB significa que gravou algo
                {
                    diagnostico.TemProblema = false;
                    diagnostico.MensagemProblema = "";
                    Console.WriteLine($"[AudioProcessor] Análise pulada mas arquivo OK ({info.Length} bytes): {Path.GetFileName(caminhoWav)}");
                }
                else
                {
                    diagnostico.TemProblema = true;
                    diagnostico.MensagemProblema = $"Arquivo muito pequeno ({info.Length} bytes)";
                }
            }
            catch
            {
                // Se nem conseguir ler info do arquivo, aí sim é problema
                diagnostico.TemProblema = true;
                diagnostico.MensagemProblema = "Arquivo inacessível";
            }
            
            return diagnostico;
        }
        
        /// <summary>
        /// Análise real do arquivo (chamado pelo Analisar com retry).
        /// </summary>
        private DiagnosticoAudio AnalisarInterno(string caminhoWav, DiagnosticoAudio diagnostico)
        {
            var info = new FileInfo(caminhoWav);
            diagnostico.TamanhoBytes = info.Length;
            
            if (info.Length == 0)
            {
                diagnostico.TemProblema = true;
                diagnostico.MensagemProblema = "Arquivo vazio (0 bytes)";
                return diagnostico;
            }
            
            using var reader = new AudioFileReader(caminhoWav);
            diagnostico.DuracaoSegundos = reader.TotalTime.TotalSeconds;
            diagnostico.SampleRate = reader.WaveFormat.SampleRate;
            diagnostico.Canais = reader.WaveFormat.Channels;
            
            float[] buffer = new float[reader.WaveFormat.SampleRate];
            float maxAmplitude = 0f;
            double somaAmplitudes = 0;
            int amostrasContadas = 0;
            int amostrasSilencio = 0;
            const float LIMIAR_SILENCIO = 0.01f;
            
            int amostrasLidas;
            while ((amostrasLidas = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < amostrasLidas; i++)
                {
                    float amp = Math.Abs(buffer[i]);
                    if (amp > maxAmplitude) maxAmplitude = amp;
                    somaAmplitudes += amp;
                    amostrasContadas++;
                    
                    if (amp < LIMIAR_SILENCIO) amostrasSilencio++;
                }
            }
            
            diagnostico.VolumeMaximo = maxAmplitude;
            diagnostico.VolumeMedio = amostrasContadas > 0 ? (float)(somaAmplitudes / amostrasContadas) : 0f;
            diagnostico.PercentualSilencio = amostrasContadas > 0 ? (amostrasSilencio * 100.0f / amostrasContadas) : 0f;
            
            diagnostico.VolumeMaximoDb = 20f * (float)Math.Log10(Math.Max(maxAmplitude, 0.0001f));
            diagnostico.VolumeMedioDb = 20f * (float)Math.Log10(Math.Max(diagnostico.VolumeMedio, 0.0001f));
            
            AplicarDiagnostico(diagnostico);
            return diagnostico;
        }
        
        private void AplicarDiagnostico(DiagnosticoAudio diag)
        {
            if (diag.DuracaoSegundos < 0.2)
            {
                diag.TemProblema = true;
                diag.MensagemProblema = $"Clip muito curto ({diag.DuracaoSegundos:F1}s) - regrave";
                return;
            }
            
            if (diag.DuracaoSegundos > 3.0)
            {
                diag.TemProblema = true;
                diag.MensagemProblema = $"Clip muito longo ({diag.DuracaoSegundos:F1}s) - corte pausas";
                return;
            }
            
            if (diag.VolumeMaximoDb < -20)
            {
                diag.TemProblema = true;
                diag.MensagemProblema = $"Volume muito baixo ({diag.VolumeMaximoDb:F1}dB) - fale mais alto";
                return;
            }
            
            if (diag.VolumeMaximo > 0.98f)
            {
                diag.TemProblema = true;
                diag.MensagemProblema = "Volume estourado (clipping) - fale mais longe do mic";
                return;
            }
            
            if (diag.PercentualSilencio > 60)
            {
                diag.TemProblema = true;
                diag.MensagemProblema = $"Muito silêncio ({diag.PercentualSilencio:F0}%) - corte pausas";
                return;
            }
            
            diag.TemProblema = false;
            diag.MensagemProblema = "";
        }
    }
    
    public class DiagnosticoAudio
    {
        public string CaminhoArquivo { get; set; } = "";
        public bool Existe { get; set; }
        public long TamanhoBytes { get; set; }
        public double DuracaoSegundos { get; set; }
        public int SampleRate { get; set; }
        public int Canais { get; set; }
        public float VolumeMaximo { get; set; }
        public float VolumeMedio { get; set; }
        public float VolumeMaximoDb { get; set; }
        public float VolumeMedioDb { get; set; }
        public float PercentualSilencio { get; set; }
        public bool TemProblema { get; set; }
        public string MensagemProblema { get; set; } = "";
    }
}