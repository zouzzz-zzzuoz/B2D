using ExcelClosedXML.Droid.Services;
using ExcelClosedXML.Services;

namespace ExcelClosedXML.Droid
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();

            builder
                .UseSharedMauiApp();

            // Services spécifiques Android
            builder.Services.AddSingleton<IFilePickerService, AndroidFilePickerService>();
            builder.Services.AddSingleton<ITagFileOpener,     AndroidTagFileOpener>();
            builder.Services.AddSingleton<IWorkbookStorageService, AndroidWorkbookStorageService>();

            return builder.Build();
        }
    }
}
