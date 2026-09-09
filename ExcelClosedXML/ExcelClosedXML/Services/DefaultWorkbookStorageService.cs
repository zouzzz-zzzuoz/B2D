namespace ExcelClosedXML.Services
{
    public class DefaultWorkbookStorageService : IWorkbookStorageService
    {
        public Task<Stream?> OpenReadAsync(ExcelFileInfo fileInfo, CancellationToken cancellationToken = default)
        {
            if (fileInfo.Stream != null)
            {
                if (fileInfo.Stream.CanSeek)
                    fileInfo.Stream.Position = 0;

                return Task.FromResult<Stream?>(fileInfo.Stream);
            }

            if (!string.IsNullOrWhiteSpace(fileInfo.Directory))
            {
                var path = Path.Combine(fileInfo.Directory, fileInfo.FileName);
                if (File.Exists(path))
                    return Task.FromResult<Stream?>(File.OpenRead(path));
            }

            return Task.FromResult<Stream?>(null);
        }

        public Task<string> SaveAsync(ExcelFileInfo fileInfo, Action<Stream> writeWorkbook, CancellationToken cancellationToken = default)
        {
            var primaryPath = ResolvePrimaryPath(fileInfo);
            Directory.CreateDirectory(Path.GetDirectoryName(primaryPath)!);

            using var stream = File.Create(primaryPath);
            writeWorkbook(stream);
            stream.Flush();
            return Task.FromResult(primaryPath);
        }

        private static string ResolvePrimaryPath(ExcelFileInfo fileInfo)
        {
            if (!string.IsNullOrWhiteSpace(fileInfo.Directory))
                return Path.Combine(fileInfo.Directory, fileInfo.FileName);

            return Path.Combine(FileSystem.AppDataDirectory, fileInfo.FileName);
        }
    }
}
