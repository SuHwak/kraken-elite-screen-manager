using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace KrakenEliteScreenManager.Services;

/// <summary>
/// Renders the dashboard HTML to a 640x640 frame with headless Chromium.
/// JPEG capture (fast to encode/decode) for the per-frame loop.
/// </summary>
public sealed class WebRenderer : IAsyncDisposable
{
    // Stable desktop UA so video providers serve standard embed/player behavior.
    private const string DefaultUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36";

    public int Size { get; }

    private IPlaywright? _pw;
    private IBrowser? _browser;
    private IPage? _page;

    public WebRenderer(int size = 640) => Size = size;

    public async Task StartAsync(string target)
    {
        _pw = await Playwright.CreateAsync();
        _browser = await _pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[]
            {
                // Let videos (YouTube embeds, <video>) play without a user gesture.
                "--autoplay-policy=no-user-gesture-required",
                // Keep JS timers (clock/temps polling) running at full rate — headless pages
                // are otherwise treated as "background" and throttled to ~1/min after a few
                // minutes, which freezes the dashboard's clock/temps mid-stream.
                "--disable-background-timer-throttling",
                "--disable-backgrounding-occluded-windows",
                "--disable-renderer-backgrounding",
            },
        });
        _page = await _browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = Size, Height = Size },
            DeviceScaleFactor = 1,
            UserAgent = DefaultUserAgent,
        });
        await GotoAsync(target);
    }

    /// <summary>Re-navigate the existing page to a new target (reuses the browser).</summary>
    public async Task GotoAsync(string target)
    {
        if (_page is null) throw new InvalidOperationException("Renderer not started.");
        var resolved = ResolveTarget(target);
        await ConfigureTargetHeadersAsync(resolved);
        // 'Load' rather than 'NetworkIdle' — streaming/video pages never go idle.
        await _page.GotoAsync(resolved, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 20000 });
    }

    /// <summary>Capture the page. transparent=true → PNG with the page background omitted
    /// (alpha preserved) for compositing over a GIF; otherwise fast opaque JPEG.</summary>
    public async Task<byte[]> CaptureAsync(bool transparent = false)
    {
        if (_page is null) throw new InvalidOperationException("Renderer not started.");
        return await _page.ScreenshotAsync(new PageScreenshotOptions
        {
            Type = transparent ? ScreenshotType.Png : ScreenshotType.Jpeg,
            Quality = transparent ? null : 85,   // Quality is invalid for PNG
            OmitBackground = transparent,
            Clip = new Clip { X = 0, Y = 0, Width = Size, Height = Size },
        });
    }

    private static string ResolveTarget(string target)
    {
        if (target.StartsWith("http://") || target.StartsWith("https://") || target.StartsWith("file://"))
            return target;
        var full = Path.GetFullPath(target);
        if (!File.Exists(full)) throw new FileNotFoundException($"HTML file not found: {full}");
        return new Uri(full).AbsoluteUri;
    }

    private async Task ConfigureTargetHeadersAsync(string target)
    {
        if (_page is null) return;

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            await _page.SetExtraHTTPHeadersAsync(new Dictionary<string, string>());
            return;
        }

        if (IsYouTubeHost(uri.Host))
        {
            await _page.SetExtraHTTPHeadersAsync(new Dictionary<string, string>
            {
                ["Referer"] = "https://www.youtube.com/",
                ["Origin"] = "https://www.youtube.com",
                ["Accept-Language"] = "en-US,en;q=0.9",
            });
            return;
        }

        await _page.SetExtraHTTPHeadersAsync(new Dictionary<string, string>());
    }

    private static bool IsYouTubeHost(string host)
    {
        host = host.ToLowerInvariant();
        return host.Contains("youtube.com")
            || host.Contains("youtube-nocookie.com")
            || host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _pw?.Dispose();
    }
}
