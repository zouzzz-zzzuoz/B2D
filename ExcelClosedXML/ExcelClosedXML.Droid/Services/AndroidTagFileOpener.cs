using Android.Content;
using Android.Provider;
using ExcelClosedXML.Services;
using Microsoft.Maui.ApplicationModel;
using System.Linq;
using AndroidApp = Android.App.Application;
using SMBLibrary;
using SMBLibrary.Client;
using SMBLibrary.SMB2;
using System.Net;
using XFileProvider = AndroidX.Core.Content.FileProvider;

namespace ExcelClosedXML.Droid.Services
{
    /// <summary>
    /// Ouvre un fichier .tag avec Apk4Tag.
    ///
    /// Acces au .tag sur Android 10+ (Scoped Storage) :
    ///   1. TreeUri : utilise l URI d arborescence accorde via OPEN_DOCUMENT_TREE
    ///                → DocumentsContract.BuildDocumentUriUsingTree  (methode recommandee)
    ///   2. DocumentsContract sans tree (fallback)
    ///   3. File.Copy direct (Android &lt;= 9 uniquement)
    /// </summary>
    public class AndroidTagFileOpener : ITagFileOpener
    {
        private const string Authority = "com.companyname.excelclosedxml.fileprovider";

        public async Task OpenTagFileAsync(string filePath, string? sourceDocumentUri = null)
        {
            try
            {
                var context   = AndroidApp.Context;
                var cacheDir  = context.CacheDir!.AbsolutePath;
                var fileName  = Path.GetFileName(filePath);
                var cachePath = Path.Combine(cacheDir, fileName);
                var extension = Path.GetExtension(fileName);
                var isTagFile = string.Equals(extension, ".tag", StringComparison.OrdinalIgnoreCase);

                bool copied = false;

                var smbSettings = await SmbSettingsStore.LoadAsync();
                copied = await TryCopySmbFileAsync(smbSettings, fileName, cachePath);

                if (!copied && sourceDocumentUri != null)
                    copied = await TryCopyViaTreeUri(context, sourceDocumentUri, fileName, cachePath);

                if (!copied && sourceDocumentUri != null)
                    copied = await TryCopyViaDocumentUri(context, sourceDocumentUri, filePath, cachePath);

                if (!copied && sourceDocumentUri != null)
                    copied = await TryCopyViaSiblingProviderUri(context, sourceDocumentUri, fileName, cachePath);

                if (!copied && File.Exists(filePath))
                {
                    await Task.Run(() => File.Copy(filePath, cachePath, overwrite: true));
                    copied = true;
                }

                if (!copied)
                    throw new FileNotFoundException($"Fichier introuvable : {fileName}", filePath);

                var javaFile = new Java.IO.File(cachePath);
                var uri      = XFileProvider.GetUriForFile(context, Authority, javaFile);
                var mimeType = GetMimeType(extension);

                var intent = new Intent(Intent.ActionView);
                intent.SetDataAndType(uri, mimeType);
                intent.AddFlags(ActivityFlags.GrantReadUriPermission
                              | ActivityFlags.GrantWriteUriPermission
                              | ActivityFlags.NewTask);

                var preferredPackage = isTagFile ? FindApk4TagPackage(context.PackageManager!) : null;
                if (!string.IsNullOrWhiteSpace(preferredPackage))
                    intent.SetPackage(preferredPackage);

                try
                {
                    context.StartActivity(intent);
                    return;
                }
                catch (ActivityNotFoundException)
                {
                    var fallbackIntent = new Intent(Intent.ActionView);
                    fallbackIntent.SetDataAndType(uri, "*/*");
                    fallbackIntent.AddFlags(ActivityFlags.GrantReadUriPermission
                                          | ActivityFlags.GrantWriteUriPermission
                                          | ActivityFlags.NewTask);

                    if (!string.IsNullOrWhiteSpace(preferredPackage))
                        fallbackIntent.SetPackage(preferredPackage);

                    try
                    {
                        context.StartActivity(fallbackIntent);
                        return;
                    }
                    catch (ActivityNotFoundException)
                    {
                        if (isTagFile)
                            throw new InvalidOperationException("Apk4Tag n'est pas installé sur cet appareil.");

                        throw new InvalidOperationException($"Aucune application ne peut ouvrir le fichier {fileName}.");
                    }
                }
            }
            catch (FileNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Impossible d'ouvrir le fichier : " + ex.Message, ex);
            }
        }

        private static string GetMimeType(string? extension)
        {
            return extension?.ToLowerInvariant() switch
            {
                ".tag" => "application/x-tag",
                ".dxf" => "image/vnd.dxf",
                ".dwg" => "application/acad",
                ".pdf" => "application/pdf",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                _ => "*/*"
            };
        }

        private static string? FindApk4TagPackage(Android.Content.PM.PackageManager pm)
        {
            try
            {
                var packages = pm.GetInstalledPackages(Android.Content.PM.PackageInfoFlags.MatchAll);
                foreach (var packageInfo in packages)
                {
                    var packageName = packageInfo.PackageName ?? string.Empty;
                    if (packageName.Contains("apk4tag", StringComparison.OrdinalIgnoreCase))
                        return packageName;
                }

                foreach (var packageInfo in packages)
                {
                    var appName = packageInfo.ApplicationInfo?.LoadLabel(pm)?.ToString() ?? string.Empty;
                    if (appName.Contains("apk4tag", StringComparison.OrdinalIgnoreCase)
                        || appName.Contains("apk 4 tag", StringComparison.OrdinalIgnoreCase))
                        return packageInfo.PackageName;
                }
            }
            catch
            {
                // ignore, on tombera sur le fallback d'ouverture directe
            }

            return null;
        }

        // ── Strategie 1 : TreeUri (OPEN_DOCUMENT_TREE) ───────────────────────────
        // treeUriString doit etre un URI d arborescence :
        //   content://com.android.externalstorage.documents/tree/primary%3ABon%20de%20debit
        private static async Task<bool> TryCopyViaTreeUri(
            Context context, string treeUriString, string tagFileName, string cachePath)
        {
            try
            {
                var treeUri = Android.Net.Uri.Parse(treeUriString);
                if (treeUri == null) return false;

                // Verifier que c est bien un URI d arborescence
                string? treeDocId = null;
                try { treeDocId = DocumentsContract.GetTreeDocumentId(treeUri); } catch { return false; }
                if (treeDocId == null) return false;

                // Construire le docId du .tag : <treeDocId>/fichiers/<nom>.tag
                string tagDocId = treeDocId.TrimEnd('/') + "/fichiers/" + tagFileName;

                var tagUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, tagDocId);
                if (tagUri == null) return false;

                using var inputStream = context.ContentResolver?.OpenInputStream(tagUri);
                if (inputStream == null) return false;

                await using var output = File.Create(cachePath);
                await inputStream.CopyToAsync(output);
                return true;
            }
            catch { return false; }
        }

        // ── Strategie 2 : DocumentsContract depuis docUri ────────────────────────
        private static async Task<bool> TryCopyViaDocumentUri(
            Context context, string sourceDocUriString, string tagFilePath, string cachePath)
        {
            try
            {
                var docUri = Android.Net.Uri.Parse(sourceDocUriString);
                if (docUri == null) return false;

                string? docId = null;
                try { docId = DocumentsContract.GetDocumentId(docUri); } catch { }
                if (string.IsNullOrEmpty(docId)) return false;

                int colon = docId.IndexOf(':');
                if (colon < 0) return false;
                int lastSlash = docId.LastIndexOf('/');

                string root       = docId.Substring(0, colon + 1);
                string parentPath = lastSlash > colon
                    ? docId.Substring(colon + 1, lastSlash - colon - 1)
                    : string.Empty;
                string tagName  = Path.GetFileName(tagFilePath);
                string tagDocId = string.IsNullOrEmpty(parentPath)
                    ? root + "fichiers/" + tagName
                    : root + parentPath + "/fichiers/" + tagName;

                string authority = docUri.Authority ?? "com.android.externalstorage.documents";
                var tagUri = DocumentsContract.BuildDocumentUri(authority, tagDocId);
                if (tagUri == null) return false;

                using var inputStream = context.ContentResolver?.OpenInputStream(tagUri);
                if (inputStream == null) return false;

                await using var output = File.Create(cachePath);
                await inputStream.CopyToAsync(output);
                return true;
            }
            catch { return false; }
        }

        // ── Strategie 3 : URI sœur via provider tiers (ex: SMB file manager cache) ───
        private static async Task<bool> TryCopyViaSiblingProviderUri(
            Context context, string sourceDocUriString, string tagFileName, string cachePath)
        {
            try
            {
                var sourceUri = Android.Net.Uri.Parse(sourceDocUriString);
                if (sourceUri == null) return false;

                // Certains providers SMB exposent des URI de type fileprovider/root.
                // Utiliser le chemin décodé puis Uri.Builder évite les URI invalides
                // provoquées par la concaténation d'un chemin déjà encodé.
                string? sourcePath = sourceUri.Path;
                int lastSlash = sourcePath?.LastIndexOf('/') ?? -1;
                if (lastSlash <= 0) return false;

                var prefix = sourcePath!.Substring(0, lastSlash);
                var candidates = new List<Android.Net.Uri>
                {
                    BuildProviderUri(sourceDocUriString, prefix + "/fichiers/" + tagFileName)
                };

                if (prefix.Contains("/root/", StringComparison.OrdinalIgnoreCase))
                {
                    var documentPrefix = prefix.Replace("/root/", "/document/", StringComparison.OrdinalIgnoreCase);
                    candidates.Add(BuildProviderUri(sourceDocUriString, documentPrefix + "/fichiers/" + tagFileName));
                }

                foreach (var candidate in candidates)
                {
                    try
                    {
                        using var inputStream = context.ContentResolver?.OpenInputStream(candidate);
                        if (inputStream == null)
                            continue;

                        await using var output = File.Create(cachePath);
                        await inputStream.CopyToAsync(output);
                        return true;
                    }
                    catch
                    {
                        // essaie la prochaine variante
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static Android.Net.Uri BuildProviderUri(string sourceUriString, string decodedPath)
        {
            var encodedPath = Android.Net.Uri.Encode(decodedPath, "/");
            int schemeSeparator = sourceUriString.IndexOf("://", StringComparison.Ordinal);
            if (schemeSeparator <= 0)
                throw new ArgumentException("URI source invalide.", nameof(sourceUriString));

            int authorityStart = schemeSeparator + 3;
            int authorityEnd = sourceUriString.IndexOf('/', authorityStart);
            if (authorityEnd <= authorityStart)
                throw new ArgumentException("URI source sans autorité.", nameof(sourceUriString));

            var prefix = sourceUriString.Substring(0, authorityStart);
            var authority = sourceUriString.Substring(authorityStart, authorityEnd - authorityStart);
            return Android.Net.Uri.Parse($"{prefix}{authority}{encodedPath}")!;
        }

        private static Task<bool> TryCopySmbFileAsync(
            SmbSettings settings, string fileName, string cachePath)
        {
            return Task.Run(() =>
            {
                ISMBClient? client = null;
                ISMBFileStore? store = null;
                object? fileHandle = null;

                try
                {
                    var userName = string.IsNullOrWhiteSpace(settings.UserName)
                        ? string.Empty
                        : settings.UserName;
                    var (loginStatus, connectedClient) = SmbSettingsStore.TryConnectAndLoginWithFallbacks(
                        IPAddress.Parse(settings.ServerAddress),
                        userName,
                        settings.Password ?? string.Empty);
                    client = connectedClient;
                    if (loginStatus != NTStatus.STATUS_SUCCESS || client == null)
                    {
                        // Repli pour les serveurs qui acceptent une session anonyme vide.
                        client = new SMB2Client();
                        if (!client.Connect(IPAddress.Parse(settings.ServerAddress), SMBTransportType.DirectTCPTransport))
                            return false;

                        loginStatus = client.Login(string.Empty, string.Empty, string.Empty);
                        if (loginStatus != NTStatus.STATUS_SUCCESS)
                            return false;
                    }

                    store = client.TreeConnect(settings.Share, out var treeStatus);
                    if (treeStatus != NTStatus.STATUS_SUCCESS || store == null)
                        return false;

                    var remoteDirectory = settings.Path ?? string.Empty;
                    if (!string.IsNullOrEmpty(remoteDirectory))
                        remoteDirectory = remoteDirectory.Replace('/', '\\').Trim('\\');

                    var remotePath = BuildSmbPath(remoteDirectory, fileName);

                    var status = store.CreateFile(
                        out fileHandle,
                        out _,
                        remotePath,
                        AccessMask.GENERIC_READ,
                        SMBLibrary.FileAttributes.Normal,
                        ShareAccess.Read | ShareAccess.Write,
                        CreateDisposition.FILE_OPEN,
                        CreateOptions.FILE_NON_DIRECTORY_FILE,
                        null);

                    if (status != NTStatus.STATUS_SUCCESS || fileHandle == null)
                    {
                        if (fileHandle != null)
                        {
                            try { store.CloseFile(fileHandle); } catch { }
                            fileHandle = null;
                        }

                        var suffixedFileName = FindSuffixedSmbFileName(
                            store,
                            remoteDirectory,
                            fileName);
                        if (string.IsNullOrWhiteSpace(suffixedFileName))
                            return false;

                        remotePath = BuildSmbPath(remoteDirectory, suffixedFileName);
                        status = store.CreateFile(
                            out fileHandle,
                            out _,
                            remotePath,
                            AccessMask.GENERIC_READ,
                            SMBLibrary.FileAttributes.Normal,
                            ShareAccess.Read | ShareAccess.Write,
                            CreateDisposition.FILE_OPEN,
                            CreateOptions.FILE_NON_DIRECTORY_FILE,
                            null);

                        if (status != NTStatus.STATUS_SUCCESS || fileHandle == null)
                            return false;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    using var output = File.Create(cachePath);
                    long offset = 0;
                    const int blockSize = 64 * 1024;

                    while (true)
                    {
                        status = store.ReadFile(out var data, fileHandle, offset, blockSize);
                        if (status != NTStatus.STATUS_SUCCESS || data == null || data.Length == 0)
                            break;

                        output.Write(data, 0, data.Length);
                        offset += data.Length;
                        if (data.Length < blockSize)
                            break;
                    }

                    return output.Length > 0;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    if (store != null && fileHandle != null)
                    {
                        try { store.CloseFile(fileHandle); } catch { }
                    }

                    try { client?.Logoff(); } catch { }
                    try { client?.Disconnect(); } catch { }
                }
            });
        }

        private static string BuildSmbPath(string remoteDirectory, string fileName)
        {
            return string.IsNullOrEmpty(remoteDirectory)
                ? fileName
                : remoteDirectory.TrimEnd('\\') + "\\" + fileName;
        }

        private static string? FindSuffixedSmbFileName(
            ISMBFileStore store, string remoteDirectory, string requestedFileName)
        {
            var baseName = Path.GetFileNameWithoutExtension(requestedFileName);
            var extension = Path.GetExtension(requestedFileName);
            if (string.IsNullOrWhiteSpace(baseName) || string.IsNullOrWhiteSpace(extension))
                return null;

            var prefix = baseName + "-";
            object? directoryHandle = null;

            try
            {
                var status = store.CreateFile(
                    out directoryHandle,
                    out _,
                    remoteDirectory,
                    AccessMask.GENERIC_READ,
                    SMBLibrary.FileAttributes.Directory,
                    ShareAccess.Read | ShareAccess.Write,
                    CreateDisposition.FILE_OPEN,
                    CreateOptions.FILE_DIRECTORY_FILE,
                    null);
                if (status != NTStatus.STATUS_SUCCESS || directoryHandle == null)
                    return null;

                status = store.QueryDirectory(
                    out var entries,
                    directoryHandle,
                    prefix + "*",
                    FileInformationClass.FileDirectoryInformation);
                if ((status != NTStatus.STATUS_SUCCESS
                     && status != NTStatus.STATUS_NO_MORE_FILES)
                    || entries == null)
                    return null;

                return entries
                    .Select(entry => ((FileDirectoryInformation)entry).FileName)
                    .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Path.GetExtension(name), extension,
                            StringComparison.OrdinalIgnoreCase))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
            finally
            {
                if (directoryHandle != null)
                {
                    try { store.CloseFile(directoryHandle); } catch { }
                }
            }
        }

        public async Task<bool> TryPickAndOpenTagFileManuallyAsync(string cellText, string[] extensions)
        {
            try
            {
                var activity = Platform.CurrentActivity;
                if (activity == null)
                    return false;

                var tcs = new TaskCompletionSource<Intent?>();
                void Handler(Intent? data) { MainActivity.FilePickResult -= Handler; tcs.TrySetResult(data); }
                MainActivity.FilePickResult += Handler;

                var pickIntent = new Intent(Intent.ActionOpenDocument);
                pickIntent.AddCategory(Intent.CategoryOpenable);
                pickIntent.SetType("*/*");

                var mimeTypes = new List<string>();
                foreach (var extension in extensions)
                {
                    var mime = GetMimeType(extension);
                    if (!mimeTypes.Contains(mime, StringComparer.OrdinalIgnoreCase))
                        mimeTypes.Add(mime);
                }

                pickIntent.PutExtra(Intent.ExtraMimeTypes, mimeTypes.ToArray());
                pickIntent.AddFlags(ActivityFlags.GrantReadUriPermission |
                                    ActivityFlags.GrantWriteUriPermission |
                                    ActivityFlags.GrantPersistableUriPermission);

                try
                {
                    var chooser = Intent.CreateChooser(pickIntent, $"Sélectionner le fichier {cellText}");
                    activity.StartActivityForResult(chooser, AndroidFilePickerService.RequestCode);
                }
                catch
                {
                    MainActivity.FilePickResult -= Handler;
                    return false;
                }

                var resultIntent = await tcs.Task;
                var fileUri = resultIntent?.Data;
                if (fileUri == null)
                    return false;

                TryTakePersistablePermissions(AndroidApp.Context, fileUri);

                var fileName = GetDisplayName(AndroidApp.Context, fileUri) ?? string.Empty;
                var selectedExtension = Path.GetExtension(fileName);
                if (!extensions.Any(ext => string.Equals(ext, selectedExtension, StringComparison.OrdinalIgnoreCase)))
                    return false;

                var selectedBaseName = Path.GetFileNameWithoutExtension(fileName);
                if (!string.Equals(selectedBaseName, cellText, StringComparison.OrdinalIgnoreCase)
                    && !selectedBaseName.StartsWith(cellText + "-", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                await OpenPickedFileUriAsync(AndroidApp.Context, fileUri, selectedExtension);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static async Task OpenPickedFileUriAsync(Context context, Android.Net.Uri fileUri, string extension)
        {
            await Task.Yield();

            var isTagFile = string.Equals(extension, ".tag", StringComparison.OrdinalIgnoreCase);
            var mimeType = GetMimeType(extension);

            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(fileUri, mimeType);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission
                          | ActivityFlags.GrantWriteUriPermission
                          | ActivityFlags.NewTask);

            var preferredPackage = isTagFile ? FindApk4TagPackage(context.PackageManager!) : null;
            if (!string.IsNullOrWhiteSpace(preferredPackage))
                intent.SetPackage(preferredPackage);

            try
            {
                context.StartActivity(intent);
                return;
            }
            catch (ActivityNotFoundException)
            {
                var fallbackIntent = new Intent(Intent.ActionView);
                fallbackIntent.SetDataAndType(fileUri, "*/*");
                fallbackIntent.AddFlags(ActivityFlags.GrantReadUriPermission
                                      | ActivityFlags.GrantWriteUriPermission
                                      | ActivityFlags.NewTask);

                if (!string.IsNullOrWhiteSpace(preferredPackage))
                    fallbackIntent.SetPackage(preferredPackage);

                context.StartActivity(fallbackIntent);
            }
        }

        private static void TryTakePersistablePermissions(Context context, Android.Net.Uri uri)
        {
            try
            {
                context.ContentResolver?.TakePersistableUriPermission(uri,
                    ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
            }
            catch
            {
            }
        }

        private static string? GetDisplayName(Context context, Android.Net.Uri uri)
        {
            try
            {
                string[] projection = { IOpenableColumns.DisplayName };
                using var cursor = context.ContentResolver?.Query(uri, projection, null, null, null);
                if (cursor?.MoveToFirst() == true)
                {
                    int idx = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                    if (idx >= 0) return cursor.GetString(idx);
                }
            }
            catch
            {
            }

            return null;
        }
    }
}
