using System;
using System.Threading.Tasks;
using MarketCore.Models;

namespace MarketCore.Contracts
{
    public interface IMarketRecorder : IDisposable
    {
        Task<bool> IniciarPregaoAsync(DateOnly data);
        Task<bool> GravarTradeAsync(string ativo, MarketCore.Models.TradeEvent trade);
        Task<bool> GravarBookAsync(string ativo, MarketCore.Models.BookSnapshot book);
        Task<bool> GravarEventoAsync(string evento, DateTime timestamp);
        Task<bool> FinalizarPregaoAsync();

        /// <summary>
        /// Grava um snapshot do FlowScore a cada 1 segundo.
        /// Gera WIN_flowscore.bin — usado pelo AutoCalibradorEngine.
        /// </summary>
        Task<bool> GravarFlowScoreAsync(
            string ativo, double preco, double scoreTotal,
            double brokerFlow, double fluxoDireto, double book, double detectores);

        RecorderStatus Status { get; }
        event EventHandler<RecorderErrorEventArgs>?   ErroGravacao;
        event EventHandler<RecorderWarningEventArgs>? AvisoGravacao;
    }

    public class RecorderStatus
    {
        public DateOnly? PregaoAtivo   { get; set; }
        public double    EspacoLivreGB { get; set; }
        public int       FilaTrades    { get; set; }
        public int       FileBook      { get; set; }
        public long      TotaisTrades  { get; set; }
        public long      TotaisBooks   { get; set; }
        public long      BytesGravados { get; set; }
    }

    public class RecorderErrorEventArgs : EventArgs
    {
        public string    Mensagem  { get; set; } = string.Empty;
        public Exception? Excecao  { get; set; }
        public DateTime  Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class RecorderWarningEventArgs : EventArgs
    {
        public string   Mensagem  { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
