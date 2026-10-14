using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace FastGet.App;

/// <summary>Message from the browser extension (via the native host) or from a second app instance.</summary>
public sealed class BrowserMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "download";
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("referer")] public string? Referer { get; set; }
    [JsonPropertyName("filename")] public string? FileName { get; set; }
    [JsonPropertyName("cookies")] public string? Cookies { get; set; }
    [JsonPropertyName("userAgent")] public string? UserAgent { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
    [JsonPropertyName("links")] public List<PageLink>? Links { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("streams")] public List<CapturedStream>? Streams { get; set; }
}

/// <summary>A media response the extension saw while the page played a video.</summary>
public sealed class CapturedStream
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    /// <summary>"hls" or "file".</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "file";
    [JsonPropertyName("size")] public long? Size { get; set; }
}

public sealed class PageLink
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("text")] public string? Text { get; set; }
}

public static class Ipc
{
    static readonly string Suffix = Environment.UserName;
    public static readonly string PipeName = "FastGet.DownloadManager." + Suffix;
    public static readonly string MutexName = @"Local\FastGet.DownloadManager";

    public static bool TrySend(BrowserMessage message, bool launchIfMissing)
    {
        var payload = JsonSerializer.Serialize(message);
        if (Send(payload, 1000)) return true;
        if (!launchIfMissing || Environment.ProcessPath is not { } exe) return false;

        // Shell execute so the app does not inherit (and hold open) the browser's stdio pipes.
        Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = true });
        for (int i = 0; i < 20; i++)
        {
            if (Send(payload, 500)) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    static bool Send(string payload, int timeoutMs)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(timeoutMs);
            var bytes = Encoding.UTF8.GetBytes(payload);
            pipe.Write(bytes);
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Receives messages for the running instance. Restricted to the current user's processes.</summary>
public sealed class PipeServer : IDisposable
{
    readonly CancellationTokenSource _cts = new();

    public PipeServer(Action<BrowserMessage> onMessage)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(Ipc.PipeName, PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_cts.Token);
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    var json = await reader.ReadToEndAsync(_cts.Token);
                    var msg = JsonSerializer.Deserialize<BrowserMessage>(json);
                    if (msg != null) onMessage(msg);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is IOException or JsonException) { }
            }
        });
    }

    public void Dispose() => _cts.Cancel();
}

/// <summary>Chrome native messaging: 4-byte little-endian length + UTF-8 JSON, on stdin/stdout.</summary>
static class NativeHost
{
    public static int Run()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        var header = new byte[4];
        while (ReadExactly(input, header))
        {
            int length = BitConverter.ToInt32(header);
            if (length <= 0 || length > 16 * 1024 * 1024) return 1;
            var body = new byte[length];
            if (!ReadExactly(input, body)) return 1;

            bool ok;
            try
            {
                var msg = JsonSerializer.Deserialize<BrowserMessage>(body);
                ok = msg != null && msg.Type == "ping" || msg != null && Ipc.TrySend(msg, launchIfMissing: true);
            }
            catch (JsonException)
            {
                ok = false;
            }
            var reply = Encoding.UTF8.GetBytes(ok ? "{\"ok\":true}" : "{\"ok\":false}");
            output.Write(BitConverter.GetBytes(reply.Length));
            output.Write(reply);
            output.Flush();
        }
        return 0;
    }

    static bool ReadExactly(Stream s, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer, read, buffer.Length - read);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}

/// <summary>Registers this exe as the native messaging host for the bundled extensions (Chromium browsers and Firefox).</summary>
public static class BrowserIntegration
{
    public const string HostName = "com.fastget.host";
    /// <summary>Derived from the public key in extension/chrome/manifest.json, so the unpacked extension always has this ID.</summary>
    public const string ExtensionId = "nomjcihllkmhgcflkembbleomaknglla";
    /// <summary>gecko id from extension/firefox/manifest.json.</summary>
    public const string FirefoxExtensionId = "fastget@fastget.app";

    static readonly string[] RegistryRoots =
    [
        @"Software\Google\Chrome\NativeMessagingHosts",
        @"Software\Microsoft\Edge\NativeMessagingHosts",
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
        @"Software\Chromium\NativeMessagingHosts",
    ];
    const string FirefoxRegistryRoot = @"Software\Mozilla\NativeMessagingHosts";

    public static string ManifestPath => Path.Combine(Program.DataDir, HostName + ".json");
    public static string FirefoxManifestPath => Path.Combine(Program.DataDir, HostName + ".firefox.json");
    public static string ExtensionFolder => Path.Combine(AppContext.BaseDirectory, "extension");

    /// <summary>Chrome starts the host with the extension origin; Firefox with the manifest path and the add-on id.</summary>
    public static bool IsNativeHostLaunch(string[] args) =>
        args.Length > 0 && (args[0].StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
                            || args.Contains(FirefoxExtensionId, StringComparer.OrdinalIgnoreCase));

    public static bool IsInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryRoots[0] + "\\" + HostName);
        return key?.GetValue(null) is string path && File.Exists(path);
    }

    public static void Install()
    {
        var manifest = new
        {
            name = HostName,
            description = "FastGet Download Manager",
            path = Environment.ProcessPath,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" },
        };
        Directory.CreateDirectory(Program.DataDir);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var root in RegistryRoots)
        {
            using var key = Registry.CurrentUser.CreateSubKey(root + "\\" + HostName);
            key.SetValue(null, ManifestPath);
        }

        // Firefox uses the same format but lists add-on ids instead of origins.
        var firefox = new
        {
            name = HostName,
            description = "FastGet Download Manager",
            path = Environment.ProcessPath,
            type = "stdio",
            allowed_extensions = new[] { FirefoxExtensionId },
        };
        File.WriteAllText(FirefoxManifestPath, JsonSerializer.Serialize(firefox, new JsonSerializerOptions { WriteIndented = true }));
        using (var key = Registry.CurrentUser.CreateSubKey(FirefoxRegistryRoot + "\\" + HostName))
            key.SetValue(null, FirefoxManifestPath);
    }

    public static void Uninstall()
    {
        foreach (var root in RegistryRoots.Append(FirefoxRegistryRoot))
            Registry.CurrentUser.DeleteSubKeyTree(root + "\\" + HostName, throwOnMissingSubKey: false);
        File.Delete(ManifestPath);
        File.Delete(FirefoxManifestPath);
    }
}
