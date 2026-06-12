using Microsoft.Playwright;

namespace DataExtractor.Services;

/// <summary>
/// Fetches pages through headless Chromium. Booking.com fronts its pages with an
/// AWS WAF JavaScript challenge that plain HttpClient requests cannot pass, so the
/// page must be loaded in a real browser engine to reach the listing content.
/// </summary>
public sealed class BrowserPageFetcher : IAsyncDisposable
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task<string> FetchHtmlAsync(string url)
    {
        var browser = await GetBrowserAsync();

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = UserAgent,
            Locale = "en-GB",
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 }
        });

        try
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 45000
            });

            // The WAF challenge page resolves and redirects on its own; wait until
            // listing markup shows up rather than for a fixed delay.
            try
            {
                await page.Locator("meta[property='og:title']").First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Attached,
                    Timeout = 25000
                });
            }
            catch (TimeoutException)
            {
                // Fall through with whatever content loaded; the caller reports
                // missing details to the user.
            }

            // The room/price table hydrates after the head metadata; give it a
            // moment so price extraction doesn't race the rendering.
            try
            {
                await page.Locator("[data-testid='price-and-discounted-price'], .bui-price-display__value, td.totalPrice").First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Attached,
                    Timeout = 8000
                });
            }
            catch (TimeoutException)
            {
                // Not every page shows prices (e.g. no dates in the URL); continue.
            }

            return await page.ContentAsync();
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    /// <summary>
    /// Renders a page (typically the app's own /Print view) to a PDF.
    /// </summary>
    public async Task<byte[]> RenderPdfAsync(string url)
    {
        var browser = await GetBrowserAsync();

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1190, Height = 1684 }
        });

        try
        {
            var page = await context.NewPageAsync();
            // NetworkIdle so listing images have loaded before printing.
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 60000
            });

            return await page.PdfAsync(new PagePdfOptions
            {
                Format = "A4",
                PrintBackground = true,
                Margin = new Margin { Top = "14mm", Bottom = "14mm", Left = "12mm", Right = "12mm" }
            });
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    public static bool LooksLikeBotChallenge(string html) =>
        html.Contains("awsWafCookieDomainList", StringComparison.OrdinalIgnoreCase)
        || html.Contains("challenge-container", StringComparison.OrdinalIgnoreCase);

    private async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser is { IsConnected: true })
            return _browser;

        await _initLock.WaitAsync();
        try
        {
            if (_browser is { IsConnected: true })
                return _browser;

            _playwright ??= await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
            return _browser;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null)
            await _browser.DisposeAsync();

        _playwright?.Dispose();
        _initLock.Dispose();
    }
}
