using System.Text.Json;
using EthSignal.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace EthSignal.Infrastructure.Db;

/// <summary>
/// Writes sampled adaptive parameter evaluations to the adaptive_parameter_log table.
/// </summary>
public sealed class AdaptiveParameterLogRepository
{
    private readonly string _connectionString;
    private readonly ILogger<AdaptiveParameterLogRepository> _logger;

    public AdaptiveParameterLogRepository(string connectionString,
        ILogger<AdaptiveParameterLogRepository>? logger = null)
    {
        _connectionString = connectionString;
        _logger = logger ?? NullLogger<AdaptiveParameterLogRepository>.Instance;
    }

    public async Task LogAsync(AdaptiveParameterLogEntry entry, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO ""ETH"".adaptive_parameter_log
                    (bar_time_utc, condition_class, volatility_tier, trend_strength,
                     trading_session, spread_quality, volume_tier,
                     base_confidence_buy, adapted_confidence_buy,
                     base_confidence_sell, adapted_confidence_sell,
                     overlay_deltas_json, atr14, atr_sma50, adx14,
                     regime_score, spread_pct)
                VALUES (@barTime, @condition, @vol, @trend,
                        @session, @spread, @volume,
                        @baseConfBuy, @adaptedConfBuy,
                        @baseConfSell, @adaptedConfSell,
                        @overlayJson, @atr14, @atrSma50, @adx14,
                        @regimeScore, @spreadPct)", conn);

            cmd.Parameters.AddWithValue("barTime", entry.BarTimeUtc);
            cmd.Parameters.AddWithValue("condition", entry.ConditionClass);
            cmd.Parameters.AddWithValue("vol", entry.VolatilityTier);
            cmd.Parameters.AddWithValue("trend", entry.TrendStrength);
            cmd.Parameters.AddWithValue("session", entry.TradingSession);
            cmd.Parameters.AddWithValue("spread", entry.SpreadQuality);
            cmd.Parameters.AddWithValue("volume", entry.VolumeTier);
            cmd.Parameters.AddWithValue("baseConfBuy", entry.BaseConfidenceBuy);
            cmd.Parameters.AddWithValue("adaptedConfBuy", entry.AdaptedConfidenceBuy);
            cmd.Parameters.AddWithValue("baseConfSell", entry.BaseConfidenceSell);
            cmd.Parameters.AddWithValue("adaptedConfSell", entry.AdaptedConfidenceSell);
            cmd.Parameters.Add(new NpgsqlParameter("overlayJson", NpgsqlDbType.Jsonb)
            { Value = (object?)entry.OverlayDeltasJson ?? DBNull.Value });
            cmd.Parameters.AddWithValue("atr14", entry.Atr14);
            cmd.Parameters.AddWithValue("atrSma50", entry.AtrSma50);
            cmd.Parameters.AddWithValue("adx14", entry.Adx14);
            cmd.Parameters.AddWithValue("regimeScore", entry.RegimeScore);
            cmd.Parameters.AddWithValue("spreadPct", entry.SpreadPct);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AdaptiveLog] Failed to write adaptive parameter log entry");
        }
    }
}

public sealed record AdaptiveParameterLogEntry
{
    public required DateTimeOffset BarTimeUtc { get; init; }
    public required string ConditionClass { get; init; }
    public required string VolatilityTier { get; init; }
    public required string TrendStrength { get; init; }
    public required string TradingSession { get; init; }
    public required string SpreadQuality { get; init; }
    public required string VolumeTier { get; init; }
    public int BaseConfidenceBuy { get; init; }
    public int AdaptedConfidenceBuy { get; init; }
    public int BaseConfidenceSell { get; init; }
    public int AdaptedConfidenceSell { get; init; }
    public string? OverlayDeltasJson { get; init; }
    public decimal Atr14 { get; init; }
    public decimal AtrSma50 { get; init; }
    public decimal Adx14 { get; init; }
    public int RegimeScore { get; init; }
    public decimal SpreadPct { get; init; }
}
