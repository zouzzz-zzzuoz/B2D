using Android.Content;
using Android.Provider;
using ExcelClosedXML.Services;
using AndroidApp = Android.App.Application;

namespace ExcelClosedXML.Droid.Services
{
    public class AndroidFilePickerService : IFilePickerService
    {
        public const int RequestCode     = 9877;
        public const int TreeRequestCode = 9878;

        public async Task<ExcelFileInfo?> PickExcelFileAsync()
        {
            var tcs = new TaskCompletionSource<Intent?>();
            void Handler(Intent? data) { MainActivity.FilePickResult -= Handler; tcs.TrySetResult(data); }
            MainActivity.FilePickResult += Handler;

            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("*/*");
            intent.PutExtra(Intent.ExtraMimeTypes, new string[]
            {
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "application/vnd.ms-excel",
                "application/octet-stream"
            });
            Platform.CurrentActivity?.StartActivityForResult(intent, RequestCode);

            var resultIntent = await tcs.Task;
            if (resultIntent?.Data == null) return null;

            var uri     = resultIntent.Data;
            var context = AndroidApp.Context;

            string fileName = GetDisplayName(context, uri) ?? "fichier.xlsx";
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext == ".xls") { await AlertUnsupported(); return null; }

            string? directory = TryResolveDirectory(context, uri);
            TryTakePersistablePermissions(context, uri);

            return new ExcelFileInfo
            {
                FileName          = fileName,
                Stream            = null,
                Directory         = directory,
                SourceDocumentUri = uri.ToString(),
                TreeUri           = TryBuildTreeUri(uri)?.ToString()
            };
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

        private static Android.Net.Uri? TryBuildTreeUri(Android.Net.Uri uri)
        {
            try
            {
                if (uri.Authority != "com.android.externalstorage.documents")
                    return null;

                var docId = DocumentsContract.GetDocumentId(uri);
                if (string.IsNullOrWhiteSpace(docId))
                    return null;

                int slash = docId.LastIndexOf('/');
                string treeDocId = slash > 0 ? docId.Substring(0, slash) : docId;
                return DocumentsContract.BuildTreeDocumentUri(uri.Authority, treeDocId);
            }
            catch
            {
                return null;
            }
        }

        private static string? TryResolveDirectory(Context context, Android.Net.Uri uri)
        {
            if (uri.Scheme == "file") return Path.GetDirectoryName(uri.Path);
            if (uri.Authority == "com.android.externalstorage.documents")
            {
                var docId = DocumentsContract.GetDocumentId(uri);
                if (docId != null)
                {
                    var parts = docId.Split(':');
                    if (parts.Length >= 2)
                    {
                        string rootPath = parts[0].Equals("primary", StringComparison.OrdinalIgnoreCase)
                            ? Android.OS.Environment.ExternalStorageDirectory!.AbsolutePath
                            : "/storage/" + parts[0];
                        return Path.GetDirectoryName(rootPath + "/" + parts[1]);
                    }
                }
            }
            if (uri.Authority == "com.android.providers.downloads.documents")
            {
                var docId = DocumentsContract.GetDocumentId(uri);
                if (docId?.StartsWith("raw:") == true) return Path.GetDirectoryName(docId.Substring(4));
                if (long.TryParse(docId, out long numId))
                {
                    var baseUri = Android.Net.Uri.Parse("content://downloads/public_downloads");
                    if (baseUri != null)
                    {
                        var dlUri = ContentUris.WithAppendedId(baseUri, numId);
                        var path = QueryDataColumn(context, dlUri);
                        if (path != null) return Path.GetDirectoryName(path);
                    }
                }
            }
            var dataPath = QueryDataColumn(context, uri);
            return dataPath != null ? Path.GetDirectoryName(dataPath) : null;
        }

        private static string? QueryDataColumn(Context context, Android.Net.Uri uri)
        {
            try
            {
                string[] projection = { MediaStore.IMediaColumns.Data };
                using var cursor = context.ContentResolver?.Query(uri, projection, null, null, null);
                if (cursor?.MoveToFirst() == true)
                {
                    int idx = cursor.GetColumnIndex(MediaStore.IMediaColumns.Data);
                    if (idx >= 0) return cursor.GetString(idx);
                }
            }
            catch { }
            return null;
        }

        private static string? GetDisplayName(Context context, Android.Net.Uri uri)
        {
            try
            {
                string[] projection = { Android.Provider.IOpenableColumns.DisplayName };
                using var cursor = context.ContentResolver?.Query(uri, projection, null, null, null);
                if (cursor?.MoveToFirst() == true)
                {
                    int idx = cursor.GetColumnIndex(Android.Provider.IOpenableColumns.DisplayName);
                    if (idx >= 0) return cursor.GetString(idx);
                }
            }
            catch { }
            return null;
        }

        private static Task AlertUnsupported()
            => MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
                if (page != null)
                    await page.DisplayAlertAsync("Format non supporte",
                        "Les fichiers .xls ne sont pas supportes par ClosedXML. Convertissez en .xlsx.", "OK");
            });
    }
}