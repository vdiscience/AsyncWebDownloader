using System.Net;
using AsyncWebDownloader.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.ObjectPool;
using StackExchange.Redis;

namespace AsyncWebDownloader.Core.Helpers;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWebDownloaderServices(this IServiceCollection services, string redisConnectionString)
    {
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnectionString));

        services.AddSingleton<ObjectPool<HttpClient>>(sp =>
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false,
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true // For testing; use proper validation in production
            };
            var pool = new DefaultObjectPool<HttpClient>(new HttpClientPooledObjectPolicy(handler));
            return pool;
        });

        services.AddSingleton<IWebDownloaderService, WebDownloaderService>();

        return services;
    }

    private class HttpClientPooledObjectPolicy : IPooledObjectPolicy<HttpClient>
    {
        private readonly HttpClientHandler _handler;

        public HttpClientPooledObjectPolicy(HttpClientHandler handler)
        {
            _handler = handler;
        }

        public HttpClient Create()
        {
            return new HttpClient(_handler, false)
            {
                Timeout = Timeout.InfiniteTimeSpan // Timeout handled per request
            };
        }

        public bool Return(HttpClient obj)
        {
            return true; // HttpClient is reusable
        }
    }
}