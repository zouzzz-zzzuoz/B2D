using SMBLibrary;
using SMBLibrary.Client;
using SMBLibrary.SMB2;
using System.Net;

namespace ExcelClosedXML.Services
{
    /// <summary>
    /// Paramètres de connexion SMB simplifiés.
    /// Format serveur: 192.168.1.14/Users/moumo/Desktop
    /// </summary>
    public sealed class SmbSettings
    {
        /// <summary>
        /// Format: {IP}/{Share}/{Path}
        /// Exemple: 192.168.1.14/Users/moumo/Desktop
        /// </summary>
        public string Server { get; set; } = "192.168.1.14/Users/moumo/Desktop";

        public string UserName { get; set; } = "Guest";
        public string Password { get; set; } = string.Empty;
        public bool IsAnonymous { get; set; }
       // public string DisplayName { get; set; } = "Bons de débit";

        public string ServerAddress
        {
            get
            {
                var parts = Server.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 0 ? parts[0] : "127.0.0.1";
            }
        }

        public string Share
        {
            get
            {
                var parts = Server.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 1 ? parts[1] : "Users";
            }
        }

        public string Path
        {
            get
            {
                var parts = Server.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length <= 2)
                    return string.Empty;

                return string.Join("\\", parts.Skip(2));
            }
        }
    }

    public static class SmbSettingsStore
    {
        private const string ServerKey = "smb_server";
        private const string UserNameKey = "smb_username";
        private const string PasswordKey = "smb_password";
        private const string IsAnonymousKey = "smb_anonymous";
        //private const string DisplayNameKey = "smb_displayname";

        private static void Log(string message) => AppLog.Write($"[SmbSettingsStore] {message}");

        public static async Task<SmbSettings> LoadAsync()
        {
            string password = string.Empty;
            try
            {
                password = await SecureStorage.Default.GetAsync(PasswordKey) ?? string.Empty;
            }
            catch
            {
            }

            return new SmbSettings
            {
                Server = Preferences.Default.Get(ServerKey, "192.168.1.14/Users/moumo/Desktop/DEBIT/fichiers"),
                UserName = Preferences.Default.Get(UserNameKey, "Asl"),
                Password = password,
                IsAnonymous = Preferences.Default.Get(IsAnonymousKey, false),
                //DisplayName = Preferences.Default.Get(DisplayNameKey, "Bons de débit")
            };
        }

        public static async Task SaveAsync(SmbSettings settings)
        {
            Preferences.Default.Set(ServerKey, settings.Server.Trim());
            Preferences.Default.Set(UserNameKey, settings.UserName.Trim());
            Preferences.Default.Set(IsAnonymousKey, settings.IsAnonymous);
            // Preferences.Default.Set(DisplayNameKey, settings.DisplayName.Trim());

            try
            {
                await SecureStorage.Default.SetAsync(PasswordKey, settings.Password ?? string.Empty);
            }
            catch
            {
            }
        }

        public static (NTStatus Status, ISMBClient? Client) TryConnectAndLoginWithFallbacks(
            IPAddress serverAddress,
            string userName,
            string password)
        {
            var candidateDomains = new List<string> { string.Empty, "WORKGROUP" };

            try
            {
                var hostName = Dns.GetHostEntry(serverAddress).HostName;
                if (!string.IsNullOrWhiteSpace(hostName))
                {
                    candidateDomains.Add(hostName);

                    var shortHostName = hostName.Split('.')[0];
                    if (!string.IsNullOrWhiteSpace(shortHostName))
                        candidateDomains.Add(shortHostName);
                }
            }
            catch
            {
            }

            NTStatus lastStatus = NTStatus.STATUS_LOGON_FAILURE;

            foreach (var domain in candidateDomains.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var client = new SMB2Client();
                try
                {
                    if (!client.Connect(serverAddress, SMBTransportType.DirectTCPTransport))
                    {
                        try { client.Disconnect(); } catch { }
                        continue;
                    }

                    // IMPORTANT : la signature de SMBLibrary est Login(domainName, userName, password).
                    // L'ordre precedent (userName, password, domain) envoyait le mot de passe
                    // comme identifiant et le domaine comme mot de passe -> echec systematique.
                    var status = client.Login(domain, userName, password);
                    if (status == NTStatus.STATUS_SUCCESS)
                        return (status, client);

                    lastStatus = status;
                }
                catch
                {
                }

                try { client.Disconnect(); } catch { }
            }

            return (lastStatus, null);
        }

        /// <summary>
        /// Écrit un fichier directement sur le partage SMB configuré, en contournant
        /// toute application tierce (ex. gestionnaire de fichiers Android) qui pourrait
        /// ne pas synchroniser correctement les écritures faites via son Content Provider.
        /// </summary>
        /// <param name="relativePath">
        /// Chemin du fichier RELATIF au dossier configuré dans settings.Path
        /// (ex. "2999 - bon 1 SCIE CISAILLE LASER.xlsx").
        /// </param>
        public static Task<(bool Success, string Message)> WriteFileAsync(
            SmbSettings settings, string relativePath, byte[] payload, string? folderPathOverride = null)
        {
            return Task.Run(() =>
            {
                ISMBClient? client = null;

                try
                {
                    var effectiveFolderPath = folderPathOverride ?? settings.Path;
                    Log($"WriteFileAsync démarre : Server={settings.Server}, Share={settings.Share}, FolderPath={effectiveFolderPath}, relativePath={relativePath}, payload={payload.Length} octets");

                    if (!IPAddress.TryParse(settings.ServerAddress, out var ipAddress))
                    {
                        Log($"Adresse IP invalide : {settings.ServerAddress}");
                        return (false, $"Adresse IP invalide : {settings.ServerAddress}");
                    }

                    var userName = settings.IsAnonymous ? string.Empty : (settings.UserName ?? string.Empty);
                    var password = settings.IsAnonymous ? string.Empty : (settings.Password ?? string.Empty);
                    var (loginStatus, connectedClient) = TryConnectAndLoginWithFallbacks(ipAddress, userName, password);
                    client = connectedClient;
                    if (loginStatus != NTStatus.STATUS_SUCCESS || client == null)
                    {
                        Log($"Échec authentification SMB : {loginStatus}");
                        return (false, $"Échec de l'authentification SMB : {loginStatus}");
                    }

                    var store = client.TreeConnect(settings.Share, out var treeStatus);
                    if (treeStatus != NTStatus.STATUS_SUCCESS || store == null)
                    {
                        Log($"Échec TreeConnect sur partage '{settings.Share}' : {treeStatus}");
                        return (false, $"Connexion au partage '{settings.Share}' impossible : {treeStatus}");
                    }

                    var fullPath = string.IsNullOrEmpty(effectiveFolderPath)
                        ? relativePath
                        : $"{effectiveFolderPath}\\{relativePath}";

                    Log($"Chemin complet ciblé sur le partage : '{fullPath}'");

                    object? handle = null;
                    try
                    {
                        var createStatus = store.CreateFile(
                            out handle,
                            out _,
                            fullPath,
                            AccessMask.GENERIC_WRITE | AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                            SMBLibrary.FileAttributes.Normal,
                            ShareAccess.None,
                            CreateDisposition.FILE_OVERWRITE_IF,
                            CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
                            null);

                        if (createStatus != NTStatus.STATUS_SUCCESS || handle == null)
                        {
                            Log($"Échec CreateFile('{fullPath}') : {createStatus}");
                            return (false, $"Impossible d'ouvrir/créer '{fullPath}' sur le partage : {createStatus}");
                        }

                        int offset = 0;
                        const int maxChunk = 60 * 1024; // marge sous la limite de négociation SMB2 habituelle
                        while (offset < payload.Length)
                        {
                            int chunkSize = Math.Min(maxChunk, payload.Length - offset);
                            var chunk = new byte[chunkSize];
                            Array.Copy(payload, offset, chunk, 0, chunkSize);

                            var writeStatus = store.WriteFile(out int numberOfBytesWritten, handle, offset, chunk);
                            if (writeStatus != NTStatus.STATUS_SUCCESS)
                            {
                                Log($"Échec WriteFile à l'offset {offset} : {writeStatus}");
                                return (false, $"Échec d'écriture à l'offset {offset} : {writeStatus}");
                            }

                            if (numberOfBytesWritten <= 0)
                            {
                                Log($"WriteFile a écrit 0 octet à l'offset {offset} -> arrêt");
                                return (false, $"Écriture bloquée à l'offset {offset} (0 octet écrit)");
                            }

                            offset += numberOfBytesWritten;
                        }

                        store.CloseFile(handle);
                        handle = null;

                        Log($"Écriture SMB directe réussie -> '{fullPath}' ({payload.Length} octets)");
                        return (true, $"Fichier écrit avec succès sur le partage : {fullPath}");
                    }
                    finally
                    {
                        if (handle != null)
                        {
                            try { store.CloseFile(handle); } catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"EXCEPTION dans WriteFileAsync : {ex}");
                    return (false, $"Erreur SMB pendant l'écriture : {ex.Message}");
                }
                finally
                {
                    try { client?.Disconnect(); } catch { }
                }
            });
        }

        public static Task<(bool Success, string Message)> TestConnectionAsync(SmbSettings settings)
        {
            return Task.Run(() =>
            {
                ISMBClient? client = null;
                ISMBFileStore? store = null;

                try
                {
                    if (!IPAddress.TryParse(settings.ServerAddress, out var ipAddress))
                        return (false, $"Adresse IP invalide : {settings.ServerAddress}");

                    var userName = settings.IsAnonymous ? string.Empty : (settings.UserName ?? string.Empty);
                    var password = settings.IsAnonymous ? string.Empty : (settings.Password ?? string.Empty);
                    var (loginStatus, connectedClient) = TryConnectAndLoginWithFallbacks(ipAddress, userName, password);
                    client = connectedClient;
                    if (loginStatus != NTStatus.STATUS_SUCCESS || client == null)
                        return (false, $"Échec de l'authentification SMB : {loginStatus}");

                    store = client.TreeConnect(settings.Share, out var treeStatus);
                    if (treeStatus != NTStatus.STATUS_SUCCESS || store == null)
                        return (false, $"Connexion au partage '{settings.Share}' impossible : {treeStatus}");

                    return (true, $"Connexion SMB OK\nServeur : {settings.ServerAddress}\nPartage : {settings.Share}");
                }
                catch (Exception ex)
                {
                    return (false, $"Erreur SMB : {ex.Message}");
                }
                finally
                {
                    try { client?.Disconnect(); } catch { }
                }
            });
        }
    }
}
