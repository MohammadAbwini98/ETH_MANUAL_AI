namespace EthSignal.Domain.Models;

/// <summary>
/// ML feature vector built at signal evaluation time.
/// 80 features across 8 categories: raw indicators, derived, contextual, lookback,
/// market structure, volatility regime, signal saturation, and BTC cross-asset context.
/// </summary>
public sealed record MlFeatureVector
{
    public required Guid EvaluationId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Symbol { get; init; }
    public required string Timeframe { get; init; }

    // ─── Category A: 14 raw indicator features ──────────
    public decimal Ema20 { get; init; }
    public decimal Ema50 { get; init; }
    public decimal Rsi14 { get; init; }
    public decimal MacdHist { get; init; }
    public decimal Adx14 { get; init; }
    public decimal PlusDi { get; init; }
    public decimal MinusDi { get; init; }
    public decimal Atr14 { get; init; }
    public decimal Vwap { get; init; }
    public decimal VolumeSma20 { get; init; }
    public decimal Spread { get; init; }
    public decimal CloseMid { get; init; }
    public decimal Volume { get; init; }
    public decimal BodyRatio { get; init; }

    // ─── Category B: 18 derived features ────────────────
    public decimal Ema20MinusEma50 { get; init; }
    public decimal Ema20MinusEma50Pct { get; init; }
    public decimal Ema20Slope3 { get; init; }
    public decimal Ema20Slope5 { get; init; }
    public decimal Rsi14Delta { get; init; }
    public decimal Rsi14Delta3 { get; init; }
    public decimal MacdHistDelta { get; init; }
    public decimal MacdHistDelta3 { get; init; }
    public decimal Adx14Delta { get; init; }
    public decimal Atr14Pct { get; init; }
    public decimal Atr14DeltaPct { get; init; }
    public decimal DistanceToEma20Pct { get; init; }
    public decimal DistanceToVwapPct { get; init; }
    public decimal VolumeRatio { get; init; }
    public decimal SpreadPct { get; init; }
    public decimal DiDifferential { get; init; }
    public decimal DiRatio { get; init; }
    public decimal CandleRangePct { get; init; }

    // ─── Category C: 13 contextual features ─────────────
    public int RegimeLabel { get; init; }       // 0=NEUTRAL, 1=BULLISH, 2=BEARISH
    public int RegimeScore { get; init; }
    public int RegimeAgeBars { get; init; }
    public int RuleBasedScore { get; init; }
    public int DirectionEncoded { get; init; }   // 1=BUY, 0=NO_TRADE, -1=SELL
    public int TimeframeEncoded { get; init; }   // 0=unknown, 1=1m, 2=5m, 3=15m, 4=30m, 5=1h, 6=4h
    public int HourOfDay { get; init; }
    public int DayOfWeek { get; init; }
    public int MinutesSinceOpen { get; init; }
    public bool IsLondonSession { get; init; }
    public bool IsNySession { get; init; }
    public bool IsAsiaSession { get; init; }
    public bool IsOverlap { get; init; }

    // ─── Category D: 14 lookback features ───────────────
    public decimal RecentWinRate10 { get; init; }
    public decimal RecentWinRate20 { get; init; }
    public decimal RecentAvgPnlR10 { get; init; }
    public decimal RecentAvgPnlR20 { get; init; }
    public int ConsecutiveWins { get; init; }
    public int ConsecutiveLosses { get; init; }
    public int BarsSinceLastSignal { get; init; }
    public decimal AvgAtr20Bars { get; init; }
    public decimal AtrZscore { get; init; }
    public decimal AvgVolume10Bars { get; init; }
    public decimal VolumeZscore { get; init; }
    public decimal PriceRange20BarsPct { get; init; }
    public int RegimeChangesLast20 { get; init; }
    public decimal PullbackDepthPct { get; init; }

    // ─── Category E: 7 market structure features ────────
    public decimal SessionRangePositionPct { get; init; }    // where price sits within session range [0..1]
    public decimal DistanceToPriorDayHighPct { get; init; }  // (close - priorDayHigh) / close
    public decimal DistanceToPriorDayLowPct { get; init; }   // (close - priorDayLow) / close
    public decimal DistanceToSessionVwapPct { get; init; }   // (close - sessionVwap) / close
    public decimal RangePositionPct { get; init; }           // where close sits in 20-bar range [0..1]
    public decimal DistanceTo20BarHighPct { get; init; }     // (close - 20barHigh) / close
    public decimal DistanceTo20BarLowPct { get; init; }      // (close - 20barLow) / close

    // ─── Category F: 6 volatility regime features ───────
    public decimal RealizedVol15m { get; init; }             // realized volatility over ~3 bars (15m)
    public decimal RealizedVol1h { get; init; }              // realized volatility over ~12 bars (1h)
    public decimal RealizedVol4h { get; init; }              // realized volatility over ~48 bars (4h)
    public decimal VolatilityCompressionFlag { get; init; }  // 1 if short-vol < 0.7 * long-vol, else 0
    public decimal VolatilityExpansionFlag { get; init; }    // 1 if short-vol > 1.5 * long-vol, else 0
    public decimal AtrPercentileRank { get; init; }          // ATR percentile within rolling 50-bar window [0..1]

    // ─── Category G: 5 signal saturation features ───────
    public int SignalsLast10Bars { get; init; }              // total signals in last 10 bars
    public int SameDirectionSignalsLast10 { get; init; }     // same-direction signals in last 10 bars
    public int OppositeDirectionSignalsLast10 { get; init; } // opposite-direction signals in last 10 bars
    public int RecentStopOutCount { get; init; }             // stop-outs in last 10 resolved outcomes
    public decimal RecentFalseBreakoutRate { get; init; }    // fraction of recent losses that were stop-outs

    // ─── Category H: 3 BTC cross-asset context (optional, 0 when unavailable) ──
    public decimal BtcRecentReturn { get; init; }            // BTC close-to-close return over lookback
    public int BtcRegimeLabel { get; init; }                 // 0=NEUTRAL, 1=BULLISH, 2=BEARISH (0 if unavailable)
    public decimal EthBtcRelativeStrength { get; init; }     // ETH return minus BTC return

    public static int EncodeTimeframe(string? timeframe) => timeframe?.ToLowerInvariant() switch
    {
        "1m" => 1,
        "5m" => 2,
        "15m" => 3,
        "30m" => 4,
        "1h" => 5,
        "4h" => 6,
        _ => 0
    };

    public static string DecodeTimeframe(int encoded) => encoded switch
    {
        1 => "1m",
        2 => "5m",
        3 => "15m",
        4 => "30m",
        5 => "1h",
        6 => "4h",
        _ => "unknown"
    };

    public IReadOnlyDictionary<string, float> ToFeatureMap()
    {
        return new Dictionary<string, float>(StringComparer.Ordinal)
        {
            // Category A (14)
            ["ema20"] = (float)Ema20,
            ["ema50"] = (float)Ema50,
            ["rsi14"] = (float)Rsi14,
            ["macd_hist"] = (float)MacdHist,
            ["adx14"] = (float)Adx14,
            ["plus_di"] = (float)PlusDi,
            ["minus_di"] = (float)MinusDi,
            ["atr14"] = (float)Atr14,
            ["vwap"] = (float)Vwap,
            ["volume_sma20"] = (float)VolumeSma20,
            ["spread"] = (float)Spread,
            ["close_mid"] = (float)CloseMid,
            ["volume"] = (float)Volume,
            ["body_ratio"] = (float)BodyRatio,

            // Category B (18)
            ["ema20_minus_ema50"] = (float)Ema20MinusEma50,
            ["ema20_minus_ema50_pct"] = (float)Ema20MinusEma50Pct,
            ["ema20_slope_3"] = (float)Ema20Slope3,
            ["ema20_slope_5"] = (float)Ema20Slope5,
            ["rsi14_delta"] = (float)Rsi14Delta,
            ["rsi14_delta_3"] = (float)Rsi14Delta3,
            ["macd_hist_delta"] = (float)MacdHistDelta,
            ["macd_hist_delta_3"] = (float)MacdHistDelta3,
            ["adx14_delta"] = (float)Adx14Delta,
            ["atr14_pct"] = (float)Atr14Pct,
            ["atr14_delta_pct"] = (float)Atr14DeltaPct,
            ["distance_to_ema20_pct"] = (float)DistanceToEma20Pct,
            ["distance_to_vwap_pct"] = (float)DistanceToVwapPct,
            ["volume_ratio"] = (float)VolumeRatio,
            ["spread_pct"] = (float)SpreadPct,
            ["di_differential"] = (float)DiDifferential,
            ["di_ratio"] = (float)DiRatio,
            ["candle_range_pct"] = (float)CandleRangePct,

            // Category C (13)
            ["regime_label"] = RegimeLabel,
            ["regime_score"] = RegimeScore,
            ["regime_age_bars"] = RegimeAgeBars,
            ["rule_based_score"] = RuleBasedScore,
            ["direction_encoded"] = DirectionEncoded,
            ["timeframe_encoded"] = TimeframeEncoded,
            ["hour_of_day"] = HourOfDay,
            ["day_of_week"] = DayOfWeek,
            ["minutes_since_open"] = MinutesSinceOpen,
            ["is_london_session"] = IsLondonSession ? 1f : 0f,
            ["is_ny_session"] = IsNySession ? 1f : 0f,
            ["is_asia_session"] = IsAsiaSession ? 1f : 0f,
            ["is_overlap"] = IsOverlap ? 1f : 0f,

            // Category D (14)
            ["recent_win_rate_10"] = (float)RecentWinRate10,
            ["recent_win_rate_20"] = (float)RecentWinRate20,
            ["recent_avg_pnl_r_10"] = (float)RecentAvgPnlR10,
            ["recent_avg_pnl_r_20"] = (float)RecentAvgPnlR20,
            ["consecutive_wins"] = ConsecutiveWins,
            ["consecutive_losses"] = ConsecutiveLosses,
            ["bars_since_last_signal"] = BarsSinceLastSignal,
            ["avg_atr_20_bars"] = (float)AvgAtr20Bars,
            ["atr_zscore"] = (float)AtrZscore,
            ["avg_volume_10_bars"] = (float)AvgVolume10Bars,
            ["volume_zscore"] = (float)VolumeZscore,
            ["price_range_20_bars_pct"] = (float)PriceRange20BarsPct,
            ["regime_changes_last_20"] = RegimeChangesLast20,
            ["pullback_depth_pct"] = (float)PullbackDepthPct,

            // Category E (7) — market structure
            ["session_range_position_pct"] = (float)SessionRangePositionPct,
            ["distance_to_prior_day_high_pct"] = (float)DistanceToPriorDayHighPct,
            ["distance_to_prior_day_low_pct"] = (float)DistanceToPriorDayLowPct,
            ["distance_to_session_vwap_pct"] = (float)DistanceToSessionVwapPct,
            ["range_position_pct"] = (float)RangePositionPct,
            ["distance_to_20_bar_high_pct"] = (float)DistanceTo20BarHighPct,
            ["distance_to_20_bar_low_pct"] = (float)DistanceTo20BarLowPct,

            // Category F (6) — volatility regime
            ["realized_vol_15m"] = (float)RealizedVol15m,
            ["realized_vol_1h"] = (float)RealizedVol1h,
            ["realized_vol_4h"] = (float)RealizedVol4h,
            ["volatility_compression_flag"] = (float)VolatilityCompressionFlag,
            ["volatility_expansion_flag"] = (float)VolatilityExpansionFlag,
            ["atr_percentile_rank"] = (float)AtrPercentileRank,

            // Category G (5) — signal saturation
            ["signals_last_10_bars"] = SignalsLast10Bars,
            ["same_direction_signals_last_10"] = SameDirectionSignalsLast10,
            ["opposite_direction_signals_last_10"] = OppositeDirectionSignalsLast10,
            ["recent_stop_out_count"] = RecentStopOutCount,
            ["recent_false_breakout_rate"] = (float)RecentFalseBreakoutRate,

            // Category H (3) — BTC cross-asset context
            ["btc_recent_return"] = (float)BtcRecentReturn,
            ["btc_regime_label"] = BtcRegimeLabel,
            ["eth_btc_relative_strength"] = (float)EthBtcRelativeStrength
        };
    }

    /// <summary>Convert all features to a float array for ML inference.</summary>
    public float[] ToFloatArray(IReadOnlyList<string>? orderedFeatureNames = null)
    {
        var featureMap = ToFeatureMap();
        var featureNames = orderedFeatureNames ?? FeatureNames;
        var values = new float[featureNames.Count];

        for (var i = 0; i < featureNames.Count; i++)
            values[i] = featureMap.TryGetValue(featureNames[i], out var value) ? value : 0f;

        return values;
    }

    /// <summary>Feature names in the same order as ToFloatArray(). Used for model metadata.</summary>
    public static readonly IReadOnlyList<string> FeatureNames = new[]
    {
        // Category A (14)
        "ema20", "ema50", "rsi14", "macd_hist", "adx14", "plus_di", "minus_di", "atr14",
        "vwap", "volume_sma20", "spread", "close_mid", "volume", "body_ratio",
        // Category B (18)
        "ema20_minus_ema50", "ema20_minus_ema50_pct", "ema20_slope_3", "ema20_slope_5",
        "rsi14_delta", "rsi14_delta_3", "macd_hist_delta", "macd_hist_delta_3",
        "adx14_delta", "atr14_pct", "atr14_delta_pct", "distance_to_ema20_pct",
        "distance_to_vwap_pct", "volume_ratio", "spread_pct", "di_differential",
        "di_ratio", "candle_range_pct",
        // Category C (13)
        "regime_label", "regime_score", "regime_age_bars", "rule_based_score",
        "direction_encoded", "timeframe_encoded", "hour_of_day", "day_of_week", "minutes_since_open",
        "is_london_session", "is_ny_session", "is_asia_session", "is_overlap",
        // Category D (14)
        "recent_win_rate_10", "recent_win_rate_20",
        "recent_avg_pnl_r_10", "recent_avg_pnl_r_20",
        "consecutive_wins", "consecutive_losses",
        "bars_since_last_signal",
        "avg_atr_20_bars", "atr_zscore", "avg_volume_10_bars", "volume_zscore",
        "price_range_20_bars_pct", "regime_changes_last_20", "pullback_depth_pct",
        // Category E (7) — market structure
        "session_range_position_pct", "distance_to_prior_day_high_pct",
        "distance_to_prior_day_low_pct", "distance_to_session_vwap_pct",
        "range_position_pct", "distance_to_20_bar_high_pct", "distance_to_20_bar_low_pct",
        // Category F (6) — volatility regime
        "realized_vol_15m", "realized_vol_1h", "realized_vol_4h",
        "volatility_compression_flag", "volatility_expansion_flag", "atr_percentile_rank",
        // Category G (5) — signal saturation
        "signals_last_10_bars", "same_direction_signals_last_10",
        "opposite_direction_signals_last_10", "recent_stop_out_count",
        "recent_false_breakout_rate",
        // Category H (3) — BTC cross-asset context
        "btc_recent_return", "btc_regime_label", "eth_btc_relative_strength"
    };
}
