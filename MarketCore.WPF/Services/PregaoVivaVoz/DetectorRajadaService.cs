using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarketCore.WPF.Models.PregaoVivaVoz;

namespace MarketCore.WPF.Services.PregaoVivaVoz
{
    /// <summary>
    /// Serviço detector de RAJADAS de agressões.
    /// 
    /// LÓGICA:
    /// - Mantém buffer circular POR PLAYER com timestamps em milissegundos
    /// - Detecta INÍCIO de rajada quando: N agressões do mesmo lado em X ms + volume >= Y
    /// - Detecta FIM de rajada quando: player fica Z ms sem nova agressão
    /// 
    /// EXEMPLO REAL (baseado no print do JPM que Anderson mandou):
    /// - JPM manda 21 trades em 10ms → detecta INÍCIO IMEDIATAMENTE
    /// - JPM continua batendo por 15 segundos
    /// - JPM para → após 3 segundos sem novo trade → dispara "JPM parou de vender"
    /// - Este é o gatilho MAIS VALIOSO do sistema
    /// </summary>
    public class DetectorRajadaService : IDisposable
    {
        private readonly ConcurrentDictionary<string, BufferRajada> _buffers = new();
        private readonly ConfigRajadaGlobal _config;
        private readonly CancellationTokenSource _cts = new();
        private bool _iniciado = false;
        
        /// <summary>
        /// Evento disparado quando uma rajada é DETECTADA (início).
        /// </summary>
        public event EventHandler<EventoOrderFlow>? RajadaIniciada;
        
        /// <summary>
        /// Evento disparado quando uma rajada PARA.
        /// </summary>
        public event EventHandler<EventoOrderFlow>? RajadaParou;
        
        public DetectorRajadaService(ConfigRajadaGlobal config)
        {
            _config = config ?? new ConfigRajadaGlobal();
        }
        
        /// <summary>
        /// Inicia o monitor de silêncio (verifica quem parou).
        /// </summary>
        public void Iniciar()
        {
            if (_iniciado) return;
            _iniciado = true;
            
            Task.Run(() => MonitorSilencioLoop(_cts.Token));
            
            Console.WriteLine($"[DetectorRajada] Iniciado. Config: {_config.SequenciaMinima} agressões em {_config.JanelaMilissegundos}ms, vol ≥ {_config.VolumeMinimo}, silêncio {_config.SilencioParouMilissegundos}ms");
        }
        
        /// <summary>
        /// Registra uma nova agressão no detector.
        /// Chamado pelo motor a cada agressão detectada.
        ///
        /// <paramref name="volumeMinimoPlayer"/> é o limiar de volume acumulado
        /// deste player específico (vem de FiltroRajada.VolumeMinimo). Substitui
        /// o antigo _config.VolumeMinimo global — agora cada player carrega o
        /// próprio limiar. Se <= 0, cai no default de 100.
        /// </summary>
        public void RegistrarAgressao(string playerChave, string playerNome, string lado, int quantidade, int volumeMinimoPlayer)
        {
            if (string.IsNullOrEmpty(playerChave)) return;

            int limiteVolume = volumeMinimoPlayer > 0 ? volumeMinimoPlayer : 100;

            var buffer = _buffers.GetOrAdd(playerChave, key => new BufferRajada { PlayerChave = key });
            var timestamp = DateTime.Now;

            lock (buffer)
            {
                // Se mudou de lado, reseta
                if (buffer.LadoAtivo != null && buffer.LadoAtivo != lado)
                {
                    buffer.Reset();
                }

                buffer.LadoAtivo = lado;
                buffer.AdicionarAgressao(quantidade, lado, timestamp);
                buffer.LimparAntigas(_config.JanelaMilissegundos);

                // Verifica se atingiu limiar de rajada
                int contagem = buffer.ContarAgressoes(lado);
                int volumeTotal = buffer.VolumeTotal(lado);

                if (!buffer.RajadaEmAndamento &&
                    contagem >= _config.SequenciaMinima &&
                    volumeTotal >= limiteVolume)
                {
                    // RAJADA DETECTADA!
                    buffer.RajadaEmAndamento = true;
                    buffer.VolumeAcumulado = volumeTotal;
                    
                    var tipo = lado == "compra" 
                        ? TipoEvento.RajadaInicioCompra 
                        : TipoEvento.RajadaInicioVenda;
                    
                    var evento = new EventoOrderFlow
                    {
                        Timestamp = timestamp,
                        PlayerChave = playerChave,
                        PlayerNome = playerNome,
                        Tipo = tipo,
                        Quantidade = volumeTotal
                    };
                    
                    Console.WriteLine($"🔥 [RAJADA INÍCIO] {playerNome} {lado} · {contagem} agressões · vol acumulado {volumeTotal}");
                    RajadaIniciada?.Invoke(this, evento);
                }
                else if (buffer.RajadaEmAndamento)
                {
                    // Atualiza volume acumulado da rajada em andamento
                    buffer.VolumeAcumulado = volumeTotal;
                }
            }
        }
        
        /// <summary>
        /// Loop que verifica periodicamente quem parou de operar.
        /// Roda a cada 100ms.
        /// </summary>
        private async Task MonitorSilencioLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var agora = DateTime.Now;
                    var limite = agora.AddMilliseconds(-_config.SilencioParouMilissegundos);
                    
                    foreach (var kvp in _buffers)
                    {
                        var buffer = kvp.Value;
                        
                        lock (buffer)
                        {
                            if (buffer.RajadaEmAndamento && buffer.UltimaAgressao < limite)
                            {
                                // PAROU DE OPERAR!
                                var tipo = buffer.LadoAtivo == "compra"
                                    ? TipoEvento.RajadaPararCompra
                                    : TipoEvento.RajadaPararVenda;
                                
                                var evento = new EventoOrderFlow
                                {
                                    Timestamp = agora,
                                    PlayerChave = buffer.PlayerChave,
                                    PlayerNome = buffer.PlayerChave, // será preenchido pelo Engine
                                    Tipo = tipo,
                                    Quantidade = buffer.VolumeAcumulado
                                };
                                
                                Console.WriteLine($"⏹️ [RAJADA PAROU] {buffer.PlayerChave} {buffer.LadoAtivo} · vol total {buffer.VolumeAcumulado}");
                                RajadaParou?.Invoke(this, evento);

                                // Remove o entry — ConcurrentDictionary suporta remoção
                                // durante iteração foreach (sem exceção, documentado no .NET).
                                // Se o mesmo player voltar a operar, GetOrAdd criará novo buffer.
                                _buffers.TryRemove(kvp.Key, out _);
                            }
                        }
                    }
                    
                    await Task.Delay(100, token); // verifica a cada 100ms
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DetectorRajada] Erro no monitor: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Estatísticas do detector.
        /// </summary>
        public string ObterEstatisticas()
        {
            int rajadasAtivas = 0;
            int playersMonitorados = _buffers.Count;
            
            foreach (var kvp in _buffers)
            {
                if (kvp.Value.RajadaEmAndamento) rajadasAtivas++;
            }
            
            return $"Players: {playersMonitorados} · Rajadas ativas: {rajadasAtivas}";
        }
        
        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
