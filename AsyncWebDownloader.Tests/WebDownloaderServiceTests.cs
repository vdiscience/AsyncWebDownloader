using AsyncWebDownloader.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Moq;
using Moq.Protected;
using StackExchange.Redis;
using System.Net;
using System.Text;
using WireMock.Server;

namespace AsyncWebDownloader.Tests;

/// <summary>
/// Tests for the WebDownloaderService class.
/// </summary>
public class WebDownloaderServiceTests : IDisposable
{
    #region Private Fields
    private readonly Mock<ILogger<WebDownloaderService>> _loggerMock;
    private readonly Mock<IConfiguration> _configMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly Mock<IConnectionMultiplexer> _redisMock;
    private readonly Mock<IDatabase> _redisDbMock;

    private readonly ObjectPool<HttpClient> _httpClientPool;
    private readonly WebDownloaderService _service;
    private readonly WireMockServer _wireMockServer;
    #endregion Private Fields

    #region Constructor and Dispose
    /// <summary>
    /// Constructor for WebDownloaderServiceTests.
    /// </summary>
    public WebDownloaderServiceTests()
    {
        _loggerMock = new Mock<ILogger<WebDownloaderService>>();
        _configMock = new Mock<IConfiguration>();
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        _redisMock = new Mock<IConnectionMultiplexer>();
        _redisDbMock = new Mock<IDatabase>();
        _redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_redisDbMock.Object);

        // Use the mocked handler for HttpClient
        _httpClientPool = new DefaultObjectPool<HttpClient>(
            new WebDownloaderService.HttpClientPooledObjectPolicy(_httpMessageHandlerMock.Object));

        // Mock IConfiguration.GetSection to return a mock section for each config key
        var maxConcurrentDownloadsSection = new Mock<IConfigurationSection>();
        maxConcurrentDownloadsSection.Setup(x => x.Value).Returns("2");
        _configMock.Setup(x => x.GetSection("WebDownloader:MaxConcurrentDownloads")).Returns(maxConcurrentDownloadsSection.Object);

        var timeoutSecondsSection = new Mock<IConfigurationSection>();
        timeoutSecondsSection.Setup(x => x.Value).Returns("10");
        _configMock.Setup(x => x.GetSection("WebDownloader:TimeoutSeconds")).Returns(timeoutSecondsSection.Object);

        var cacheMinutesSection = new Mock<IConfigurationSection>();
        cacheMinutesSection.Setup(x => x.Value).Returns("10");
        _configMock.Setup(x => x.GetSection("WebDownloader:CacheMinutes")).Returns(cacheMinutesSection.Object);

        // Also mock the indexer for completeness (if used elsewhere)
        _configMock.Setup(x => x[It.Is<string>(s => s == "WebDownloader:MaxConcurrentDownloads")])
            .Returns("2");
        _configMock.Setup(x => x[It.Is<string>(s => s == "WebDownloader:TimeoutSeconds")])
            .Returns("10");
        _configMock.Setup(x => x[It.Is<string>(s => s == "WebDownloader:CacheMinutes")])
            .Returns("10");

        _service = new WebDownloaderService(_redisMock.Object, _httpClientPool, _loggerMock.Object, _configMock.Object);
        _wireMockServer = WireMockServer.Start();
    }

    public void Dispose()
    {
        _wireMockServer.Stop();
        _wireMockServer.Dispose();
        _service.Dispose();
        GC.SuppressFinalize(this); // Fix for CA1816 - https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca1816
    }

    #endregion Constructor and Dispose

    #region Tests

    /// <summary>
    /// DownloadPagesAsync should return success results for valid URLs.
    /// </summary>
    /// <returns></returns>
    [Fact]
    public async Task DownloadPagesAsync_ValidUrls_ReturnsSuccessResults()
    {
        // Arrange
        var urls = new[] { "https://www.microsoft.com", "https://www.github.com" };
        var content1 = "<!DOCTYPE html><html xmlns:mscom=\"http://schemas.microsoft.com/CMSvNext\"\r\n        xmlns:md=\"http:/...";
        var content2 = "<!DOCTYPE html>\r\n<html\r\n  lang=\"en\"\r\n  data-color-mode=\"dark\" data-dark-theme=\"dark\"\r\n  data-col...";

        _redisDbMock.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisValue)RedisValue.Null);

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("microsoft.com")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content1, Encoding.UTF8, "text/html")
            });

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("github.com")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content2, Encoding.UTF8, "text/html")
            });

        _redisDbMock.Setup(db => db.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        // Act
        var progress = new Mock<IProgress<int>>();
        var results = await _service.DownloadPagesAsync(urls, progress.Object);

        // Assert
        var resultList = results.ToList();
        Assert.Equal(2, resultList.Count);
        Assert.All(resultList, r => Assert.True(r.IsSuccess));
        Assert.Contains(resultList, r => r.Content == content1);
        Assert.Contains(resultList, r => r.Content == content2);
        progress.Verify(p => p.Report(It.IsAny<int>()), Times.AtLeastOnce());
        _redisDbMock.Verify(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()), Times.Exactly(2));
    }

    /// <summary>
    /// DownloadPagesAsync should return cached results if available.
    /// </summary>
    /// <returns></returns>

    [Fact]
    public async Task DownloadPagesAsync_CachedResult_ReturnsFromCache()
    {
        // Arrange
        var url = "https://www.microsoft.com";
        var cachedContent = "Cached content";
        _redisDbMock.Setup(db => db.StringGetAsync(It.Is<RedisKey>(k => k == $"webpage:{url.GetHashCode()}"), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisValue)cachedContent);

        // Act
        var results = await _service.DownloadPagesAsync(new[] { url });

        // Assert
        var result = Assert.Single(results);
        Assert.True(result.IsSuccess);
        Assert.Equal(cachedContent, result.Content);
    }

    /// <summary>
    /// DownloadPagesAsync should handle failed requests gracefully.
    /// </summary>
    /// <returns></returns>
    [Fact]
    public async Task DownloadPagesAsync_FailedRequest_ReturnsErrorResult()
    {
        // Arrange
        var urls = new[] { "https://www.microsoft.com" };
        _redisDbMock.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisValue)RedisValue.Null);

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Request failed"));

        // Act
        var results = await _service.DownloadPagesAsync(urls);

        // Assert
        var result = Assert.Single(results);
        Assert.False(result.IsSuccess);
        Assert.Equal("Request failed", result.ErrorMessage);
    }

    /// <summary>
    /// DownloadPagesAsync should handle timeouts correctly.
    /// </summary>
    /// <returns></returns>
    [Fact]
    public async Task DownloadPagesAsync_Cancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        var urls = new[] { "https://www.microsoft.com" };
        var cts = new CancellationTokenSource();
        _redisDbMock.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisValue)RedisValue.Null);

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage _, CancellationToken ct) =>
            {
                await Task.Delay(1000, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        // Act
        var downloadTask = _service.DownloadPagesAsync(urls, cancellationToken: cts.Token);
        cts.Cancel(); // Cancel after starting the download

        var results = await downloadTask;

        // Assert
        var result = Assert.Single(results);
        Assert.False(result.IsSuccess);
        Assert.Equal("Download cancelled", result.ErrorMessage);
    }

    /// <summary>
    /// DownloadPagesAsync should throw ArgumentException for invalid URLs.
    /// </summary>
    /// <returns></returns>
    [Fact]
    public async Task DownloadPagesAsync_InvalidUrl_ThrowsArgumentException()
    {
        // Arrange
        var urls = new[] { "http://localhost/invalid" };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.DownloadPagesAsync(urls));
    }

    /// <summary>
    /// Constructor should initialize the service correctly.
    /// </summary>
    [Fact]
    public void Constructor_NullRedis_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new WebDownloaderService(null!, _httpClientPool, _loggerMock.Object, _configMock.Object));
    }

    /// <summary>
    /// Constructor should initialize the service correctly with a null HttpClientPool.
    /// </summary>
    [Fact]
    public void Constructor_NullHttpClientPool_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new WebDownloaderService(_redisMock.Object, null!, _loggerMock.Object, _configMock.Object));
    }

    /// <summary>
    /// Constructor should initialize the service correctly with a null Logger.
    /// </summary>

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new WebDownloaderService(_redisMock.Object, _httpClientPool, null!, _configMock.Object));
    }

    /// <summary>
    /// Constructor should initialize the service correctly with a null Configuration.
    /// </summary>
    [Fact]
    public void Constructor_NullConfiguration_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new WebDownloaderService(_redisMock.Object, _httpClientPool, _loggerMock.Object, null!));
    }

    /// <summary>
    /// DownloadPagesAsync should throw ObjectDisposedException after the service is disposed.
    /// </summary>
    /// <returns></returns>
    [Fact]
    public async Task DownloadPagesAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        _service.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            _service.DownloadPagesAsync(new[] { "https://www.datadog.com" }));
    }
    #endregion Tests
}