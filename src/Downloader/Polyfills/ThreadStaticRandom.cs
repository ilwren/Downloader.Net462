#if NETFRAMEWORK || NETSTANDARD2_0
using System;
using System.Threading;

namespace Downloader.Polyfills;

/// <summary>
/// Thread-safe random number source standing in for <c>Random.Shared</c>, which only exists on
/// .NET 6+. A single shared <see cref="Random"/> would need locking on the legacy targets —
/// exactly the contention the .NET 6 API was designed to avoid — so each thread gets its own.
/// </summary>
internal static class ThreadStaticRandom
{
    [ThreadStatic] private static Random _random;

    /// <summary>Returns a pseudo-random integer in <c>[0, maxExclusive)</c>.</summary>
    public static int Next(int maxExclusive)
    {
        // Seed with values that differ across threads created in the same TickCount window.
        _random ??= new Random(unchecked(Environment.TickCount * 31 + Environment.CurrentManagedThreadId));
        return _random.Next(maxExclusive);
    }
}
#endif
