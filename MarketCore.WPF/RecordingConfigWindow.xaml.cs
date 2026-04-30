using System;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using MarketCore.FlowSense;

namespace MarketCore.WPF
{
    public partial class RecordingConfigWindow : Window
    {
        public string? SelectedPath { get; private set; }
        private readonly RecordingConfig _config;

        public RecordingConfigWindow()
        {
            InitializeComponent();
            _config = RecordingConfig.Load();
            TxPath.Text = _config.RecordingsPath;
            UpdateSpaceInfo(_config.RecordingsPath);
            TxPath.TextChanged += (s, e) => UpdateSpaceInfo(TxPath.Text);
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description         = "Selecione a pasta para armazenar os históricos de gravação",
                SelectedPath        = TxPath.Text,
                ShowNewFolderButton = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                TxPath.Text = dialog.SelectedPath;
                UpdateSpaceInfo(dialog.SelectedPath);
            }
        }

        private void BtnDefault_Click(object sender, RoutedEventArgs e)
        {
            TxPath.Text = RecordingConfig.GetDefaultPath();
            UpdateSpaceInfo(TxPath.Text);
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var path = TxPath.Text.Trim();

            if (string.IsNullOrWhiteSpace(path))
            {
                System.Windows.MessageBox.Show(
                    "Por favor, informe um caminho válido.",
                    "Caminho inválido",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) && !Directory.Exists(root))
            {
                var result = System.Windows.MessageBox.Show(
                    $"O drive '{root}' não foi encontrado.\n\n" +
                    "Se for um HD externo, conecte-o antes de iniciar o programa.\n\n" +
                    "Deseja salvar mesmo assim?",
                    "Drive não encontrado",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result == MessageBoxResult.No) return;
            }

            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Não foi possível criar a pasta:\n{ex.Message}",
                    "Erro",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _config.RecordingsPath = path;
            _config.Save();
            SelectedPath = path;

            System.Windows.MessageBox.Show(
                $"Caminho salvo com sucesso!\n\n{path}",
                "Configuração salva",
                MessageBoxButton.OK, MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void UpdateSpaceInfo(string path)
        {
            try
            {
                var root = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                {
                    var info = new DriveInfo(root);
                    TbFreeSpace.Text = FormatBytes(info.AvailableFreeSpace);
                }
                else
                {
                    TbFreeSpace.Text = "N/D";
                }

                if (Directory.Exists(path))
                {
                    long total = 0;
                    foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                        total += new FileInfo(f).Length;
                    TbUsedSpace.Text = FormatBytes(total);
                }
                else
                {
                    TbUsedSpace.Text = "0 B";
                }
            }
            catch
            {
                TbFreeSpace.Text = "N/D";
                TbUsedSpace.Text = "N/D";
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
            if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F1} MB";
            if (bytes >= 1_024)         return $"{bytes / 1_024.0:F1} KB";
            return $"{bytes} B";
        }
    }
}
