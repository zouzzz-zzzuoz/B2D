using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Provider;
using Android.Views;
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
        private const int StoragePermissionRequestCode = 2001;

        /// <summary>Déclenché lorsque l'utilisateur sélectionne un fichier via le sélecteur système.</summary>
        public static event Action<Intent?>? FilePickResult;

        /// <summary>Déclenché lorsque l'utilisateur accorde l'accès à un dossier via OPEN_DOCUMENT_TREE.</summary>
        public static event Action<Intent?>? TreePickResult;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
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
