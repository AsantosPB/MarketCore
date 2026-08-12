using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace MarketCore.WPF.Services.PregaoVivaVoz
{
    /// <summary>
    /// Serviço de reprodução de áudio com fila FIFO.
    /// VERSÃO FINAL: usa NAudio direto, sem condicional #if NAUDIO.
    /// 
    /// COMPORTAMENTO:
    /// - Toca clips em sequência (concatenação)
    /// - Sem sobreposição (fila garantida)
    /// - Se o arquivo WAV não existir, LOGA no console em vez de tocar
    /// </summary>
    public class AudioPlaybackService : IDisposable
    {
        private readonly ConcurrentQueue<PlaybackItem> _fila = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _reproducaoAtiva = new(1, 1);
        
        private int _volumeMaster = 70;
        private bool _pausado = false;
        private bool _iniciado = false;
        
        public int VolumeMaster
        {
            get => _volumeMaster;
            set => _volumeMaster = Math.Clamp(value, 0, 100);
        }
        
        public bool Pausado
        {
            get => _pausado;
            set => _pausado = value;
        }
        
        public int TamanhoFila => _fila.Count;
        
        /// <summary>
        /// Disparado quando um item da fila termina de reproduzir. O payload
        /// <see cref="NarracaoInfo"/> traz o texto narrado + o callbackInfo original
        /// (string do callback da DLL que gerou a narração). Assim o log correlaciona
        /// perfeitamente narração ↔ callback, mesmo que outros callbacks tenham
        /// chegado enquanto a narração estava na fila de áudio.
        /// </summary>
        public event EventHandler<NarracaoInfo>? ItemReproduzido;
        public event EventHandler<string>? ErroReproducao;
        
        public void Iniciar()
        {
            if (_iniciado) return;
            _iniciado = true;
            
            Task.Run(() => WorkerLoop(_cts.Token));
            
            Console.WriteLine("[AudioPlayback] Serviço iniciado");
        }
        
        public void Enfileirar(List<string> arquivosWav, string textoOriginal = "", string? callbackInfo = null)
        {
            if (arquivosWav == null || arquivosWav.Count == 0) return;

            _fila.Enqueue(new PlaybackItem
            {
                Arquivos = arquivosWav,
                TextoOriginal = textoOriginal,
                CallbackInfo = callbackInfo,
                TimestampEnfileiramento = DateTime.Now
            });
        }
        
        public void LimparFila()
        {
            while (_fila.TryDequeue(out _)) { }
        }
        
        private async Task WorkerLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_pausado)
                    {
                        await Task.Delay(200, token);
                        continue;
                    }
                    
                    if (_fila.TryDequeue(out var item))
                    {
                        await ReproduzirItem(item, token);
                    }
                    else
                    {
                        await Task.Delay(50, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AudioPlayback] Erro no worker: {ex.Message}");
                    ErroReproducao?.Invoke(this, ex.Message);
                }
            }
        }
        
        private async Task ReproduzirItem(PlaybackItem item, CancellationToken token)
        {
            await _reproducaoAtiva.WaitAsync(token);
            
            try
            {
                bool algumArquivoExiste = false;
                foreach (var arquivo in item.Arquivos)
                {
                    if (File.Exists(arquivo))
                    {
                        algumArquivoExiste = true;
                        break;
                    }
                }
                
                if (!algumArquivoExiste)
                {
                    var textoLog = string.IsNullOrEmpty(item.TextoOriginal)
                        ? string.Join(" + ", item.Arquivos)
                        : item.TextoOriginal;

                    Console.WriteLine($"🔊 [WOULD PLAY] {textoLog}");
                    ItemReproduzido?.Invoke(this, new NarracaoInfo($"[LOG] {textoLog}", item.CallbackInfo));

                    await Task.Delay(item.Arquivos.Count * 500, token);
                    return;
                }

                foreach (var arquivo in item.Arquivos)
                {
                    if (token.IsCancellationRequested) break;

                    if (!File.Exists(arquivo))
                    {
                        Console.WriteLine($"🔊 [SKIP] Arquivo não encontrado: {Path.GetFileName(arquivo)}");
                        await Task.Delay(300, token);
                        continue;
                    }

                    await TocarArquivoWav(arquivo, token);
                }

                ItemReproduzido?.Invoke(this, new NarracaoInfo(item.TextoOriginal, item.CallbackInfo));
            }
            finally
            {
                _reproducaoAtiva.Release();
            }
        }
        
        private async Task TocarArquivoWav(string caminho, CancellationToken token)
        {
            try
            {
                using var reader = new AudioFileReader(caminho);
                using var outputDevice = new WaveOutEvent();
                
                reader.Volume = _volumeMaster / 100f;
                
                outputDevice.Init(reader);
                outputDevice.Play();
                
                while (outputDevice.PlaybackState == PlaybackState.Playing)
                {
                    if (token.IsCancellationRequested || _pausado)
                    {
                        outputDevice.Stop();
                        break;
                    }
                    await Task.Delay(50, token);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioPlayback] Erro ao tocar {Path.GetFileName(caminho)}: {ex.Message}");
                ErroReproducao?.Invoke(this, ex.Message);
            }
        }
        
        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            _reproducaoAtiva.Dispose();
        }
        
        private class PlaybackItem
        {
            public List<string> Arquivos { get; set; } = new();
            public string TextoOriginal { get; set; } = "";
            public string? CallbackInfo { get; set; }
            public DateTime TimestampEnfileiramento { get; set; }
        }
    }

    /// <summary>
    /// Payload do evento <see cref="AudioPlaybackService.ItemReproduzido"/>.
    /// Texto narrado + callbackInfo original que gerou a narração (nullable
    /// quando a narração foi disparada por lógica derivada, ex: rajada).
    /// </summary>
    public class NarracaoInfo
    {
        public string Texto { get; }
        public string? CallbackInfo { get; }

        public NarracaoInfo(string texto, string? callbackInfo)
        {
            Texto = texto ?? string.Empty;
            CallbackInfo = callbackInfo;
        }
    }
}
