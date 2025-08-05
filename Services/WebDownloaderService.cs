using AsyncWebDownloader.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Polly;
using Polly.Retry;
using StackExchange.Redis;
using System.Buffers;
using System.Diagnostics.Metrics;
using System.Text;

namespace AsyncWebDownloader.Core;

/// <summary>
/// This class provides functionality to download web pages asynchronously.
/// </summary>
public partial class WebDownloaderService : IDisposable, IWebDownloaderService
{
    #region Private Fields
    private readonly IConnectionMultiplexer _redis;
    private readonly ObjectPool<HttpClient> _httpClientPool;
    private readonly ILogger<WebDownloaderService> _logger;
    private readonly IConfiguration _configuration;
    private readonly bool _allowLoopback;
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly int _maxConcurrentDownloads;
    private readonly int _timeoutSeconds;
    private readonly int _cacheMinutes;
    private readonly Meter _meter;
    private readonly Counter<long> _downloadCounter;
    private readonly Histogram<double> _downloadDuration;
    private bool _disposed;
    #endregion Private Fields

    #region Constructor & Destructor
    /// <summary>
    /// Constructor for WebDownloaderService.
    /// </summary>
    /// <param name="redis"></param>
    /// <param name="httpClientPool"></param>
    /// <param name="logger"></param>
    /// <param name="configuration"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public WebDownloaderService(
        IConnectionMultiplexer redis,
        ObjectPool<HttpClient> httpClientPool,
        ILogger<WebDownloaderService> logger,
        IConfiguration configuration)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _httpClientPool = httpClientPool ?? throw new ArgumentNullException(nameof(httpClientPool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

        _maxConcurrentDownloads = configuration.GetValue<int>("WebDownloader:MaxConcurrentDownloads", 5);
        _timeoutSeconds = configuration.GetValue<int>("WebDownloader:TimeoutSeconds", 30);
        _cacheMinutes = configuration.GetValue<int>("WebDownloader:CacheMinutes", 10);

        _retryPolicy = Policy
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                onRetry: (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogWarning("Retry {RetryCount} for {Url} after {TimeSpan} due to {Exception}",
                        retryCount, context["url"], timeSpan, exception.Message);
                });

        _meter = new Meter("WebDownloader");
        _downloadCounter = _meter.CreateCounter<long>("web_downloads_total", description: "Total number of web downloads");
        _downloadDuration = _meter.CreateHistogram<double>("web_download_duration_seconds", description: "Duration of web downloads in seconds");
        _disposed = false;
    }

    /// <summary>
    /// Destructs the <see cref="WebDownloaderService"/> instance and releases resources.
    /// </summary>
    ~WebDownloaderService()
    {
        Dispose(false);
        _logger.LogDebug("WebDownloaderService finalized");
    }
    #endregion Constructor & Destructor

    #region Public Methods
    /// <summary>
    /// Downloads multiple web pages asynchronously.
    /// </summary>
    /// <param name="urls"></param>
    /// <param name="progress"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="ObjectDisposedException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="ArgumentException"></exception>
    public async Task<IEnumerable<WebPageResult>> DownloadPagesAsync(IEnumerable<string> urls, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WebDownloaderService)); // Ensures the service is not disposed

        if (urls == null)
        {
            throw new ArgumentNullException(nameof(urls));
        }

        var urlList = urls.ToList(); // Convert to list for easier manipulation

        if (!urlList.Any())
        {
            _logger.LogWarning("No URLs provided for download.");

            return Enumerable.Empty<WebPageResult>();
        }

        if (!urlList.All(IsValidUrl))
        {
            _logger.LogError("One or more invalid URLs provided.");

            _downloadCounter.Add(urlList.Count, new KeyValuePair<string, object?>("status", "invalid_urls")); // Record the count of invalid URLs
            _downloadDuration.Record(0, new KeyValuePair<string, object?>("status", "invalid_urls")); // Record duration as 0 for invalid URLs

            throw new ArgumentException("Invalid URLs provided."); // Throw exception for invalid URLs
        }

        _logger.LogInformation("Starting download of {Count} URLs", urlList.Count);

        var results = new List<WebPageResult>(); // List to store results of downloaded pages
        int completed = 0;
        var semaphore = new SemaphoreSlim(_maxConcurrentDownloads); // Semaphore to limit concurrent downloads

        var tasks = urlList.Select(async url =>
        {
            await semaphore.WaitAsync(cancellationToken); // Wait for an available slot in the semaphore

            try
            {
                var result = await DownloadPageAsync(url, cancellationToken); // Download the page asynchronously

                Interlocked.Increment(ref completed); // Increment completed count atomically

                progress?.Report(completed * 100 / urlList.Count); // Report progress if a progress handler is provided

                _logger.LogInformation("Downloaded {Url} ({Progress}%)", url, completed * 100 / urlList.Count);

                return result;
            }
            finally
            {
                semaphore.Release(); // Release the semaphore to allow another download to proceed
            }
        });

        results.AddRange(await Task.WhenAll(tasks)); // Wait for all download tasks to complete

        _logger.LogInformation("Completed download of {Count} URLs", urlList.Count);

        return results;
    }

    /// <summary>
    /// Disposes the resources used by the <see cref="WebDownloaderService"/> instance.
    /// </summary>
    public void Dispose()
    {
        Dispose(true); // Dispose managed resources
        _disposed = true; // Mark as disposed to prevent multiple disposals
        GC.SuppressFinalize(this);// Suppress finalization to avoid unnecessary overhead
    }

    #endregion Public Methods

    #region Private Methods
    /// <summary>
    /// Downloads the content of a web page from the specified URL asynchronously.
    /// </summary>
    /// <remarks>This method attempts to retrieve the web page content from a Redis cache before downloading
    /// it. If the content is not found in the cache, it downloads the page using an HTTP client and caches the result
    /// for future use. The method streams the response to minimize memory usage and validates that the content type is
    /// HTML. If the download fails or is canceled, the returned <see cref="WebPageResult"/> will indicate the failure
    /// or cancellation.</remarks>
    /// <param name="url">The URL of the web page to download. Must be a valid, absolute URL.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. If the operation is canceled, the method will return a result
    /// indicating cancellation.</param>
    /// <returns>A <see cref="WebPageResult"/> containing the downloaded content, a flag indicating whether the operation was
    /// successful, and an optional error message if the download failed.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the <see cref="WebDownloaderService"/> instance has been disposed.</exception>
    private async Task<WebPageResult> DownloadPageAsync(string url, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WebDownloaderService)); // Ensures the service is not disposed

        var startTime = DateTime.UtcNow;

        var tags = new[] {
            new KeyValuePair<string, object?>("url", url)
        }; // Tags for metrics and logging

        if (!IsValidUrl(url))
        {
            _logger.LogError("Invalid URL: {Url}", url);

            return new WebPageResult(url, string.Empty, false, "Invalid URL"); // Return a result indicating invalid URL
        }

        var db = _redis.GetDatabase(); // Get Redis database instance
        var cacheKey = $"webpage:{url.GetHashCode()}"; // Generate a cache key based on the URL

        try
        {
            var cachedContent = await db.StringGetAsync(cacheKey); // Check Redis cache

            if (cachedContent.IsNullOrEmpty) // Check if the content is cached
            {
                _logger.LogDebug("Cache miss for {Url}", url);
            }
            else
            {
                cachedContent = cachedContent.ToString(); // Deserialize cached content
            }

            if (cancellationToken.IsCancellationRequested) // Check if cancellation is requested
            {
                _logger.LogWarning("Download cancelled for {Url}", url);

                _downloadCounter.Add(1, tags);
                _downloadDuration.Record((DateTime.UtcNow - startTime).TotalSeconds, tags);

                return new WebPageResult(url, string.Empty, false, "Download cancelled"); // Return a result indicating cancellation
            }

            if (cachedContent.HasValue)
            {
                _logger.LogDebug("Cache hit for {Url}", url);

                _downloadCounter.Add(1, tags);
                _downloadDuration.Record((DateTime.UtcNow - startTime).TotalSeconds, tags);

                return new WebPageResult(url, cachedContent.ToString() ?? string.Empty, true); // Return cached content
            }

            var httpClient = _httpClientPool.Get(); // Get HttpClient from pool

            if (httpClient == null)
            {
                _logger.LogError("Failed to get HttpClient from pool for {Url}", url);

                _downloadCounter.Add(1, tags);
                _downloadDuration.Record((DateTime.UtcNow - startTime).TotalSeconds, tags);

                return new WebPageResult(url, string.Empty, false, "HttpClient pool exhausted");// Return a result indicating failure to get HttpClient
            }

            try
            {
                _logger.LogDebug("Downloading {Url}", url);

                var response = await _retryPolicy.ExecuteAsync(
                    async (context, ct) =>
                    {
                        var httpResponse = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

                        if (!httpResponse.Content.Headers.ContentType?.MediaType.Contains("text/html") ?? true)
                        {
                            throw new HttpRequestException("Invalid content type");
                        }

                        return httpResponse;
                    },

                    new Dictionary<string, object> {
                        {
                            "url",
                            url
                        }
                    }, cancellationToken); // Execute the HTTP request with retry policy

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);// Stream response to minimize memory usage
                using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true); // Use StreamReader to read the response content

                var contentBuilder = new StringBuilder();
                char[] buffer = ArrayPool<char>.Shared.Rent(4096); // Rent a buffer from the shared pool to read the content

                try
                {
                    int charsRead;
                    while ((charsRead = await reader.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        contentBuilder.Append(buffer, 0, charsRead);
                    } // Read the content asynchronously and append it to the StringBuilder
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(buffer); // Return the buffer to the shared pool
                }

                var content = contentBuilder.ToString();
                var result = new WebPageResult(url, content, true);

                await db.StringSetAsync(cacheKey, content, TimeSpan.FromMinutes(_cacheMinutes));// Cache in Redis

                _downloadCounter.Add(1, tags);
                _downloadDuration.Record((DateTime.UtcNow - startTime).TotalSeconds, tags);

                _logger.LogDebug("Successfully downloaded {Url}", url);

                return result;
            }
            finally
            {
                _httpClientPool.Return(httpClient); // Return HttpClient to the pool
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to download {Url}", url);

            _downloadCounter.Add(1, tags);
            _downloadDuration.Record((DateTime.UtcNow - startTime).TotalSeconds, tags);

            return new WebPageResult(url, string.Empty, false, ex.Message);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Download cancelled for {Url}", url);

            _downloadCounter.Add(1, tags);
            _downloadDuration.Record((DateTime.UtcNow - startTime).TotalSeconds, tags);

            return new WebPageResult(url, string.Empty, false, "Download cancelled");
        }
    }

    /// <summary>
    /// Checks if the provided URL is valid.
    /// </summary>
    /// <param name="url"></param>
    /// <returns></returns>
    private bool IsValidUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uriResult)
               && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps)
               && !uriResult.IsLoopback; // Checks if the URL is absolute and uses HTTP or HTTPS scheme
    }
    #endregion Private Methods

    #region Protected Methods
    
    /// <summary>
    /// Disposes the resources used by the <see cref="WebDownloaderService"/> instance.
    /// </summary>
    /// <param name="disposing"></param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _meter.Dispose();// Dispose managed resources
        }

        _disposed = true; // Mark as disposed to prevent multiple disposals
    }

    #endregion Protected Methods
}