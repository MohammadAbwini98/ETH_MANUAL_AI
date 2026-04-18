using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Notifications;

public static class TelegramMessageFormatter
{
    // ─── System Events ────────────────────────────────────

    /// <summary>Sent when DataIngestionService.ExecuteAsync begins.</summary>
    public static string SystemStart(string symbol, string environment) =>
        $"""
        🟢 <b>{InstrumentName(symbol)} Signal System — STARTED</b>

        🔗 Symbol      : <code>{symbol}</code>
        🌐 Environment : <code>{environment}</code>
        🕐 Time (UTC)  : <code>{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}</code>

        Live tick processor is active. Monitoring for signals.
        """;

    /// <summary>Sent when DataIngestionService.StopAsync / cancellation is triggered.</summary>
    public static string SystemStop(string symbol, string reason) =>
        $"""
        🔴 <b>{InstrumentName(symbol)} Signal System — STOPPED</b>

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
        RegimeResult? regime,
        int outcomeTimeoutBars = 60)
    {
        var dir = signal.Direction == SignalDirection.BUY ? "🟢 BUY" : "🔴 SELL";
        var dirIcon = signal.Direction == SignalDirection.BUY ? "📈" : "📉";

        // Expiry: signal time + (timeframe duration × OutcomeTimeoutBars)
        // Approximate using standard TF durations
        var tfMinutes = TfMinutes(signal.Timeframe);
        var expiry = signal.SignalTimeUtc.AddMinutes(tfMinutes * outcomeTimeoutBars);
        var expiryStr = expiry.ToString("yyyy-MM-dd HH:mm") + " UTC";

        // Risk/Reward
        var rr = signal.SlPrice > 0
            ? Math.Round(Math.Abs(signal.TpPrice - signal.EntryPrice) /
                         Math.Abs(signal.EntryPrice - signal.SlPrice), 2)
            : 0m;

        // ML block
        var ml = decision.MlPrediction;
        var mlStr = ml != null
            ? $"""

               ── ML Prediction ──────────────────
        🤖 ML Mode        : <code>{ml.Mode}</code>
        📈 Raw Win Prob   : <code>{ml.RawWinProbability:P1}</code>
        📊 Cal Win Prob   : <code>{ml.CalibratedWinProbability:P1}</code>
        🎯 Confidence     : <code>{ml.PredictionConfidence}%</code>
        📐 Threshold      : <code>{ml.RecommendedThreshold}</code>
        💰 Expected R     : <code>{ml.ExpectedValueR:+0.00;-0.00}R</code>
        🔬 Model          : <code>{Escape(ml.ModelVersion)}</code>
        ⚡ Active         : <code>{(ml.IsActive ? "YES — gating decisions" : "SHADOW — annotation only")}</code>
        """
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
        "1m" => 1,
        "5m" => 5,
        "15m" => 15,
        "30m" => 30,
        "1h" => 60,
        "4h" => 240,
        _ => 5
    };

    /// <summary>Escape HTML special characters for Telegram HTML parse mode.</summary>
    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Returns a display name for the instrument symbol.</summary>
    private static string InstrumentName(string symbol) => symbol.ToUpperInvariant() switch
    {
        "ETHUSD" => "ETH",
        _ => symbol
    };
}
