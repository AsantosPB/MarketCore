using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MarketCore.WPF.Services.PregaoVivaVoz
{
    /// <summary>
    /// Simulador de eventos pra TESTAR o motor sem precisar do pregão ao vivo.
    /// 
    /// Gera eventos aleatórios simulando:
    /// - Agressões normais (60% dos eventos)
    /// - Book (30% dos eventos)
    /// - Rajadas (10% - sequência de 5-10 agressões rápidas)
    /// 
    /// Uso: 
    ///   var sim = new EventoSimulador(engine);
    ///   sim.Iniciar();
    ///   ...
    ///   sim.Parar();
    /// </summary>
    public class EventoSimulador : IDisposable
    {
        private readonly PregaoVivaVozEngine _engine;
        private readonly CancellationTokenSource _cts = new();
        private readonly Random _random = new();
        private bool _rodando = false;
        
        // Players ativos pra simular (usa os principais)
        private readonly List<(string Chave, string Nome, int PesoRajada)> _playersAtivos = new()
        {
            ("goldman", "Goldman", 30),  // 30% chance de ser rajadeiro
            ("jpm", "JPM", 40),          // 40% - manda muitas rajadas
            ("morgan", "Morgan", 25),
            ("merrill", "Merrill", 35),  // manda distribuições fortes
            ("citi", "Citi", 15),
            ("btg", "BTG", 10),
            ("itau", "Itaú", 8),
        };
        
        /// <summary>
        /// Velocidade da simulação (ms entre eventos).
        /// Padrão: 500-2000ms randomizado.
        /// </summary>
        public int IntervaloMinMs { get; set; } = 500;
        public int IntervaloMaxMs { get; set; } = 2000;
        
        /// <summary>
        /// Se está simulando rajadas.
        /// </summary>
        public bool SimularRajadas { get; set; } = true;
        
        public event EventHandler<string>? StatusMudou;
        
        public EventoSimulador(PregaoVivaVozEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }
        
        public void Iniciar()
        {
            if (_rodando) return;
            _rodando = true;
            
            Task.Run(() => LoopSimulacao(_cts.Token));
            
            Console.WriteLine("[EventoSimulador] Iniciado - gerando eventos aleatórios");
            StatusMudou?.Invoke(this, "Simulador ativo");
        }
        
        public void Parar()
        {
            _rodando = false;
            StatusMudou?.Invoke(this, "Simulador parado");
        }
        
        private async Task LoopSimulacao(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!_rodando)
                    {
                        await Task.Delay(500, token);
                        continue;
                    }
                    
                    // Decide o tipo de evento
                    int chance = _random.Next(100);
                    
                    if (SimularRajadas && chance < 8) // 8% chance de rajada
                    {
                        await GerarRajada(token);
                    }
                    else if (chance < 45) // 37% agressão normal
                    {
                        GerarAgressao();
                    }
                    else if (chance < 85) // 40% book
                    {
                        GerarBook();
                    }
                    else
                    {
                        GerarAgressao(); // mais uma agressão pra variar
                    }
                    
                    // Intervalo aleatório
                    int intervalo = _random.Next(IntervaloMinMs, IntervaloMaxMs);
                    await Task.Delay(intervalo, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EventoSimulador] Erro: {ex.Message}");
                }
            }
        }
        
        private void GerarAgressao()
        {
            var player = _playersAtivos[_random.Next(_playersAtivos.Count)];
            var lado = _random.Next(2) == 0 ? "compra" : "venda";
            
            // Quantidades típicas do WIN: 5-500 na maioria, ocasional 500-2000
            int qtd;
            int roll = _random.Next(100);
            if (roll < 60)
                qtd = _random.Next(5, 100);      // 60% pequenas
            else if (roll < 90)
                qtd = _random.Next(100, 500);    // 30% médias
            else
                qtd = _random.Next(500, 2000);   // 10% grandes
            
            _engine.ProcessarAgressao(player.Nome, lado, qtd);
        }
        
        private void GerarBook()
        {
            var player = _playersAtivos[_random.Next(_playersAtivos.Count)];
            var lado = _random.Next(2) == 0 ? "compra" : "venda";
            var nivel = _random.Next(1, 6); // 1-5
            
            // Ordens no book tendem a ser maiores (100-5000)
            int roll = _random.Next(100);
            int qtd;
            if (roll < 50)
                qtd = _random.Next(100, 300);
            else if (roll < 85)
                qtd = _random.Next(300, 1000);
            else
                qtd = _random.Next(1000, 5000);
            
            _engine.ProcessarBook(player.Nome, lado, nivel, qtd);
        }
        
        /// <summary>
        /// Gera uma RAJADA REAL - sequência de 5-15 agressões do mesmo player 
        /// no mesmo lado, em poucos milissegundos.
        /// Baseado nos prints reais do JPM e Merrill.
        /// </summary>
        private async Task GerarRajada(CancellationToken token)
        {
            // Escolhe player com peso pra rajada
            var candidatos = _playersAtivos;
            var player = candidatos[_random.Next(candidatos.Count)];
            
            // Chance da rajada ocorrer baseada no peso
            if (_random.Next(100) > player.PesoRajada) return;
            
            var lado = _random.Next(2) == 0 ? "compra" : "venda";
            int sequencia = _random.Next(5, 15); // 5-15 agressões
            
            Console.WriteLine($"\n💥 [SIMULADOR] Gerando rajada: {player.Nome} {lado} · {sequencia} agressões\n");
            
            for (int i = 0; i < sequencia; i++)
            {
                if (token.IsCancellationRequested) break;
                
                int qtd = _random.Next(20, 100); // agressões médias
                _engine.ProcessarAgressao(player.Nome, lado, qtd);
                
                // Rajadas reais: 50-300ms entre agressões
                int intervaloMs = _random.Next(50, 300);
                await Task.Delay(intervaloMs, token);
            }
            
            // Depois da rajada, silêncio (o detector vai disparar "parou")
            Console.WriteLine($"[SIMULADOR] {player.Nome} silenciou · detector deve disparar 'parou' em ~3s\n");
        }
        
        /// <summary>
        /// Gera UM ÚNICO evento específico (pra testes manuais).
        /// </summary>
        public void GerarEventoUnico(string tipo, string playerNome, int quantidade)
        {
            switch (tipo.ToLower())
            {
                case "agressao_compra":
                    _engine.ProcessarAgressao(playerNome, "compra", quantidade);
                    break;
                case "agressao_venda":
                    _engine.ProcessarAgressao(playerNome, "venda", quantidade);
                    break;
                case "book_compra":
                    _engine.ProcessarBook(playerNome, "compra", 1, quantidade);
                    break;
                case "book_venda":
                    _engine.ProcessarBook(playerNome, "venda", 1, quantidade);
                    break;
            }
        }
        
        public void Dispose()
        {
            _rodando = false;
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
