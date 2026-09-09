namespace ExcelClosedXML.Services
{
    /// <summary>Stockage temporaire pour le passage de données entre pages (contient un Stream).</summary>
    public static class NavigationData
    {
        public static ExcelFileInfo? PendingFile { get; set; }
    }
}
