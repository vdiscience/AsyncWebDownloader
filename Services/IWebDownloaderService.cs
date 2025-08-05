namespace AsyncWebDownloader.Core.Services;

/// <summary>
/// Defines the IWebDownloaderService service.
/// </summary>
public interface IWebDownloaderService
{
    /// <summary>
    /// Downloads web pages from the specified URLs asynchronously.
    /// </summary>
    /// <param name="urls"></param>
    /// <param name="progress"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<WebPageResult>> DownloadPagesAsync(IEnumerable<string> urls, IProgress<int>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents the result of a web page retrieval operation.
/// </summary>
/// <remarks>This record encapsulates the outcome of an attempt to retrieve a web page, including the URL, the
/// retrieved content (if successful), and any error information if the operation failed.</remarks>
/// <param name="Url">The URL of the web page that was retrieved.</param>
/// <param name="Content">The content of the web page as a string. This will be empty if <paramref name="IsSuccess"/> is <see
/// langword="false"/>.</param>
/// <param name="IsSuccess"><see langword="true"/> if the web page was successfully retrieved; otherwise, <see langword="false"/>.</param>
/// <param name="ErrorMessage">An error message describing the reason for failure, if <paramref name="IsSuccess"/> is <see langword="false"/>. This
/// value will be <see langword="null"/> if the operation was successful.</param>
public record WebPageResult(string Url, string Content, bool IsSuccess, string? ErrorMessage = null);