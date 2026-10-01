using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Downloader.Exceptions;

namespace Downloader.Extensions;

internal static class ExceptionHelper
{
#if NETFRAMEWORK || NETSTANDARD2_0
    // .NET Framework's and netstandard2.0's HttpStatusCode enums both predate RFC 7538 (308)
    // and RFC 6585 (428/429) — the named members only exist on .NET 5+;
    // compare against the numeric values there.
    private const HttpStatusCode PermanentRedirectStatus = (HttpStatusCode)308;
    private const HttpStatusCode PreconditionRequiredStatus = (HttpStatusCode)428;
    private const HttpStatusCode TooManyRequestsStatus = (HttpStatusCode)429;
#else
    private const HttpStatusCode PermanentRedirectStatus = HttpStatusCode.PermanentRedirect;
    private const HttpStatusCode PreconditionRequiredStatus = HttpStatusCode.PreconditionRequired;
    private const HttpStatusCode TooManyRequestsStatus = HttpStatusCode.TooManyRequests;
#endif

    private static bool IsRedirectStatus(this HttpStatusCode statusCode)
    {
        return statusCode is
            HttpStatusCode.Moved or
            HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or
            PermanentRedirectStatus;
    }

    extension(Exception error)
    {
        internal bool IsRequestedRangeNotSatisfiable()
        {
            // StatusCode lives on HttpRequestException only on .NET 5+; on net462/netstandard2.0
            // it is carried by the downloader-owned DownloaderHttpRequestException instead.
            return error is HttpRequestException httpError &&
                   httpError.GetHttpStatus() == HttpStatusCode.RequestedRangeNotSatisfiable;
        }

        internal bool IsMomentumError()
        {
            // Classify retry-ability by exception type and HTTP status code only — never by
            // Exception.Source. Source is derived from stack/reflection metadata that is empty
            // under AOT/trimming, which previously made identical errors fatal only in AOT builds
            // (issue #226). Type/status classification is deterministic across JIT and AOT.
            bool isMomentum = error switch {
                // Transient transport / timeout / stream-teardown failures.
                SocketException => true,
                IOException => true, // includes HttpIOException (connection reset / premature end)
                TaskCanceledException => true, // request/read timeout (user cancellation is checked before this is consulted)
                ObjectDisposedException => true, // stream closed on timeout/cancel
                WebException { Status: WebExceptionStatus.Timeout } => true,

                // HTTP responses: retry only transient / overload / redirect statuses.
                HttpRequestException httpError => IsMomentumStatus(httpError.GetHttpStatus()),

                // Permanent client errors (400/401/403/404/...) and server errors such as
                // 500/502 are not worth retrying.
                _ => false
            };

            if (isMomentum)
                return true;

            return error.InnerException?.IsMomentumError() ?? false;
        }

        internal bool HasTypeOf(params Type[] types)
        {
            Exception innerException = error;
            while (innerException != null)
            {
                if (types.Any(type => innerException.GetType() == type))
                    return true;

                innerException = innerException.InnerException;
            }

            return false;
        }

        internal bool HasSource(params string[] sources)
        {
            Exception innerException = error;
            while (innerException != null)
            {
                foreach (string source in sources)
                {
                    if (string.Equals(innerException.Source, source, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                innerException = innerException.InnerException;
            }

            return false;
        }

        internal bool IsRedirectError()
        {
            return error is HttpRequestException httpError &&
                   httpError.GetHttpStatus() is { } status &&
                   status.IsRedirectStatus();
        }
    }

    /// <summary>
    /// Reads the status code carried by an <see cref="HttpRequestException"/>: the framework
    /// property on .NET 5+, the downloader-owned <see cref="DownloaderHttpRequestException"/>
    /// payload on the legacy targets (net462 / netstandard2.0), which predate that property.
    /// </summary>
    private static HttpStatusCode? GetHttpStatus(this HttpRequestException error)
    {
#if NET5_0_OR_GREATER
        return error?.StatusCode;
#else
        return (error as DownloaderHttpRequestException)?.StatusCode;
#endif
    }

    /// <summary>
    /// Whether an HTTP status (or its absence) is worth a retry. No response at all means a
    /// transport-level failure (always retry); permanent client errors and hard server errors
    /// are not.
    /// </summary>
    private static bool IsMomentumStatus(HttpStatusCode? status)
    {
        return status is null || // no response received → transport failure
               status is HttpStatusCode.RequestTimeout or       // 408
                   PreconditionRequiredStatus or                // 428 — some CDNs (e.g. BunnyCDN) use it as a concurrency throttle (#226)
                   TooManyRequestsStatus or                     // 429
                   HttpStatusCode.ServiceUnavailable or         // 503
                   HttpStatusCode.GatewayTimeout or             // 504
                   HttpStatusCode.Ambiguous or                  // 300
                   HttpStatusCode.Moved or                      // 301
                   HttpStatusCode.Redirect or                   // 302
                   HttpStatusCode.RedirectMethod or             // 303
                   HttpStatusCode.TemporaryRedirect or          // 307
                   PermanentRedirectStatus;                     // 308
    }

    /// <summary>
    /// Sometimes a server get certificate validation error
    /// https://stackoverflow.com/questions/777607/the-remote-certificate-is-invalid-according-to-the-validation-procedure-using
    /// </summary>
    internal static bool CertificateValidationCallBack(object sender,
        X509Certificate certificate,
        X509Chain chain,
        SslPolicyErrors sslPolicyErrors)
    {
        // If the certificate is a valid, signed certificate, return true.
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        // If there are errors in the certificate chain, look at each error to determine the cause.
        if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateChainErrors) != 0)
        {
            if (chain?.ChainStatus != null)
            {
                foreach (X509ChainStatus status in chain.ChainStatus)
                {
                    if (status.Status == X509ChainStatusFlags.NotTimeValid)
                    {
                        // If the error is for certificate expiration then it can be continued
                        return true;
                    }

                    if (status.Status == X509ChainStatusFlags.UntrustedRoot &&
                        certificate.Subject == certificate.Issuer)
                    {
                        // Self-signed certificates with an untrusted root are valid. 
                    }
                    else if (status.Status != X509ChainStatusFlags.NoError)
                    {
                        // If there are any other errors in the certificate chain, the certificate is invalid,
                        // so the method returns false.
                        return false;
                    }
                }
            }

            // When processing reaches this line, the only errors in the certificate chain are 
            // untrusted root errors for self-signed certificates. These certificates are valid
            // for default Exchange server installations, so return true.
            return true;
        }

        // In all other cases, return false.
        return false;
    }
}