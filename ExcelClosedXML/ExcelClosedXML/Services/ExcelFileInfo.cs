namespace ExcelClosedXML.Services
{
    public class ExcelFileInfo
    {
        public required string FileName { get; set; }
        public Stream? Stream { get; set; }
        /// <summary>Répertoire du fichier original sur le périphérique (null si indéterminable)</summary>
        public string? Directory { get; set; }
        /// <summary>URI Android (content://) du document source, sérialisé en string</summary>
        public string? SourceDocumentUri { get; set; }
        /// <summary>URI d'arborescence (ACTION_OPEN_DOCUMENT_TREE) du dossier parent</summary>
        public string? TreeUri { get; set; }
    }
}
