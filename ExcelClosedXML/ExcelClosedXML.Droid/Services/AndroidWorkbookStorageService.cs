using Android.Content;
using ExcelClosedXML.Services;
using AndroidApp = Android.App.Application;

namespace ExcelClosedXML.Droid.Services
{
    public class AndroidWorkbookStorageService : IWorkbookStorageService
    {
        public Task<Stream?> OpenReadAsync(ExcelFileInfo fileInfo, CancellationToken cancellationToken = default)
        {
            if (fileInfo.Stream != null)
            {
                if (fileInfo.Stream.CanSeek)
                    fileInfo.Stream.Position = 0;

                return Task.FromResult<Stream?>(fileInfo.Stream);
            }

            var context = AndroidApp.Context;
            if (!string.IsNullOrWhiteSpace(fileInfo.SourceDocumentUri))
            {
                var uri = Android.Net.Uri.Parse(fileInfo.SourceDocumentUri);
                var stream = uri != null ? context.ContentResolver?.OpenInputStream(uri) : null;
                if (stream != null)
                {
                    if (stream.CanSeek)
                        return Task.FromResult<Stream?>(stream);

                    var buffered = new MemoryStream();
                    stream.CopyTo(buffered);
                    buffered.Position = 0;
                    stream.Dispose();
                    return Task.FromResult<Stream?>(buffered);
                }
            }

            if (!string.IsNullOrWhiteSpace(fileInfo.Directory))
            {
                var path = Path.Combine(fileInfo.Directory, fileInfo.FileName);
                if (File.Exists(path))
                    return Task.FromResult<Stream?>(File.OpenRead(path));
            }

            return Task.FromResult<Stream?>(null);
        }

        public async Task<string> SaveAsync(ExcelFileInfo fileInfo, Action<Stream> writeWorkbook, CancellationToken cancellationToken = default)
        {
            var context = AndroidApp.Context;
            if (!string.IsNullOrWhiteSpace(fileInfo.SourceDocumentUri))
            {
                try
                {
                    await using var buffer = new MemoryStream();
                    writeWorkbook(buffer);
                    var payload = buffer.ToArray();

                    var uri = Android.Net.Uri.Parse(fileInfo.SourceDocumentUri);
                    if (uri != null)
                    {
                        byte[]? backup = null;
                        await using (var backupInput = context.ContentResolver?.OpenInputStream(uri))
                        {
                            if (backupInput != null)
                            {
                                using var backupBuffer = new MemoryStream();
                                await backupInput.CopyToAsync(backupBuffer, cancellationToken);
                                backup = backupBuffer.ToArray();
                            }
                        }

                        try
                        {
                            await using var output = context.ContentResolver?.OpenOutputStream(uri, "rwt");
                            if (output != null)
                            {
                                await output.WriteAsync(payload, cancellationToken);
                                await output.FlushAsync(cancellationToken);
                                return fileInfo.Directory != null
                                    ? Path.Combine(fileInfo.Directory, fileInfo.FileName)
                                    : fileInfo.SourceDocumentUri;
                            }
                        }
                        catch
                        {
                            if (backup != null)
                            {
                                await using var restore = context.ContentResolver?.OpenOutputStream(uri, "rwt");
                                if (restore != null)
                                {
                                    await restore.WriteAsync(backup, cancellationToken);
                                    await restore.FlushAsync(cancellationToken);
                                }
                            }

                            throw;
                        }
                    }
                }
                catch
                {
                }
            }

            try
            {
                var fallbackPath = !string.IsNullOrWhiteSpace(fileInfo.Directory)
                    ? Path.Combine(fileInfo.Directory, fileInfo.FileName)
                    : Path.Combine(FileSystem.AppDataDirectory, fileInfo.FileName);

                Directory.CreateDirectory(Path.GetDirectoryName(fallbackPath)!);
                await using var fileStream = File.Create(fallbackPath);
                writeWorkbook(fileStream);
                await fileStream.FlushAsync(cancellationToken);
                return fallbackPath;
            }
            catch
            {
                var safePath = Path.Combine(FileSystem.AppDataDirectory, fileInfo.FileName);
                Directory.CreateDirectory(Path.GetDirectoryName(safePath)!);
                await using var safeStream = File.Create(safePath);
                writeWorkbook(safeStream);
                await safeStream.FlushAsync(cancellationToken);
                return safePath;
            }
        }
    }
}
