using System.Net;
using System.Net.Http;

namespace Downloader.Exceptions;

/// <summary>
/// An <see cref="HttpRequestException"/> that carries the failing response's
/// <see cref="HttpStatusCode"/>.
/// </summary>
/// <remarks>
/// Only ever thrown on the legacy targets (net462 / netstandard2.0): the framework's
/// <see cref="HttpRequestException"/> gained a <c>StatusCode</c> property in .NET 5, so
/// <c>EnsuresuccessStatusCode</c> is not usable when the retry policy needs the status code
/// (which statuses are momentum/transient, redirect handling, 416 handling). Thrown from
/// <c>SocketClient.SendRequestAsync</c> at the exact point the modern build calls
/// <c>EnsureSuccessStatusCode()</c>, so both code paths surface the same information to
/// <c>ExceptionHelper</c> — and to consumers, since this still is-a
/// <see cref="HttpRequestException"/>.
/// </remarks>
internal class DownloaderHttpRequestException : HttpRequestException
{
    /// <summary>The HTTP status code the server responded with.</summary>
    public HttpStatusCode StatusCode { get; }

    public DownloaderHttpRequestException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }
}
