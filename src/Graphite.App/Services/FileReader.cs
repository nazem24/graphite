using System.IO;

namespace Graphite.App.Services;

/// <summary>
/// Reads a whole file without getting in the way of whoever else has it open. <c>File.ReadAllBytesAsync</c>
/// refuses to read a file another program holds open for writing (OneDrive finishing a sync, Word saving,
/// a PDF printer still flushing) — which showed up as a document that "can't be opened" and then opens
/// fine a second later. This shares the file and retries briefly on a sharing violation.
/// </summary>
public static class FileReader
{
    public static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[checked((int)stream.Length)];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(read), ct);
                    if (n == 0) break;
                    read += n;
                }
                if (read < buffer.Length) Array.Resize(ref buffer, read); // the file shrank while it was read
                return buffer;
            }
            catch (IOException ex) when (attempt < 3 && !ct.IsCancellationRequested &&
                                          ex is not (FileNotFoundException or DirectoryNotFoundException))
            {
                await Task.Delay(200 * (attempt + 1), ct); // locked for a moment: wait and try again
            }
        }
    }
}
