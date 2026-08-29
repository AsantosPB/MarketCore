using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace MarketCore.Providers.Nelogica
{
    public class OfferBookMarshalDiagnostic
    {
        private const string DLL_PATH = @"ProfitDLL64.dll";

        // TESTE COM 24 BYTES (Pack = 8, adiciona padding de 4 bytes)
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
        private struct TAssetID_24
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string Ticker;
            [MarshalAs(UnmanagedType.LPWStr)] public string Bolsa;
            public int nFeedType;
            private int _padding;  // 4 bytes de alinhamento para chegar a 24 bytes
        }

        private delegate void TOfferBookCallback_24(
            TAssetID_24 assetId, int nAction, int nPosition,
            int side, int nQtd, int nAgent, long nOfferID, double sPrice,
            int bHasPrice, int bHasQtd, int bHasDate, int bHasOfferID, int bHasAgent,
            [MarshalAs(UnmanagedType.LPWStr)] string date,
            IntPtr pArraySell, IntPtr pArrayBuy);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int SetOfferBookCallback(TOfferBookCallback_24 callback);

        private static int _count = 0;
        private static readonly StringBuilder _log = new();

        private static void OnOfferBookCallback(
            TAssetID_24 assetId, int nAction, int nPosition,
            int side, int nQtd, int nAgent, long nOfferID, double sPrice,
            int bHasPrice, int bHasQtd, int bHasDate, int bHasOfferID, int bHasAgent,
            string date, IntPtr pArraySell, IntPtr pArrayBuy)
        {
            if (_count >= 10) return;
            _count++;

            _log.AppendLine($"═══ EVENTO {_count} ═══");
            _log.AppendLine($"Ticker: {assetId.Ticker ?? "NULL"}");
            _log.AppendLine($"Bolsa: {assetId.Bolsa ?? "NULL"}");
            _log.AppendLine($"nFeedType: {assetId.nFeedType}");
            _log.AppendLine($"nAction: {nAction}");
            _log.AppendLine($"nPosition: {nPosition}");
            _log.AppendLine($"side: {side}");
            _log.AppendLine($"nQtd: {nQtd}");
            _log.AppendLine($"nAgent: {nAgent}");
            _log.AppendLine($"nOfferID: {nOfferID}");
            _log.AppendLine($"sPrice: {sPrice:F2}");
            _log.AppendLine($"bHasPrice: {bHasPrice}");
            _log.AppendLine($"bHasQtd: {bHasQtd}");
            _log.AppendLine($"bHasOfferID: {bHasOfferID}");
            _log.AppendLine($"bHasAgent: {bHasAgent}");
            _log.AppendLine();

            if (_count == 10)
            {
                MessageBox.Show(_log.ToString(), 
                    "TESTE 24 BYTES - Primeiros 10 eventos", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Information);
            }
        }

        public static void ExecutarTeste()
        {
            MessageBox.Show(
                "TESTE COM 24 BYTES (Pack=8)\n\n" +
                "Vai capturar os próximos 10 eventos do OfferBook\n" +
                "e mostrar os dados RAW.\n\n" +
                "Clique OK para continuar.",
                "TESTE 24 BYTES",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            int r = SetOfferBookCallback(OnOfferBookCallback);
            
            MessageBox.Show(
                $"SetOfferBookCallback retornou: {r}\n\n" +
                "Aguarde alguns segundos...\n" +
                "Um popup vai aparecer com os 10 primeiros eventos capturados.",
                "TESTE 24 BYTES",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
}
