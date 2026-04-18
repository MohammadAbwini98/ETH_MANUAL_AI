using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using EthSignal.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace EthSignal.Infrastructure.Apis;

/// <summary>
/// Scrapes live bid/ask prices from the Capital.com trading platform UI
/// using Playwright. Target: ~5 ticks/sec (200 ms polling interval).
///
/// SELECTORS: Verified against Capital.com trading platform (April 2026).
/// Update the constants in PlaywrightSelectors if the UI changes.
/// Use F12 → Elements → Ctrl+F to find replacements.
/// </summary>
public sealed class PlaywrightTickProvider : ITickProvider
{
    // ── Tuning constants ────────────────────────────────────────────────
    private const int PollIntervalMs = 200;   // target 5 Hz
    private const int ChannelCapacity = 500;   // ~1.6 min of ticks at 5 Hz
    private const int LoginPollSec = 5;     // poll for login every N sec
    private const int NavigateTimeoutMs = 60_000;
    private const int ElementTimeoutMs = 15_000;
    private const int PostNavigateDelayMs = 5_000; // wait for SPA to fully render
    private static readonly string[] BrowserArgs =
    [
        "--disable-blink-features=AutomationControlled",
        "--disable-infobars",
        "--no-first-run",
        "--no-default-browser-check",
        "--start-maximized",
        "--foreground"
    ];

    // ── Verified selectors ─────────────────────────────────────────────
    private static class Sel
    {
        // Confirmed live in DOM (April 2026):
        //   <input placeholder="Search" class="searchbar-text-field" />
        // Extra fallbacks cover Capital.com UI variants across layout updates.
        public const string SearchInput =
            "input[placeholder='Search'], " +
            "input.searchbar-text-field, " +
            "input[type='search'], " +
            "input[placeholder*='search' i], " +
            "[class*='search'] input, " +
            "[class*='Search'] input, " +
            "[data-testid*='search'] input";

        // Buttons/icons that *reveal* the search input when clicked.
        // Capital.com hides the text field behind a magnifying-glass trigger on
        // some layouts; clicking any of these should make SearchInput appear.
        public const string SearchTrigger =
            "button[aria-label*='search' i], " +
            "button[data-testid*='search' i], " +
            "[class*='search-icon'], " +
            "[class*='searchIcon'], " +
            "[class*='SearchIcon'], " +
            "[class*='search-trigger'], " +
            "[class*='SearchTrigger']";

        // Account balance — any of these indicates a logged-in session
        public const string AccountBalance =
            "[class*='account-balance'], [class*='equity'], [class*='availableFunds']";

        // Login button (presence = not logged in)
        public const string LoginButton =
            "button:has-text('Log in'), button:has-text('Login'), a:has-text('Log in'), a:has-text('Login'), [class*='login-btn']";

        // Price extraction JS — returns { bid, ask, sellPath, buyPath } or null.
        // Tries structured selectors first, then a precise row-scoped fallback that
        // walks the rendered Sell/Buy buttons in Capital.com's market list and reads
        // the numeric child node next to the label.
        public const string PriceExtractJs = @"() => {
            const NUM_RE = /([0-9]{1,3}(?:,[0-9]{3})*(?:\.[0-9]+)?)/;

            function parseNumber(text) {
                if (!text) return null;
                const m = String(text).replace(/\u00A0/g, ' ').match(NUM_RE);
                if (!m) return null;
                const n = parseFloat(m[1].replace(/,/g, ''));
                return (!isNaN(n) && n > 0) ? n : null;
            }

            // Try a list of CSS selectors, return { value, selector } on first hit
            function tryCss(side, sels) {
                for (const s of sels) {
                    const el = document.querySelector(s);
                    if (!el) continue;
                    const v = parseNumber(el.innerText || el.textContent || '');
                    if (v !== null) return { value: v, path: 'css:' + s };
                }
                return null;
            }

            // Row-scoped scan: find a row containing the active instrument label,
            // then read the Sell and Buy cells inside that row. This is much more
            // reliable than the page-wide button scan because it can't pick up the
            // wrong instrument's price when several markets are visible.
            function rowScopedQuotes() {
                const rows = Array.from(document.querySelectorAll(
                    'tr, [role=row], [class*=row], [class*=Row], li'));
                for (const row of rows) {
                    const txt = (row.innerText || row.textContent || '');
                    if (!/Ethereum|ETH\/USD/i.test(txt)) continue;

                    // Find sell and buy candidate elements inside the row
                    const cells = Array.from(row.querySelectorAll(
                        'button, [role=button], [class*=sell], [class*=buy], [class*=Sell], [class*=Buy]'));
                    let sell = null, buy = null;
                    for (const c of cells) {
                        const ct = (c.innerText || c.textContent || '');
                        const isSell = /sell/i.test(ct) || /sell/i.test(c.className || '');
                        const isBuy  = /buy/i.test(ct)  || /buy/i.test(c.className || '');
                        if (!isSell && !isBuy) continue;
                        const v = parseNumber(ct);
                        if (v === null) continue;
                        if (isSell && sell === null) sell = v;
                        else if (isBuy && buy === null) buy = v;
                        if (sell !== null && buy !== null) break;
                    }
                    if (sell !== null && buy !== null) {
                        return { sell: sell, buy: buy, path: 'row-scoped' };
                    }
                }
                return null;
            }

            // Page-wide button scan (last resort): walk every clickable Sell/Buy
            // button and pick the first numeric value. Risk: may pick the wrong
            // instrument if Ethereum is not the focused row.
            function buttonScan(label) {
                const nodes = Array.from(document.querySelectorAll('button, [role=button], a'));
                for (const n of nodes) {
                    const t = ((n.innerText || n.textContent || '') + '').trim();
                    if (!t || !new RegExp(label, 'i').test(t)) continue;
                    const v = parseNumber(t);
                    if (v !== null) return v;
                }
                return null;
            }

            const sellCssCandidates = [
                ""[data-testid='sell-price']"",
                ""[data-testid*='sell']"",
                ""[aria-label*='Sell']"",
                "".sell-price"",
                ""[class*='sellPrice']"",
                ""[class*='sell-price']"",
                ""[class*='SellPrice']"",
                ""[class*='deal-ticket'] [class*='sell']"",
                ""[class*='sell-btn'] [class*='price']""
            ];
            const buyCssCandidates = [
                ""[data-testid='buy-price']"",
                ""[data-testid*='buy']"",
                ""[aria-label*='Buy']"",
                "".buy-price"",
                ""[class*='buyPrice']"",
                ""[class*='buy-price']"",
                ""[class*='BuyPrice']"",
                ""[class*='deal-ticket'] [class*='buy']"",
                ""[class*='buy-btn'] [class*='price']""
            ];

            const sellCss = tryCss('sell', sellCssCandidates);
            const buyCss = tryCss('buy', buyCssCandidates);

            let sell = sellCss ? sellCss.value : null;
            let buy = buyCss ? buyCss.value : null;
            let sellPath = sellCss ? sellCss.path : null;
            let buyPath = buyCss ? buyCss.path : null;

            if (sell === null || buy === null) {
                const row = rowScopedQuotes();
                if (row) {
                    if (sell === null) { sell = row.sell; sellPath = row.path; }
                    if (buy  === null) { buy  = row.buy;  buyPath  = row.path; }
                }
            }
            // T3-4: Instrument validation gate — only use button-scan as last resort when
            // the page is confirmed to be showing Ethereum/ETH/USD. Without this check,
            // buttonScan could pick up prices from a different visible instrument.
            const pageBodyText = (document.body && document.body.innerText ? document.body.innerText : '') + document.title;
            const onEthPage = /Ethereum|ETH[\s\/]?USD/i.test(pageBodyText);

            if (sell === null && onEthPage) {
                const v = buttonScan('sell');
                if (v !== null) { sell = v; sellPath = 'button-scan'; }
            }
            if (buy === null && onEthPage) {
                const v = buttonScan('buy');
                if (v !== null) { buy = v; buyPath = 'button-scan'; }
            }

            if (sell === null || buy === null) return null;
            // Sanity: buy must be >= sell (spread can be 0 but never negative)
            if (buy < sell) return null;

            return { bid: sell, ask: buy, sellPath: sellPath, buyPath: buyPath, instrumentValidated: onEthPage };
        }";

        // Selector probe: deep DOM scan that returns all candidate selectors carrying
        // numeric Sell/Buy text. Used by the admin probe endpoint so the operator can
        // discover live selectors without restarting the app or leaving the session.
        public const string SelectorProbeJs = @"() => {
            function describe(el) {
                if (!el) return null;
                const cls = (el.className && typeof el.className === 'string') ? el.className : '';
                const id = el.id || '';
                return {
                    tag: el.tagName.toLowerCase(),
                    id: id,
                    classes: cls,
                    testId: el.getAttribute('data-testid') || '',
                    ariaLabel: el.getAttribute('aria-label') || '',
                    text: ((el.innerText || el.textContent || '') + '').trim().slice(0, 80)
                };
            }

            function scan(label) {
                const out = [];
                const all = document.querySelectorAll('*');
                for (const el of all) {
                    const txt = ((el.innerText || el.textContent || '') + '').trim();
                    if (!txt || txt.length > 60) continue;
                    if (!new RegExp(label, 'i').test(txt)) continue;
                    const m = txt.match(/([0-9]{1,3}(?:,[0-9]{3})*(?:\.[0-9]+)?)/);
                    if (!m) continue;
                    out.push(describe(el));
                    if (out.length >= 25) break;
                }
                return out;
            }

            return {
                url: location.href,
                title: document.title,
                bodyTextLen: (document.body && document.body.innerText ? document.body.innerText.length : 0),
                sellCandidates: scan('sell'),
                buyCandidates: scan('buy')
            };
        }";
    }

    private readonly ILogger<PlaywrightTickProvider> _logger;
    private volatile bool _headless;
    private readonly string _browserChannel;
    private readonly string _userDataDir;
    private readonly string _storageStatePath;
    private readonly int _manualLoginTimeoutSec;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;
    private string? _epicSearchTerm; // e.g. "Ethereum" derived from epic "ETHUSD"
    private string? _currentEpic;
    private volatile bool _scrapeLoopStarted;
    private volatile bool _sessionReloadInProgress;

    private readonly Channel<SpotPrice> _channel =
        Channel.CreateBounded<SpotPrice>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = false
        });

    // Health & metrics
    private volatile bool _healthy = false;
    private DateTimeOffset _windowStart = DateTimeOffset.UtcNow;
    private double _tickRateHz = 0;
    private double _changeRateHz = 0;
    private long _changesInWindow = 0;
    private DateTimeOffset _lastPriceChangeTime = DateTimeOffset.MinValue;

    public TickProviderKind Kind => TickProviderKind.Playwright;
    public bool IsHealthy => _healthy;
    public double TickRateHz => _tickRateHz;
    public double ChangeRateHz => _changeRateHz;
    public DateTimeOffset LastPriceChangeTime => _lastPriceChangeTime;
    public bool Headless => _headless;

    public PlaywrightTickProvider(
        ILogger<PlaywrightTickProvider> logger,
        bool headless = true,
        string browserChannel = "chrome",
        string? userDataDir = null,
        int manualLoginTimeoutSec = 180)
    {
        _logger = logger;
        _headless = headless;
        _browserChannel = string.IsNullOrWhiteSpace(browserChannel)
            ? "chrome"
            : browserChannel.Trim();
        _userDataDir = ResolvePersistentDir(userDataDir);
        _storageStatePath = Path.Combine(_userDataDir, "capital-storage-state.json");
        _manualLoginTimeoutSec = Math.Max(30, manualLoginTimeoutSec);
    }

    // ── StartAsync ─────────────────────────────────────────────────────
    public async Task StartAsync(string epic, CancellationToken ct)
    {
        _currentEpic = epic;
        _epicSearchTerm = DeriveSearchTerm(epic);

        await _sessionGate.WaitAsync(ct);
        try
        {
            if (_page == null || _context == null)
            {
                await InitializeBrowserSessionAsync(ct);
            }

            if (!_scrapeLoopStarted)
            {
                _scrapeLoopStarted = true;
                _ = Task.Run(() => ScrapeLoopAsync(ct), ct);
                _logger.LogInformation("[Playwright] Price scraping loop started");
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<bool> SetHeadlessModeAsync(bool headless, CancellationToken ct = default)
    {
        var changed = _headless != headless;
        _headless = headless;

        if (!changed)
            return false;

        _logger.LogInformation("[Playwright] Updating browser mode live: headless={Headless}", headless);

        if (string.IsNullOrWhiteSpace(_currentEpic))
            return true;

        await _sessionGate.WaitAsync(ct);
        try
        {
            await RestartBrowserSessionAsync(ct);
        }
        finally
        {
            _sessionGate.Release();
        }

        return true;
    }

    private async Task InitializeBrowserSessionAsync(CancellationToken ct)
    {
        _sessionReloadInProgress = true;
        try
        {
            _logger.LogInformation("[Playwright] Starting for epic={Epic} searchTerm={Term}",
                _currentEpic, _epicSearchTerm);

            Directory.CreateDirectory(_userDataDir);
            _logger.LogInformation(
                "[Playwright] Using persistent profile dir={UserDataDir} channel={Channel} headless={Headless}",
                _userDataDir, _browserChannel, _headless);
            _logger.LogInformation(
                "[Playwright] Existing storage snapshot found={HasState} path={StoragePath}",
                File.Exists(_storageStatePath), _storageStatePath);

            _playwright ??= await Playwright.CreateAsync();
            _context = await LaunchPersistentContextAsync();
            _page = _context.Pages.FirstOrDefault() ?? await _context.NewPageAsync();
            await ApplyStealthInitAsync(_page);

            await _page.GotoAsync("https://capital.com/trading/platform/trade",
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.Load,
                    Timeout = NavigateTimeoutMs
                });

            await Task.Delay(PostNavigateDelayMs, ct);
            _logger.LogInformation("[Playwright] Page loaded, checking login state...");

            await EnsureLoggedInAsync(ct);
            await WaitForPlatformReadyAsync(ct);

            if (!_page.Url.Contains("/trading/platform"))
            {
                _logger.LogWarning(
                    "[Playwright] Post-SPA URL is {Url} — session likely expired, re-checking login",
                    _page.Url);

                await _page.GotoAsync("https://capital.com/trading/platform/trade",
                    new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.Load,
                        Timeout = NavigateTimeoutMs
                    });
                await Task.Delay(PostNavigateDelayMs, ct);
                await EnsureLoggedInAsync(ct);
                await WaitForPlatformReadyAsync(ct);

                if (!_page.Url.Contains("/trading/platform"))
                {
                    throw new InvalidOperationException(
                        $"[Playwright] Failed to reach trading platform after re-login. Final URL: {_page.Url}");
                }
            }

            await OpenInstrumentAsync(ct);
            _healthy = false;
        }
        finally
        {
            _sessionReloadInProgress = false;
        }
    }

    private async Task RestartBrowserSessionAsync(CancellationToken ct)
    {
        _healthy = false;
        await DisposeBrowserSessionAsync();
        await Task.Delay(350, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await InitializeBrowserSessionAsync(ct);
    }

    // ── ReadAllAsync ────────────────────────────────────────────────────
    public async IAsyncEnumerable<SpotPrice> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var price in _channel.Reader.ReadAllAsync(ct))
            yield return price;
    }

    // ── Internal: Login detection ───────────────────────────────────────
    private async Task EnsureLoggedInAsync(CancellationToken ct)
    {
        if (await IsLoggedInAsync())
        {
            await SaveStorageStateSnapshotAsync();
            return;
        }

        _logger.LogWarning("[Playwright] Not logged in. Please log in manually in the browser.");
        _logger.LogInformation(
            "[Playwright] Waiting up to {TimeoutSec}s for manual login + 2FA",
            _manualLoginTimeoutSec);

        var startedAt = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromSeconds(_manualLoginTimeoutSec);

        while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow - startedAt < timeout)
        {
            await Task.Delay(LoginPollSec * 1000, ct);
            if (await IsLoggedInAsync())
            {
                _logger.LogInformation("[Playwright] Login confirmed.");
                await SaveStorageStateSnapshotAsync();
                return;
            }
        }

        if (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Manual login timed out after {_manualLoginTimeoutSec}s. " +
                "Complete Google/Capital 2FA and retry.");
        }

        ct.ThrowIfCancellationRequested();
    }

    private async Task<bool> IsLoggedInAsync()
    {
        if (_page == null) return false;
        try
        {
            // Check 0 (negative): if a CDK overlay backdrop exists in the DOM,
            // NOT logged in — use JS existence check (not visibility) because
            // the overlay may be animating in with opacity=0 but still blocks clicks.
            var overlayExists = await _page.EvaluateAsync<bool>(
                "() => document.querySelector('.cdk-overlay-backdrop') !== null");
            if (overlayExists)
            {
                _logger.LogDebug("[Playwright] Login check: CDK overlay in DOM — not logged in");
                return false;
            }

            // Check 1: account balance element visible
            var bal = _page.Locator(Sel.AccountBalance);
            if (await bal.CountAsync() > 0 && await bal.First.IsVisibleAsync())
            {
                _logger.LogDebug("[Playwright] Login check: account balance visible");
                return true;
            }

            // Check 1b: text probe fallback used in SRS guide
            var equityText = _page.Locator("xpath=//*[contains(text(),'Available') or contains(text(),'Equity')]");
            if (await equityText.CountAsync() > 0)
            {
                _logger.LogDebug("[Playwright] Login check: equity/available text found");
                return true;
            }

            // Check 2: on platform URL + no visible login button
            var onPlatform = _page.Url.Contains("/trading/platform");
            var loginBtn = _page.Locator(Sel.LoginButton);
            var loginVisible = await loginBtn.CountAsync() > 0
                && await loginBtn.First.IsVisibleAsync();
            _logger.LogDebug("[Playwright] Login check: url={Url} onPlatform={OnPlatform} loginVisible={LoginVisible}",
                _page.Url, onPlatform, loginVisible);
            return onPlatform && !loginVisible;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Playwright] Login check exception");
            return false;
        }
    }

    // ── Internal: Wait for the platform SPA to fully bootstrap ─────────
    private async Task WaitForPlatformReadyAsync(CancellationToken ct)
    {
        _logger.LogInformation("[Playwright] Waiting for platform SPA to finish rendering...");

        // NetworkIdle fires once all XHR/fetch connections have been quiet for
        // ≥500 ms — the most reliable signal that Angular has finished loading.
        try
        {
            await _page!.WaitForLoadStateAsync(LoadState.NetworkIdle,
                new PageWaitForLoadStateOptions { Timeout = 30_000 });
            _logger.LogInformation("[Playwright] Network idle reached");
        }
        catch
        {
            _logger.LogWarning("[Playwright] NetworkIdle timeout — continuing anyway");
        }

        // Belt-and-suspenders: also wait for a known platform shell element.
        try
        {
            await _page!.WaitForSelectorAsync(
                "app-root, [class*='platform-layout'], [class*='trading-app'], " +
                "[class*='mainLayout'], [class*='main-layout']",
                new PageWaitForSelectorOptions { Timeout = 10_000 });
        }
        catch { /* best-effort */ }
    }

    // ── Internal: Returns true when the search <input> is visible ───────
    private async Task<bool> IsSearchVisibleAsync()
    {
        if (_page == null) return false;
        try
        {
            var loc = _page.Locator(Sel.SearchInput);
            return await loc.CountAsync() > 0 && await loc.First.IsVisibleAsync();
        }
        catch { return false; }
    }

    // ── Internal: Log every visible <input> to help find the real selector
    private async Task DumpVisibleInputsAsync()
    {
        if (_page == null) return;
        try
        {
            var dump = await _page.EvaluateAsync<string>(@"() => {
                return Array.from(document.querySelectorAll('input'))
                    .filter(i => !!(i.offsetParent))
                    .map(i => JSON.stringify({
                        type: i.type || '',
                        placeholder: i.placeholder || '',
                        cls: (i.className || '').substring(0, 80),
                        id: i.id || '',
                        name: i.name || '',
                        rect: i.getBoundingClientRect().width + 'x' + i.getBoundingClientRect().height
                    }))
                    .join('\n');
            }");
            _logger.LogInformation("[Playwright] Visible <input> elements on page:\n{Dump}", dump);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Playwright] Input dump failed");
        }
    }

    // ── Internal: Reveal the search input if hidden behind a trigger ────
    private async Task TryRevealSearchInputAsync()
    {
        if (_page == null) return;

        // 1. Already visible? Nothing to do.
        if (await IsSearchVisibleAsync()) return;

        // 2. Wait for network idle — covers the case where Angular hasn't finished
        //    bootstrapping the sidebar / watchlist panel yet.
        try
        {
            await _page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                new PageWaitForLoadStateOptions { Timeout = 15_000 });
        }
        catch { /* best-effort */ }

        if (await IsSearchVisibleAsync()) return;

        // 3. Try clicking any visible search icon/trigger button to reveal the input.
        var trigger = _page.Locator(Sel.SearchTrigger);
        var count = await trigger.CountAsync();
        for (var i = 0; i < count; i++)
        {
            try
            {
                if (await trigger.Nth(i).IsVisibleAsync())
                {
                    await trigger.Nth(i).ClickAsync();
                    _logger.LogDebug("[Playwright] Clicked search trigger #{Index}", i);
                    await _page.WaitForTimeoutAsync(1_200);
                    if (await IsSearchVisibleAsync()) return;
                }
            }
            catch { /* best-effort */ }
        }

        // 4. Dump all visible inputs so the real selector can be identified from logs.
        _logger.LogWarning("[Playwright] Search input not found after trigger scan — dumping DOM inputs for diagnostics");
        await DumpVisibleInputsAsync();
    }

    // ── Internal: Open ETH/USD instrument panel ─────────────────────────
    private async Task OpenInstrumentAsync(CancellationToken ct)
    {
        if (_page == null) return;

        _logger.LogInformation("[Playwright] Searching for {Term}... URL={Url}", _epicSearchTerm, _page.Url);

        // Capital.com may hide the search input behind a trigger icon; reveal it first.
        await TryRevealSearchInputAsync();

        // Find and use the search box.
        // Use a generous 45 s timeout: TryRevealSearchInputAsync already waited
        // for NetworkIdle, so this final wait is just a safety net for slow SPAs.
        const int SearchTimeoutMs = 45_000;
        var searchBox = _page.Locator(Sel.SearchInput);
        try
        {
            await searchBox.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = SearchTimeoutMs
            });
        }
        catch (Exception ex)
        {
            // Save screenshot for debugging
            var screenshotPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "logs", "playwright-debug.png");
            screenshotPath = Path.GetFullPath(screenshotPath);
            try { await _page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath }); }
            catch { /* best-effort */ }
            _logger.LogError(ex, "[Playwright] Search box not found. Screenshot saved to {Path}. Page URL: {Url}", screenshotPath, _page.Url);
            throw;
        }
        await searchBox.ClickAsync();
        await searchBox.ClearAsync();
        await searchBox.FillAsync(_epicSearchTerm!);

        // Wait for search results to populate
        await _page.WaitForTimeoutAsync(1_500);

        // Strategy A: exact text match "Ethereum/USD"
        // Strategy B: row containing "Ethereum"
        ILocator? ethRow = null;
        try
        {
            var byText = _page.GetByText("Ethereum/USD",
                new PageGetByTextOptions { Exact = true });
            await byText.WaitForAsync(new() { Timeout = 3_000, State = WaitForSelectorState.Visible });
            ethRow = byText;
            _logger.LogDebug("[Playwright] Found ETH/USD via exact text match");
        }
        catch
        {
            _logger.LogDebug("[Playwright] Exact text not found — trying row selector");
            var byRow = _page.Locator(
                "[class*='row']:has-text('Ethereum'), " +
                "[class*='item']:has-text('Ethereum'), " +
                "li:has-text('Ethereum/USD'), " +
                "div:has-text('Ethereum/USD')").First;
            await byRow.WaitForAsync(new() { Timeout = 6_000, State = WaitForSelectorState.Visible });
            ethRow = byRow;
        }

        await ethRow.ClickAsync();
        _logger.LogInformation("[Playwright] Clicked ETH/USD row");

        // Wait until both Sell and Buy prices are visible in the page
        await _page.WaitForFunctionAsync(@"() => {
            const text = document.body.innerText;
            return text.includes('Sell') && text.includes('Buy') && text.includes('Ethereum');
        }", null, new PageWaitForFunctionOptions { Timeout = 10_000 });

        _logger.LogInformation("[Playwright] ETH/USD price panel confirmed open");
    }

    // ── Internal: Scraping loop ─────────────────────────────────────────
    private async Task ScrapeLoopAsync(CancellationToken ct)
    {
        SpotPrice? lastValid = null;
        var windowTicks = 0L;
        var nullCycles = 0;
        var firstTickLogged = false;
        var totalPolls = 0L;

        // Stale-price warning throttle: emit on transition + exponential backoff
        // (10s → 20s → 40s → … capped at 600s gap) so a flat-line market doesn't spam.
        // NOTE: `nextStaleWarnAtSec` is a *threshold* (in staleSec units); once crossed,
        // we advance it past the current staleSec by `staleWarnIntervalSec` to prevent
        // every subsequent poll (5-10 Hz) from re-firing the warning.
        var stalenessWarned = false;
        var staleWarnIntervalSec = 10;
        var nextStaleWarnAtSec = 10;
        const int maxStaleWarnIntervalSec = 600;

        // Selector path tracking: only log when the active extraction path changes,
        // and only at DEBUG level. The previous code logged every 100 polls at INFO.
        string? lastSellPath = null;
        string? lastBuyPath = null;

        while (!ct.IsCancellationRequested)
        {
            var start = DateTimeOffset.UtcNow;
            totalPolls++;

            try
            {
                if (_sessionReloadInProgress || _page == null)
                {
                    _healthy = false;
                    await Task.Delay(250, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    continue;
                }

                var priceTask = _page!.EvaluateAsync<PriceResult?>(Sel.PriceExtractJs);
                var finished = await Task.WhenAny(priceTask, Task.Delay(5_000, ct));
                if (finished != priceTask)
                    throw new TimeoutException("Playwright price extraction timed out after 5s");

                var prices = await priceTask;

                if (prices != null && prices.Bid > 0 && prices.Ask > 0)
                {
                    nullCycles = 0;
                    _healthy = true;
                    windowTicks++;

                    // Change-detection: only write to channel when price actually changed
                    bool priceChanged = lastValid == null
                        || prices.Bid != lastValid.Bid
                        || prices.Ask != lastValid.Ask;

                    if (priceChanged)
                    {
                        var spot = new SpotPrice(prices.Bid, prices.Ask,
                            (prices.Bid + prices.Ask) / 2m,
                            DateTimeOffset.UtcNow);

                        _channel.Writer.TryWrite(spot);
                        lastValid = spot;
                        _lastPriceChangeTime = DateTimeOffset.UtcNow;
                        _changesInWindow++;

                        // Reset staleness throttle on every confirmed price tick.
                        if (stalenessWarned)
                        {
                            _logger.LogInformation(
                                "[Playwright] Price feed recovered after stall (bid={Bid} ask={Ask})",
                                spot.Bid, spot.Ask);
                        }
                        stalenessWarned = false;
                        staleWarnIntervalSec = 10;
                        nextStaleWarnAtSec = 10;

                        if (!firstTickLogged)
                        {
                            firstTickLogged = true;
                            _logger.LogInformation("[Playwright] First tick received bid={Bid} ask={Ask} mid={Mid}",
                                spot.Bid, spot.Ask, spot.Mid);
                        }
                    }

                    // Selector-path transition logging (DEBUG only). Logs once on
                    // initial path discovery, then again only if the path actually
                    // changes — no more 100-poll spam at INFO level.
                    if (prices.SellPath != lastSellPath || prices.BuyPath != lastBuyPath)
                    {
                        _logger.LogDebug(
                            "[Playwright] Selector path changed sell={SellPath} buy={BuyPath}",
                            prices.SellPath ?? "(none)", prices.BuyPath ?? "(none)");
                        lastSellPath = prices.SellPath;
                        lastBuyPath = prices.BuyPath;
                    }

                    // Update tick rate and change rate every 10 seconds
                    var elapsed = (DateTimeOffset.UtcNow - _windowStart).TotalSeconds;
                    if (elapsed >= 10)
                    {
                        _tickRateHz = windowTicks / elapsed;
                        _changeRateHz = _changesInWindow / elapsed;
                        windowTicks = 0;
                        _changesInWindow = 0;
                        _windowStart = DateTimeOffset.UtcNow;
                        _logger.LogDebug("[Playwright] Poll rate: {PollHz:F1} Hz, Change rate: {ChangeHz:F1} Hz",
                            _tickRateHz, _changeRateHz);
                    }

                    // Warn if no price change for an extended period — but with
                    // exponential backoff (10s → 20s → 40s → … → 600s) so a
                    // genuinely flat market doesn't fill the log file. The
                    // `Selector path changed` debug line above tells us whether
                    // the extractor is still finding the price element.
                    if (_lastPriceChangeTime != DateTimeOffset.MinValue)
                    {
                        var staleSec = (DateTimeOffset.UtcNow - _lastPriceChangeTime).TotalSeconds;
                        if (staleSec >= nextStaleWarnAtSec)
                        {
                            _logger.LogWarning(
                                "[Playwright] Price unchanged for {Seconds:F0}s sellPath={SellPath} buyPath={BuyPath} — flat market or DOM stale",
                                staleSec,
                                lastSellPath ?? "(none)",
                                lastBuyPath ?? "(none)");
                            stalenessWarned = true;
                            staleWarnIntervalSec = Math.Min(staleWarnIntervalSec * 2, maxStaleWarnIntervalSec);
                            // Advance threshold past the *current* staleSec so the next warn waits
                            // a full `staleWarnIntervalSec` instead of firing on every poll.
                            nextStaleWarnAtSec = (int)staleSec + staleWarnIntervalSec;
                        }
                    }
                }
                else
                {
                    nullCycles++;
                    if (nullCycles == 1 || nullCycles % 25 == 0)
                    {
                        _logger.LogWarning(
                            "[Playwright] Price extraction returned null (count={Count}) url={Url}. Waiting/retrying...",
                            nullCycles,
                            _page.Url);
                    }

                    if (nullCycles % 50 == 0)
                    {
                        _logger.LogWarning("[Playwright] Re-opening instrument after repeated null prices (count={Count})", nullCycles);
                        await OpenInstrumentAsync(ct);
                    }

                    _healthy = false;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Playwright] Scrape error");
                _healthy = false;

                // Try to re-open the instrument panel on error
                try { await OpenInstrumentAsync(ct); }
                catch (Exception retryEx)
                {
                    _logger.LogError(retryEx, "[Playwright] Recovery failed — will retry next cycle");
                }
            }

            // Maintain target poll interval
            var elapsed2 = DateTimeOffset.UtcNow - start;
            var delay = TimeSpan.FromMilliseconds(PollIntervalMs) - elapsed2;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _channel.Writer.Complete();
        _logger.LogInformation("[Playwright] Scrape loop stopped");
    }

    // ── IAsyncDisposable ────────────────────────────────────────────────
    public async ValueTask DisposeAsync()
    {
        await _sessionGate.WaitAsync();
        try
        {
            await DisposeBrowserSessionAsync();
            _playwright?.Dispose();
            _playwright = null;
            _channel.Writer.TryComplete();
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task DisposeBrowserSessionAsync()
    {
        var context = _context;
        _context = null;
        _page = null;

        if (context == null)
            return;

        _sessionReloadInProgress = true;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await SaveStorageStateSnapshotAsync(context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Playwright] Storage state save failed during dispose — continuing");
        }

        try
        {
            await context.CloseAsync().WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[Playwright] Context close timed out after 5s during dispose — forcing abort");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Playwright] Context close failed during dispose");
        }
        finally
        {
            _sessionReloadInProgress = false;
        }
    }

    private async Task<IBrowserContext> LaunchPersistentContextAsync()
    {
        var options = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = _headless,
            SlowMo = 0,
            ViewportSize = _headless ? new ViewportSize { Width = 1400, Height = 900 } : ViewportSize.NoViewport,
            Channel = _browserChannel,
            IgnoreDefaultArgs = ["--enable-automation"],
            Args = BrowserArgs
        };

        try
        {
            return await _playwright!.Chromium.LaunchPersistentContextAsync(_userDataDir, options);
        }
        catch (Exception ex)
        {
            var msg = ex.Message ?? string.Empty;
            if (msg.Contains("Opening in existing browser session", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("already in use", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("singleton", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Chrome profile is already open. Close Chrome windows using the automation profile and retry.", ex);
            }

            throw new InvalidOperationException(
                $"Failed to launch Chrome channel '{_browserChannel}'. Google login requires real Chrome. " +
                "Install/update Chrome and keep BrowserChannel='chrome'.",
                ex);
        }
    }

    private static Task ApplyStealthInitAsync(IPage page)
    {
        const string script = @"() => {
            Object.defineProperty(navigator, 'webdriver', {
                get: () => undefined,
                configurable: true
            });
        }";

        return page.AddInitScriptAsync(script);
    }

    private async Task SaveStorageStateSnapshotAsync(IBrowserContext? context = null)
    {
        context ??= _context;
        if (context == null) return;

        try
        {
            await context.StorageStateAsync(new BrowserContextStorageStateOptions
            {
                Path = _storageStatePath
            });

            _logger.LogInformation("[Playwright] Session state snapshot saved at {Path}", _storageStatePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Playwright] Failed to persist storage state snapshot");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────
    private static string ResolvePersistentDir(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(configuredPath);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".eth-manual", "capital-chrome-profile");
    }

    private static string DeriveSearchTerm(string epic) =>
        epic.ToUpperInvariant() switch
        {
            "ETHUSD" or "ETH" => "Ethereum",
            "BTCUSD" or "BTC" => "Bitcoin",
            _ => epic  // pass through and let the search box handle it
        };

    // Internal DTO returned by the JavaScript evaluation. SellPath/BuyPath are
    // diagnostic strings (e.g. 'css:[data-testid=sell-price]', 'row-scoped',
    // 'button-scan') describing which extraction path actually fired so we can
    // tell at a glance whether the structured CSS selectors are still working.
    private sealed class PriceResult
    {
        public decimal Bid { get; set; }
        public decimal Ask { get; set; }
        public string? SellPath { get; set; }
        public string? BuyPath { get; set; }
    }

    // Diagnostic snapshot returned by ProbeSelectorsAsync — used by the admin
    // probe endpoint so an operator can discover live selectors at runtime
    // without restarting the app or losing the Capital.com session.
    public sealed class SelectorProbeResult
    {
        public string Url { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public int BodyTextLen { get; set; }
        public List<SelectorProbeCandidate> SellCandidates { get; set; } = new();
        public List<SelectorProbeCandidate> BuyCandidates { get; set; } = new();
    }

    public sealed class SelectorProbeCandidate
    {
        public string Tag { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Classes { get; set; } = string.Empty;
        public string TestId { get; set; } = string.Empty;
        public string AriaLabel { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// Run the in-page selector probe against the live DOM and return all
    /// candidate Sell/Buy elements (tag, id, classes, testid, aria-label,
    /// short text). Used by the admin diagnostic endpoint to discover the
    /// correct selectors when Capital.com changes its DOM, without restarting.
    /// </summary>
    public async Task<SelectorProbeResult?> ProbeSelectorsAsync()
    {
        if (_page == null) return null;
        try
        {
            return await _page.EvaluateAsync<SelectorProbeResult?>(Sel.SelectorProbeJs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Playwright] Selector probe failed");
            return null;
        }
    }
}
