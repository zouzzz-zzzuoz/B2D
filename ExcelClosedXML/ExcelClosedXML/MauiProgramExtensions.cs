using ExcelClosedXML.Services;
using Microsoft.Extensions.Logging;

namespace ExcelClosedXML
{
    public static class MauiProgramExtensions
    {
        public static MauiAppBuilder UseSharedMauiApp(this MauiAppBuilder builder)
        {
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            // Services
            builder.Services.AddSingleton<IWorkbookStorageService, DefaultWorkbookStorageService>();

            // Pages
            builder.Services.AddTransient<MainPage>();
            builder.Services.AddTransient<SpreadsheetPage>();

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder;
        }
    }
}
