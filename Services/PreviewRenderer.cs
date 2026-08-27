using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace KrakenEliteScreenManager.Services;

/// <summary>
/// Renders ONE upright preview frame of the current (possibly unsaved) settings for the
/// GUI's live preview. Owns its own headless Chromium + sensor server (port 9235) so it
/// never touches the LCD or the running service. No rotation — the GUI shows it upright.
/// </summary>
public sealed class PreviewRenderer : IAsyncDisposable
{
    private const int Size = KrakenLcdDriver.Width;
    private const int Port = 9235;

    private WebRenderer? _web;
    private SensorServer? _sensor;
    private string _curTarget = "";

    private string? _gifPath;
    private Image<Rgba32>? _gifFrame0;
    private string? _videoPath;
    private byte[]? _videoPng;
    private readonly HttpClient _serviceSensors = new() { Timeout = TimeSpan.FromMilliseconds(500) };
    private DateTime _coolantStamp;
    private double? _coolantCached;

    /// <summary>Returns PNG/JPEG bytes of the preview, or null if the mode has no preview (Coolant).</summary>
    public async Task<byte[]?> RenderAsync(DashboardMode mode, OverlayStyle style, int dim,
                                           string? gifPath, string webUrl, string videoFile)
    {
        switch (mode)
        {
            case DashboardMode.Dashboard:
                await GotoAsync($"http://localhost:{Port}/");
                return await _web!.CaptureAsync(transparent: false);

            case DashboardMode.WebPage:
                if (string.IsNullOrWhiteSpace(webUrl)) return null;
                await GotoAsync(ServiceRunner.ResolveWebTarget(webUrl));
                return await _web!.CaptureAsync(transparent: false);

            case DashboardMode.GifDashboard:
                await GotoAsync($"http://localhost:{Port}/?transparent=1&style={style.ToString().ToLowerInvariant()}&dim={dim}");
                var overlay = await _web!.CaptureAsync(transparent: true);
                return CompositeGifDash(gifPath, overlay);

            case DashboardMode.GifLoop:
                var f = GifFrame0(gifPath);
                return f is null ? null : ToPng(f);

            case DashboardMode.Video:
                return await VideoFramePngAsync(videoFile);

            default: // Coolant — nothing to preview
                return null;
        }
    }

    private async Task EnsureWebAsync()
    {
        if (_sensor is null)
        {
            var html = Path.Combine(AppContext.BaseDirectory, "assets", "dashboard.html");
            _sensor = new SensorServer(html, new TemperatureService(ReadCoolantFromService), Port);
            _sensor.Start();
        }
        if (_web is null)
        {
            _web = new WebRenderer(Size);
            _curTarget = $"http://localhost:{Port}/";
            await _web.StartAsync(_curTarget);
        }
    }

    private async Task GotoAsync(string target)
    {
        await EnsureWebAsync();
        if (target != _curTarget) { await _web!.GotoAsync(target); _curTarget = target; }
    }

    /// <summary>Decode (and cache) the GIF's first frame, fit to 640² upright (no rotation).</summary>
    private Image<Rgba32>? GifFrame0(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _gifFrame0?.Dispose(); _gifFrame0 = null; _gifPath = null; return null;
        }
        if (path == _gifPath && _gifFrame0 is not null) return _gifFrame0;
        try
        {
            var frames = GifFrames.Load(File.ReadAllBytes(path), Size, 0);
            _gifFrame0?.Dispose();
            _gifFrame0 = frames[0].Image;
            for (int i = 1; i < frames.Count; i++) frames[i].Image.Dispose();
            _gifPath = path;
            return _gifFrame0;
        }
        catch { return null; }
    }

    private byte[] CompositeGifDash(string? gifPath, byte[] overlayPng)
    {
        try
        {
            using var overlay = Image.Load<Rgba32>(overlayPng);
            var bg = GifFrame0(gifPath);
            if (bg is null) return overlayPng; // no GIF yet — show the dashboard alone
            using var canvas = bg.Clone();
            canvas.Mutate(c => c.DrawImage(overlay, 1f));
            return ToPng(canvas);
        }
        catch { return overlayPng; }
    }

    private async Task<byte[]?> VideoFramePngAsync(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return null;
        if (file == _videoPath && _videoPng is not null) return _videoPng; // static — cache per file
        try
        {
            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-i", file, "-frames:v", "1",
                                      "-vf", $"scale={Size}:{Size}:force_original_aspect_ratio=increase,crop={Size}:{Size}",
                                      "-f", "image2pipe", "-vcodec", "png", "-" })
                psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            using var ms = new MemoryStream();
            await proc.StandardOutput.BaseStream.CopyToAsync(ms);
            proc.WaitForExit(4000);
            if (ms.Length == 0) return null;
            _videoPng = ms.ToArray();
            _videoPath = file;
            return _videoPng;
        }
        catch { return null; }
    }

    private static byte[] ToPng(Image img)
    {
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Preview process fallback: re-use coolant from the running service's sensor endpoint.
    /// Cached briefly so we do not block preview on every JSON request.
    /// </summary>
    private double? ReadCoolantFromService()
    {
        if ((DateTime.UtcNow - _coolantStamp).TotalMilliseconds < 1200)
            return _coolantCached;

        try
        {
            var json = _serviceSensors.GetStringAsync("http://localhost:9234/data.json").GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("coolant", out var c) && c.ValueKind == JsonValueKind.Number)
                _coolantCached = c.GetDouble();
        }
        catch
        {
            // Service may be stopped while editing settings; keep previous cached value.
        }

        _coolantStamp = DateTime.UtcNow;
        return _coolantCached;
    }

    public async ValueTask DisposeAsync()
    {
        if (_web is not null) await _web.DisposeAsync();
        _sensor?.Dispose();
        _gifFrame0?.Dispose();
        _serviceSensors.Dispose();
    }
}
