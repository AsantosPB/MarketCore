using System;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;

namespace MarketCore.WPF.Services.PregaoVivaVoz
{
    /// <summary>
    /// Serviço de gravação de áudio via microfone.
    /// 
    /// FORMATO: WAV mono 16-bit 44.1kHz (padrão broadcast, ~86KB/segundo).
    /// USO: Iniciar() → falar → Parar() → arquivo salvo.
    /// 
    /// VERSÃO FINAL: usa NAudio direto, sem condicional #if NAUDIO.
    /// Requer o pacote NuGet NAudio instalado no projeto.
    /// </summary>
    public class AudioRecorderService : IDisposable
    {
        private WaveInEvent? _waveIn;
        private WaveFileWriter? _writer;
        
        private DateTime _inicioGravacao;
        private string _caminhoAtual = "";
        private bool _gravando = false;
        
        private float _volumeMaxUltimoClip = 0f;
        private float _volumeMedioUltimoClip = 0f;
        private double _duracaoUltimoClipSegundos = 0;
        
        public bool Gravando => _gravando;
        public float VolumeMaxUltimoClip => _volumeMaxUltimoClip;
        public float VolumeMedioUltimoClip => _volumeMedioUltimoClip;
        public double DuracaoUltimoClipSegundos => _duracaoUltimoClipSegundos;
        
        public event EventHandler<float>? NivelAudioMudou;
        public event EventHandler<string>? ErroGravacao;
        
        /// <summary>
        /// Lista os dispositivos de entrada disponíveis.
        /// SEMPRE retorna pelo menos 1 item pra UI não ficar vazia.
        /// </summary>
        public static string[] ListarDispositivosEntrada()
        {
            try
            {
                int count = WaveInEvent.DeviceCount;
                Console.WriteLine($"[AudioRecorder] NAudio detectou {count} dispositivo(s) de entrada");
                
                if (count == 0)
                {
                    Console.WriteLine("[AudioRecorder] AVISO: NAudio retornou 0 dispositivos!");
                    return new[] { "⚠ Nenhum microfone detectado - conecte um mic" };
                }
                
                var lista = new string[count];
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var caps = WaveInEvent.GetCapabilities(i);
                        lista[i] = $"[{i}] {caps.ProductName}";
                        Console.WriteLine($"[AudioRecorder]   {i}: {caps.ProductName} (channels: {caps.Channels})");
                    }
                    catch (Exception exDev)
                    {
                        lista[i] = $"[{i}] Dispositivo {i} (erro: {exDev.Message})";
                        Console.WriteLine($"[AudioRecorder] Erro ao ler dispositivo {i}: {exDev.Message}");
                    }
                }
                return lista;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioRecorder] Erro geral ao listar dispositivos: {ex.Message}");
                return new[] { $"⚠ Erro ao listar mics: {ex.Message}" };
            }
        }
        
        public bool Iniciar(string caminhoDestino, int dispositivoIndex = 0)
        {
            if (_gravando)
            {
                Console.WriteLine("[AudioRecorder] Já está gravando");
                return false;
            }
            
            try
            {
                var dir = Path.GetDirectoryName(caminhoDestino);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                
                _caminhoAtual = caminhoDestino;
                _inicioGravacao = DateTime.Now;
                _volumeMaxUltimoClip = 0f;
                _volumeMedioUltimoClip = 0f;
                _duracaoUltimoClipSegundos = 0;
                
                _waveIn = new WaveInEvent
                {
                    DeviceNumber = dispositivoIndex,
                    WaveFormat = new WaveFormat(48000, 16, 1),
                    BufferMilliseconds = 200,
                    NumberOfBuffers = 3
                };
                
                _writer = new WaveFileWriter(caminhoDestino, _waveIn.WaveFormat);
                
                double somaVolumes = 0;
                int amostrasContadas = 0;
                
                _waveIn.DataAvailable += (s, e) =>
                {
                    try
                    {
                        _writer?.Write(e.Buffer, 0, e.BytesRecorded);
                        
                        float max = 0;
                        for (int i = 0; i < e.BytesRecorded; i += 2)
                        {
                            short amostra = (short)((e.Buffer[i + 1] << 8) | e.Buffer[i]);
                            float amplitude = Math.Abs(amostra) / 32768f;
                            if (amplitude > max) max = amplitude;
                            somaVolumes += amplitude;
                            amostrasContadas++;
                        }
                        
                        if (max > _volumeMaxUltimoClip) _volumeMaxUltimoClip = max;
                        
                        if (amostrasContadas > 0)
                            _volumeMedioUltimoClip = (float)(somaVolumes / amostrasContadas);
                        
                        NivelAudioMudou?.Invoke(this, max);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AudioRecorder] Erro no DataAvailable: {ex.Message}");
                    }
                };
                
                _waveIn.RecordingStopped += (s, e) =>
                {
                    _writer?.Dispose();
                    _writer = null;
                    _waveIn?.Dispose();
                    _waveIn = null;
                    
                    if (e.Exception != null)
                    {
                        ErroGravacao?.Invoke(this, e.Exception.Message);
                    }
                };
                
                _waveIn.StartRecording();
                _gravando = true;
                
                Console.WriteLine($"🎙️ [AudioRecorder] Gravando dispositivo #{dispositivoIndex} em: {Path.GetFileName(caminhoDestino)}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioRecorder] Erro ao iniciar: {ex.Message}");
                ErroGravacao?.Invoke(this, ex.Message);
                _gravando = false;
                return false;
            }
        }
        
        public string? Parar()
        {
            if (!_gravando) return null;
            
            try
            {
                _duracaoUltimoClipSegundos = (DateTime.Now - _inicioGravacao).TotalSeconds;
                
                _waveIn?.StopRecording();
                
                // Aguarda o RecordingStopped disparar E o Windows liberar o arquivo
                // Antes: 500ms (10 x 50ms) - às vezes não era suficiente
                // Agora: até 1500ms (30 x 50ms) - garante liberação
                int tentativas = 0;
                while (_writer != null && tentativas < 30)
                {
                    System.Threading.Thread.Sleep(50);
                    tentativas++;
                }
                
                // Espera adicional pro Windows liberar o handle do arquivo
                System.Threading.Thread.Sleep(200);
                
                _gravando = false;
                
                Console.WriteLine($"✅ [AudioRecorder] Salvo: {Path.GetFileName(_caminhoAtual)} · {_duracaoUltimoClipSegundos:F1}s · vol max {_volumeMaxUltimoClip:F2}");
                
                return _caminhoAtual;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioRecorder] Erro ao parar: {ex.Message}");
                ErroGravacao?.Invoke(this, ex.Message);
                _gravando = false;
                return null;
            }
        }
        
        public void Cancelar()
        {
            if (!_gravando) return;
            
            try
            {
                _waveIn?.StopRecording();
                _writer?.Dispose();
                _writer = null;
                _waveIn?.Dispose();
                _waveIn = null;
                
                if (File.Exists(_caminhoAtual))
                {
                    File.Delete(_caminhoAtual);
                }
                
                _gravando = false;
                Console.WriteLine("[AudioRecorder] Gravação cancelada");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioRecorder] Erro ao cancelar: {ex.Message}");
            }
        }
        
        public void Dispose()
        {
            if (_gravando) Cancelar();
        }
    }
}