using ExcelClosedXML.Droid.Services;
using ExcelClosedXML.Services;
using Microsoft.Maui.LifecycleEvents;

namespace ExcelClosedXML.Droid
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();

            builder
                .UseSharedMauiApp()
                .ConfigureLifecycleEvents(events =>
                {
                    events.AddAndroid(android => android
                        .OnPause(activity =>
                        {
                            // Déclenché par : bouton Retour (quitte l'app), Accueil, Multitâche.
                            // Best effort, NON bloquant : on ne retarde jamais la mise en pause
                            // de l'activité. En contrepartie, si Android tue le process avant la
                            // fin de l'écriture (ex. réseau SMB lent + coupure brutale mémoire),
                            // la sauvegarde peut rester incomplète.
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await SpreadsheetPage.SaveCurrentWorkbookIfNeededAsync();
                                }
                                catch
                                {
                                    // Ne jamais laisser une exception de sauvegarde remonter ici.
                                }
                            });
                        })
                    );
                });

            // Services spécifiques Android
            builder.Services.AddSingleton<IFilePickerService, AndroidFilePickerService>();
            builder.Services.AddSingleton<ITagFileOpener, AndroidTagFileOpener>();
            builder.Services.AddSingleton<IWorkbookStorageService, AndroidWorkbookStorageService>();

            return builder.Build();
        }
    }
}
