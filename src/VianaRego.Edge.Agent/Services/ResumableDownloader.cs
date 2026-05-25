using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;

namespace VianaRego.Edge.Agent.Services;

public class ResumableDownloader
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ResumableDownloader> _logger;

    public ResumableDownloader(HttpClient httpClient, ILogger<ResumableDownloader> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string> DownloadAsync(
        string url,
        string destinationPath,
        CancellationToken ct,
        Action<long, long>? onProgress = null)
    {
        var tempPath = destinationPath + ".part";
        var fileInfo = new FileInfo(tempPath);
        long existingLength = 0;

        if (fileInfo.Exists)
        {
            existingLength = fileInfo.Length;
            _logger.LogInformation("Found partial download ({Path}). Resuming from byte {Length}...", tempPath, existingLength);
        }

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Download failed for {Url}. Status: {Status}. Response: {Response}", url, response.StatusCode, content);
            response.EnsureSuccessStatusCode();
        }

        var isResuming = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!isResuming && existingLength > 0)
        {
            _logger.LogWarning("Server does not support Range requests or resource changed. Restarting download.");
            existingLength = 0;
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        var totalSize = response.Content.Headers.ContentLength ?? 0;
        if (isResuming)
        {
            var contentRange = response.Content.Headers.ContentRange;
            if (contentRange != null && contentRange.Length.HasValue)
            {
                totalSize = contentRange.Length.Value;
            }
            else
            {
                totalSize += existingLength;
            }
        }

        using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        using var fileStream = new FileStream(
            tempPath,
            existingLength > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        var buffer = new byte[81920];
        long currentBytes = existingLength;
        onProgress?.Invoke(currentBytes, totalSize);

        int bytesRead;
        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
            currentBytes += bytesRead;
            onProgress?.Invoke(currentBytes, totalSize);
        }

        fileStream.Close();

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        File.Move(tempPath, destinationPath);
        _logger.LogInformation("Download complete: {Path}", destinationPath);
        return destinationPath;
    }
}
