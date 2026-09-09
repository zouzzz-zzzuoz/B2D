namespace ExcelClosedXML.Services
{
    public interface IFilePickerService
    {
        /// <summary>
        /// Ouvre le sélecteur de fichiers et retourne les informations du fichier sélectionné.
        /// Retourne null si l'utilisateur annule.
        /// </summary>
        Task<ExcelFileInfo?> PickExcelFileAsync();
    }
}
