using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace EthSignal.Infrastructure.Db;

public sealed class PostgresPortalOverridesRepository : IPortalOverridesRepository
{
    private readonly string _connectionString;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PostgresPortalOverridesRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task EnsureTableExistsAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            CREATE TABLE IF NOT EXISTS ""ETH"".portal_overrides (
                id INT NOT NULL DEFAULT 1 PRIMARY KEY,
                CONSTRAINT portal_overrides_singleton CHECK (id = 1),
                settings JSONB NOT NULL DEFAULT '{}',
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_by TEXT
            );
            INSERT INTO ""ETH"".portal_overrides (id, settings)
            VALUES (1, '{}')
            ON CONFLICT (id) DO NOTHING;", conn);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<PortalOverrides?> GetAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(
            @"SELECT settings, updated_at, updated_by FROM ""ETH"".portal_overrides WHERE id = 1;", conn);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        var json = r.GetString(0);
        var updatedAt = r.IsDBNull(1) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(1);
        var updatedBy = r.IsDBNull(2) ? null : r.GetString(2);

        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOpts);
        if (settings == null) return null;

        return new PortalOverrides
        {
            MaxOpenPositions = TryGetInt(settings, "maxOpenPositions"),
            MaxOpenPerTimeframe = TryGetInt(settings, "maxOpenPerTimeframe"),
            MaxOpenPerDirection = TryGetInt(settings, "maxOpenPerDirection"),
            DailyLossCapPercent = TryGetDecimal(settings, "dailyLossCapPercent"),
            MaxConsecutiveLossesPerDay = TryGetInt(settings, "maxConsecutiveLossesPerDay"),
            ScalpMaxConsecutiveLossesPerDay = TryGetInt(settings, "scalpMaxConsecutiveLossesPerDay"),
            RecommendedSignalExecutionEnabled = TryGetBool(settings, "recommendedSignalExecutionEnabled"),
            UpdatedAt = updatedAt,
            UpdatedBy = updatedBy
        };
    }

    public async Task SaveAsync(PortalOverrides overrides, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var existing = await LoadRawSettingsAsync(conn, ct);
        var json = JsonSerializer.Serialize(BuildMergedSettings(existing, overrides), JsonOpts);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".portal_overrides (id, settings, updated_at, updated_by)
            VALUES (1, @settings::jsonb, NOW(), @updatedBy)
            ON CONFLICT (id) DO UPDATE
                SET settings = EXCLUDED.settings,
                    updated_at = EXCLUDED.updated_at,
                    updated_by = EXCLUDED.updated_by;", conn);

        cmd.Parameters.AddWithValue("settings", NpgsqlDbType.Jsonb, json);
        cmd.Parameters.AddWithValue("updatedBy", NpgsqlDbType.Text,
            (object?)overrides.UpdatedBy ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static int? TryGetInt(Dictionary<string, JsonElement> d, string key)
    {
        if (d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var i)) return i;
        return null;
    }

    private static bool? TryGetBool(Dictionary<string, JsonElement> d, string key)
    {
        if (d.TryGetValue(key, out var v) && (v.ValueKind is JsonValueKind.True or JsonValueKind.False))
            return v.GetBoolean();
        return null;
    }

    private static decimal? TryGetDecimal(Dictionary<string, JsonElement> d, string key)
    {
        if (d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetDecimal(out var dec)) return dec;
        return null;
    }

    internal static Dictionary<string, object?> BuildMergedSettings(
        Dictionary<string, JsonElement> existing,
        PortalOverrides overrides)
    {
        var settings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in existing)
            settings[key] = DeserializeJsonElement(value);

        if (overrides.MaxOpenPositions.HasValue) settings["maxOpenPositions"] = overrides.MaxOpenPositions.Value;
        if (overrides.MaxOpenPerTimeframe.HasValue) settings["maxOpenPerTimeframe"] = overrides.MaxOpenPerTimeframe.Value;
        if (overrides.MaxOpenPerDirection.HasValue) settings["maxOpenPerDirection"] = overrides.MaxOpenPerDirection.Value;
        if (overrides.DailyLossCapPercent.HasValue) settings["dailyLossCapPercent"] = overrides.DailyLossCapPercent.Value;
        if (overrides.MaxConsecutiveLossesPerDay.HasValue) settings["maxConsecutiveLossesPerDay"] = overrides.MaxConsecutiveLossesPerDay.Value;
        if (overrides.ScalpMaxConsecutiveLossesPerDay.HasValue) settings["scalpMaxConsecutiveLossesPerDay"] = overrides.ScalpMaxConsecutiveLossesPerDay.Value;
        if (overrides.RecommendedSignalExecutionEnabled.HasValue) settings["recommendedSignalExecutionEnabled"] = overrides.RecommendedSignalExecutionEnabled.Value;

        return settings;
    }

    private static object? DeserializeJsonElement(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt32(out var i) => i,
            JsonValueKind.Number when value.TryGetDecimal(out var d) => d,
            JsonValueKind.Null => null,
            _ => JsonSerializer.Deserialize<object>(value.GetRawText(), JsonOpts)
        };

    private static async Task<Dictionary<string, JsonElement>> LoadRawSettingsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"SELECT settings FROM ""ETH"".portal_overrides WHERE id = 1;", conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is not string json || string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOpts)
            ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
    }
}
