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

        // ------------------------------------------------------------------
        // Mise à jour automatique de l'Hôte selon le .xlsx ouvert
        // ------------------------------------------------------------------
        // Alpha File Manager ne transmet pas l'adresse SMB d'origine : on retrouve
        // donc le fichier sur le serveur (nom + taille) et on en déduit son dossier.
        // Hôte = {IP}/{Partage}/{dossier du .xlsx}/fichiers

        private const int SearchMaxDepth = 8;
        private const int SearchMaxDirectories = 1500;
        private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(20);

        private static readonly HashSet<string> SearchSkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
        {
            "fichiers", "AppData", "$RECYCLE.BIN", "System Volume Information",
            "Windows", "Program Files", "Program Files (x86)"
        };

        private static readonly object HostUpdateLock = new();
        private static Task _pendingHostUpdate = Task.CompletedTask;

        /// <summary>Lance la mise à jour de l'Hôte en arrière-plan (ne bloque pas l'ouverture du fichier).</summary>
        public static void BeginUpdateHostForOpenedFile(string fileName, long fileSize, string? cacheDirectory = null)
        {
            lock (HostUpdateLock)
            {
                _pendingHostUpdate = UpdateHostForOpenedFileAsync(fileName, fileSize, cacheDirectory);
            }
        }

        /// <summary>Attend la fin d'une mise à jour d'Hôte éventuellement en cours (ne lève jamais).</summary>
        public static Task WaitForPendingHostUpdateAsync()
        {
            Task pending;
            lock (HostUpdateLock)
            {
                pending = _pendingHostUpdate;
            }
            return pending;
        }

        public static async Task UpdateHostForOpenedFileAsync(string fileName, long fileSize, string? cacheDirectory = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileName))
                    return;

                var settings = await LoadAsync();
                var located = await Task.Run(() => LocateFileOnServer(settings, fileName, fileSize, cacheDirectory));

                if (located == null)
                {
                    Log($"UpdateHost : '{fileName}' ({fileSize} octets) introuvable ou ambigu sur le serveur -> Hôte inchangé ({settings.Server})");
                    return;
                }

                var (share, folderSegments) = located.Value;
                var parts = new List<string> { settings.ServerAddress, share };
                parts.AddRange(folderSegments);
                parts.Add("fichiers");
                var newServer = string.Join("/", parts);

                if (string.Equals(newServer, settings.Server?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    Log($"UpdateHost : Hôte déjà correct ({newServer})");
                    return;
                }

                Log($"UpdateHost : Hôte '{settings.Server}' -> '{newServer}'");
                settings.Server = newServer;
                await SaveAsync(settings);
            }
            catch (Exception ex)
            {
                Log($"UpdateHost : EXCEPTION (ignorée) : {ex}");
            }
        }

        private static (string Share, List<string> FolderSegments)? LocateFileOnServer(
            SmbSettings settings, string fileName, long fileSize, string? cacheDirectory)
        {
            ISMBClient? client = null;
            try
            {
                if (!IPAddress.TryParse(settings.ServerAddress, out var ipAddress))
                    return null;

                var userName = settings.IsAnonymous ? string.Empty : (settings.UserName ?? string.Empty);
                var password = settings.IsAnonymous ? string.Empty : (settings.Password ?? string.Empty);
                var (loginStatus, connectedClient) = TryConnectAndLoginWithFallbacks(ipAddress, userName, password);
                client = connectedClient;
                if (loginStatus != NTStatus.STATUS_SUCCESS || client == null)
                {
                    Log($"UpdateHost : authentification impossible ({loginStatus})");
                    return null;
                }

                var deadline = DateTime.UtcNow + SearchTimeout;
                int directoriesVisited = 0;

                // Partages à explorer : le partage actuel d'abord, puis les autres.
                var shares = new List<string> { settings.Share };
                try
                {
                    var listed = client.ListShares(out var listStatus);
                    if (listStatus == NTStatus.STATUS_SUCCESS && listed != null)
                    {
                        foreach (var s in listed)
                        {
                            if (!s.EndsWith("$") && !shares.Contains(s, StringComparer.OrdinalIgnoreCase))
                                shares.Add(s);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"UpdateHost : ListShares impossible : {ex.Message}");
                }

                // Chemin rapide : dossier .xlsx actuellement configuré.
                var currentFolder = settings.Path;
                var currentSegments = currentFolder.Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (currentSegments.Count > 0 &&
                    string.Equals(currentSegments[^1], "fichiers", StringComparison.OrdinalIgnoreCase))
                    currentSegments.RemoveAt(currentSegments.Count - 1);

                var matches = new List<(string Share, List<string> Segments)>();

                foreach (var share in shares)
                {
                    var store = client.TreeConnect(share, out var treeStatus);
                    if (treeStatus != NTStatus.STATUS_SUCCESS || store == null)
                        continue;

                    try
                    {
                        if (string.Equals(share, settings.Share, StringComparison.OrdinalIgnoreCase) && currentSegments.Count > 0)
                        {
                            var entries = ListDirectory(store, string.Join("\\", currentSegments));
                            if (entries != null && entries.Any(e => IsMatch(e, fileName, fileSize)))
                            {
                                Log($"UpdateHost : trouvé dans le dossier configuré ({share}\\{string.Join("\\", currentSegments)})");
                                return (share, currentSegments);
                            }
                        }

                        SearchShare(store, share, fileName, fileSize, deadline, ref directoriesVisited, matches);
                    }
                    finally
                    {
                        try { store.Disconnect(); } catch { }
                    }

                    if (DateTime.UtcNow > deadline || directoriesVisited >= SearchMaxDirectories)
                        break;
                }

                if (matches.Count == 1)
                    return (matches[0].Share, matches[0].Segments);

                if (matches.Count > 1)
                {
                    // Départage n°1 : Alpha File Manager nomme le dossier de cache local
                    // avec le hashCode Java du dossier d'origine (ex. "/DEBITS/test").
                    var cacheName = string.IsNullOrWhiteSpace(cacheDirectory)
                        ? null
                        : System.IO.Path.GetFileName(cacheDirectory.TrimEnd('/', '\\'));

                    if (long.TryParse(cacheName, out var cacheHash))
                    {
                        var byHash = matches.Where(m =>
                        {
                            var folder = "/" + m.Share + (m.Segments.Count > 0 ? "/" + string.Join("/", m.Segments) : string.Empty);
                            return JavaHashCode(folder) == cacheHash || JavaHashCode(folder.ToLowerInvariant()) == cacheHash;
                        }).ToList();

                        if (byHash.Count == 1)
                        {
                            Log($"UpdateHost : {matches.Count} correspondances, départagées par le dossier de cache ({cacheName})");
                            return (byHash[0].Share, byHash[0].Segments);
                        }
                    }

                    // Départage n°2 : un même dossier physique peut être exposé par plusieurs partages
                    // (ex. DEBITS et Users\...\DEBITS) : on privilégie le partage configuré.
                    var inCurrentShare = matches
                        .Where(m => string.Equals(m.Share, settings.Share, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (inCurrentShare.Count == 1)
                    {
                        Log($"UpdateHost : {matches.Count} correspondances, partage configuré '{settings.Share}' retenu");
                        return (inCurrentShare[0].Share, inCurrentShare[0].Segments);
                    }

                    Log($"UpdateHost : {matches.Count} fichiers correspondent -> ambigu");
                }

                return null;
            }
            catch (Exception ex)
            {
                Log($"UpdateHost : EXCEPTION pendant la recherche : {ex}");
                return null;
            }
            finally
            {
                try { client?.Disconnect(); } catch { }
            }
        }

        private static long JavaHashCode(string value)
        {
            int h = 0;
            foreach (var c in value)
                h = unchecked(31 * h + c);
            return h;
        }

        private static bool IsMatch(FileDirectoryInformation entry, string fileName, long fileSize)
        {
            if ((entry.FileAttributes & SMBLibrary.FileAttributes.Directory) != 0)
                return false;
            if (!string.Equals(entry.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                return false;
            return fileSize <= 0 || entry.EndOfFile == fileSize;
        }

        /// <summary>Parcours en largeur d'un partage, borné en profondeur, nombre de dossiers et durée.</summary>
        private static void SearchShare(
            ISMBFileStore store, string share, string fileName, long fileSize,
            DateTime deadline, ref int directoriesVisited, List<(string Share, List<string> Segments)> matches)
        {
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((string.Empty, 0));

            while (queue.Count > 0)
            {
                if (DateTime.UtcNow > deadline || directoriesVisited >= SearchMaxDirectories)
                {
                    Log($"UpdateHost : recherche interrompue (dossiers visités={directoriesVisited})");
                    return;
                }

                var (path, depth) = queue.Dequeue();
                directoriesVisited++;

                var entries = ListDirectory(store, path);
                if (entries == null)
                    continue;

                foreach (var entry in entries)
                {
                    if (entry.FileName == "." || entry.FileName == "..")
                        continue;

                    var isDirectory = (entry.FileAttributes & SMBLibrary.FileAttributes.Directory) != 0;
                    if (isDirectory)
                    {
                        if (depth < SearchMaxDepth && !SearchSkippedDirectories.Contains(entry.FileName))
                            queue.Enqueue((string.IsNullOrEmpty(path) ? entry.FileName : $"{path}\\{entry.FileName}", depth + 1));
                    }
                    else if (IsMatch(entry, fileName, fileSize))
                    {
                        var segments = path.Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();
                        Log($"UpdateHost : correspondance trouvée : {share}\\{path}\\{entry.FileName}");
                        matches.Add((share, segments));
                        if (matches.Count(m => string.Equals(m.Share, share, StringComparison.OrdinalIgnoreCase)) > 1)
                            return; // ambigu au sein du même partage
                    }
                }
            }
        }

        private static List<FileDirectoryInformation>? ListDirectory(ISMBFileStore store, string path)
        {
            object? handle = null;
            try
            {
                var status = store.CreateFile(
                    out handle,
                    out _,
                    path,
                    AccessMask.GENERIC_READ,
                    SMBLibrary.FileAttributes.Directory,
                    ShareAccess.Read | ShareAccess.Write,
                    CreateDisposition.FILE_OPEN,
                    CreateOptions.FILE_DIRECTORY_FILE,
                    null);

                if (status != NTStatus.STATUS_SUCCESS || handle == null)
                    return null;

                status = store.QueryDirectory(
                    out List<QueryDirectoryFileInformation> fileList,
                    handle,
                    "*",
                    FileInformationClass.FileDirectoryInformation);

                if (status != NTStatus.STATUS_SUCCESS && status != NTStatus.STATUS_NO_MORE_FILES)
                    return null;

                return fileList?.OfType<FileDirectoryInformation>().ToList();
            }
            catch
            {
                return null;
            }
            finally
            {
                if (handle != null)
                {
                    try { store.CloseFile(handle); } catch { }
                }
            }
        }

        // ------------------------------------------------------------------
        // Test de débit (fichier temporaire de 3 Mo, supprimé ensuite)
        // ------------------------------------------------------------------
        private const int SpeedTestSize = 3 * 1024 * 1024;
        private const int SpeedTestChunk = 60 * 1024;

        /// <summary>Appréciation d'un débit en Mo/s (les libellés sont repris par la page pour la couleur).</summary>
        public static string RateSpeed(double mbPerSecond)
        {
            if (mbPerSecond >= 5.0) return "EXCELLENT";
            if (mbPerSecond >= 4.0) return "très bon";
            if (mbPerSecond >= 2.5) return "bon";
            if (mbPerSecond >= 1.5) return "passable";
            if (mbPerSecond >= 0.5) return "lent";
            return "TROP LENT";
        }

        private static string RunSpeedTest(ISMBFileStore store, string folderPath)
        {
            // Essaie d'abord le dossier configuré, puis la racine du partage.
            var folders = new List<string>();
            if (!string.IsNullOrEmpty(folderPath))
                folders.Add(folderPath);
            folders.Add(string.Empty);

            string lastError = "inconnue";

            foreach (var folder in folders)
            {
                var path = (string.IsNullOrEmpty(folder) ? string.Empty : folder + "\\")
                           + $".smbspeedtest_{Guid.NewGuid():N}.tmp";
                object? handle = null;

                try
                {
                    var data = new byte[SpeedTestSize];
                    new Random(42).NextBytes(data);

                    // ---- Écriture ----
                    var status = store.CreateFile(
                        out handle, out _, path,
                        AccessMask.GENERIC_WRITE | AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                        SMBLibrary.FileAttributes.Normal, ShareAccess.None,
                        CreateDisposition.FILE_CREATE,
                        CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
                        null);

                    if (status != NTStatus.STATUS_SUCCESS || handle == null)
                    {
                        lastError = $"création du fichier test impossible ({status})";
                        handle = null;
                        continue;
                    }

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    int offset = 0;
                    while (offset < data.Length)
                    {
                        int size = Math.Min(SpeedTestChunk, data.Length - offset);
                        var chunk = new byte[size];
                        Array.Copy(data, offset, chunk, 0, size);

                        var ws = store.WriteFile(out int written, handle, offset, chunk);
                        if (ws != NTStatus.STATUS_SUCCESS || written <= 0)
                        {
                            lastError = $"écriture échouée ({ws})";
                            throw new InvalidOperationException(lastError);
                        }
                        offset += written;
                    }
                    sw.Stop();
                    double writeSeconds = sw.Elapsed.TotalSeconds;

                    store.CloseFile(handle);
                    handle = null;

                    // ---- Lecture (le fichier est supprimé à la fermeture) ----
                    status = store.CreateFile(
                        out handle, out _, path,
                        AccessMask.GENERIC_READ | AccessMask.DELETE | AccessMask.SYNCHRONIZE,
                        SMBLibrary.FileAttributes.Normal, ShareAccess.None,
                        CreateDisposition.FILE_OPEN,
                        CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT | CreateOptions.FILE_DELETE_ON_CLOSE,
                        null);

                    if (status != NTStatus.STATUS_SUCCESS || handle == null)
                    {
                        lastError = $"relecture impossible ({status})";
                        handle = null;
                        throw new InvalidOperationException(lastError);
                    }

                    sw.Restart();
                    long totalRead = 0;
                    while (totalRead < SpeedTestSize)
                    {
                        var rs = store.ReadFile(out byte[]? readData, handle, totalRead, SpeedTestChunk);
                        if (rs != NTStatus.STATUS_SUCCESS || readData == null || readData.Length == 0)
                        {
                            lastError = $"lecture échouée ({rs})";
                            throw new InvalidOperationException(lastError);
                        }
                        totalRead += readData.Length;
                    }
                    sw.Stop();
                    double readSeconds = sw.Elapsed.TotalSeconds;

                    store.CloseFile(handle); // supprime le fichier
                    handle = null;

                    const double mb = 3.0;
                    var where = string.IsNullOrEmpty(folder) ? "racine du partage" : folder;
                    Log($"Test débit ({where}) : écriture {mb / writeSeconds:F2} Mo/s, lecture {mb / readSeconds:F2} Mo/s");

                    var fr = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
                    double writeMbps = mb / writeSeconds;
                    double readMbps = mb / readSeconds;
                    return $"Test de débit (fichier de 3 Mo)\nÉcriture : {writeMbps.ToString("F2", fr)} Mo/s ({writeSeconds.ToString("F3", fr)} s) ---> {RateSpeed(writeMbps)}\nLecture : {readMbps.ToString("F2", fr)} Mo/s ({readSeconds.ToString("F3", fr)} s) ---> {RateSpeed(readMbps)}";
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Log($"Test débit : exception ({folder}) : {ex.Message}");
                }
                finally
                {
                    if (handle != null)
                    {
                        try { store.CloseFile(handle); } catch { }
                    }

                    // Nettoyage si l'écriture a réussi mais pas la relecture.
                    try
                    {
                        var cleanupStatus = store.CreateFile(
                            out var h2, out _, path,
                            AccessMask.DELETE | AccessMask.SYNCHRONIZE,
                            SMBLibrary.FileAttributes.Normal, ShareAccess.None,
                            CreateDisposition.FILE_OPEN,
                            CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_DELETE_ON_CLOSE,
                            null);
                        if (cleanupStatus == NTStatus.STATUS_SUCCESS && h2 != null)
                            store.CloseFile(h2);
                    }
                    catch { }
                }
            }

            return $"Test de débit impossible : {lastError}";
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

                    var message = $"Connexion SMB OK\nServeur : {settings.ServerAddress}\nPartage : {settings.Share}";
                    message += "\n\n" + RunSpeedTest(store, settings.Path);
                    return (true, message);
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