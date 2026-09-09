namespace ExcelClosedXML.Services
{
    public interface IWorkbookStorageService
    {
        Task<Stream?> OpenReadAsync(ExcelFileInfo fileInfo, CancellationToken cancellationToken = default);
        Task<string> SaveAsync(ExcelFileInfo fileInfo, Action<Stream> writeWorkbook, CancellationToken cancellationToken = default);
    }
}
