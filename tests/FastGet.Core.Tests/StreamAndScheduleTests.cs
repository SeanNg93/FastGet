using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace FastGet.Core.Tests;

public sealed class StreamAndScheduleTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "fastget-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    DownloadManager NewManager()
    {
        var m = new DownloadManager(Path.Combine(_dir, "data"));
        var s = m.Settings.Clone();
        s.Connections = 4;
        s.MaxRetries = 1;
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

    static byte[] Bytes(int seed, int size)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Fact]
    public void Master_playlist_lists_variants_best_first()
    {
        const string master = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="en",URI="audio/en.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS="avc1.4d401e,mp4a.40.2"
            low/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,AUDIO="aud"
            https://cdn.example.com/hd/index.m3u8
            """;
        var variants = Hls.ParseMaster(master, new Uri("https://site.example/v/master.m3u8"));

        Assert.Equal(2, variants.Count);
        Assert.Equal("1920x1080", variants[0].Resolution);
        Assert.True(variants[0].SeparateAudio);
        Assert.Equal("https://site.example/v/low/index.m3u8", variants[1].Uri.ToString());
        Assert.Equal("avc1.4d401e,mp4a.40.2", variants[1].Codecs);
    }

    [Fact]
    public void Sample_aes_streams_are_reported_as_protected()
    {
        const string media = "#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://key\"\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n";
        Assert.Throws<NotSupportedException>(() => Hls.ParseMedia(media, new Uri("https://x.example/p.m3u8")));
    }

    [Fact]
    public async Task Hls_segments_are_downloaded_in_parallel_decrypted_and_joined_in_order()
    {
        var key = Bytes(1, 16);
        var plain = Enumerable.Range(0, 12).Select(i => Bytes(100 + i, 50_000 + i * 997)).ToList();
        var routes = new Dictionary<string, byte[]> { ["/key.bin"] = key };
        var playlist = new StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:7\n");
        for (int i = 0; i < plain.Count; i++)
        {
            // First half uses the sequence number as IV, second half an explicit IV.
            byte[]? iv = i < 6 ? null : Bytes(200 + i, 16);
            if (i == 0) playlist.Append("#EXT-X-KEY:METHOD=AES-128,URI=\"/key.bin\"\n");
            if (i >= 6) playlist.Append($"#EXT-X-KEY:METHOD=AES-128,URI=\"/key.bin\",IV=0x{Convert.ToHexString(iv!)}\n");
            var ivBytes = iv ?? new byte[16];
            if (iv == null) System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(ivBytes.AsSpan(8), 7 + i);
            using var aes = Aes.Create();
            aes.Key = key;
            routes[$"/seg{i}.ts"] = aes.EncryptCbc(plain[i], ivBytes, PaddingMode.PKCS7);
            playlist.Append($"#EXTINF:4.0,\nseg{i}.ts\n");
        }
        playlist.Append("#EXT-X-ENDLIST\n");
        routes["/video/index.m3u8"] = Encoding.UTF8.GetBytes(playlist.ToString().Replace("seg", "/seg"));
        routes["/master.m3u8"] = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=1280x720\n/video/index.m3u8\n");
        using var server = new TestServer(routes);
        using var m = NewManager();

        var options = await m.InspectStreamAsync(server.Url("master.m3u8"), null, null, null, CancellationToken.None);
        var option = Assert.Single(options);
        Assert.Equal(DownloadKind.Hls, option.Kind);
        Assert.Contains("1280x720", option.Label);

        var item = m.Add(new NewDownload { Kind = DownloadKind.Hls, Url = option.Url, FileName = "clip.ts" }, AddMode.StartNow);
        Assert.Equal("Video", item.Category);
        await WaitFor(() => item.Status is DownloadStatus.Completed or DownloadStatus.Error);

        Assert.Null(item.Error);
        Assert.Equal(12, item.StreamDone);
        Assert.Equal(plain.SelectMany(p => p).ToArray(), File.ReadAllBytes(item.FullPath));
        Assert.False(Directory.Exists(item.SegmentsPath));
    }

    [Fact]
    public async Task Fmp4_stream_with_byte_ranges_becomes_mp4_with_init_segment_first()
    {
        var init = Bytes(5, 1000);
        var body = Bytes(6, 90_000);
        var routes = new Dictionary<string, byte[]>
        {
            ["/media.mp4"] = init.Concat(body).ToArray(),
            ["/index.m3u8"] = Encoding.UTF8.GetBytes(
                "#EXTM3U\n#EXT-X-MAP:URI=\"media.mp4\",BYTERANGE=\"1000@0\"\n" +
                "#EXTINF:2,\n#EXT-X-BYTERANGE:40000@1000\nmedia.mp4\n" +
                "#EXTINF:2,\n#EXT-X-BYTERANGE:50000\nmedia.mp4\n#EXT-X-ENDLIST\n"),
        };
        using var server = new TestServer(routes);
        using var m = NewManager();

        var item = m.Add(new NewDownload { Kind = DownloadKind.Hls, Url = server.Url("index.m3u8"), FileName = "movie.ts" }, AddMode.StartNow);
        await WaitFor(() => item.Status is DownloadStatus.Completed or DownloadStatus.Error);

        Assert.Null(item.Error);
        Assert.Equal("movie.mp4", item.FileName);
        Assert.Equal(init.Concat(body).ToArray(), File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public void Scheduler_starts_once_per_scheduled_day_and_stops_at_stop_time()
    {
        using var m = NewManager();
        var s = m.Settings.Clone();
        s.Schedule.StartEnabled = true;
        s.Schedule.StartTime = "02:00";
        s.Schedule.Daily = true;
        s.Schedule.Days = [DayOfWeek.Monday];
        s.Schedule.StopEnabled = true;
        s.Schedule.StopTime = "06:30";
        m.UpdateSettings(s);
        m.Add(new NewDownload { Url = "http://127.0.0.1:1/never.bin" }, AddMode.Later); // something to run
        using var scheduler = new QueueScheduler(m, autoTick: false);
        int starts = 0;
        scheduler.Started += () => starts++;

        var monday = new DateTime(2026, 10, 12); // a Monday
        scheduler.Tick(monday.AddHours(1.9));
        Assert.Equal(0, starts);
        scheduler.Tick(monday.AddHours(2).AddMinutes(1));
        Assert.Equal(1, starts);
        Assert.True(m.QueueRunning || starts == 1);
        scheduler.Tick(monday.AddHours(2).AddMinutes(5));
        Assert.Equal(1, starts); // not re-fired within the same slot

        scheduler.Tick(monday.AddHours(6.5).AddMinutes(1));
        Assert.False(m.QueueRunning);

        scheduler.Tick(monday.AddDays(1).AddHours(2).AddMinutes(1)); // Tuesday is not selected
        Assert.Equal(1, starts);
        Assert.Equal(monday.AddDays(7).AddHours(2), scheduler.NextStart(monday.AddHours(3)));
    }

    [Fact]
    public void One_time_schedule_disables_itself_after_firing()
    {
        using var m = NewManager();
        var s = m.Settings.Clone();
        s.Schedule.StartEnabled = true;
        s.Schedule.Daily = false;
        s.Schedule.OnceDate = "2026-11-03";
        s.Schedule.StartTime = "23:50";
        m.UpdateSettings(s);
        using var scheduler = new QueueScheduler(m, autoTick: false);
        int starts = 0;
        scheduler.Started += () => starts++;

        scheduler.Tick(new DateTime(2026, 11, 2, 23, 55, 0));
        Assert.Equal(0, starts);
        scheduler.Tick(new DateTime(2026, 11, 4, 0, 5, 0)); // just after midnight, still inside the grace window
        Assert.Equal(1, starts);
        Assert.False(m.Settings.Schedule.StartEnabled);
    }

    [Fact]
    public async Task Queue_completed_fires_once_after_all_queued_files_finish_but_not_for_an_empty_queue()
    {
        using var server = new TestServer(Bytes(9, 200_000));
        using var m = NewManager();
        int completed = 0;
        m.QueueCompleted += () => Interlocked.Increment(ref completed);

        m.StartQueue(); // empty: must not count as "finished processing"
        await Task.Delay(200);
        Assert.Equal(0, completed);

        var a = m.Add(new NewDownload { Url = server.Url("a.bin") }, AddMode.Later);
        var b = m.Add(new NewDownload { Url = server.Url("b.bin") }, AddMode.Later);
        m.StartQueue();
        await WaitFor(() => completed > 0);
        Assert.Equal(DownloadStatus.Completed, a.Status);
        Assert.Equal(DownloadStatus.Completed, b.Status);
        await Task.Delay(300);
        Assert.Equal(1, completed);
    }
}
