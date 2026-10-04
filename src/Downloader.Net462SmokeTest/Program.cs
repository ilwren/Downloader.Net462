using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Downloader;

namespace Net462SmokeTest;

/// <summary>
/// End-to-end smoke checks for the Downloader library's net462 target. The full unit/integration
/// suites run against the modern TFMs (net10/net11) — this app exists because the suite's
/// Kestrel-based dummy server cannot run on .NET Framework, and the net462 HTTP path
/// (HttpClientHandler / ServicePoint stack) needs at least one real verification leg: compile
/// checks alone would not catch TLS, connection-limit, or stream-read differences.
///
/// Run by the Windows CI leg against the in-process <see cref="MiniHttpServer"/>; exit code 0
/// means every check passed.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main()
    {
        Console.WriteLine("Downloader net462 smoke test");
        Console.WriteLine("  CLR: " + Environment.Version);

        string workDir = Path.Combine(Path.GetTempPath(), "downloader-net462-smoke");
        Directory.CreateDirectory(workDir);

        try
        {
            using (MiniHttpServer server = new())
            {
                Console.WriteLine("  Server: " + server.BaseUrl);

                await CheckSingleChunkDownload(workDir, server).ConfigureAwait(false);
                await CheckMultiChunkDownload(workDir, server).ConfigureAwait(false);
                await CheckNoRangeDownload(workDir, server).ConfigureAwait(false);
                await CheckMetadataResolver(server).ConfigureAwait(false);
                await CheckPauseResumeCancel(workDir, server).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Fail("unhandled exception: " + ex);
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch
            {
                // best effort cleanup
            }
        }

        Console.WriteLine();
        Console.WriteLine($"SMOKE RESULT: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>Verifies a plain single-connection download and its terminal event.</summary>
    private static async Task CheckSingleChunkDownload(string workDir, MiniHttpServer server)
    {
        string target = Path.Combine(workDir, "single.bin");
        var config = new DownloadConfiguration {
            ChunkCount = 1,
            ParallelCount = 1,
            ParallelDownload = false,
        };

        using (var service = new DownloadService(config))
        {
            AsyncCompletedEventArgs completed = null;
            service.DownloadFileCompleted += (_, e) => completed = e;

            await service.DownloadFileTaskAsync(server.BaseUrl + "/file.bin", target).ConfigureAwait(false);
            await Task.Delay(100).ConfigureAwait(false); // let the completion event settle

            Check(completed != null && completed.Error == null && !completed.Cancelled,
                "single-chunk download completes with a terminal event (no error, not cancelled)");
            Check(File.Exists(target), "single-chunk target file exists");
            Check(MiniHttpServer.MatchesPattern(File.ReadAllBytes(target), MiniHttpServer.FileSize),
                "single-chunk downloaded bytes match the expected pattern");
        }
    }

    /// <summary>Verifies a chunked, ranged, parallel download reassembles correctly.</summary>
    private static async Task CheckMultiChunkDownload(string workDir, MiniHttpServer server)
    {
        string target = Path.Combine(workDir, "multi.bin");
        var config = new DownloadConfiguration {
            ChunkCount = 8,
            ParallelCount = 4,
            ParallelDownload = true,
        };

        using (var service = new DownloadService(config))
        {
            AsyncCompletedEventArgs completed = null;
            service.DownloadFileCompleted += (_, e) => completed = e;

            await service.DownloadFileTaskAsync(server.BaseUrl + "/file.bin", target).ConfigureAwait(false);
            await Task.Delay(100).ConfigureAwait(false);

            Check(completed != null && completed.Error == null && !completed.Cancelled,
                "multi-chunk parallel download completes without error");
            Check(MiniHttpServer.MatchesPattern(File.ReadAllBytes(target), MiniHttpServer.FileSize),
                "multi-chunk reassembled file matches the expected pattern");
        }
    }

    /// <summary>Verifies the server-without-range-support path (single connection, full body).</summary>
    private static async Task CheckNoRangeDownload(string workDir, MiniHttpServer server)
    {
        string target = Path.Combine(workDir, "norange.bin");
        var config = new DownloadConfiguration {
            ChunkCount = 4, // server refuses ranges -> must collapse to a single chunk
            ParallelCount = 2,
            ParallelDownload = true,
        };

        using (var service = new DownloadService(config))
        {
            AsyncCompletedEventArgs completed = null;
            service.DownloadFileCompleted += (_, e) => completed = e;

            await service.DownloadFileTaskAsync(server.BaseUrl + "/norange.bin", target).ConfigureAwait(false);
            await Task.Delay(100).ConfigureAwait(false);

            Check(completed != null && completed.Error == null && !completed.Cancelled,
                "no-range server download completes without error");
            Check(MiniHttpServer.MatchesPattern(File.ReadAllBytes(target), MiniHttpServer.NoRangeSize),
                "no-range downloaded bytes match the expected pattern");
        }
    }

    /// <summary>Verifies RemoteFileResolver's header probe (name, size, range support).</summary>
    private static async Task CheckMetadataResolver(MiniHttpServer server)
    {
        RemoteFileInfo info = await RemoteFileResolver
            .GetFileInfoAsync(server.BaseUrl + "/file.bin")
            .ConfigureAwait(false);

        Check(info.FileName == "file.bin",
            "resolver reads the file name from Content-Disposition (got: " + info.FileName + ")");
        Check(info.FileSize == MiniHttpServer.FileSize,
            "resolver reads the size from the probe (got: " + info.FileSize + ")");
        Check(info.SupportsRange, "resolver detects range support");
        Check(info.ContentType == "application/octet-stream",
            "resolver reads the content type (got: " + info.ContentType + ")");
    }

    /// <summary>Verifies pause/resume/cancel on an in-flight (drip-fed) transfer.</summary>
    private static async Task CheckPauseResumeCancel(string workDir, MiniHttpServer server)
    {
        string target = Path.Combine(workDir, "big.bin");
        var config = new DownloadConfiguration {
            ChunkCount = 1,
            ParallelCount = 1,
            ParallelDownload = false,
        };

        using (var service = new DownloadService(config))
        {
            var completionSource = new TaskCompletionSource<AsyncCompletedEventArgs>();
            service.DownloadFileCompleted += (_, e) => completionSource.TrySetResult(e);

            Task download = service.DownloadFileTaskAsync(server.BaseUrl + "/big.bin", target);

            // Orchestrate by time, not by progress events: while paused, the read loop blocks on
            // the pause token, so no further progress events fire — an event-driven state machine
            // would deadlock the scenario itself. The body is drip-fed (~3 MB at ~1 MB/s), so a
            // few hundred ms in, the transfer is guaranteed to still be in flight.
            await Task.Delay(400).ConfigureAwait(false);
            service.Pause();
            bool sawPaused = service.IsPaused;

            // Bytes already in flight when Pause() lands are allowed to drain — each active
            // chunk finishes the read it was inside before honoring the pause token. So sample
            // only AFTER a drain window, twice: a paused transfer must show zero growth between
            // the two samples, not necessarily between "Pause returned" and "one request later".
            await Task.Delay(500).ConfigureAwait(false);
            long positionAfterDrain = service.Package?.ReceivedBytesSize ?? -1;
            await Task.Delay(400).ConfigureAwait(false);
            long positionAfterPauseWait = service.Package?.ReceivedBytesSize ?? -1;
            service.Resume();

            await Task.Delay(400).ConfigureAwait(false);
            service.CancelAsync();

            Task finished = await Task.WhenAny(completionSource.Task, Task.Delay(TimeSpan.FromSeconds(30)))
                .ConfigureAwait(false);
            Check(ReferenceEquals(finished, completionSource.Task),
                "cancelled download reports a terminal event within the timeout");

            AsyncCompletedEventArgs completed = completionSource.Task.Status == TaskStatus.RanToCompletion
                ? completionSource.Task.Result
                : null;
            Check(completed != null && completed.Cancelled,
                "cancelled download surfaces as Cancelled=true");
            Check(sawPaused, "Pause() transitions the service into the paused state");
            Check(positionAfterDrain > 0 && positionAfterPauseWait == positionAfterDrain,
                $"no bytes arrive while paused (after drain: {positionAfterDrain}, 400ms later: {positionAfterPauseWait})");

            try
            {
                await download.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // acceptable — surfaced via the completion event either way
            }
        }
    }

    private static void Check(bool condition, string name)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine("[PASS] " + name);
        }
        else
        {
            _failed++;
            Console.WriteLine("[FAIL] " + name);
        }
    }

    private static void Fail(string name)
    {
        Check(false, name);
    }
}
