using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace KrakenEliteScreenManager.Services;

/// <summary>
/// The headless daemon (run via `KrakenEliteScreenManager service`, started by systemd).
/// Applies the configured mode and keeps it up:
///   GifLoop   - upload the GIF once; firmware loops it (no overlay).
///   Dashboard - render dashboard.html with headless Chromium and LIVE-STREAM each
///               frame as raw BGR888 (CAM's asset mode 0x09) — smooth, moving.
///   Coolant   - the built-in liquid-temperature screen.
/// Restores the (upright) coolant screen on clean stop; self-recovers from USB hiccups.
/// </summary>
public static class ServiceRunner
{
    public static async Task<int> RunAsync()
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });

        Log("service starting");

        KrakenLcdDriver? driver = null;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var cfg = DashboardConfig.Load();
                driver?.Dispose();
                driver = new KrakenLcdDriver(Log);
                driver.Open();
                var temps = new TemperatureService(driver.ReadCoolantTemperature);

                bool hasGif = File.Exists(DashboardConfig.GifFile);

                if (cfg.Mode == DashboardMode.GifLoop && hasGif)
                {
                    driver.SetBrightness(cfg.Brightness, orientation: 0);
                    var gif = GifPreparer.MakeLoop(await File.ReadAllBytesAsync(DashboardConfig.GifFile, cts.Token), cfg.Rotation);
                    driver.PushImage(gif);
                    Log("gif-loop active");
                    await Task.Delay(Timeout.Infinite, cts.Token);
                }
                else if (cfg.Mode == DashboardMode.Dashboard)
                {
                    await RunDashboardAsync(driver, temps, cfg, cts.Token); // enters streaming mode itself
                }
                else if (cfg.Mode == DashboardMode.GifDashboard && hasGif)
                {
                    await RunGifDashboardAsync(driver, temps, cfg, cts.Token); // gif + live dashboard overlay
                }
                else if (cfg.Mode == DashboardMode.WebPage && !string.IsNullOrWhiteSpace(cfg.WebUrl))
                {
                    await RunWebPageAsync(driver, temps, cfg, cts.Token); // stream any web page / YouTube loop
                }
                else if (cfg.Mode == DashboardMode.Video && File.Exists(cfg.VideoFile))
                {
                    await RunVideoAsync(driver, cfg, cts.Token); // local video file via ffmpeg
                }
                else
                {
                    if (cfg.Mode is DashboardMode.GifLoop or DashboardMode.GifDashboard)
                        Log("no bg.gif found — showing coolant");
                    driver.SetBrightness(cfg.Brightness, orientation: cfg.Rotation / 90); // upright stock screen
                    driver.ShowLiquid();
                    Log("coolant active");
                    await Task.Delay(Timeout.Infinite, cts.Token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"error: {ex.Message} — resetting device, retry in 5s");
                KrakenLcdDriver.ResetDevice(Log);
                driver?.Dispose();
                driver = null;
                try { await Task.Delay(TimeSpan.FromSeconds(5), cts.Token); } catch { break; }
            }
        }

        // Clean stop: restore panel orientation, then the upright stock coolant screen.
        try
        {
            if (driver is not null)
            {
                var cfg = DashboardConfig.Load();
                driver.SetBrightness(cfg.Brightness, orientation: cfg.Rotation / 90);
                driver.ShowLiquid();
            }
        }
        catch { /* best effort */ }
        driver?.Dispose();
        Log("service stopping");
        return 0;
    }

    private static async Task RunDashboardAsync(KrakenLcdDriver driver, TemperatureService temps, DashboardConfig cfg, CancellationToken ct)
    {
        var html = Path.Combine(AppContext.BaseDirectory, "assets", "dashboard.html");
        using var server = new SensorServer(html, temps);
        server.Start();
        await using var renderer = new WebRenderer(KrakenLcdDriver.Width);
        await renderer.StartAsync(server.Url);

        driver.EnterStreamingMode(cfg.Brightness); // CAM's live path (asset mode 0x09, BGR888)
        Log("dashboard streaming (live)");

        int interval = MinIntervalMs(cfg.MaxFps);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            long start = sw.ElapsedMilliseconds;
            var png = await renderer.CaptureAsync();
            using (var img = Image.Load<Rgba32>(png))
            {
                LcdFrame.Fit(img, KrakenLcdDriver.Width, cfg.Rotation);
                driver.PushFrameRaw(LcdFrame.ToBgr888(img)); // full 640×640 raw frame
                if (PreviewDue()) SavePreview(img, cfg.Rotation);
            }
            int wait = interval - (int)(sw.ElapsedMilliseconds - start);
            if (wait > 0) { try { await Task.Delay(wait, ct); } catch { break; } }
        }
    }

    /// <summary>Stream any web page / YouTube loop, full-frame, at the configured cap.</summary>
    private static async Task RunWebPageAsync(KrakenLcdDriver driver, TemperatureService temps, DashboardConfig cfg, CancellationToken ct)
    {
        // A local HTML file is served through our sensor server so the page can
        // fetch('/data.json') same-origin; a remote URL is loaded directly.
        var input = cfg.WebUrl.Trim();
        bool isYouTube = IsYouTubeUrl(input);

        // Prefer direct media streaming for YouTube when tools are available; this avoids
        // embedded-player policy/codec issues (e.g. Error 152/153 in headless Chromium).
        if (isYouTube && HasCommand("yt-dlp") && HasCommand("ffmpeg"))
        {
            await RunYouTubeFallbackAsync(driver, cfg, input, ct);
            return;
        }

        bool isLocalFile = !input.StartsWith("http", StringComparison.OrdinalIgnoreCase) && File.Exists(input);

        SensorServer? server = null;
        string target;
        if (isLocalFile)
        {
            server = new SensorServer(input, temps);
            server.Start();
            target = server.Url;
        }
        else
        {
            target = ResolveWebTarget(input);
        }

        try
        {
            try
            {
                await using var renderer = new WebRenderer(KrakenLcdDriver.Width);
                await renderer.StartAsync(target);

                driver.EnterStreamingMode(cfg.Brightness);
                Log($"web-page streaming (live): {cfg.WebUrl}");

                int interval = MinIntervalMs(cfg.MaxFps);
                var sw = Stopwatch.StartNew();
                while (!ct.IsCancellationRequested)
                {
                    long start = sw.ElapsedMilliseconds;
                    var png = await renderer.CaptureAsync();
                    using (var img = Image.Load<Rgba32>(png))
                    {
                        LcdFrame.Fit(img, KrakenLcdDriver.Width, cfg.Rotation);
                        driver.PushFrameRaw(LcdFrame.ToBgr888(img));
                        if (PreviewDue()) SavePreview(img, cfg.Rotation);
                    }
                    int wait = interval - (int)(sw.ElapsedMilliseconds - start);
                    if (wait > 0) { try { await Task.Delay(wait, ct); } catch { break; } }
                }
            }
            catch when (isYouTube)
            {
                if (!HasCommand("yt-dlp") || !HasCommand("ffmpeg")) throw;

                Log("web-page embed failed for YouTube; falling back to yt-dlp + ffmpeg");
                await RunYouTubeFallbackAsync(driver, cfg, input, ct);
            }
        }
        finally { server?.Dispose(); }
    }

    /// <summary>Stream a local video file, decoded + looped + scaled to 640² BGR888 by ffmpeg.</summary>
    private static async Task RunVideoAsync(KrakenLcdDriver driver, DashboardConfig cfg, CancellationToken ct)
    {
        int size = KrakenLcdDriver.Width, frameBytes = size * size * 3;
        string vf = $"scale={size}:{size}:force_original_aspect_ratio=increase,crop={size}:{size}{RotateFilter(cfg.Rotation)}";

        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-stream_loop", "-1", "-i", cfg.VideoFile,
                                  "-vf", vf, "-pix_fmt", "bgr24", "-f", "rawvideo" })
            psi.ArgumentList.Add(a);
        if (cfg.MaxFps > 0) { psi.ArgumentList.Add("-r"); psi.ArgumentList.Add(cfg.MaxFps.ToString()); }
        psi.ArgumentList.Add("-");

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("ffmpeg not found — install it (e.g. sudo dnf install ffmpeg).");

        driver.EnterStreamingMode(cfg.Brightness);
        Log($"video streaming (live): {Path.GetFileName(cfg.VideoFile)}");

        var stream = proc.StandardOutput.BaseStream;
        var buf = new byte[frameBytes];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int off = 0;
                while (off < frameBytes)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(off, frameBytes - off), ct);
                    if (n <= 0) { off = -1; break; }
                    off += n;
                }
                if (off < 0) throw new InvalidOperationException("ffmpeg stream ended (bad file or decode error).");
                driver.PushFrameRaw(buf);
                if (PreviewDue())
                {
                    using var raw = Image.LoadPixelData<Bgr24>(buf, size, size);
                    using var p = raw.CloneAs<Rgba32>();
                    SavePreview(p, cfg.Rotation);
                }
            }
        }
        finally { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } }
    }

    /// <summary>ffmpeg transpose filter for a clockwise rotation in 90° steps.</summary>
    private static string RotateFilter(int degrees) => (((degrees % 360) + 360) % 360) switch
    {
        90 => ",transpose=1",
        180 => ",transpose=1,transpose=1",
        270 => ",transpose=2",
        _ => "",
    };

    /// <summary>
    /// YouTube fallback path: resolve a direct media URL with yt-dlp and stream frames via ffmpeg.
    /// This avoids browser codec/embed policy problems.
    /// </summary>
    private static async Task RunYouTubeFallbackAsync(KrakenLcdDriver driver, DashboardConfig cfg, string youtubeUrl, CancellationToken ct)
    {
        int size = KrakenLcdDriver.Width, frameBytes = size * size * 3;
        string vf = $"scale={size}:{size}:force_original_aspect_ratio=increase,crop={size}:{size}{RotateFilter(cfg.Rotation)}";

        driver.EnterStreamingMode(cfg.Brightness);
        Log("youtube fallback streaming (yt-dlp + ffmpeg)");

        while (!ct.IsCancellationRequested)
        {
            var mediaUrl = await ResolveYouTubeMediaUrlAsync(youtubeUrl, ct)
                ?? throw new InvalidOperationException("yt-dlp could not resolve a playable media URL.");

            var psi = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var a in new[]
                     {
                         "-hide_banner", "-loglevel", "error",
                         "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_delay_max", "2",
                         "-i", mediaUrl,
                         "-vf", vf,
                         "-pix_fmt", "bgr24",
                         "-f", "rawvideo",
                     })
                psi.ArgumentList.Add(a);
            if (cfg.MaxFps > 0) { psi.ArgumentList.Add("-r"); psi.ArgumentList.Add(cfg.MaxFps.ToString()); }
            psi.ArgumentList.Add("-");

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("ffmpeg not found — install it (e.g. sudo dnf install ffmpeg). ");

            var stream = proc.StandardOutput.BaseStream;
            var buf = new byte[frameBytes];

            try
            {
                bool gotFrame = false;
                while (!ct.IsCancellationRequested)
                {
                    int off = 0;
                    while (off < frameBytes)
                    {
                        int n = await stream.ReadAsync(buf.AsMemory(off, frameBytes - off), ct);
                        if (n <= 0) { off = -1; break; }
                        off += n;
                    }

                    if (off < 0) break;
                    gotFrame = true;
                    driver.PushFrameRaw(buf);
                    if (PreviewDue())
                    {
                        using var raw = Image.LoadPixelData<Bgr24>(buf, size, size);
                        using var p = raw.CloneAs<Rgba32>();
                        SavePreview(p, cfg.Rotation);
                    }
                }

                if (!gotFrame)
                    throw new InvalidOperationException("ffmpeg received no frames from YouTube media URL.");
            }
            finally
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            }

            // Direct media URLs expire; re-resolve and continue to simulate looping behavior.
            if (!ct.IsCancellationRequested)
                Log("youtube fallback: stream ended, resolving next media URL");
        }
    }

    private static bool HasCommand(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("which")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(name);
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(1500);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<string?> ResolveYouTubeMediaUrlAsync(string url, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("yt-dlp")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var a in new[]
                 {
                     "--no-playlist",
                     "-f", "bv*[height<=1080]/bv*/b[height<=1080]/b/best",
                     "-g",
                     url,
                 })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("yt-dlp not found — install yt-dlp for YouTube fallback.");

        string stdout = await proc.StandardOutput.ReadToEndAsync();
        string stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp failed: {stderr.Trim()}");

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return line;
        }

        return null;
    }

    /// <summary>Min ms between frames for a target fps; 0 fps = uncapped (0 ms).</summary>
    private static int MinIntervalMs(int maxFps) => maxFps > 0 ? Math.Max(1, 1000 / maxFps) : 0;

    // --- live preview: write the on-screen frame to a file the GUI can show ---
    private static DateTime _previewStamp;
    internal static string PreviewPath => Path.Combine(DashboardConfig.Dir, "preview.png");

    /// <summary>True at most ~twice a second — gate preview writes off the hot loop.</summary>
    private static bool PreviewDue()
    {
        var now = DateTime.UtcNow;
        if ((now - _previewStamp).TotalMilliseconds < 500) return false;
        _previewStamp = now;
        return true;
    }

    /// <summary>Atomically write the on-screen frame as an UPRIGHT preview.png for the GUI
    /// (un-rotates the device rotation so the GUI can show it with no transform).</summary>
    private static void SavePreview(Image<Rgba32> frame, int deviceRotation)
    {
        try
        {
            Directory.CreateDirectory(DashboardConfig.Dir);
            using var up = frame.Clone();
            int back = (360 - (deviceRotation % 360)) % 360;
            if (back != 0) up.Mutate(c => c.Rotate(back));
            var tmp = PreviewPath + ".tmp";
            up.SaveAsPng(tmp);
            File.Move(tmp, PreviewPath, overwrite: true);
        }
        catch { /* preview is best-effort */ }
    }

    /// <summary>Turn a YouTube watch/short link into a muted, looping, chrome-less embed; pass anything else through.</summary>
    internal static string ResolveWebTarget(string url)
    {
        url = url.Trim();
        string? id = null;
        var m = System.Text.RegularExpressions.Regex.Match(url,
            @"(?:youtube\.com/(?:watch\?v=|embed/|shorts/)|youtu\.be/)([A-Za-z0-9_-]{11})");
        if (m.Success) id = m.Groups[1].Value;
        if (id is null) return url; // plain URL or local .html — WebRenderer resolves file paths
         return $"https://www.youtube.com/embed/{id}" +
             $"?autoplay=1&mute=1&loop=1&playlist={id}&controls=0&modestbranding=1&playsinline=1&rel=0";
    }

    private static bool IsYouTubeUrl(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url,
            @"(?:youtube\.com/(?:watch\?v=|embed/|shorts/)|youtu\.be/)([A-Za-z0-9_-]{11})");
        return m.Success;
    }

    /// <summary>
    /// GIF + Dashboard: animate the GIF frame-by-frame and composite the live dashboard
    /// (transparent background) on top of each frame, streaming the result (asset mode 0x09).
    /// The dashboard overlay is re-rendered on a slow background cadence; the GIF advances
    /// every frame, so playback stays smooth without paying the Chromium cost per frame.
    /// </summary>
    private static async Task RunGifDashboardAsync(KrakenLcdDriver driver, TemperatureService temps, DashboardConfig cfg, CancellationToken ct)
    {
        var html = Path.Combine(AppContext.BaseDirectory, "assets", "dashboard.html");
        var frames = GifFrames.Load(await File.ReadAllBytesAsync(DashboardConfig.GifFile, ct), KrakenLcdDriver.Width, cfg.Rotation);

        using var server = new SensorServer(html, temps);
        server.Start();
        await using var renderer = new WebRenderer(KrakenLcdDriver.Width);
        var url = $"{server.Url}?transparent=1&style={cfg.OverlayStyle.ToString().ToLowerInvariant()}&dim={cfg.OverlayDim}";
        await renderer.StartAsync(url);

        // Cache the transparent dashboard overlay; refresh it slowly in the background.
        var gate = new object();
        var overlay = await CaptureOverlayAsync(renderer, cfg.Rotation);
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var refresher = Task.Run(async () =>
        {
            while (!refresh.IsCancellationRequested)
            {
                try { await Task.Delay(1000, refresh.Token); } catch { break; }
                try
                {
                    var next = await CaptureOverlayAsync(renderer, cfg.Rotation);
                    lock (gate) { var old = overlay; overlay = next; old.Dispose(); }
                }
                catch { /* keep the last good overlay */ }
            }
        });

        driver.EnterStreamingMode(cfg.Brightness);
        Log("gif+dashboard streaming (live)");

        int interval = MinIntervalMs(cfg.MaxFps);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int idx = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (gifFrame, delayMs) = frames[idx];
                long start = sw.ElapsedMilliseconds;

                using (var composite = gifFrame.Clone())
                {
                    lock (gate) composite.Mutate(c => c.DrawImage(overlay, 1f));
                    driver.PushFrameRaw(LcdFrame.ToBgr888(composite));
                    if (PreviewDue()) SavePreview(composite, cfg.Rotation);
                }

                idx = (idx + 1) % frames.Count;
                // honour the GIF's own frame delay, but never faster than the fps cap
                int wait = Math.Max(delayMs, interval) - (int)(sw.ElapsedMilliseconds - start);
                if (wait > 0) { try { await Task.Delay(wait, ct); } catch { break; } }
            }
        }
        finally
        {
            refresh.Cancel();
            try { await refresher; } catch { }
            lock (gate) overlay.Dispose();
            foreach (var f in frames) f.Image.Dispose();
        }
    }

    private static async Task<SixLabors.ImageSharp.Image<Rgba32>> CaptureOverlayAsync(WebRenderer renderer, int rotation)
    {
        var img = Image.Load<Rgba32>(await renderer.CaptureAsync(transparent: true));
        LcdFrame.Fit(img, KrakenLcdDriver.Width, rotation);
        return img;
    }

    private static void Log(string m) => Console.WriteLine($"[kraken] {m}");
}
