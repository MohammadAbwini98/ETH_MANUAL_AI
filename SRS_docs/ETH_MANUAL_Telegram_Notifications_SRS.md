# ETH_MANUAL — Telegram Notification System SRS
**Feature:** Real-time Telegram notifications for system events and trading signals  
**Date:** 2026-04-06  
**Status:** FOR IMPLEMENTATION  
**Bot:** @Ethusd_GPT_Bot | Chat ID: 1495017760

---

## TABLE OF CONTENTS
1. [Overview](#1-overview)
2. [New Files to Create](#2-new-files-to-create)
3. [Files to Modify](#3-files-to-modify)
4. [Configuration](#4-configuration)
5. [Message Format Specification](#5-message-format-specification)
6. [Test Cases](#6-test-cases)
7. [Implementation Checklist](#7-implementation-checklist)

---

## 1. OVERVIEW

### 1.1 Scope
Add a `TelegramNotifier` service to the `EthSignal.Infrastructure` project.  
Wire it into `DataIngestionService` (start/stop events) and `LiveTickProcessor` (signal events).  
No external Telegram SDK — use raw `HttpClient` POST to the Telegram Bot API.

### 1.2 Telegram Bot Credentials
```
Bot Token  : 8634786346:AAF-eqW95DWCxMuff0Mz9Y5XvK_8bxqPNFI
Bot URL    : https://t.me/Ethusd_GPT_Bot
Chat ID    : 1495017760
API Base   : https://api.telegram.org/bot{TOKEN}/sendMessage
```

### 1.3 Multi-Chat-ID Design
The service accepts a `List<long>` of Chat IDs from configuration.  
Currently only `1495017760` is configured, but the list can be extended without code changes.

### 1.4 NuGet Package Required
No new packages needed. Uses `System.Net.Http.HttpClient` (already present in .NET 9).

---

## 2. NEW FILES TO CREATE

---

### 2.1 `src/EthSignal.Infrastructure/Notifications/ITelegramNotifier.cs`

**Purpose:** Interface allowing test mocking and DI substitution.

```csharp
namespace EthSignal.Infrastructure.Notifications;

public interface ITelegramNotifier
{
    Task SendAsync(string message, CancellationToken ct = default);
}
```

---

### 2.2 `src/EthSignal.Infrastructure/Notifications/TelegramNotifier.cs`

**Purpose:** Concrete implementation. Sends a message to every configured Chat ID.  
Failures are logged and swallowed — notifications must never crash the trading engine.

```csharp
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Notifications;

public sealed class TelegramNotifier : ITelegramNotifier
{
    private readonly HttpClient _http;
    private readonly string _token;
    private readonly IReadOnlyList<long> _chatIds;
    private readonly ILogger<TelegramNotifier> _logger;

    public TelegramNotifier(
        string botToken,
        IEnumerable<long> chatIds,
        ILogger<TelegramNotifier> logger)
    {
        _token   = botToken;
        _chatIds = chatIds.ToList().AsReadOnly();
        _logger  = logger;
        _http    = new HttpClient
        {
            BaseAddress = new Uri("https://api.telegram.org/"),
            Timeout     = TimeSpan.FromSeconds(10)
        };
    }

    public async Task SendAsync(string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_token) || _chatIds.Count == 0)
        {
            _logger.LogDebug("[Telegram] Skipped — bot token or chat IDs not configured");
            return;
        }

        foreach (var chatId in _chatIds)
        {
            try
            {
                var payload = new
                {
                    chat_id    = chatId,
                    text       = message,
                    parse_mode = "HTML"
                };

                var url = $"bot{_token}/sendMessage";
                var response = await _http.PostAsJsonAsync(url, payload, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning(
                        "[Telegram] Message to chat {ChatId} failed ({Status}): {Body}",
                        chatId, (int)response.StatusCode, body);
                }
                else
                {
                    _logger.LogDebug("[Telegram] Message sent to chat {ChatId}", chatId);
                }
            }
            catch (Exception ex)
            {
                // Never propagate — notifications must not crash the engine
                _logger.LogWarning(ex, "[Telegram] Exception sending to chat {ChatId} (non-fatal)", chatId);
            }
        }
    }
}
```

---

### 2.3 `src/EthSignal.Infrastructure/Notifications/TelegramMessageFormatter.cs`

**Purpose:** Builds all human-readable Telegram messages. Keeps formatting out of business logic.  
Uses HTML parse mode so bold (`<b>`) and monospace (`<code>`) render in Telegram.

```csharp
using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Notifications;

public static class TelegramMessageFormatter
{
    // ─── System Events ────────────────────────────────────

    /// <summary>Sent when DataIngestionService.ExecuteAsync begins.</summary>
    public static string SystemStart(string symbol, string environment) =>
        $"""
        🟢 <b>ETH Signal System — STARTED</b>

        🔗 Symbol      : <code>{symbol}</code>
        🌐 Environment : <code>{environment}</code>
        🕐 Time (UTC)  : <code>{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}</code>

        Live tick processor is active. Monitoring for signals.
        """;

    /// <summary>Sent when DataIngestionService.StopAsync / cancellation is triggered.</summary>
    public static string SystemStop(string symbol, string reason) =>
        $"""
        🔴 <b>ETH Signal System — STOPPED</b>

        🔗 Symbol  : <code>{symbol}</code>
        📋 Reason  : <code>{Escape(reason)}</code>
        🕐 Time    : <code>{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}</code>
        """;

    // ─── Signal Event ─────────────────────────────────────

    /// <summary>
    /// Sent when a BUY or SELL signal is confirmed and persisted.
    /// Includes all trading parameters, ML data, and reasons.
    /// </summary>
    public static string NewSignal(
        SignalRecommendation signal,
        SignalDecision decision,
        RegimeResult? regime)
    {
        var dir     = signal.Direction == SignalDirection.BUY ? "🟢 BUY" : "🔴 SELL";
        var dirIcon = signal.Direction == SignalDirection.BUY ? "📈" : "📉";

        // Expiry: signal time + (timeframe duration × OutcomeTimeoutBars)
        // Approximate using standard TF durations
        var tfMinutes = TfMinutes(signal.Timeframe);
        var expiry    = signal.SignalTimeUtc.AddMinutes(tfMinutes * 60); // 60 bars default
        var expiryStr = expiry.ToString("yyyy-MM-dd HH:mm") + " UTC";

        // Risk/Reward
        var rr = signal.SlPrice > 0
            ? Math.Round(Math.Abs(signal.TpPrice - signal.EntryPrice) /
                         Math.Abs(signal.EntryPrice - signal.SlPrice), 2)
            : 0m;

        // ML block
        var ml    = decision.MlPrediction;
        var mlStr = ml != null
            ? $"""

               ── ML Prediction ──────────────────
        🤖 ML Mode        : <code>{ml.Mode}</code>
        📊 Win Probability: <code>{ml.PredictedWinProbability:P1}</code>
        🎯 Confidence     : <code>{ml.CalibratedConfidence}%</code>
        📐 Threshold      : <code>{ml.RecommendedThreshold}</code>
        💰 Expected R     : <code>{ml.ExpectedValueR:+0.00;-0.00}R</code>
        🔬 Model          : <code>{Escape(ml.ModelVersion)}</code>
        ⚡ Active         : <code>{(ml.IsActive ? "YES — gating decisions" : "SHADOW — annotation only")}</code>"""
            : "\n🤖 ML Mode        : <code>DISABLED</code>";

        // Blended confidence
        var blended = decision.BlendedConfidence.HasValue
            ? $"\n🔀 Blended Conf   : <code>{decision.BlendedConfidence}%</code>"
            : string.Empty;

        // Regime block
        var regimeStr = regime != null
            ? $"<code>{regime.Regime}</code> (score {regime.RegimeScore}/5)"
            : "<code>UNKNOWN</code>";

        // Reason summary (first 3)
        var topReasons = decision.ReasonDetails
            .Take(3)
            .Select((r, i) => $"  {i + 1}. {Escape(r)}")
            .ToList();
        var reasonBlock = topReasons.Count > 0
            ? string.Join("\n", topReasons)
            : "  (none)";

        // Reject codes
        var rejectCodes = decision.ReasonCodes.Count > 0
            ? string.Join(", ", decision.ReasonCodes.Select(r => r.ToString()))
            : "NONE";

        return $"""
        {dirIcon} <b>NEW SIGNAL — {signal.Symbol} {signal.Timeframe}</b>

        ── Decision ───────────────────────
        📌 Direction  : <b>{dir}</b>
        📋 Outcome    : <code>{decision.OutcomeCategory}</code>
        ⏰ Signal Time: <code>{signal.SignalTimeUtc:yyyy-MM-dd HH:mm:ss} UTC</code>
        ⏳ Expiry     : <code>{expiryStr}</code>
        🔄 Timeframe  : <code>{signal.Timeframe}</code>

        ── Prices ─────────────────────────
        🎯 Entry Price: <code>{signal.EntryPrice:F2}</code>
        ✅ Take Profit: <code>{signal.TpPrice:F2}</code>
        🛑 Stop Loss  : <code>{signal.SlPrice:F2}</code>
        📊 R:R Ratio  : <code>{rr}:1</code>

        ── Risk ───────────────────────────
        💵 Risk USD   : <code>${signal.RiskUsd:F2}</code>
        📉 Risk %     : <code>{signal.RiskPercent:F2}%</code>

        ── Strategy ───────────────────────
        🏆 Score      : <code>{signal.ConfidenceScore}/100</code>{blended}
        📡 15m Regime : {regimeStr}
        📦 Version    : <code>{signal.StrategyVersion}</code>
        {mlStr}

        ── Top Reasons ────────────────────
        {reasonBlock}

        ── Reject Codes ───────────────────
        ⚠️ <code>{rejectCodes}</code>
        """;
    }

    // ─── Helpers ──────────────────────────────────────────

    private static int TfMinutes(string tf) => tf switch
    {
        "1m"  => 1,
        "5m"  => 5,
        "15m" => 15,
        "30m" => 30,
        "1h"  => 60,
        "4h"  => 240,
        _     => 5
    };

    /// <summary>Escape HTML special characters for Telegram HTML parse mode.</summary>
    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
```

---

### 2.4 `tests/EthSignal.Tests/Infrastructure/TelegramNotifierTests.cs`

**Purpose:** xUnit unit tests covering all notification scenarios using Moq.  
See Section 6 for full test case specifications.

---

## 3. FILES TO MODIFY

---

### 3.1 `src/EthSignal.Web/Program.cs`

**Where:** After credential validation block (after line ~60), before `builder.Services.AddSingleton<ICapitalClient>`.

**What to add — read config and register TelegramNotifier:**

```csharp
// ─── Telegram Notifications ────────────────────────────
var telegramToken   = builder.Configuration["TELEGRAM_BOT_TOKEN"]
    ?? builder.Configuration["Telegram:BotToken"]
    ?? "";
var telegramChatIds = (builder.Configuration["Telegram:ChatIds"] ?? "1495017760")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(id => long.TryParse(id, out var v) ? v : 0L)
    .Where(id => id > 0)
    .ToList();

builder.Services.AddSingleton<ITelegramNotifier>(sp =>
    new TelegramNotifier(
        telegramToken,
        telegramChatIds,
        sp.GetRequiredService<ILogger<TelegramNotifier>>()));
```

**Also add the using:**
```csharp
using EthSignal.Infrastructure.Notifications;
```

---

### 3.2 `src/EthSignal.Web/BackgroundServices/DataIngestionService.cs`

**Purpose:** Send system start and stop notifications.

**Step 1 — Add field and constructor parameter:**

```csharp
// Add field:
private readonly ITelegramNotifier _telegram;

// Add constructor parameter after ILogger:
ITelegramNotifier telegram,

// Assign in constructor body:
_telegram = telegram;
```

**Step 2 — System Start notification (inside ExecuteAsync, after `await _migrator.MigrateAsync`, before backfill):**

```csharp
// Notify: system started
_ = _telegram.SendAsync(
    TelegramMessageFormatter.SystemStart(symbol, "Production"),
    stoppingToken);
```

**Step 3 — System Stop notification (in the `finally` or `catch (OperationCanceledException)` block):**

Add a `try/finally` wrapper around the main `try` block so stop fires on any exit path:

```csharp
// Replace the existing try/catch with:
try
{
    // ... existing code unchanged ...
}
catch (OperationCanceledException)
{
    _logger.LogInformation("Data ingestion service stopping.");
}
catch (Exception ex)
{
    _logger.LogCritical(ex, "Data ingestion service failed.");
    _ = _telegram.SendAsync(
        TelegramMessageFormatter.SystemStop(symbol, ex.Message));
    throw;
}
finally
{
    _ = _telegram.SendAsync(
        TelegramMessageFormatter.SystemStop(symbol, "Graceful shutdown"));
}
```

> Note: `_ = _telegram.SendAsync(...)` (fire-and-forget) is used for stop/crash notifications  
> because the CancellationToken may already be cancelled at that point.

---

### 3.3 `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs`

**Purpose:** Send new signal notifications when a BUY/SELL signal is generated and persisted.

**Step 1 — Add field and constructor parameter:**

```csharp
// Add field (after existing fields, ~line 37):
private readonly ITelegramNotifier _telegram;

// Add constructor parameter (after AdaptiveParameterTuner adaptiveTuner):
ITelegramNotifier telegram,

// Assign in constructor body:
_telegram = telegram;
```

**Step 2 — Fire notification after signal is persisted (inside `TryGenerateSignal`, after `await _signalRepo.InsertSignalAsync(fullSignal, ct)`, ~line 635):**

```csharp
// After: await _signalRepo.InsertSignalAsync(fullSignal, ct);
// Add:
_ = _telegram.SendAsync(
    TelegramMessageFormatter.NewSignal(fullSignal, decision, _currentRegimeResult), ct);
```

**Step 3 — Repeat for the 1m scalp path (inside `TryEvaluateScalpSignal`, after `await _signalRepo.InsertSignalAsync(fullSignal, ct)`, ~line 845):**

```csharp
// After: await _signalRepo.InsertSignalAsync(fullSignal, ct);
// Add:
_ = _telegram.SendAsync(
    TelegramMessageFormatter.NewSignal(fullSignal, decision!, _currentRegimeResult), ct);
```

> The `decision` variable must be in scope. Confirm that `decision` returned from  
> `SignalEngine.EvaluateWithDecision` is available at the notification call site.  
> If `decision` is `null` in the scalp path (nullable), create a minimal fallback decision  
> before the notification rather than skipping it.

---

### 3.4 `src/EthSignal.Infrastructure/EthSignal.Infrastructure.csproj`

No new NuGet packages required. `System.Net.Http` is part of .NET 9 base libraries.

---

### 3.5 `src/EthSignal.Web/appsettings.json`

**Add Telegram section (no secrets here — token comes from environment):**

```json
"Telegram": {
  "ChatIds": "1495017760"
}
```

> The bot token must be set via environment variable `TELEGRAM_BOT_TOKEN`.  
> Never commit the token to source control.

---

### 3.6 `src/EthSignal.Web/appsettings.Development.json`

**Add for local dev/testing (uses the real bot token during development):**

```json
"Telegram": {
  "BotToken": "8634786346:AAF-eqW95DWCxMuff0Mz9Y5XvK_8bxqPNFI",
  "ChatIds": "1495017760"
}
```

---

## 4. CONFIGURATION

### 4.1 Configuration Keys

| Key | Source | Description |
|-----|--------|-------------|
| `TELEGRAM_BOT_TOKEN` | Environment variable (production) | Bot token — never in appsettings.json |
| `Telegram:BotToken` | appsettings.Development.json only | Dev/test bot token |
| `Telegram:ChatIds` | appsettings.json | Comma-separated list of Chat IDs |

### 4.2 Multi-Chat-ID Example

To add a second recipient (e.g. a group chat):

```json
"Telegram": {
  "ChatIds": "1495017760,-1002345678901"
}
```

Group Chat IDs are negative. Individual chat IDs are positive.

### 4.3 Disabling Notifications

Set `TELEGRAM_BOT_TOKEN` to empty string or omit it.  
The `TelegramNotifier.SendAsync` method returns immediately when the token is empty — no error thrown.

---

## 5. MESSAGE FORMAT SPECIFICATION

### 5.1 System Start Message Example

```
🟢 ETH Signal System — STARTED

🔗 Symbol      : ETHUSD
🌐 Environment : Production
🕐 Time (UTC)  : 2026-04-06 08:00:00

Live tick processor is active. Monitoring for signals.
```

### 5.2 System Stop Message Example

```
🔴 ETH Signal System — STOPPED

🔗 Symbol  : ETHUSD
📋 Reason  : Graceful shutdown
🕐 Time    : 2026-04-06 16:00:00
```

### 5.3 New Signal Message Example — BUY

```
📈 NEW SIGNAL — ETHUSD 5m

── Decision ───────────────────────
📌 Direction  : 🟢 BUY
📋 Outcome    : SIGNAL_GENERATED
⏰ Signal Time: 2026-04-06 09:35:00 UTC
⏳ Expiry     : 2026-04-06 14:35:00 UTC
🔄 Timeframe  : 5m

── Prices ─────────────────────────
🎯 Entry Price: 2062.45
✅ Take Profit: 2069.33
🛑 Stop Loss  : 2058.92
📊 R:R Ratio  : 2.03:1

── Risk ───────────────────────────
💵 Risk USD   : $0.25
📉 Risk %     : 0.50%

── Strategy ───────────────────────
🏆 Score      : 72/100
🔀 Blended Conf: 68%
📡 15m Regime : BULLISH (score 4/5)
📦 Version    : v3.0

── ML Prediction ──────────────────
🤖 ML Mode        : SHADOW
📊 Win Probability: 64.3%
🎯 Confidence     : 68%
📐 Threshold      : 62
💰 Expected R     : +0.47R
🔬 Model          : heuristic-v1
⚡ Active         : SHADOW — annotation only

── Top Reasons ────────────────────
  1. Regime=BULLISH aligned (+20)
  2. Pullback-and-reclaim confirmed (+20)
  3. RSI(52.3) in zone (+15)

── Reject Codes ───────────────────
⚠️ ADX_TOO_LOW, BODY_RATIO_TOO_SMALL
```

### 5.4 New Signal Message Example — SELL

Same structure, with `📉 SELL` direction and appropriate prices.

---

## 6. TEST CASES

### 6.1 Test File Location

```
tests/EthSignal.Tests/Infrastructure/TelegramNotifierTests.cs
```

### 6.2 Full Test Class

```csharp
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EthSignal.Tests.Infrastructure;

public class TelegramNotifierTests
{
    // ─── TC-1: Constructor — empty token disables sending ─────────────
    [Fact]
    public async Task SendAsync_EmptyToken_ReturnsWithoutSending()
    {
        // Arrange: notifier with blank token
        var notifier = new TelegramNotifier("", [1495017760L], NullLogger<TelegramNotifier>.Instance);

        // Act + Assert: no exception, completes immediately
        await notifier.Invoking(n => n.SendAsync("test")).Should().NotThrowAsync();
    }

    // ─── TC-2: Constructor — empty chat list disables sending ──────────
    [Fact]
    public async Task SendAsync_NoChatIds_ReturnsWithoutSending()
    {
        var notifier = new TelegramNotifier("sometoken", [], NullLogger<TelegramNotifier>.Instance);
        await notifier.Invoking(n => n.SendAsync("test")).Should().NotThrowAsync();
    }

    // ─── TC-3: Formatter — SystemStart contains required fields ────────
    [Fact]
    public void SystemStart_ContainsRequiredFields()
    {
        var msg = TelegramMessageFormatter.SystemStart("ETHUSD", "Production");

        msg.Should().Contain("STARTED");
        msg.Should().Contain("ETHUSD");
        msg.Should().Contain("Production");
        msg.Should().Contain("🟢");
    }

    // ─── TC-4: Formatter — SystemStop contains required fields ─────────
    [Fact]
    public void SystemStop_ContainsRequiredFields()
    {
        var msg = TelegramMessageFormatter.SystemStop("ETHUSD", "Graceful shutdown");

        msg.Should().Contain("STOPPED");
        msg.Should().Contain("ETHUSD");
        msg.Should().Contain("Graceful shutdown");
        msg.Should().Contain("🔴");
    }

    // ─── TC-5: Formatter — BUY signal contains all required data fields
    [Fact]
    public void NewSignal_Buy_ContainsAllRequiredFields()
    {
        var signal = MakeSignal(SignalDirection.BUY);
        var decision = MakeDecision(signal, mlPrediction: null);
        var regime = MakeRegime(Regime.BULLISH);

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, regime);

        msg.Should().Contain("BUY");
        msg.Should().Contain("2062");          // entry price
        msg.Should().Contain("SIGNAL_GENERATED");
        msg.Should().Contain("BULLISH");
        msg.Should().Contain("📈");
        msg.Should().Contain("Take Profit");
        msg.Should().Contain("Stop Loss");
        msg.Should().Contain("R:R Ratio");
        msg.Should().Contain("Score");
        msg.Should().Contain("Risk");
        msg.Should().Contain("Regime");
    }

    // ─── TC-6: Formatter — SELL signal direction label ─────────────────
    [Fact]
    public void NewSignal_Sell_ContainsSellLabel()
    {
        var signal = MakeSignal(SignalDirection.SELL);
        var decision = MakeDecision(signal, mlPrediction: null);

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, null);

        msg.Should().Contain("SELL");
        msg.Should().Contain("📉");
        msg.Should().NotContain("BUY");
    }

    // ─── TC-7: Formatter — ML SHADOW block rendered when prediction present
    [Fact]
    public void NewSignal_WithMlPrediction_RendersMLBlock()
    {
        var signal = MakeSignal(SignalDirection.BUY);
        var ml = new MlPrediction
        {
            PredictionId = Guid.NewGuid(),
            EvaluationId = Guid.NewGuid(),
            ModelVersion = "v2026_test",
            ModelType = "outcome_predictor",
            PredictedWinProbability = 0.643m,
            CalibratedConfidence = 68,
            RecommendedThreshold = 62,
            ExpectedValueR = 0.47m,
            InferenceLatencyUs = 120,
            IsActive = false,
            Mode = MlMode.SHADOW
        };
        var decision = MakeDecision(signal, ml);

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, null);

        msg.Should().Contain("64.3%");         // win probability
        msg.Should().Contain("68%");           // calibrated confidence
        msg.Should().Contain("62");            // threshold
        msg.Should().Contain("+0.47R");        // expected R
        msg.Should().Contain("v2026_test");    // model version
        msg.Should().Contain("SHADOW");
        msg.Should().Contain("annotation only");
    }

    // ─── TC-8: Formatter — ML ACTIVE block shows "gating decisions" ────
    [Fact]
    public void NewSignal_WithActiveMl_ShowsActiveLabel()
    {
        var signal = MakeSignal(SignalDirection.BUY);
        var ml = new MlPrediction
        {
            PredictionId = Guid.NewGuid(),
            EvaluationId = Guid.NewGuid(),
            ModelVersion = "v_active",
            ModelType = "outcome_predictor",
            PredictedWinProbability = 0.72m,
            CalibratedConfidence = 75,
            RecommendedThreshold = 65,
            ExpectedValueR = 0.8m,
            InferenceLatencyUs = 100,
            IsActive = true,
            Mode = MlMode.ACTIVE
        };
        var decision = MakeDecision(signal, ml);

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, null);

        msg.Should().Contain("ACTIVE");
        msg.Should().Contain("gating decisions");
    }

    // ─── TC-9: Formatter — null regime shows UNKNOWN ───────────────────
    [Fact]
    public void NewSignal_NullRegime_ShowsUnknown()
    {
        var signal = MakeSignal(SignalDirection.BUY);
        var decision = MakeDecision(signal, null);

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, null);

        msg.Should().Contain("UNKNOWN");
    }

    // ─── TC-10: Formatter — reject codes rendered ─────────────────────
    [Fact]
    public void NewSignal_WithRejectCodes_RendersRejectBlock()
    {
        var signal = MakeSignal(SignalDirection.BUY);
        var decision = MakeDecision(signal, null, new[]
        {
            RejectReasonCode.ADX_TOO_LOW,
            RejectReasonCode.BODY_RATIO_TOO_SMALL
        });

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, null);

        msg.Should().Contain("ADX_TOO_LOW");
        msg.Should().Contain("BODY_RATIO_TOO_SMALL");
    }

    // ─── TC-11: Formatter — HTML special chars in reasons are escaped ──
    [Fact]
    public void NewSignal_ReasonsWithHtmlChars_AreEscaped()
    {
        var signal = MakeSignal(SignalDirection.BUY);
        var decision = MakeDecision(signal, null, [],
            reasons: ["Score < threshold & direction > 0"]);

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, null);

        msg.Should().Contain("&lt;");
        msg.Should().Contain("&amp;");
        msg.Should().Contain("&gt;");
        msg.Should().NotContain("<threshold");
    }

    // ─── TC-12: Formatter — blended confidence shown when present ──────
    [Fact]
    public void NewSignal_BlendedConfidence_RenderedWhenSet()
    {
        var signal = MakeSignal(SignalDirection.BUY);
        var decision = MakeDecision(signal, null) with { BlendedConfidence = 71 };

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, null);

        msg.Should().Contain("Blended Conf");
        msg.Should().Contain("71%");
    }

    // ─── TC-13: Formatter — no blended confidence section when null ────
    [Fact]
    public void NewSignal_NoBlendedConfidence_SectionOmitted()
    {
        var signal = MakeSignal(SignalDirection.BUY);
        var decision = MakeDecision(signal, null); // BlendedConfidence is null

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, null);

        msg.Should().NotContain("Blended Conf");
    }

    // ─── TC-14: Formatter — R:R ratio computed correctly ───────────────
    [Fact]
    public void NewSignal_RRRatio_ComputedCorrectly()
    {
        // Entry=2062, TP=2070 (8 pts), SL=2058 (4 pts) → RR = 2.0
        var signal = MakeSignal(SignalDirection.BUY, entry: 2062m, tp: 2070m, sl: 2058m);
        var decision = MakeDecision(signal, null);

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, null);

        msg.Should().Contain("2:1");
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static SignalRecommendation MakeSignal(
        SignalDirection dir,
        decimal entry = 2062.45m,
        decimal tp = 2069.33m,
        decimal sl = 2058.92m) =>
        new()
        {
            Symbol          = "ETHUSD",
            Timeframe       = "5m",
            SignalTimeUtc   = DateTimeOffset.UtcNow,
            Direction       = dir,
            EntryPrice      = entry,
            TpPrice         = tp,
            SlPrice         = sl,
            RiskPercent     = 0.5m,
            RiskUsd         = 0.25m,
            ConfidenceScore = 72,
            Regime          = Regime.BULLISH,
            StrategyVersion = "v3.0",
            Reasons         = ["Regime aligned (+20)", "Pullback confirmed (+20)", "RSI in zone (+15)"]
        };

    private static SignalDecision MakeDecision(
        SignalRecommendation signal,
        MlPrediction? mlPrediction,
        IEnumerable<RejectReasonCode>? codes = null,
        IEnumerable<string>? reasons = null) =>
        new()
        {
            Symbol            = signal.Symbol,
            Timeframe         = signal.Timeframe,
            DecisionTimeUtc   = signal.SignalTimeUtc,
            BarTimeUtc        = signal.SignalTimeUtc,
            DecisionType      = signal.Direction,
            OutcomeCategory   = OutcomeCategory.SIGNAL_GENERATED,
            UsedRegime        = Regime.BULLISH,
            ReasonCodes       = (codes ?? []).ToList(),
            ReasonDetails     = (reasons ?? signal.Reasons).ToList(),
            IndicatorSnapshot = new Dictionary<string, decimal>(),
            MlPrediction      = mlPrediction,
            SourceMode        = SourceMode.LIVE
        };

    private static RegimeResult MakeRegime(Regime r) =>
        new()
        {
            Symbol               = "ETHUSD",
            CandleOpenTimeUtc    = DateTimeOffset.UtcNow,
            Regime               = r,
            RegimeScore          = 4,
            TriggeredConditions  = ["EMA alignment", "ADX > 20", "HH/HL structure"],
            DisqualifyingConditions = []
        };
}
```

---

## 7. IMPLEMENTATION CHECKLIST

Work through these steps in order. Mark each done before moving to the next.

```
[ ] 1. Create: src/EthSignal.Infrastructure/Notifications/ITelegramNotifier.cs
[ ] 2. Create: src/EthSignal.Infrastructure/Notifications/TelegramNotifier.cs
[ ] 3. Create: src/EthSignal.Infrastructure/Notifications/TelegramMessageFormatter.cs
[ ] 4. Modify: src/EthSignal.Web/Program.cs
         - Add using EthSignal.Infrastructure.Notifications
         - Read TELEGRAM_BOT_TOKEN from config
         - Read Telegram:ChatIds from config
         - Register ITelegramNotifier as singleton
[ ] 5. Modify: src/EthSignal.Web/BackgroundServices/DataIngestionService.cs
         - Add ITelegramNotifier field + constructor parameter
         - Send SystemStart after MigrateAsync
         - Send SystemStop in finally/catch blocks
[ ] 6. Modify: src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs
         - Add ITelegramNotifier field + constructor parameter
         - Send NewSignal after InsertSignalAsync in TryGenerateSignal (~line 635)
         - Send NewSignal after InsertSignalAsync in TryEvaluateScalpSignal (~line 845)
[ ] 7. Modify: src/EthSignal.Web/appsettings.json
         - Add "Telegram": { "ChatIds": "1495017760" }
[ ] 8. Modify: src/EthSignal.Web/appsettings.Development.json
         - Add "Telegram": { "BotToken": "...", "ChatIds": "1495017760" }
[ ] 9. Create: tests/EthSignal.Tests/Infrastructure/TelegramNotifierTests.cs
         - 14 test cases as specified in Section 6
[  ] 10. Run tests: dotnet test tests/EthSignal.Tests/
[  ] 11. Manual smoke test: start system, verify 3 messages arrive in Telegram
         - 🟢 System start
         - (wait for a signal or trigger replay)
         - 📈/📉 Signal notification
         - Stop system → 🔴 System stop
```

---

## SECURITY NOTES

- The bot token `8634786346:AAF-eqW95DWCxMuff0Mz9Y5XvK_8bxqPNFI` is included in this SRS for reference only. It is already in `appsettings.Development.json`. In production, set `TELEGRAM_BOT_TOKEN` as an environment variable and remove the token from all JSON files before any public commit.
- Messages are sent over HTTPS to `api.telegram.org`. No sensitive trading credentials are included in Telegram messages — only public market data.
- The `TelegramNotifier` is fire-and-forget from the trading engine's perspective. Any Telegram API failure is logged at Warning level and does not affect signal generation.

---

*End of Telegram Notifications SRS*
