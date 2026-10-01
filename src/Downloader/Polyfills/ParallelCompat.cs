using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Downloader.Polyfills;

/// <summary>
/// A <c>Parallel.ForEachAsync</c> stand-in for the legacy targets (net462 / netstandard2.0),
/// where that API doesn't exist (.NET 6+). Preserves the semantics the download pipeline
/// relies on: bounded concurrency, lazy enumeration (chunk tasks are created on demand),
/// cooperative cancellation, and failing the whole pass when a body faults.
/// </summary>
internal static class ParallelCompat
{
#if NETFRAMEWORK || NETSTANDARD2_0
    public static async Task ForEachAsync<T>(IEnumerable<T> source, int maxDegreeOfParallelism,
        CancellationToken cancellationToken, Func<T, CancellationToken, Task> body)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));
        if (body is null)
            throw new ArgumentNullException(nameof(body));
        if (maxDegreeOfParallelism <= 0)
            maxDegreeOfParallelism = 1;

        using SemaphoreSlim throttle = new(maxDegreeOfParallelism, maxDegreeOfParallelism);
        List<Task> workers = new();

        foreach (T item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);

            workers.Add(Task.Run(async () =>
            {
                try
                {
                    await body(item, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    throttle.Release();
                }
            }, CancellationToken.None));
        }

        // Propagates the first observed body exception, like Parallel.ForEachAsync does —
        // remaining work still runs to completion/cancellation, matching its drain semantics.
        await Task.WhenAll(workers).ConfigureAwait(false);
    }
#else
    public static Task ForEachAsync<T>(IEnumerable<T> source, int maxDegreeOfParallelism,
        CancellationToken cancellationToken, Func<T, CancellationToken, Task> body)
    {
        ParallelOptions options = new() {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };
        return Parallel.ForEachAsync(source, options, body);
    }
#endif
}
