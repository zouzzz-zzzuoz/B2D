namespace ExcelClosedXML.Services
{
    public interface ITagFileOpener
    {
        /// <summary>Ouvre le fichier .tag à l'aide de l'application Apk4Tag installée.</summary>
        /// <param name="filePath">Chemin absolu du fichier .tag.</param>
        /// <param name="sourceDocumentUri">URI Android (content://) du document .xlsx source, utilisé pour accéder au .tag sans permission supplémentaire.</param>
        Task OpenTagFileAsync(string filePath, string? sourceDocumentUri = null);

        /// <summary>
        /// Fallback manuel : demande à l'utilisateur de choisir le fichier puis tente de l'ouvrir.
        /// </summary>
        Task<bool> TryPickAndOpenTagFileManuallyAsync(string cellText, string[] extensions);
    }
}
