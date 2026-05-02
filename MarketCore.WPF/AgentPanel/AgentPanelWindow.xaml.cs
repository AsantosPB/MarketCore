using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace MarketCore.AgentPanel
{
    // ══════════════════════════════════════════════════════════
    // Modelos de dados
    // ══════════════════════════════════════════════════════════

    public class ContextItem
    {
        public string Icon  { get; set; } = "";
        public string Texto { get; set; } = "";
    }

    public class PlayerItem
    {
        public string Nome   { get; set; } = "";
        public string Tipo   { get; set; } = "";
        public string VolStr { get; set; } = "";
        public double Pct    { get; set; }
    }

    public class PatternItem
    {
        public string Hora         { get; set; } = "";
        public string Nome         { get; set; } = "";
        public string Resultado    { get; set; } = "";
        public string ResultadoCor { get; set; } = "#00FF88";
        public string Icone        { get; set; } = "";
    }

    public class CorrelItem
    {
        public string Ativo  { get; set; } = "";
        public string Valor  { get; set; } = "";
        public string PctStr { get; set; } = "";
        public string Cor    { get; set; } = "";
        public string PctCor { get; set; } = "";
        public string Extra  { get; set; } = "";
    }

    public class LogItem
    {
        public string Hora     { get; set; } = "";
        public string Tag      { get; set; } = "";
        public string Mensagem { get; set; } = "";
        public string TagCor   { get; set; } = "";
        public string TagBg    { get; set; } = "";

        /// <summary>
        /// Cria um LogItem com cores baseadas no tipo:
        /// - SINAL COMPRA  → fundo verde escuro, badge verde
        /// - SINAL VENDA   → fundo vermelho escuro, badge vermelho
        /// - NEUTRO/outros → fundo cinza escuro, badge cinza
        /// </summary>
        public static LogItem Criar(string tag, string mensagem)
        {
            // Detecta direção pelo conteúdo da mensagem
            bool isCompra = mensagem.Contains("compra", StringComparison.OrdinalIgnoreCase)
                         || mensagem.Contains("comprar", StringComparison.OrdinalIgnoreCase)
                         || mensagem.Contains("↑", StringComparison.OrdinalIgnoreCase)
                         || mensagem.Contains("alta", StringComparison.OrdinalIgnoreCase)
                         || (tag == "SINAL" && mensagem.Contains("78%", StringComparison.OrdinalIgnoreCase));

            bool isVenda  = mensagem.Contains("venda", StringComparison.OrdinalIgnoreCase)
                         || mensagem.Contains("vender", StringComparison.OrdinalIgnoreCase)
                         || mensagem.Contains("↓", StringComparison.OrdinalIgnoreCase)
                         || mensagem.Contains("baixo", StringComparison.OrdinalIgnoreCase)
                         || mensagem.Contains("inverteu para baixo", StringComparison.OrdinalIgnoreCase);

            string cor, bg;

            if (tag == "SINAL" && isCompra)
            {
                // Sinal de COMPRA — verde
                cor = "#00FF88";
                bg  = "#006633";
            }
            else if (tag == "SINAL" && isVenda)
            {
                // Sinal de VENDA — vermelho
                cor = "#FF4466";
                bg  = "#660020";
            }
            else if (tag == "SINAL")
            {
                // Sinal NEUTRO — cinza
                cor = "#94A3B8";
                bg  = "#141820";
            }
            else if (tag == "ALERTA")
            {
                // Alerta — laranja
                cor = "#FB923C";
                bg  = "#1A0F00";
            }
            else if (tag == "PADRÃO" || tag == "PADRão" || tag.StartsWith("PAD"))
            {
                // Padrão — azul
                cor = "#38BDF8";
                bg  = "#001A20";
            }
            else if (tag == "PLAYER")
            {
                // Player — amarelo
                cor = "#FBBF24";
                bg  = "#1A1400";
            }
            else if (tag == "CORRELAÇÃO" || tag.StartsWith("CORREL"))
            {
                // Correlação — roxo
                cor = "#A78BFA";
                bg  = "#120A20";
            }
            else if (tag == "CONTEXTO")
            {
                // Contexto — cinza médio
                cor = "#94A3B8";
                bg  = "#141820";
            }
            else if (tag == "GESTÃO" || tag.StartsWith("GEST"))
            {
                // Gestão — rosa
                cor = "#E879F9";
                bg  = "#180A1A";
            }
            else
            {
                // Default neutro — cinza
                cor = "#94A3B8";
                bg  = "#141820";
            }

            return new LogItem
            {
                Hora     = DateTime.Now.ToString("HH:mm:ss"),
                Tag      = tag,
                Mensagem = mensagem,
                TagCor   = cor,
                TagBg    = bg,
            };
        }
    }

    // ══════════════════════════════════════════════════════════
    // AgentSnapshot
    // ══════════════════════════════════════════════════════════
    public enum Sinal { Compra, Venda, Neutro }

    public class AgentSnapshot
    {
        public Sinal   Sinal      { get; set; }
        public double  Preco      { get; set; }
        public double  Confianca  { get; set; }
        public double  FlowScore  { get; set; }
        public double  Stop       { get; set; }
        public int     StopPts    { get; set; }
        public double  Alvo       { get; set; }
        public int     AlvoPts    { get; set; }
        public double  RR         { get; set; }
        public int     Lote       { get; set; }
        public List<ContextItem> Contexto    { get; set; } = new();
        public List<PlayerItem>  Compradores { get; set; } = new();
        public List<PlayerItem>  Vendedores  { get; set; } = new();
        public double  BookImbalance { get; set; }
        public int     CVDAcc        { get; set; }
        public List<PatternItem> Padroes     { get; set; } = new();
        public int     PatOk    { get; set; }
        public int     PatFail  { get; set; }
        public double  PatWR    { get; set; }
        public List<CorrelItem> Correlacoes  { get; set; } = new();
        public double  CorrWinWdo { get; set; }
        public double  CorrWinWsp { get; set; }
        public string  CorrGap    { get; set; } = "";
        public double  WinRate    { get; set; }
        public string  Regime     { get; set; } = "";
        public string  ProxEvento { get; set; } = "";
    }

    // ══════════════════════════════════════════════════════════
    // AgentPanelWindow
    // ══════════════════════════════════════════════════════════
    public partial class AgentPanelWindow : Window
    {
        private readonly ObservableCollection<ContextItem> _contextItems = new();
        private readonly ObservableCollection<PlayerItem>  _buyers       = new();
        private readonly ObservableCollection<PlayerItem>  _sellers      = new();
        private readonly ObservableCollection<PatternItem> _patterns     = new();
        private readonly ObservableCollection<CorrelItem>  _correlItems  = new();
        private readonly ObservableCollection<LogItem>     _logItems     = new();

        private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        private static readonly string _posFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MarketCore", "agentpanel_pos.json");

        public AgentPanelWindow()
        {
            InitializeComponent();

            ContextList.ItemsSource  = _contextItems;
            BuyersList.ItemsSource   = _buyers;
            SellersList.ItemsSource  = _sellers;
            PatternsList.ItemsSource = _patterns;
            CorrelList.ItemsSource   = _correlItems;
            LogList.ItemsSource      = _logItems;

            _clockTimer.Tick += (_, _) => ClockLabel.Text = DateTime.Now.ToString("HH:mm:ss");
            _clockTimer.Start();

            CarregarDadosIniciais();
        }

        // ══ Atualiza todos os painéis ══
        public void AtualizarPainel(AgentSnapshot snap)
        {
            Dispatcher.InvokeAsync(() =>
            {
                // Sinal
                var cor = snap.Sinal switch
                {
                    Sinal.Compra => Color.FromRgb(0x00, 0xFF, 0x88),
                    Sinal.Venda  => Color.FromRgb(0xFF, 0x44, 0x66),
                    _            => Color.FromRgb(0x94, 0xA3, 0xB8),
                };
                SignalLabel.Text       = snap.Sinal switch { Sinal.Compra => "▲  COMPRA", Sinal.Venda => "▼  VENDA", _ => "◆  NEUTRO" };
                SignalLabel.Foreground  = new SolidColorBrush(cor);
                SignalBorder.BorderBrush = new SolidColorBrush(cor);
                PriceLabel.Text         = snap.Preco.ToString("N0");
                ConfLabel.Text          = $"{snap.Confianca:0}%";
                ConfBar.Value           = snap.Confianca;
                ScoreValueLabel.Text    = snap.FlowScore.ToString("F2");
                ScoreBar.Value          = snap.FlowScore * 100;

                // Gestão
                StopLabel.Text      = snap.Stop.ToString("N0");
                StopPtsLabel.Text   = $"{snap.StopPts:+0;-0} pts";
                TargetLabel.Text    = snap.Alvo.ToString("N0");
                TargetPtsLabel.Text = $"{snap.AlvoPts:+0;-0} pts";
                RRLabel.Text        = $"1 : {snap.RR:F1}";
                LoteLabel.Text      = $"{snap.Lote} contrato{(snap.Lote != 1 ? "s" : "")}";

                // Contexto
                _contextItems.Clear();
                foreach (var c in snap.Contexto) _contextItems.Add(c);

                // Players
                _buyers.Clear();
                foreach (var b in snap.Compradores) _buyers.Add(b);
                _sellers.Clear();
                foreach (var s in snap.Vendedores) _sellers.Add(s);

                // Pressão
                ImbalanceLabel.Text = $"{snap.BookImbalance:0}% BID";
                ImbalanceLabel.Foreground = snap.BookImbalance > 50
                    ? new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x88))
                    : new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x66));
                BidCol.Width = new GridLength(snap.BookImbalance, GridUnitType.Star);
                AskCol.Width = new GridLength(100 - snap.BookImbalance, GridUnitType.Star);
                CVDAccLabel.Text = $"{snap.CVDAcc:+0;-0}";
                CVDAccLabel.Foreground = snap.CVDAcc >= 0
                    ? new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x88))
                    : new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x66));
                CVDBar.Value = Math.Min(100, Math.Abs(snap.CVDAcc) / 40.0 * 100);

                // Padrões
                _patterns.Clear();
                foreach (var p in snap.Padroes) _patterns.Add(p);
                PatternStatsLabel.Text = $"{snap.PatOk}✅ {snap.PatFail}❌";
                PatternWRLabel.Text    = $"{snap.PatWR:0}% acerto";

                // Correlações
                _correlItems.Clear();
                foreach (var c in snap.Correlacoes) _correlItems.Add(c);
                CorrWinWdoLabel.Text = snap.CorrWinWdo.ToString("F2");
                CorrWinWdoBar.Value  = Math.Abs(snap.CorrWinWdo) * 100;
                CorrWinWspLabel.Text = snap.CorrWinWsp.ToString("+0.00;-0.00");
                CorrWinWspBar.Value  = snap.CorrWinWsp * 100;
                CorrGapLabel.Text    = snap.CorrGap;

                // Saúde
                WinRateRun.Text = $"{snap.WinRate:0}%";
                RegimeRun.Text  = snap.Regime;
                EventoRun.Text  = snap.ProxEvento;
            });
        }

        // ══ Adiciona evento no log ══
        public void AdicionarLog(string tag, string mensagem)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _logItems.Insert(0, LogItem.Criar(tag, mensagem));
                while (_logItems.Count > 50)
                    _logItems.RemoveAt(_logItems.Count - 1);
            });
        }

        // ══ Salva / restaura posição ══
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(_posFile))
                {
                    var json = File.ReadAllText(_posFile);
                    var pos  = JsonSerializer.Deserialize<WindowPos>(json);
                    if (pos != null)
                    {
                        Left   = pos.Left;
                        Top    = pos.Top;
                        Width  = pos.Width  > 0 ? pos.Width  : 900;
                        Height = pos.Height > 0 ? pos.Height : 700;
                    }
                }
            }
            catch { }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_posFile)!);
                var pos  = new WindowPos { Left = Left, Top = Top, Width = Width, Height = Height };
                var json = JsonSerializer.Serialize(pos);
                File.WriteAllText(_posFile, json);
            }
            catch { }
        }

        private record WindowPos(double Left = 100, double Top = 100, double Width = 900, double Height = 700);

        // ══ Dados iniciais de exemplo ══
        private void CarregarDadosIniciais()
        {
            _contextItems.Add(new ContextItem { Icon = "✅", Texto = "Absorção em suporte detectada" });
            _contextItems.Add(new ContextItem { Icon = "✅", Texto = "BTG comprando pesado (iniciador)" });
            _contextItems.Add(new ContextItem { Icon = "✅", Texto = "CVD acelerando +14 (5s)" });
            _contextItems.Add(new ContextItem { Icon = "✅", Texto = "Book imbalance 68% bid" });
            _contextItems.Add(new ContextItem { Icon = "⚠️", Texto = "Horário moderado (14h20)" });
            _contextItems.Add(new ContextItem { Icon = "❌", Texto = "WDO sem confirmação total" });

            _buyers.Add(new PlayerItem { Nome = "BTG",  Tipo = "🔥", VolStr = "847 lotes", Pct = 100 });
            _buyers.Add(new PlayerItem { Nome = "XP",   Tipo = "",   VolStr = "412 lotes", Pct = 49  });
            _buyers.Add(new PlayerItem { Nome = "ITAU", Tipo = "",   VolStr = "201 lotes", Pct = 24  });

            _sellers.Add(new PlayerItem { Nome = "GNL",   Tipo = "⚡", VolStr = "623 lotes", Pct = 100 });
            _sellers.Add(new PlayerItem { Nome = "MODAL", Tipo = "",   VolStr = "389 lotes", Pct = 62  });
            _sellers.Add(new PlayerItem { Nome = "CLEAR", Tipo = "",   VolStr = "178 lotes", Pct = 29  });

            _patterns.Add(new PatternItem { Hora = "14:21", Nome = "Absorção suporte",  Resultado = "▲ +5pts", ResultadoCor = "#00FF88", Icone = "✅" });
            _patterns.Add(new PatternItem { Hora = "13:45", Nome = "Stop hunt",         Resultado = "▲ +4pts", ResultadoCor = "#00FF88", Icone = "✅" });
            _patterns.Add(new PatternItem { Hora = "13:12", Nome = "Divergência CVD",   Resultado = "▼ -2pts", ResultadoCor = "#FF4466", Icone = "❌" });
            _patterns.Add(new PatternItem { Hora = "11:30", Nome = "Rafada compradora", Resultado = "▲ +7pts", ResultadoCor = "#00FF88", Icone = "✅" });
            _patterns.Add(new PatternItem { Hora = "10:15", Nome = "Iceberg detectado", Resultado = "▲ +3pts", ResultadoCor = "#00FF88", Icone = "✅" });

            _correlItems.Add(new CorrelItem { Ativo = "WSP", Valor = "5.821,4", PctStr = "▲ 0.41%", Cor = "#38BDF8", PctCor = "#00FF88", Extra = "● lidera" });
            _correlItems.Add(new CorrelItem { Ativo = "WIN", Valor = "127.854", PctStr = "▲ 0.18%", Cor = "#00FF88", PctCor = "#00FF88", Extra = "● lag 28s" });
            _correlItems.Add(new CorrelItem { Ativo = "WDO", Valor = "5,312",   PctStr = "▼ 0.31%", Cor = "#FF4466", PctCor = "#FF4466", Extra = "● normal"  });

            // Logs com direções claras para testar as cores
            AdicionarLog("SINAL",      "Convergência total: WSP↑ + WDO↓ + Absorção WIN. Confiança 84%.");
            AdicionarLog("CORRELAÇÃO", "WDO inverteu para baixo. Correlação WIN/WDO normal (-0.79).");
            AdicionarLog("CORRELAÇÃO", "WSP subiu 0.41% nos últimos 3min. WIN lag ~28s.");
            AdicionarLog("SINAL",      "Sinal de compra gerado. Confiança 78%. BTG absorvendo em 127.850.");
            AdicionarLog("PLAYER",     "BTG aumentou posição compradora. Total sessão: 847 lotes.");
            AdicionarLog("PADRÃO",     "Absorção detectada em 127.850. Preço testou 3x sem romper.");
            AdicionarLog("CONTEXTO",   "Horário de boa liquidez. Sem eventos macro até 16h00 (Copom).");
            AdicionarLog("ALERTA",     "WDO sem confirmação de direção. Aguardando alinhamento.");
        }
    }
}
