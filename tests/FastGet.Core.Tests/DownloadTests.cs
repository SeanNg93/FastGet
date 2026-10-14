using Xunit;

namespace FastGet.Core.Tests;

public sealed class DownloadTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "fastget-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    static byte[] RandomBytes(int size)
    {
        var data = new byte[size];
        new Random(42).NextBytes(data);
        return data;
    }

    DownloadManager NewManager(int connections = 8)
    {
        var m = new DownloadManager(Path.Combine(_dir, "data"));
        var s = m.Settings.Clone();
        s.Connections = connections;
        s.MaxRetries = 2;
        s.DownloadRoot = Path.Combine(_dir, "out");
        s.Categories = Category.Defaults(s.DownloadRoot);
        m.UpdateSettings(s);
        return m;
    }

    static async Task WaitFor(Func<bool> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Segmented_download_reassembles_the_exact_bytes()
    {
        var data = RandomBytes(6 * 1024 * 1024 + 123);
        using var server = new TestServer(data) { ChunkDelayMs = 1 };
        using var m = NewManager(connections: 8);

        var item = m.Add(new NewDownload { Url = server.Url("big.bin") }, AddMode.StartNow);
        await WaitFor(() => item.Status is DownloadStatus.Completed or DownloadStatus.Error);

        Assert.Null(item.Error);
        Assert.True(item.Resumable);
        Assert.True(item.Segments.Count > 1, "expected the file to be split across connections");
        Assert.Equal(data, File.ReadAllBytes(item.FullPath));
        Assert.False(File.Exists(item.PartPath));
    }

    [Fact]
    public async Task Server_without_ranges_uses_one_connection()
    {
        var data = RandomBytes(700_000);
        using var server = new TestServer(data, ranges: false);
        using var m = NewManager();

        var item = m.Add(new NewDownload { Url = server.Url("plain.bin") }, AddMode.StartNow);
        await WaitFor(() => item.Status is DownloadStatus.Completed or DownloadStatus.Error);

        Assert.Null(item.Error);
        Assert.False(item.Resumable);
        Assert.Single(item.Segments);
        Assert.Equal(data, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Paused_download_resumes_after_restarting_the_app()
    {
        var data = RandomBytes(3 * 1024 * 1024);
        using var server = new TestServer(data) { ChunkDelayMs = 20 };
        Guid id;
        long before;
        using (var m = NewManager(connections: 4))
        {
            var item = m.Add(new NewDownload { Url = server.Url("resume.bin") }, AddMode.StartNow);
            id = item.Id;
            await WaitFor(() => item.Downloaded > 400_000);
            m.Pause(item);
            await WaitFor(() => item.Status == DownloadStatus.Paused);
            before = item.Downloaded;
            Assert.True(before < data.Length);
        } // Dispose saves the list, like closing the app

        server.ChunkDelayMs = 0;
        int requestsBefore = server.Requests;
        using var m2 = new DownloadManager(Path.Combine(_dir, "data"));
        var restored = m2.Find(id)!;
        Assert.True(restored.Downloaded > 0);
        m2.Start(restored);
        await WaitFor(() => restored.Status is DownloadStatus.Completed or DownloadStatus.Error);

        Assert.Null(restored.Error);
        Assert.Equal(data, File.ReadAllBytes(restored.FullPath));
        Assert.True(server.Requests > requestsBefore);
    }

    [Fact]
    public async Task Content_disposition_names_the_file_and_categories_pick_the_folder()
    {
        var data = RandomBytes(50_000);
        using var server = new TestServer(data) { ContentDisposition = "attachment; filename=\"report.pdf\"" };
        using var m = NewManager();

        var probe = await m.ProbeAsync(server.Url("get?id=1"), null, null, null, CancellationToken.None);
        Assert.Equal("report.pdf", probe.FileName);
        Assert.Equal(data.Length, probe.Size);
        Assert.True(probe.Resumable);

        var item = m.Add(new NewDownload { Url = server.Url("get?id=1"), FileName = probe.FileName }, AddMode.StartNow);
        Assert.Equal("Documents", item.Category);
        await WaitFor(() => item.Status == DownloadStatus.Completed);

        var second = m.Add(new NewDownload { Url = server.Url("get?id=1"), FileName = probe.FileName }, AddMode.Paused);
        Assert.Equal("report (2).pdf", second.FileName);
    }

    [Fact]
    public async Task Speed_limiter_caps_total_throughput()
    {
        var data = RandomBytes(600_000);
        using var server = new TestServer(data);
        using var m = NewManager(connections: 4);
        var s = m.Settings.Clone();
        s.SpeedLimitEnabled = true;
        s.SpeedLimitKBps = 300;
        m.UpdateSettings(s);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var item = m.Add(new NewDownload { Url = server.Url("slow.bin") }, AddMode.StartNow);
        await WaitFor(() => item.Status is DownloadStatus.Completed or DownloadStatus.Error);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.True(sw.Elapsed.TotalSeconds > 1.2, $"finished too fast: {sw.Elapsed}");
    }

    [Fact]
    public void Auto_extensions_support_wildcards()
    {
        var s = new AppSettings();
        Assert.True(s.IsAutoExtension("movie.MKV"));
        Assert.True(s.IsAutoExtension("archive.r05"));
        Assert.False(s.IsAutoExtension("page.html"));
    }
}
