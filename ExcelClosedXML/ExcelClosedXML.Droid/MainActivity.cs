using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Provider;
using Android.Views;
using ExcelClosedXML;
using ExcelClosedXML.Droid.Services;
using System.Runtime.Versioning;

namespace ExcelClosedXML.Droid
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
              ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation |
                                     ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
                                     ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        private const string LogTag = "SmbSave";
        private const int StoragePermissionRequestCode = 2001;

        /// <summary>Déclenché lorsque l'utilisateur sélectionne un fichier via le sélecteur système.</summary>
        public static event Action<Intent?>? FilePickResult;

        /// <summary>Déclenché lorsque l'utilisateur accorde l'accès à un dossier via OPEN_DOCUMENT_TREE.</summary>
        public static event Action<Intent?>? TreePickResult;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Redirige tous les AppLog.Write(...) du projet partagé (y compris
            // ceux de SpreadsheetPage) vers Android.Util.Log, visible dans adb logcat.
            AppLog.Sink = msg => Android.Util.Log.Debug(LogTag, msg);

            Window?.SetFlags(WindowManagerFlags.Fullscreen, WindowManagerFlags.Fullscreen);
            EnsureBroadStorageAccess();
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);

            if (requestCode == AndroidFilePickerService.RequestCode)
                FilePickResult?.Invoke(resultCode == Result.Ok ? data : null);
            else if (requestCode == AndroidFilePickerService.TreeRequestCode)
                TreePickResult?.Invoke(resultCode == Result.Ok ? data : null);
        }

        // ---------------------------------------------------------------
        // Bouton Retour (triangle / flèche)
        // ---------------------------------------------------------------
        // ⚠️ Ne fonctionne que si "enableOnBackInvokedCallback" n'est PAS
        // à true dans AndroidManifest.xml (ou absent) sur Android 13+.
#pragma warning disable CS0672 // Membre remplace un membre obsolète
        public override void OnBackPressed()
        {
            Android.Util.Log.Debug(LogTag, "OnBackPressed déclenché");
            TriggerWorkbookSaveFromLifecycle("OnBackPressed");
            base.OnBackPressed();
        }
#pragma warning restore CS0672

        // ---------------------------------------------------------------
        // Bouton Accueil (cercle) / Multitâche (carré)
        // ---------------------------------------------------------------
        protected override void OnPause()
        {
            Android.Util.Log.Debug(LogTag, "OnPause déclenché");
            TriggerWorkbookSaveFromLifecycle("OnPause");
            base.OnPause();
        }

        protected override void OnStop()
        {
            Android.Util.Log.Debug(LogTag, "OnStop déclenché");
            TriggerWorkbookSaveFromLifecycle("OnStop");
            base.OnStop();
        }

        protected override void OnDestroy()
        {
            Android.Util.Log.Debug(LogTag, "OnDestroy déclenché");
            TriggerWorkbookSaveFromLifecycle("OnDestroy");
            base.OnDestroy();
        }

        private static void TriggerWorkbookSaveFromLifecycle(string source)
        {
            _ = RunSaveWithLoggingAsync(source);
        }

        private static async Task RunSaveWithLoggingAsync(string source)
        {
            try
            {
                Android.Util.Log.Debug(LogTag, $"[{source}] Début SaveCurrentWorkbookIfNeededAsync");
                await SpreadsheetPage.SaveCurrentWorkbookIfNeededAsync();
                Android.Util.Log.Debug(LogTag, $"[{source}] Fin SaveCurrentWorkbookIfNeededAsync (OK)");
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error(LogTag, $"[{source}] ERREUR pendant la sauvegarde : {ex}");
            }
        }

        private void EnsureBroadStorageAccess()
        {
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                {
                    EnsureManageAllFilesAccessForAndroid30Plus();
                    return;
                }

                if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
                    EnsureLegacyStoragePermissionsForAndroid23Plus();
            }
            catch
            {
            }
        }

        [SupportedOSPlatform("android30.0")]
        private void EnsureManageAllFilesAccessForAndroid30Plus()
        {
            if (Android.OS.Environment.IsExternalStorageManager)
                return;

            var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission);
            intent.SetData(Android.Net.Uri.Parse($"package:{PackageName}"));
            intent.AddFlags(ActivityFlags.NewTask);
            StartActivity(intent);
        }

        [SupportedOSPlatform("android23.0")]
        private void EnsureLegacyStoragePermissionsForAndroid23Plus()
        {
            string[] permissions =
            [
                Manifest.Permission.ReadExternalStorage,
                Manifest.Permission.WriteExternalStorage
            ];

            var missingPermissions = permissions
                .Where(permission => CheckSelfPermission(permission) != Permission.Granted)
                .ToArray();

            if (missingPermissions.Length > 0)
                RequestPermissions(missingPermissions, StoragePermissionRequestCode);
        }
    }
}
