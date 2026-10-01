using Android.Content;
using ExcelClosedXML.Services;
using AndroidApp = Android.App.Application;

namespace ExcelClosedXML.Droid.Services
{
    public class AndroidWorkbookStorageService : IWorkbookStorageService
    {
        /// <summary>
        /// Retire le segment final "fichiers" d'un chemin de dossier SMB, s'il est présent.
        /// Le dossier configuré dans les paramètres SMB pointe vers le sous-dossier des
        /// pièces jointes ; le classeur .xlsx principal se trouve dans son dossier parent.
        /// </summary>
        private static string StripTrailingFichiersSegment(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                return folderPath;

            var trimmed = folderPath.TrimEnd('\\', '/');
            var lastSeparator = trimmed.LastIndexOfAny(['\\', '/']);
            var lastSegment = lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..] : trimmed;

            if (string.Equals(lastSegment, "fichiers", StringComparison.OrdinalIgnoreCase))
                return lastSeparator >= 0 ? trimmed[..lastSeparator] : string.Empty;

            return folderPath;
        }

        private static void Log(string message) => AppLog.Write($"[WorkbookStorage] {message}");

        public async Task<Stream?> OpenReadAsync(ExcelFileInfo fileInfo, CancellationToken cancellationToken = default)
        {
            if (fileInfo.Stream != null)
            {
                if (fileInfo.Stream.CanSeek)
                    fileInfo.Stream.Position = 0;

                return fileInfo.Stream;
            }

            var context = AndroidApp.Context;
            if (!string.IsNullOrWhiteSpace(fileInfo.SourceDocumentUri))
            {
                var uri = Android.Net.Uri.Parse(fileInfo.SourceDocumentUri);
                var stream = uri != null ? context.ContentResolver?.OpenInputStream(uri) : null;
                if (stream != null)
                {
                    if (stream.CanSeek)
                        return stream;

                    try
                    {
                        var capacity = stream.CanSeek && stream.Length > 0 && stream.Length <= int.MaxValue
                            ? (int)stream.Length
                            : 0;
                        var buffered = capacity > 0 ? new MemoryStream(capacity) : new MemoryStream();
                        await stream.CopyToAsync(buffered, cancellationToken);
                        buffered.Position = 0;
                        return buffered;
                    }
                    finally
                    {
                        await stream.DisposeAsync();
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(fileInfo.Directory))
            {
                var path = Path.Combine(fileInfo.Directory, fileInfo.FileName);
                if (File.Exists(path))
                    return File.OpenRead(path);
            }

            return null;
        }

        public async Task<string> SaveAsync(ExcelFileInfo fileInfo, Action<Stream> writeWorkbook, CancellationToken cancellationToken = default)
        {
            // ── Tentative 1 : écriture SMB directe via SMBLibrary ──────────
            // Contourne complètement le gestionnaire de fichiers tiers (Alpha File
            // Manager), dont le Content Provider écrit bien dans son cache local
            // mais ne remonte pas systématiquement le changement vers le vrai
            // serveur SMB. On écrit ici nous-mêmes, directement sur le partage.
            try
            {
                var smbSettings = await SmbSettingsStore.LoadAsync();
                if (!string.IsNullOrWhiteSpace(smbSettings.Server) && !string.IsNullOrWhiteSpace(fileInfo.FileName))
                {
                    await using var smbBuffer = new MemoryStream();
                    writeWorkbook(smbBuffer);
                    var smbPayload = smbBuffer.ToArray();

                    // Le dossier configuré dans les paramètres SMB (settings.Path) pointe
                    // vers le dossier "fichiers" utilisé pour les pièces jointes (tags,
                    // PDF, images — voir OnTagCellTapped). Le fichier .xlsx lui-même se
                    // trouve UN NIVEAU AU-DESSUS, dans le dossier parent.
                    var xlsxFolderPath = StripTrailingFichiersSegment(smbSettings.Path);
                    Log($"Dossier .xlsx calculé (parent de 'fichiers') : '{xlsxFolderPath}' (configuré : '{smbSettings.Path}')");

                    var (success, message) = await SmbSettingsStore.WriteFileAsync(
                        smbSettings, fileInfo.FileName, smbPayload, xlsxFolderPath);

                    if (success)
                    {
                        Log($"Écriture SMB directe réussie : {message}");
                        return !string.IsNullOrWhiteSpace(fileInfo.Directory)
                            ? Path.Combine(fileInfo.Directory, fileInfo.FileName)
                            : fileInfo.FileName;
                    }

                    Log($"Écriture SMB directe échouée, bascule sur les méthodes de repli : {message}");
                }
                else
                {
                    Log("Paramètres SMB non configurés (Server ou FileName vide) -> bascule sur les méthodes de repli");
                }
            }
            catch (Exception ex)
            {
                Log($"EXCEPTION pendant l'écriture SMB directe (non bloquant, bascule sur les méthodes de repli) : {ex}");
            }

            var context = AndroidApp.Context;

            if (!string.IsNullOrWhiteSpace(fileInfo.SourceDocumentUri))
            {
                Log($"Tentative d'écriture via ContentResolver sur SourceDocumentUri='{fileInfo.SourceDocumentUri}'");

                Android.Net.Uri? uri = null;
                try
                {
                    uri = Android.Net.Uri.Parse(fileInfo.SourceDocumentUri);
                }
                catch (Exception ex)
                {
                    Log($"ÉCHEC Uri.Parse('{fileInfo.SourceDocumentUri}') : {ex}");
                }

                if (uri != null)
                {
                    await using var buffer = new MemoryStream();
                    writeWorkbook(buffer);
                    var payload = buffer.ToArray();
                    Log($"Payload préparé : {payload.Length} octets");

                    byte[]? backup = null;
                    try
                    {
                        await using var backupInput = context.ContentResolver?.OpenInputStream(uri);
                        if (backupInput != null)
                        {
                            using var backupBuffer = new MemoryStream();
                            await backupInput.CopyToAsync(backupBuffer, cancellationToken);
                            backup = backupBuffer.ToArray();
                            Log($"Backup pré-écriture capturé : {backup.Length} octets");
                        }
                        else
                        {
                            Log("OpenInputStream (backup) a retourné null");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"ÉCHEC capture backup (non bloquant) : {ex}");
                    }

                    // On essaie successivement plusieurs modes d'ouverture : certains
                    // Content Providers (dont potentiellement celui d'Alpha File Manager)
                    // ne supportent pas "rwt" (read-write-truncate) et renvoient null ou
                    // lèvent une exception ; "rw" est un mode plus largement supporté.
                    string[] modesToTry = ["rwt", "rw", "w"];

                    foreach (var mode in modesToTry)
                    {
                        try
                        {
                            Log($"Essai OpenOutputStream(uri, \"{mode}\")");
                            await using var output = context.ContentResolver?.OpenOutputStream(uri, mode);

                            if (output == null)
                            {
                                Log($"OpenOutputStream(\"{mode}\") a retourné null (pas d'exception)");
                                continue;
                            }

                            await output.WriteAsync(payload, cancellationToken);
                            await output.FlushAsync(cancellationToken);
                            Log($"Écriture réussie via ContentResolver avec mode \"{mode}\"");

                            try
                            {
                                // Certains Content Providers (ex. gestionnaires de fichiers avec
                                // synchronisation SMB/cloud) ne déclenchent leur logique de
                                // synchronisation qu'après avoir reçu explicitement une notification
                                // de changement, indépendamment du flux d'écriture lui-même.
                                context.ContentResolver?.NotifyChange(uri, null);
                                Log($"ContentResolver.NotifyChange(uri, null) appelé sur '{uri}'");
                            }
                            catch (Exception notifyEx)
                            {
                                Log($"ÉCHEC ContentResolver.NotifyChange (non bloquant) : {notifyEx}");
                            }

                            return fileInfo.Directory != null
                                ? Path.Combine(fileInfo.Directory, fileInfo.FileName)
                                : fileInfo.SourceDocumentUri;
                        }
                        catch (Exception ex)
                        {
                            Log($"ÉCHEC OpenOutputStream/écriture avec mode \"{mode}\" : {ex}");

                            if (backup != null)
                            {
                                try
                                {
                                    await using var restore = context.ContentResolver?.OpenOutputStream(uri, mode);
                                    if (restore != null)
                                    {
                                        await restore.WriteAsync(backup, cancellationToken);
                                        await restore.FlushAsync(cancellationToken);
                                        Log($"Backup restauré avec succès (mode \"{mode}\") après échec d'écriture");
                                    }
                                }
                                catch (Exception restoreEx)
                                {
                                    Log($"ÉCHEC restauration backup (mode \"{mode}\") : {restoreEx}");
                                }
                            }

                            // On continue avec le mode suivant plutôt que d'abandonner tout de suite
                        }
                    }

                    Log("Tous les modes ContentResolver ont échoué -> bascule sur le fallback fichier local (PAS DE SYNCHRO SMB GARANTIE)");
                }
                else
                {
                    Log("Uri null après Parse -> bascule directe sur le fallback fichier local");
                }
            }
            else
            {
                Log("Aucun SourceDocumentUri renseigné -> écriture directe en fallback fichier local");
            }

            try
            {
                var fallbackPath = !string.IsNullOrWhiteSpace(fileInfo.Directory)
                    ? Path.Combine(fileInfo.Directory, fileInfo.FileName)
                    : Path.Combine(FileSystem.AppDataDirectory, fileInfo.FileName);

                Log($"Écriture fallback sur chemin fichier brut : {fallbackPath}");
                return await WriteAtomicallyAsync(fallbackPath, writeWorkbook, cancellationToken);
            }
            catch (Exception ex)
            {
                Log($"ÉCHEC fallback sur Directory : {ex} -> bascule sur AppDataDirectory");
                var safePath = Path.Combine(FileSystem.AppDataDirectory, fileInfo.FileName);
                return await WriteAtomicallyAsync(safePath, writeWorkbook, cancellationToken);
            }
        }

        /// <summary>
        /// Écrit le classeur dans un fichier temporaire (même dossier que la cible), puis le
        /// remplace via File.Move une fois l'écriture entièrement flushée sur disque.
        /// Ainsi, une interruption brutale (process tué, coupure réseau sur un partage SMB monté,
        /// batterie) laisse soit l'ancien fichier intact, soit le nouveau complet — jamais un
        /// fichier à moitié écrit à la place de l'original.
        /// </summary>
        private static async Task<string> WriteAtomicallyAsync(
            string targetPath,
            Action<Stream> writeWorkbook,
            CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(targetPath)!;
            Directory.CreateDirectory(directory);

            var tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                await using (var fileStream = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    writeWorkbook(fileStream);
                    await fileStream.FlushAsync(cancellationToken);
                    fileStream.Flush(flushToDisk: true); // force l'écriture physique, pas juste le buffer OS
                }

                // File.Move avec overwrite : remplacement atomique sur le même volume.
                File.Move(tempPath, targetPath, overwrite: true);
                Log($"Écriture fallback atomique réussie -> {targetPath}");
                return targetPath;
            }
            catch (Exception ex)
            {
                Log($"EXCEPTION dans WriteAtomicallyAsync('{targetPath}') : {ex}");

                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // best effort de nettoyage, ne masque pas l'exception d'origine
                }

                throw;
            }
        }
    }
}
