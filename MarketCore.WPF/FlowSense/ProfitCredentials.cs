using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MarketCore.FlowSense
{
    /// <summary>
    /// Armazena e persiste as credenciais do Profit de forma segura.
    /// Usa DPAPI (Windows Data Protection API) — criptografia vinculada
    /// ao usuário Windows atual, sem precisar de senha extra.
    /// Arquivo salvo em: %AppData%\MarketCore\profit_credentials.dat
    /// </summary>
    public class ProfitCredentials
    {
        public string ActivationKey { get; set; } = "";
        public string Username      { get; set; } = "";
        public string Password      { get; set; } = "";
        public bool   RememberMe    { get; set; } = false;

        // ══════════════════════════════════════════════════════
        // PATHS
        // ══════════════════════════════════════════════════════

        private static string AppDataFolder =>
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData), "MarketCore");

        private static string CredentialsFile =>
            Path.Combine(AppDataFolder, "profit_credentials.dat");

        // ══════════════════════════════════════════════════════
        // SAVE
        // ══════════════════════════════════════════════════════

        /// <summary>
        /// Salva as credenciais criptografadas com DPAPI.
        /// Se RememberMe = false, apaga o arquivo salvo.
        /// </summary>
        public void Save()
        {
            if (!RememberMe)
            {
                Delete();
                return;
            }

            Directory.CreateDirectory(AppDataFolder);

            var payload = new CredentialPayload
            {
                ActivationKey = ActivationKey,
                Username      = Username,
                Password      = Password,
                RememberMe    = RememberMe
            };

            string json       = JsonSerializer.Serialize(payload);
            byte[] plainBytes = Encoding.UTF8.GetBytes(json);

            // DPAPI — criptografa vinculado ao usuário Windows atual
            byte[] encrypted = ProtectedData.Protect(
                plainBytes,
                null,
                DataProtectionScope.CurrentUser);

            File.WriteAllBytes(CredentialsFile, encrypted);
        }

        // ══════════════════════════════════════════════════════
        // LOAD
        // ══════════════════════════════════════════════════════

        /// <summary>
        /// Carrega credenciais salvas. Retorna instância vazia se não houver.
        /// </summary>
        public static ProfitCredentials Load()
        {
            try
            {
                if (!File.Exists(CredentialsFile))
                    return new ProfitCredentials();

                byte[] encrypted  = File.ReadAllBytes(CredentialsFile);
                byte[] plainBytes = ProtectedData.Unprotect(
                    encrypted,
                    null,
                    DataProtectionScope.CurrentUser);

                string json   = Encoding.UTF8.GetString(plainBytes);
                var    payload = JsonSerializer.Deserialize<CredentialPayload>(json);

                if (payload == null) return new ProfitCredentials();

                return new ProfitCredentials
                {
                    ActivationKey = payload.ActivationKey ?? "",
                    Username      = payload.Username      ?? "",
                    Password      = payload.Password      ?? "",
                    RememberMe    = payload.RememberMe
                };
            }
            catch
            {
                // Arquivo corrompido ou de outro usuário — ignora
                return new ProfitCredentials();
            }
        }

        // ══════════════════════════════════════════════════════
        // DELETE
        // ══════════════════════════════════════════════════════

        public static void Delete()
        {
            if (File.Exists(CredentialsFile))
                File.Delete(CredentialsFile);
        }

        public bool HasSavedCredentials =>
            !string.IsNullOrWhiteSpace(ActivationKey) &&
            !string.IsNullOrWhiteSpace(Username)      &&
            !string.IsNullOrWhiteSpace(Password);

        // Classe interna para serialização
        private class CredentialPayload
        {
            public string ActivationKey { get; set; } = "";
            public string Username      { get; set; } = "";
            public string Password      { get; set; } = "";
            public bool   RememberMe    { get; set; } = false;
        }
    }
}
