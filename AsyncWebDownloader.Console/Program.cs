using AsyncWebDownloader.Core.Helpers;
using AsyncWebDownloader.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

class Program
{
    /// <summary>
    /// Main entry point for the application.
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    static async Task Main(string[] args)
    {
        var configuration = BuildConfiguration();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .CreateLogger(); // Initialize Serilog with configuration from appsettings.json

        using var serviceProvider = ConfigureServices(configuration); // Dispose ServiceProvider
        using var cts = new CancellationTokenSource(); // Cancellation token to handle Ctrl+C

        Console.CancelKeyPress += (s, e) =>
        {
            Log.Information("Cancellation requested.");
            cts.Cancel();
            e.Cancel = true;
        }; // Handles Ctrl+C to cancel the operation

        try
        {
            var urls = configuration.GetSection("Urls").Get<List<string>>();

            if (urls == null || !urls.Any())
            {
                Log.Error("No URLs configured in appsettings.json");
                Console.WriteLine("No URLs configured in appsettings.json.");

                return;
            }

            var downloader = serviceProvider.GetRequiredService<IWebDownloaderService>();
            var progress = new Progress<int>(percent => Console.WriteLine($"Progress: {percent}%"));
            var results = await downloader.DownloadPagesAsync(urls, progress, cts.Token);

            foreach (var result in results)
            {
                Console.WriteLine($"\nURL: {result.Url}");

                if (result.IsSuccess)
                {
                    Console.WriteLine($"Success");
                    Console.WriteLine($"Content Length: {result.Content.Length} characters");
                    Console.WriteLine($"First 100 characters: {result.Content.Substring(0, Math.Min(100, result.Content.Length))}..."); // Grab first 100 characters
                }
                else
                {
                    Console.WriteLine($"Failed");
                    Console.WriteLine($"Error: {result.ErrorMessage}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("Cancelled operation.");
        }
        catch (ArgumentException ex)
        {
            Log.Error(ex, "Invalid input.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected error.");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    #region Private Methods

    /// <summary>
    /// Builds the configuration from appsettings.json.
    /// </summary>
    /// <returns></returns>
    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
    }

    /// <summary>
    /// Configures the services for dependency injection.
    /// </summary>
    /// <param name="configuration"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    private static ServiceProvider ConfigureServices(IConfiguration configuration)
    {
        var redisConnectionString = configuration.GetValue<string>("Redis:ConnectionString");
        if (string.IsNullOrEmpty(redisConnectionString))
        {
            throw new InvalidOperationException("Redis connection string is not configured.");
        }

        return new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration) // <-- Add this line
            .AddLogging(builder => builder.AddSerilog())
            .AddWebDownloaderServices(redisConnectionString)
            .BuildServiceProvider();
    }
    #endregion Private Methods
}