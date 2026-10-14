using System.Diagnostics;
using Xunit;

namespace FastGet.Core.Tests;

/// <summary>Tests change global timeouts, so they must not run alongside other tests.</summary>
[CollectionDefinition("timeouts", DisableParallelization = true)]
public sealed class TimeoutCollection;

/// <summary>Misbehaving servers observed on real downloads: connection limits, tarpits, stalls, flaky first answers.</summary>
[Collection("timeouts")]
public sealed class ResilienceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "fastget-tests-" + Guid.NewGuid().ToString("N"));
    readonly TimeSpan _oldHeader = Http.HeaderTimeout;
    readonly TimeSpan _oldRead = DownloadTask.ReadTimeout;

    public ResilienceTests()
    {
        Http.HeaderTimeout = TimeSpan.FromSeconds(1);
        DownloadTask.ReadTimeout = TimeSpan.FromSeconds(1);
    }

    public void Dispose()
    {
        Http.HeaderTimeout = _oldHeader;
        DownloadTask.ReadTimeout = _oldRead;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    DownloadManager NewManager(int connections = 8)
    {
        var m = new DownloadManager(Path.Combine(_dir, "data"));
        var s = m.Settings.Clone();
        s.Connections = connections;
        s.MaxRetries = 5;
        s.DownloadRoot = Path.Combine(_dir, "out");
        s.Categories = Category.Defaults(s.DownloadRoot);
        m.UpdateSettings(s);
        return m;
    }

    static byte[] Data(int size)
    {
        var b = new byte[size];
        new Random(7).NextBytes(b);
        return b;
    }

    static async Task<TimeSpan> RunToEnd(DownloadItem item, int seconds = 30)
    {
        var sw = Stopwatch.StartNew();
        while (item.Status is not (DownloadStatus.Completed or DownloadStatus.Error))
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException($"still {item.Status} after {seconds}s");
            await Task.Delay(25);
        }
        return sw.Elapsed;
    }

    [Fact]
    public async Task Server_connection_limit_is_learned_and_does_not_leave_idle_gaps()
    {
        var data = Data(6 * 1024 * 1024);
        using var server = new TestServer(data) { MaxConcurrent = 3, ChunkDelayMs = 3 };
        using var m = NewManager(connections: 8);

        var item = m.Add(new NewDownload { Url = server.Url("limited.bin") }, AddMode.StartNow);
        var took = await RunToEnd(item);

        Assert.Null(item.Error);
        Assert.Equal(data, File.ReadAllBytes(item.FullPath));
        Assert.True(server.PeakConcurrent <= 3);
        Assert.True(m.HostLimits.Get("127.0.0.1", 8) <= 3, "the limit should be remembered for this host");
        // 6 MB at 3 x ~5 MB/s is about half a second; the old engine slept 2-30 s after every 429.
        Assert.True(took < TimeSpan.FromSeconds(6), $"took {took}");
    }

    [Fact]
    public async Task Connection_that_never_answers_does_not_hang_the_download()
    {
        var data = Data(3 * 1024 * 1024);
        // Request 1 is the probe; 2 and 3 are the first two connections.
        using var server = new TestServer(data) { NeverAnswer = [2, 3], ChunkDelayMs = 1 };
        using var m = NewManager(connections: 4);

        var item = m.Add(new NewDownload { Url = server.Url("tarpit.bin") }, AddMode.StartNow);
        var took = await RunToEnd(item);

        Assert.Null(item.Error);
        Assert.Equal(data, File.ReadAllBytes(item.FullPath));
        Assert.True(took < TimeSpan.FromSeconds(10), $"took {took}");
    }

    [Fact]
    public async Task Connection_that_stalls_mid_body_is_reopened()
    {
        var data = Data(2 * 1024 * 1024);
        using var server = new TestServer(data) { StallAfter = new() { [2] = 256 * 1024 }, ChunkDelayMs = 1 };
        using var m = NewManager(connections: 1);

        var item = m.Add(new NewDownload { Url = server.Url("stall.bin") }, AddMode.StartNow);
        var took = await RunToEnd(item);

        Assert.Null(item.Error);
        Assert.Equal(data, File.ReadAllBytes(item.FullPath));
        Assert.True(took < TimeSpan.FromSeconds(8), $"took {took}");
    }

    [Fact]
    public async Task Failed_first_answer_is_retried_instead_of_failing_the_download()
    {
        var data = Data(500_000);
        using var server = new TestServer(data) { StatusFor = new() { [1] = 503, [2] = 502 } };
        using var m = NewManager();

        var item = m.Add(new NewDownload { Url = server.Url("flaky.bin") }, AddMode.StartNow);
        await RunToEnd(item);

        Assert.Null(item.Error);
        Assert.Equal(data, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Downloaded_file_is_not_left_sparse()
    {
        var data = Data(3 * 1024 * 1024);
        using var server = new TestServer(data);
        using var m = NewManager();

        var item = m.Add(new NewDownload { Url = server.Url("sparse.bin") }, AddMode.StartNow);
        await RunToEnd(item);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.False(File.GetAttributes(item.FullPath).HasFlag(FileAttributes.SparseFile));
    }
}
