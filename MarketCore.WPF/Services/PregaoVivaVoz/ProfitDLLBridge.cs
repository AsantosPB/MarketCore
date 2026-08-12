using System;
using System.Collections.Generic;
using System.IO;

namespace MarketCore.WPF.Services.PregaoVivaVoz
{
    /// <summary>
    /// Ponte entre os callbacks reais da ProfitDLL (chegam pelo MarketCore)
    /// e o motor do Pregão Viva Voz.
    /// 
    /// USO PELO COWORK (integrador no MarketCore):
    ///
    /// 1. Nos callbacks da ProfitDLL do MarketCore, inserir chamadas assim:
    ///
    ///    // Callback de trade real (NewTradeCallback)
    ///    var bridge = ((App)Application.Current).PregaoVivaVozBridge;
    ///    bridge?.OnTradeReceived(ticker, buyAgentName, sellAgentName, qtd, tradeType, callbackInfo);
    ///
    ///    // Callback de book (OfferBookCallback)
    ///    bridge?.OnBookUpdate(ticker, agentName, "compra", nivel, qtd, callbackInfo);
    ///
    /// callbackInfo é uma string pré-formatada (ex: "TRADE bolsa=17:20:04.987 ticker=WINFUT
    /// buy=XP sell=IDEAL qtd=1 tradeType=2") que viaja pareada com o evento até o log de
    /// narração — garante correlação perfeita mesmo com muitos callbacks concorrentes.
    /// 
    /// 2. Registrar o bridge no App.xaml.cs ao startar o MarketCore:
    ///    PregaoVivaVozBridge = new ProfitDLLBridge(pregaoVivaVozViewModel.Engine);
    /// 
    /// IMPORTANTE:
    /// - O bridge só processa eventos se o motor estiver ATIVO (MotorAtivo == true)
    /// - Filtra automaticamente por ativo: só narra eventos do WIN (mini-índice)
    /// - Recebe corretora por NOME (o MarketCore já traduz o código pra nome)
    /// - Zero impacto se motor estiver parado (return imediato)
    /// </summary>
    public class ProfitDLLBridge
    {
        private readonly PregaoVivaVozEngine _engine;

        // ⚠️ IMPORTANTE — evitar DUPLICAÇÃO:
        // A ProfitDLL entrega o MESMO evento por dois tickers: o contrato específico
        // (ex: WINQ26 = mini-índice agosto/2026) e o símbolo contínuo (WINFUT). Se
        // aceitássemos qualquer prefixo "WIN", cada trade viraria 2 chamadas no motor
        // → narração dupla em ~3s (comprovado nos logs: 100% dos trades vinham 2x).
        //
        // Solução: aceita SÓ o continuous (WINFUT). É o símbolo canônico da Nelogica
        // que sempre aponta pro contrato ativo — nunca "some" quando muda o vencimento.
        // Whitelist exata (não prefixo) elimina a possibilidade de WINQ26/WINV26/etc.
        private static readonly HashSet<string> TickersAceitos = new(StringComparer.OrdinalIgnoreCase)
        {
            "WINFUT"
            // Se um dia quiser WDO, adiciona "WDOFUT" aqui.
        };

        // Estatísticas (opcional, útil pra debug)
        public long EventosRecebidos { get; private set; }
        public long EventosDescartados_MotorParado { get; private set; }
        public long EventosDescartados_AtivoErrado { get; private set; }
        public long EventosEnviadosAoEngine { get; private set; }

        // OBS histórica: existiam UltimoTradeCallbackInfo/UltimoBookCallbackInfo (voláteis)
        // que o log de narração lia depois. Isso causava DECORRELAÇÃO — o callback exibido
        // ao lado da narração era o "último visto pelo Bridge", não o que gerou a narração.
        // Removidos: agora o callbackInfo viaja pareado com o evento por toda a cadeia
        // (Hook → Bridge → Engine → AudioPlayback → ItemReproduzido → Log).

        // Log bruto de callbacks — TODOS os callbacks que chegam no Bridge, com timestamp.
        // Permite correlação independente com o log de narrações.
        private static readonly string CallbacksLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarketCore",
            "pregao_viva_voz_callbacks.log");
        private static readonly object _callbackLogGate = new();

        private static void AppendCallbackLog(string linha)
        {
            try
            {
                var dir = Path.GetDirectoryName(CallbacksLogPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                string linhaFinal = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {linha}{Environment.NewLine}";
                lock (_callbackLogGate)
                {
                    File.AppendAllText(CallbacksLogPath, linhaFinal);
                }
            }
            catch { /* best effort */ }

            // Também grava no arquivo unificado (callbacks + narrações intercalados).
            PregaoVivaVozUnifiedLog.Append("CALLBACK", linha);
        }

        public ProfitDLLBridge(PregaoVivaVozEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            Console.WriteLine("[ProfitDLLBridge] Bridge inicializado. Aguardando eventos da ProfitDLL...");
        }
        
        /// <summary>
        /// Chamado pelo MarketCore no callback de trade real (NewTradeCallback).
        /// </summary>
        /// <param name="ticker">Símbolo do ativo (ex: "WINQ25", "PETR4")</param>
        /// <param name="buyAgentName">Nome da corretora que comprou (ex: "Goldman", "JPM")</param>
        /// <param name="sellAgentName">Nome da corretora que vendeu</param>
        /// <param name="qtd">Quantidade negociada</param>
        /// <param name="tradeType">1 = agressor comprou (tomou); 2 = agressor vendeu (bateu)</param>
        public void OnTradeReceived(string ticker, string buyAgentName, string sellAgentName, int qtd, int tradeType, string callbackInfo)
        {
            EventosRecebidos++;

            AppendCallbackLog(callbackInfo);

            // FILTRO 1: motor parado? ignora
            if (!_engine.MotorAtivo)
            {
                EventosDescartados_MotorParado++;
                return;
            }

            // FILTRO 2: ativo errado? ignora
            if (!EhAtivoAceito(ticker))
            {
                EventosDescartados_AtivoErrado++;
                return;
            }

            // Descobre quem foi o agressor e qual lado
            // tradeType 1 = agressor comprou (tomou o ask) → nome do agressor é o comprador
            // tradeType 2 = agressor vendeu (bateu no bid) → nome do agressor é o vendedor
            string nomeAgressor;
            string lado;

            if (tradeType == 1)
            {
                nomeAgressor = buyAgentName;
                lado = "compra";
            }
            else if (tradeType == 2)
            {
                nomeAgressor = sellAgentName;
                lado = "venda";
            }
            else
            {
                // tradeType desconhecido, ignora
                return;
            }

            // Sanitiza nome
            if (string.IsNullOrWhiteSpace(nomeAgressor)) return;

            // Envia pro motor com o callbackInfo — vai viajar junto até o log de narração.
            _engine.ProcessarAgressao(nomeAgressor, lado, qtd, callbackInfo);
            EventosEnviadosAoEngine++;
        }
        
        /// <summary>
        /// Chamado pelo MarketCore no callback de book (OfferBookCallback).
        /// </summary>
        /// <param name="ticker">Símbolo do ativo</param>
        /// <param name="agentName">Nome da corretora que colocou a ordem</param>
        /// <param name="lado">"compra" (bid) ou "venda" (ask)</param>
        /// <param name="nivel">Nível do book (1 a 5, geralmente)</param>
        /// <param name="qtd">Quantidade da ordem</param>
        public void OnBookUpdate(string ticker, string agentName, string lado, int nivel, int qtd, string callbackInfo)
        {
            EventosRecebidos++;

            AppendCallbackLog(callbackInfo);

            if (!_engine.MotorAtivo)
            {
                EventosDescartados_MotorParado++;
                return;
            }

            if (!EhAtivoAceito(ticker))
            {
                EventosDescartados_AtivoErrado++;
                return;
            }

            if (string.IsNullOrWhiteSpace(agentName)) return;
            if (lado != "compra" && lado != "venda") return;

            _engine.ProcessarBook(agentName, lado, nivel, qtd, callbackInfo);
            EventosEnviadosAoEngine++;
        }
        
        /// <summary>
        /// Alternativa: se o MarketCore preferir mandar trade puro (sem agressor identificado).
        /// Útil pra tickers de agressão total sem detalhe.
        /// </summary>
        public void OnTradeGenerico(string ticker, string agentName, string lado, int qtd)
        {
            EventosRecebidos++;
            
            if (!_engine.MotorAtivo)
            {
                EventosDescartados_MotorParado++;
                return;
            }
            
            if (!EhAtivoAceito(ticker))
            {
                EventosDescartados_AtivoErrado++;
                return;
            }
            
            if (string.IsNullOrWhiteSpace(agentName)) return;
            
            _engine.ProcessarTrade(agentName, lado, qtd);
            EventosEnviadosAoEngine++;
        }
        
        /// <summary>
        /// Verifica se o ticker está na whitelist. Apenas o símbolo contínuo é aceito
        /// (WINFUT) — evita duplicação com o contrato específico do mês (WINQ26 etc.),
        /// que carrega os MESMOS eventos com ~5-25ms de diferença.
        /// </summary>
        private bool EhAtivoAceito(string ticker)
        {
            if (string.IsNullOrWhiteSpace(ticker)) return false;
            return TickersAceitos.Contains(ticker);
        }
        
        /// <summary>
        /// Retorna estatísticas de uso do bridge (útil pra debug).
        /// </summary>
        public string ObterEstatisticas()
        {
            return $"Bridge stats: recebidos={EventosRecebidos}, " +
                   $"descartados_motor_parado={EventosDescartados_MotorParado}, " +
                   $"descartados_ativo_errado={EventosDescartados_AtivoErrado}, " +
                   $"enviados_engine={EventosEnviadosAoEngine}";
        }
        
        /// <summary>
        /// Reseta contadores de estatísticas.
        /// </summary>
        public void ResetarEstatisticas()
        {
            EventosRecebidos = 0;
            EventosDescartados_MotorParado = 0;
            EventosDescartados_AtivoErrado = 0;
            EventosEnviadosAoEngine = 0;
        }
    }
}