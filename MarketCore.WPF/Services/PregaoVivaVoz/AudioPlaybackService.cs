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

        /// <summary>
        /// Fila de PRIORIDADE — checada primeiro pelo WorkerLoop, antes da fila
        /// normal. Populada por <see cref="NarrarComPrioridade"/>, que também
        /// esvazia a fila normal e cancela o áudio atual.
        /// </summary>
        private readonly ConcurrentQueue<PlaybackItem> _filaPrioridade = new();

        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _reproducaoAtiva = new(1, 1);

        /// <summary>
        /// CTS do áudio corrente (um item ativo por vez). Criado em
        /// <see cref="ReproduzirItem"/> como filho do <see cref="_cts"/>.
        /// <see cref="NarrarComPrioridade"/> cancela este CTS para interromper
        /// a reprodução imediatamente. O <c>_cancelGate</c> protege leitura/escrita
        /// entre worker (produz) e thread do engine (consome).
        /// </summary>
        private CancellationTokenSource? _reproducaoAtualCts;
        private readonly object _cancelGate = new();

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
        
        /// <summary>
        /// Limite máximo da fila de áudio. Quando estoura, os itens MAIS ANTIGOS
        /// são descartados — melhor perder narração velha do que acumular fila
        /// de minutos. Alinhado com o Objetivo 1 (bridge assíncrono): callbacks
        /// nunca ficam bloqueados esperando o áudio processar.
        /// </summary>
        public const int FilaMaxima = 8;

        /// <summary>
        /// TTL da fila de áudio normal: se um item esperou mais que este tempo
        /// sem ser reproduzido, é descartado silenciosamente. Evita narrar eventos
        /// de mercado que já são obsoletos quando chegam à vez de tocar.
        /// Valor calibrado para: 1 item = ~2-3s de áudio → permite 1-2 narrações
        /// em sequência antes de descartar o restante do burst.
        /// </summary>
        public const int TtlFilaAudioSegundos = 5;

        /// <summary>Contador de itens descartados por fila cheia (debug).</summary>
        public long ItensDescartados_FilaCheia { get; private set; }

        /// <summary>Contador de itens descartados por TTL expirado (debug).</summary>
        public long ItensDescartados_TTL { get; private set; }

        public void Enfileirar(List<string> arquivosWav, string textoOriginal = "", string? callbackInfo = null)
        {
            if (arquivosWav == null || arquivosWav.Count == 0) return;

            // Se a fila estourou o limite, dropa os MAIS ANTIGOS até caber o novo.
            while (_fila.Count >= FilaMaxima)
            {
                if (_fila.TryDequeue(out _))
                    ItensDescartados_FilaCheia++;
                else
                    break;
            }

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

        /// <summary>
        /// Para o áudio atual imediatamente e drena ambas as filas (normal e prioridade)
        /// sem fechar nenhum channel. Após o retorno, o serviço está pronto para
        /// receber novos clips normalmente.
        /// Thread-safe — pode ser chamado de qualquer thread.
        /// </summary>
        public void PararELimparFila()
        {
            // Cancela o item em reprodução agora (se houver)
            lock (_cancelGate)
            {
                try { _reproducaoAtualCts?.Cancel(); }
                catch { /* já disposto — ignorar */ }
            }

            // Drena fila normal SEM fechar o channel (apenas TryDequeue)
            while (_fila.TryDequeue(out _)) { }

            // Drena fila de prioridade também
            while (_filaPrioridade.TryDequeue(out _)) { }
        }

        /// <summary>
        /// Narração PRIORITÁRIA. Interrompe o áudio corrente imediatamente,
        /// descarta toda a fila normal pendente, e reproduz <paramref name="arquivosWav"/>
        /// imediatamente. Após terminar, o serviço volta ao funcionamento normal
        /// (fila vazia, pronto para novos eventos).
        ///
        /// Thread-safe — pode ser chamado do worker do engine, do Bridge worker
        /// ou de qualquer outra thread. Não bloqueia o caller (apenas enfileira +
        /// sinaliza o cancelamento; a reprodução acontece no WorkerLoop).
        /// </summary>
        public void NarrarComPrioridade(List<string> arquivosWav, string textoOriginal = "", string? callbackInfo = null)
        {
            if (arquivosWav == null || arquivosWav.Count == 0) return;

            // 1) Descarta TODA a fila normal — narração prioritária substitui pendências.
            while (_fila.TryDequeue(out _)) { }

            // 2) Limita a fila de prioridade (defensivo — não deveria acumular).
            while (_filaPrioridade.Count >= FilaMaxima)
            {
                if (!_filaPrioridade.TryDequeue(out _)) break;
            }

            _filaPrioridade.Enqueue(new PlaybackItem
            {
                Arquivos = arquivosWav,
                TextoOriginal = textoOriginal,
                CallbackInfo = callbackInfo,
                TimestampEnfileiramento = DateTime.Now
            });

            // 3) Interrompe o áudio corrente (se houver). O WorkerLoop volta ao
            // topo do loop, encontra _filaPrioridade não-vazia e reproduz.
            lock (_cancelGate)
            {
                try { _reproducaoAtualCts?.Cancel(); }
                catch { /* já disposto — ignorar */ }
            }
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

                    // PRIORIDADE PRIMEIRO — se NarrarComPrioridade acabou de enfileirar
                    // um clip aqui, ele deve tocar antes de qualquer item da fila normal.
                    if (_filaPrioridade.TryDequeue(out var priorityItem))
                    {
                        await ReproduzirItem(priorityItem, token);
                        continue;
                    }

                    if (_fila.TryDequeue(out var item))
                    {
                        // TTL: descarta narração obsoleta — evita acumular áudios de
                        // eventos de mercado que já não são relevantes quando chegam à vez.
                        // Ex.: burst de 6 BLOCOs em 7s → toca os 2 primeiros (~6s),
                        // descarta os 4 restantes que já ultrapassaram TtlFilaAudioSegundos.
                        if (DateTime.Now - item.TimestampEnfileiramento > TimeSpan.FromSeconds(TtlFilaAudioSegundos))
                        {
                            ItensDescartados_TTL++;
                            Console.WriteLine($"[AudioPlayback] TTL expirado ({TtlFilaAudioSegundos}s) — descartado: {item.TextoOriginal}");
                            continue;
                        }

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

        private async Task ReproduzirItem(PlaybackItem item, CancellationToken parentToken)
        {
            // CTS filho do parentToken. NarrarComPrioridade cancela ESTE cts para
            // interromper a reprodução; o parentToken (shutdown) continua vivo.
            CancellationTokenSource itemCts;
            lock (_cancelGate)
            {
                itemCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
                _reproducaoAtualCts = itemCts;
            }
            var itemToken = itemCts.Token;

            try
            {
                await _reproducaoAtiva.WaitAsync(itemToken);

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

                        await Task.Delay(item.Arquivos.Count * 500, itemToken);
                        return;
                    }

                    foreach (var arquivo in item.Arquivos)
                    {
                        if (itemToken.IsCancellationRequested) break;

                        if (!File.Exists(arquivo))
                        {
                            Console.WriteLine($"🔊 [SKIP] Arquivo não encontrado: {Path.GetFileName(arquivo)}");
                            await Task.Delay(300, itemToken);
                            continue;
                        }

                        await TocarArquivoWav(arquivo, itemToken);
                    }

                    ItemReproduzido?.Invoke(this, new NarracaoInfo(item.TextoOriginal, item.CallbackInfo));
                }
                finally
                {
                    _reproducaoAtiva.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelado por NarrarComPrioridade OU por Dispose — sai limpo, worker segue.
                if (parentToken.IsCancellationRequested) throw;   // shutdown → deixa o WorkerLoop encerrar
            }
            finally
            {
                lock (_cancelGate)
                {
                    if (ReferenceEquals(_reproducaoAtualCts, itemCts))
                        _reproducaoAtualCts = null;
                }
                itemCts.Dispose();
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
