using Android.Content;
using Android.Provider;
using ExcelClosedXML.Services;
using AndroidApp = Android.App.Application;
using XFileProvider = AndroidX.Core.Content.FileProvider;

namespace ExcelClosedXML.Droid.Services
{
    /// <summary>
    /// Ouvre un fichier .tag avec Apk4Tag.
    ///
    /// Acces au .tag sur Android 10+ (Scoped Storage) :
    ///   1. TreeUri : utilise l URI d arborescence accorde via OPEN_DOCUMENT_TREE
    ///                => DocumentsContract.BuildDocumentUriUsingTree  (methode recommandee)
    ///   2. DocumentsContract sans tree (fallback)
    ///   3. File.Copy direct (Android <= 9 uniquement)
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

                if (sourceDocumentUri != null)
                    copied = await TryCopyViaTreeUri(context, sourceDocumentUri, fileName, cachePath);

                if (!copied && sourceDocumentUri != null)
                    copied = await TryCopyViaDocumentUri(context, sourceDocumentUri, filePath, cachePath);

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
    }
}