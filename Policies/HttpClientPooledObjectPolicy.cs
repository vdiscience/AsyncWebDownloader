using Microsoft.Extensions.ObjectPool;

namespace AsyncWebDownloader.Core;

/// <summary>
/// Downloader service that manages HTTP requests using pooled HttpClient instances.
/// </summary>
public partial class WebDownloaderService
{
    /// <summary>
    /// Provides a policy for managing pooled <see cref="HttpClient"/> instances.
    /// </summary>
    /// <remarks>This policy is designed to create and manage <see cref="HttpClient"/> objects for use in
    /// object pooling scenarios. It ensures that <see cref="HttpClient"/> instances are created with a specified <see
    /// cref="HttpMessageHandler"/> and are properly reset when returned to the pool.</remarks>
    public class HttpClientPooledObjectPolicy : IPooledObjectPolicy<HttpClient>
    {
        #region Private Fields
        private readonly HttpMessageHandler _handler; /// The HTTP message handler used to create HttpClient instances
        #endregion Private Fields

        #region Constructor
        /// Constructor for HttpClientPooledObjectPolicy.
        public HttpClientPooledObjectPolicy(HttpMessageHandler handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }
        #endregion Constructor

        #region Public Methods
        /// <summary>
        /// Creates a new <see cref="HttpClient"/> instance.
        /// </summary>
        /// <returns></returns>
        public HttpClient Create()
        {
            return new HttpClient(_handler, false);
        }

        /// <summary>
        /// Resets the <see cref="HttpClient"/> instance before returning it to the pool.
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        public bool Return(HttpClient obj)
        {
            obj.DefaultRequestHeaders.Clear();
            obj.CancelPendingRequests(); // Cancel any pending requests to ensure a clean state

            return true;
        }
        #endregion Public Methods
    }
}