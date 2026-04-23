using System;
using System.Threading.Tasks;
using MarketCore.Models;

namespace MarketCore.Contracts
{
    /// <summary>
    /// Interface para gravação de dados de mercado em disco.
    /// Define o contrato que o MarketRecorder deve implementar.
    /// </summary>
    public interface IMarketRecorder : IDisposable
    {
        /// <summary>
        /// Inicia uma nova sessão de gravação para um pregão específico.
        /// </summary>
        /// <param name="data">Data do pregão (ex: 2026-04-23)</param>
        /// <returns>True se conseguiu inicializar; False se houve erro (ex: disco cheio)</returns>
        Task<bool> IniciarPregaoAsync(DateOnly data);

        /// <summary>
        /// Grava um evento de trade para um ativo específico.
        /// </summary>
        /// <param name="ativo">WIN, WDO ou WSP</param>
        /// <param name="trade">Dados do trade a gravar</param>
        /// <returns>True se gravado com sucesso</returns>
        Task<bool> GravarTradeAsync(string ativo, MarketCore.Models.TradeEvent trade);

        /// <summary>
        /// Grava um snapshot do book de ofertas para um ativo específico.
        /// </summary>
        /// <param name="ativo">WIN, WDO ou WSP</param>
        /// <param name="book">BookSnapshot a gravar</param>
        /// <returns>True se gravado com sucesso</returns>
        Task<bool> GravarBookAsync(string ativo, MarketCore.Models.BookSnapshot book);

        /// <summary>
        /// Grava um evento de engine (ex: rolagem de contrato, gap detectado).
        /// </summary>
        /// <param name="evento">Descrição do evento</param>
        /// <param name="timestamp">Timestamp do evento</param>
        Task<bool> GravarEventoAsync(string evento, DateTime timestamp);

        /// <summary>
        /// Finaliza a gravação do pregão atual e persiste metadata.
        /// Calcula hash de integridade, comprime se necessário, registra metadados.
        /// </summary>
        Task<bool> FinalizarPregaoAsync();

        /// <summary>
        /// Retorna o estado atual do recorder.
        /// </summary>
        RecorderStatus Status { get; }

        /// <summary>
        /// Evento disparado quando há erro na gravação.
        /// </summary>
        event EventHandler<RecorderErrorEventArgs>? ErroGravacao;

        /// <summary>
        /// Evento disparado quando há aviso (ex: disco cheio em 10%).
        /// </summary>
        event EventHandler<RecorderWarningEventArgs>? AvisoGravacao;
    }

    /// <summary>
    /// Estado atual do recorder.
    /// </summary>
    public class RecorderStatus
    {
        /// <summary>
        /// Pregão ativo (ex: 2026-04-23), ou null se nenhum pregão aberto.
        /// </summary>
        public DateOnly? PregaoAtivo { get; set; }

        /// <summary>
        /// Espaço livre em GB no disco onde está gravando.
        /// </summary>
        public double EspacoLivreGB { get; set; }

        /// <summary>
        /// Tamanho da fila de trades aguardando gravação.
        /// </summary>
        public int FilaTrades { get; set; }

        /// <summary>
        /// Tamanho da fila de books aguardando gravação.
        /// </summary>
        public int FileBook { get; set; }

        /// <summary>
        /// Número de trades gravados na sessão atual.
        /// </summary>
        public long TotaisTrades { get; set; }

        /// <summary>
        /// Número de book snapshots gravados na sessão atual.
        /// </summary>
        public long TotaisBooks { get; set; }

        /// <summary>
        /// Número de bytes gravados até agora (antes de compressão).
        /// </summary>
        public long BytesGravados { get; set; }
    }

    /// <summary>
    /// Argumentos para evento de erro na gravação.
    /// </summary>
    public class RecorderErrorEventArgs : EventArgs
    {
        public string Mensagem { get; set; } = string.Empty;
        public Exception? Excecao { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Argumentos para evento de aviso na gravação.
    /// </summary>
    public class RecorderWarningEventArgs : EventArgs
    {
        public string Mensagem { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}