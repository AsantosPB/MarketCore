using System.Windows;
using System.Windows.Controls;

namespace MarketCore.FlowSense
{
    public partial class ProfitLoginWindow : Window
    {
        public ProfitCredentials Credentials { get; private set; } = new();

        /// <summary>True = Mercado Real (ProfitDLL), False = Simulador</summary>
        public bool IsRealMarket { get; private set; } = true;

        private bool _passwordVisible = false;

        public ProfitLoginWindow()
        {
            InitializeComponent();

            // Eventos dos botões de modo
            BtnModeReal.Click      += BtnModeReal_Click;
            BtnModeSimulator.Click += BtnModeSimulator_Click;

            // Eventos dos campos
            BtnConnect.Click        += BtnConnect_Click;
            BtnCancel.Click         += BtnCancel_Click;
            BtnTogglePassword.Click += BtnTogglePassword_Click;

            TxtActivationKey.TextChanged += (s, e) => ClearError();
            TxtUsername.TextChanged      += (s, e) => ClearError();
            TxtPassword.PasswordChanged  += (s, e) => ClearError();

            TxtActivationKey.KeyDown += OnEnterKey;
            TxtUsername.KeyDown      += OnEnterKey;
            TxtPassword.KeyDown      += OnEnterKey;

            LoadSavedCredentials();
        }

        // ══════════════════════════════════════════════════════
        // SELEÇÃO DE MODO
        // ══════════════════════════════════════════════════════

        private void BtnModeReal_Click(object sender, RoutedEventArgs e)
        {
            IsRealMarket = true;
            BtnModeReal.Style      = (Style)FindResource("BtnModeActive");
            BtnModeSimulator.Style = (Style)FindResource("BtnModeInactive");
            PanelCredentials.Visibility    = Visibility.Visible;
            PanelSimulatorInfo.Visibility  = Visibility.Collapsed;
            ClearError();
        }

        private void BtnModeSimulator_Click(object sender, RoutedEventArgs e)
        {
            IsRealMarket = false;
            BtnModeSimulator.Style = (Style)FindResource("BtnModeActive");
            BtnModeReal.Style      = (Style)FindResource("BtnModeInactive");
            PanelCredentials.Visibility    = Visibility.Collapsed;
            PanelSimulatorInfo.Visibility  = Visibility.Visible;
            ClearError();
        }

        // ══════════════════════════════════════════════════════
        // CARGA DE CREDENCIAIS SALVAS
        // ══════════════════════════════════════════════════════

        private void LoadSavedCredentials()
        {
            var saved = ProfitCredentials.Load();
            if (saved.HasSavedCredentials && saved.RememberMe)
            {
                TxtActivationKey.Text = saved.ActivationKey;
                TxtUsername.Text      = saved.Username;
                TxtPassword.Password  = saved.Password;
                ChkRemember.IsChecked = true;
                BtnConnect.Focus();
            }
            else
            {
                TxtActivationKey.Focus();
            }
        }

        // ══════════════════════════════════════════════════════
        // BOTÃO CONECTAR
        // ══════════════════════════════════════════════════════

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            ClearError();

            // Modo simulador — conecta direto sem credenciais
            if (!IsRealMarket)
            {
                Credentials = new ProfitCredentials
                {
                    ActivationKey = "",
                    Username      = "",
                    Password      = "",
                    RememberMe    = false
                };
                DialogResult = true;
                Close();
                return;
            }

            // Modo real — valida credenciais
            string activationKey = TxtActivationKey.Text.Trim();
            string username      = TxtUsername.Text.Trim();
            string password      = _passwordVisible
                ? TxtPasswordVisible.Text
                : TxtPassword.Password;

            if (string.IsNullOrWhiteSpace(activationKey))
            {
                ShowError("Preencha a Chave de Ativação.");
                TxtActivationKey.Focus();
                return;
            }
            if (string.IsNullOrWhiteSpace(username))
            {
                ShowError("Preencha o Usuário.");
                TxtUsername.Focus();
                return;
            }
            if (string.IsNullOrWhiteSpace(password))
            {
                ShowError("Preencha a Senha.");
                TxtPassword.Focus();
                return;
            }

            Credentials = new ProfitCredentials
            {
                ActivationKey = activationKey,
                Username      = username,
                Password      = password,
                RememberMe    = ChkRemember.IsChecked == true
            };
            Credentials.Save();

            DialogResult = true;
            Close();
        }

        // ══════════════════════════════════════════════════════
        // BOTÃO CANCELAR
        // ══════════════════════════════════════════════════════

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ══════════════════════════════════════════════════════
        // MOSTRAR / OCULTAR SENHA
        // ══════════════════════════════════════════════════════

        private void BtnTogglePassword_Click(object sender, RoutedEventArgs e)
        {
            _passwordVisible = !_passwordVisible;

            if (_passwordVisible)
            {
                TxtPasswordVisible.Text       = TxtPassword.Password;
                TxtPassword.Visibility        = Visibility.Collapsed;
                TxtPasswordVisible.Visibility = Visibility.Visible;
                BtnTogglePassword.Foreground  = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0, 191, 255));
                TxtPasswordVisible.Focus();
                TxtPasswordVisible.CaretIndex = TxtPasswordVisible.Text.Length;
            }
            else
            {
                TxtPassword.Password          = TxtPasswordVisible.Text;
                TxtPasswordVisible.Visibility = Visibility.Collapsed;
                TxtPassword.Visibility        = Visibility.Visible;
                BtnTogglePassword.Foreground  = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(102, 102, 102));
                TxtPassword.Focus();
            }
        }

        // ══════════════════════════════════════════════════════
        // ENTER = CONECTAR
        // ══════════════════════════════════════════════════════

        private void OnEnterKey(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                BtnConnect_Click(sender, new RoutedEventArgs());
        }

        // ══════════════════════════════════════════════════════
        // HELPERS
        // ══════════════════════════════════════════════════════

        public void ShowError(string message)
        {
            ErrorLabel.Text        = message;
            ErrorBorder.Visibility = Visibility.Visible;
        }

        private void ClearError()
        {
            ErrorBorder.Visibility = Visibility.Collapsed;
            ErrorLabel.Text        = "";
        }
    }
}
