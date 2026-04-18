# CapitalBot Complete Guide
## Capital.com Trading Automation — .NET / Playwright
### Single-file reference: setup + inspect element + full C# source

---

## Table of Contents
1. [Prerequisites & Setup](#1-prerequisites--setup)
2. [Project Structure](#2-project-structure)
3. [CapitalBot.csproj](#3-capitalbotcsproj)
4. [Selectors Cheat Sheet + Inspect Element Guide](#4-selectors-cheat-sheet--inspect-element-guide)
5. [Login Detection & Manual Fallback Logic](#5-login-detection--manual-fallback-logic)
6. [Full C# Source — Program.cs](#6-full-c-source--programcs)
7. [How Each Step Works](#7-how-each-step-works)
8. [WebSocket API Alternative (Recommended for Production)](#8-websocket-api-alternative-recommended-for-production)
9. [Troubleshooting](#9-troubleshooting)

---

## 1. Prerequisites & Setup

```bash
# 1. Install .NET 8 SDK
# https://dotnet.microsoft.com/download

# 2. Create project
dotnet new console -n CapitalBot
cd CapitalBot

# 3. Add Playwright NuGet package
dotnet add package Microsoft.Playwright

# 4. Build once (required before installing browsers)
dotnet build

# 5. Install Chromium browser
pwsh bin/Debug/net8.0/playwright.ps1 install chromium

# 6. Run
dotnet run
```

---

## 2. Project Structure
CapitalBot/
├── CapitalBot.csproj
├── Program.cs ← all logic lives here
└── README.md ← this file

text

---

## 3. CapitalBot.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Playwright" Version="1.42.0" />
  </ItemGroup>
</Project>
```

---

## 4. Selectors Cheat Sheet + Inspect Element Guide

Use **F12 → Elements tab → Ctrl+F** to search each selector live on the page.

| # | Data Point        | CSS / XPath Selector                                      | Console Verification Command                                                        |
|---|-------------------|-----------------------------------------------------------|-------------------------------------------------------------------------------------|
| 1 | Login check       | `[class*="account-balance"]` or `[class*="equity"]`       | `document.querySelector("[class*='account-balance']")?.innerText`                  |
| 2 | Search box        | `input[aria-label="Search"]`                              | `document.querySelector("input[aria-label='Search']")`                             |
| 3 | Sell price        | `[data-testid="sell-price"]` or `td.sell` or `.sellPrice` | `document.querySelector("[data-testid='sell-price']")?.innerText`                  |
| 4 | Buy price         | `[data-testid="buy-price"]` or `td.buy` or `.buyPrice`    | `document.querySelector("[data-testid='buy-price']")?.innerText`                   |
| 5 | High price        | `[data-testid="high-price"]` or `.highPrice`              | `document.querySelector("[data-testid='high-price']")?.innerText`                  |
| 6 | Low price         | `[data-testid="low-price"]` or `.lowPrice`                | `document.querySelector("[data-testid='low-price']")?.innerText`                   |
| 7 | Depth tab button  | `button:has-text("Depth")`                                | `[...document.querySelectorAll("button")].find(b=>b.innerText.includes("Depth"))`  |
| 8 | Depth rows        | `[class*="depth"] tr` or `[class*="orderbook"] tr`        | `document.querySelectorAll("[class*='depth'] tr")`                                 |
| 9 | Overview tab      | `button:has-text("Overview")` or `:has-text("Details")`   | `[...document.querySelectorAll("button")].find(b=>b.innerText.includes("Overview"))`|
|10 | Market info rows  | `[class*="market-info"] tr` or `[class*="details"] tr`    | `document.querySelectorAll("[class*='details'] tr")`                               |
|11 | Price Range       | XPath: `//*[text()='Price Range']/following-sibling::*`   | JS fallback scan (see Step 7G in code)                                             |
|12 | Market Sentiment  | XPath: `//*[text()='Sentiment']/following-sibling::*`     | JS fallback scan                                                                    |
|13 | Trading Hours     | XPath: `//*[text()='Trading Hours']/following-sibling::*` | JS fallback scan                                                                    |
|14 | Leverage          | XPath: `//*[text()='Leverage']/following-sibling::*`      | JS fallback scan                                                                    |
|15 | Dynamic Spread    | XPath: `//*[text()='Dynamic Spread']/following-sibling::*`| JS fallback scan                                                                    |
|16 | Margin            | XPath: `//*[text()='Margin']/following-sibling::*`        | JS fallback scan                                                                    |

### How to Find a Broken Selector (when Capital.com updates their UI)
Open https://capital.com/trading/platform/

Press F12 → Elements

Press Ctrl+Shift+C (Pick element mode)

Click the price/button you want

Right-click the highlighted element → Copy → Copy selector

Paste new selector into the Selectors class in Program.cs

text

---

## 5. Login Detection & Manual Fallback Logic
┌─────────────────────────────────────┐
│ Open capital.com/trading/platform │
└──────────────┬──────────────────────┘
│
▼
IsLoggedInAsync()
┌───────────────────────────────────┐
│ Check 1: [class*="account-balance"]│
│ Check 2: XPath "Available"/"Equity"│
│ Check 3: URL + no login button │
└───────────┬───────────────────────┘
│
Any pass?
/
YES NO
│ │
│ Print: "Please login manually"
│ Poll every 5s for 5 minutes
│ │
│ Login detected?
│ /
│ YES NO
│ │ │
│ │ Retry (max 3 times)
│ │ │
│ │ 3rd fail → Exit
▼ ▼
Continue with Steps 3–7

text

---

## 6. Full C# Source — Program.cs

```csharp
using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

class CapitalBot
{
    // ─────────────────────────────────────────────
    // SELECTORS — update these if Capital.com changes their UI
    // ─────────────────────────────────────────────
    static class Selectors
    {
        public const string SearchBox        = "input[placeholder*='Search'], input[aria-label*='Search']";
        public const string SellPrice        = "[data-testid='sell-price'], .sell-price, [class*='sellPrice'], [class*='sell-btn'] [class*='price']";
        public const string BuyPrice         = "[data-testid='buy-price'],  .buy-price,  [class*='buyPrice'],  [class*='buy-btn']  [class*='price']";
        public const string HighPrice        = "[data-testid='high-price'], [class*='highPrice'], [class*='high-price']";
        public const string LowPrice         = "[data-testid='low-price'],  [class*='lowPrice'],  [class*='low-price']";
        public const string AccountBalance   = "[class*='account-balance'], [class*='equity'], [class*='availableFunds']";
        public const string LoginButton      = "button:has-text('Log in'), a:has-text('Log in'), [class*='login']";
        public const string DepthTab         = "button:has-text('Depth'), [role='tab']:has-text('Depth')";
        public const string OverviewTab      = "button:has-text('Overview'), button:has-text('Details'), [role='tab']:has-text('Overview')";
        public const string DepthRows        = "[class*='depth'] tr, [class*='orderbook'] tr, [class*='order-book'] tr";
        public const string MarketInfoRows   = "[class*='market-info'] tr, [class*='instrument-details'] tr, [class*='details'] tr";
    }

    // ─────────────────────────────────────────────
    // PRICE MODEL
    // ─────────────────────────────────────────────
    record PriceData(
        string Sell, string Buy,
        string High, string Low,
        DateTime Timestamp
    );

    record DepthLevel(string Price, string Size, string Side);

    record MarketInfo(
        string PriceRange, string Sentiment,
        string TradingHours, string Leverage,
        string DynamicSpread, string Margin
    );

    // ─────────────────────────────────────────────
    // ENTRY POINT
    // ─────────────────────────────────────────────
    static async Task Main()
    {
        using var playwright = await Playwright.CreateAsync();

        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = false,           // keep visible so user can login manually
            SlowMo   = 100
        });

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1400, Height = 900 }
        });

        var page = await context.NewPageAsync();

        // ── STEP 1: Open Platform ──────────────────
        Console.WriteLine(" Opening Capital.com trading platform...");[1]
        await page.GotoAsync("https://capital.com/trading/platform/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout   = 60_000
        });
        Console.WriteLine(" Page loaded.");[1]

        // ── STEP 2: Login Check with Retry ────────
        bool loggedIn = await WaitForLoginAsync(page, maxRetries: 3, waitMinutes: 5);
        if (!loggedIn)
        {
            Console.WriteLine(" Could not confirm login after 3 retries. Exiting.");
            return;
        }
        Console.WriteLine(" Login confirmed. Proceeding...");

        // ── STEP 3: Trade Tab + Search ETH/USD ────
        await NavigateToEthereumAsync(page);

        // ── STEP 4+5: Live Price Monitor ──────────
        Console.WriteLine(" Starting real-time price monitor (Ctrl+C to stop)...");
        _ = Task.Run(() => MonitorPricesAsync(page));

        // ── STEP 6: Price Depth ───────────────────
        await Task.Delay(2000); // let prices settle
        var depth = await GetPriceDepthAsync(page);
        PrintDepth(depth);

        // ── STEP 7: All Market Info ───────────────
        var info = await GetMarketInfoAsync(page);
        PrintMarketInfo(info);

        // Keep running for live price updates
        Console.WriteLine("\n[Live] Monitoring prices... Press Ctrl+C to exit.\n");
        await Task.Delay(Timeout.Infinite);
    }

    // ─────────────────────────────────────────────
    // STEP 2 — LOGIN DETECTION + MANUAL FALLBACK
    // ─────────────────────────────────────────────
    static async Task<bool> WaitForLoginAsync(IPage page, int maxRetries, int waitMinutes)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            Console.WriteLine($" Login check — attempt {attempt}/{maxRetries}");

            if (await IsLoggedInAsync(page))
                return true;

            Console.WriteLine($" Not logged in. Please log in manually in the browser window.");
            Console.WriteLine($" Waiting up to {waitMinutes} minute(s)...");

            int totalSeconds = waitMinutes * 60;
            int elapsed      = 0;

            while (elapsed < totalSeconds)
            {
                await Task.Delay(5000);
                elapsed += 5;

                if (await IsLoggedInAsync(page))
                {
                    Console.WriteLine(" Login detected!");
                    return true;
                }

                int remaining = totalSeconds - elapsed;
                Console.Write($"\r Waiting... {remaining}s remaining   ");
            }

            Console.WriteLine($"\n Timeout reached on attempt {attempt}.");
        }

        return false;
    }

    static async Task<bool> IsLoggedInAsync(IPage page)
    {
        try
        {
            // Check 1 — account balance element visible
            var balanceEl = page.Locator(Selectors.AccountBalance);
            if (await balanceEl.CountAsync() > 0 && await balanceEl.First.IsVisibleAsync())
                return true;

            // Check 2 — XPath text probe
            var equityEl = page.Locator("xpath=//*[contains(text(),'Available') or contains(text(),'Equity')]");
            if (await equityEl.CountAsync() > 0)
                return true;

            // Check 3 — URL is platform + no login button visible
            bool onPlatform  = page.Url.Contains("/trading/platform");
            var  loginBtn    = page.Locator(Selectors.LoginButton);
            bool loginVisible = await loginBtn.CountAsync() > 0 && await loginBtn.First.IsVisibleAsync();
            if (onPlatform && !loginVisible)
                return true;

            return false;
        }
        catch { return false; }
    }

    # STEP 3 — Navigate to Ethereum/USD (Rewritten with Verified Selectors)

## What the Live DOM Shows

From inspecting your actual Capital.com Trade page right now:

```
✓ Search box:          <input aria-label="Search" />
✓ ETH/USD in list:     "Ethereum/USD  +1.93%  2,155.22 Sell  2,156.97 Buy"
✓ Navigation tabs:     Trade | Discover | Charts | Portfolio | Reports
✓ Trade tab URL:       https://capital.com/trading/platform/trade
```

---

## Problem with the Original Step 3

The original code had two issues:
1. It tried to click a "Trade tab" that may not exist as a button — the Trade tab
   is a bottom navigation link, not always clickable from within the platform.
2. The ETH/USD row selector was too generic and depended on unknown class names.

---

## Fixed C# Code for Step 3

```csharp
// ── STEP 3 (FIXED) ── Navigate to Trade + Open ETH/USD ───────────────────────

public async Task NavigateToEthUsdAsync()
{
    Console.WriteLine("[STEP 3] Ensuring Trade tab is active...");

    // The Trade tab is a bottom navigation link — navigate directly by URL
    // instead of trying to click a button that may not be reliably selectable.
    if (!_page.Url.Contains("/trading/platform/trade"))
    {
        await _page.GotoAsync(
            "https://capital.com/trading/platform/trade",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle,
            new PageWaitForLoadStateOptions { Timeout = 20_000 });
    }
    Console.WriteLine("[STEP 3] ✓ Trade page active.");

    // ── Search for Ethereum ───────────────────────────────────────────────────
    // VERIFIED SELECTOR: <input aria-label="Search" /> — confirmed live in DOM
    Console.WriteLine("[STEP 3] Locating search box...");
    var searchBox = _page.Locator("input[aria-label='Search']");
    await searchBox.WaitForAsync(new LocatorWaitForOptions
    {
        State   = WaitForSelectorState.Visible,
        Timeout = 10_000
    });
    await searchBox.ClickAsync();
    await searchBox.ClearAsync();
    await searchBox.FillAsync("Ethereum");
    Console.WriteLine("[STEP 3] Typed \"Ethereum\" into search...");
    await _page.WaitForTimeoutAsync(1_800);   // wait for dropdown / list filter

    // ── Click the ETH/USD result ──────────────────────────────────────────────
    // The platform shows results in a list. We try multiple selector strategies:

    // Strategy A: text match on "Ethereum/USD" inside any clickable row
    var ethByText = _page.GetByText("Ethereum/USD", new PageGetByTextOptions { Exact = true });

    // Strategy B: row containing both "Ethereum" and "USD" with Sell/Buy cells
    var ethByRow  = _page.Locator(
        "[class*='row']:has-text('Ethereum'), " +
        "[class*='item']:has-text('Ethereum'), " +
        "[class*='result']:has-text('Ethereum'), " +
        "li:has-text('Ethereum/USD'), " +
        "div:has-text('Ethereum/USD')").First;

    // Try Strategy A first, then B
    ILocator ethRow;
    try
    {
        await ethByText.WaitForAsync(new() { Timeout = 3_000, State = WaitForSelectorState.Visible });
        ethRow = ethByText;
        Console.WriteLine("[STEP 3] ✓ Found ETH/USD via exact text match.");
    }
    catch
    {
        Console.WriteLine("[STEP 3] Exact match not found — trying row selector...");
        await ethByRow.WaitForAsync(new() { Timeout = 6_000, State = WaitForSelectorState.Visible });
        ethRow = ethByRow;
        Console.WriteLine("[STEP 3] ✓ Found ETH/USD via row selector.");
    }

    await ethRow.ClickAsync();
    Console.WriteLine("[STEP 3] ✓ Clicked Ethereum/USD row.");

    // ── Wait for instrument panel to open ────────────────────────────────────
    // The instrument detail panel/chart loads after clicking.
    // Wait for the Sell and Buy price elements to appear on screen.
    Console.WriteLine("[STEP 3] Waiting for ETH/USD price panel to load...");

    await _page.WaitForFunctionAsync(@"() => {
        // Look for any element containing a price-like number near Sell/Buy context
        const allText = document.body.innerText;
        return allText.includes('Sell') && allText.includes('Buy') &&
               allText.includes('Ethereum');
    }", null, new PageWaitForFunctionOptions { Timeout = 10_000 });

    Console.WriteLine("[STEP 3] ✓ ETH/USD panel confirmed open.");

    // ── DEBUG HELPER (remove in production) ──────────────────────────────────
    // Prints all visible text that contains a price number near "Sell" or "Buy"
    var debugPrices = await _page.EvaluateAsync<string>(@"() => {
        const candidates = [...document.querySelectorAll('*')]
            .filter(e => e.children.length === 0)
            .filter(e => /^\d{1,5}[.,]\d{2}$/.test((e.innerText||e.textContent||'')                         .trim()))
            .map(e => e.innerText.trim())
            .slice(0, 10);
        return candidates.join(' | ');
    }");
    Console.WriteLine($"[STEP 3] Visible price values: {debugPrices}");
}
```

---

## How the Fix Works

| Problem | Old Code | Fixed Code |
|---|---|---|
| Trade tab not reliably clickable | `tab.ClickAsync()` on unknown selector | Direct `GotoAsync("/trading/platform/trade")` |
| ETH result selector too fragile | `data-instrument*="ETHUSD"` (guessed) | `GetByText("Ethereum/USD")` + row fallback |
| No confirmation the panel opened | None | `WaitForFunctionAsync` checks DOM text |
| Hard to debug when it fails | Silent failure | Debug helper prints all visible prices |

---

## Verified Selectors from Live DOM (April 2026)

```
Search input:       input[aria-label="Search"]           ✓ CONFIRMED
ETH/USD text:       GetByText("Ethereum/USD", Exact=true) ✓ CONFIRMED  
Trade page URL:     /trading/platform/trade               ✓ CONFIRMED
Sell price format:  "2,155.22"  (5-digit, comma-separated, 2 decimals)
Buy price format:   "2,156.97"  (always slightly higher than Sell)
```

---

## Quick Console Test (Run in Browser F12 → Console)

Paste this in the browser console while on the Trade page to verify:

```js
// 1. Confirm search box exists
console.log("Search box:", document.querySelector("input[aria-label='Search']"));

// 2. Simulate typing and check results appear
const inp = document.querySelector("input[aria-label='Search']");
inp.click();
inp.value = "Ethereum";
inp.dispatchEvent(new Event("input", { bubbles: true }));
setTimeout(() => {
    console.log("ETH/USD visible:", document.body.innerText.includes("Ethereum/USD"));
}, 1500);
```
