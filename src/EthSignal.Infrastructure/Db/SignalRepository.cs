using System.Text;
using System.Text.Json;
using EthSignal.Domain.Models;
using Npgsql;
using NpgsqlTypes;

namespace EthSignal.Infrastructure.Db;

public sealed class SignalRepository : ISignalRepository
{
    private readonly string _connectionString;

    public SignalRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    private Task EnsureSchemaAsync(CancellationToken ct) =>
        RuntimeDbSchemaGuard.EnsureMigratedAsync(_connectionString, ct);

    public async Task InsertSignalAsync(SignalRecommendation signal, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".signals
                (signal_id, symbol, timeframe, signal_time_utc, direction,
                 entry_price, tp_price, sl_price, risk_percent, risk_usd,
                 confidence_score, regime, strategy_version, reasons_json, status,
                 market_condition_class, evaluation_id,
                 tp1_price, tp2_price, tp3_price, risk_reward_ratio, exit_model, exit_explanation,
                 created_at_utc)
            VALUES (@id, @s, @tf, @t, @dir, @entry, @tp, @sl, @rp, @ru,
                    @score, @regime, @sv, @reasons, @status, @mcc, @evalId,
                    @tp1, @tp2, @tp3, @rr, @exitModel, @exitExpl,
                    NOW())
            ON CONFLICT (signal_id) DO NOTHING;", conn);

        cmd.Parameters.AddWithValue("id", signal.SignalId);
        cmd.Parameters.AddWithValue("s", signal.Symbol);
        cmd.Parameters.AddWithValue("tf", signal.Timeframe);
        cmd.Parameters.AddWithValue("t", signal.SignalTimeUtc);
        cmd.Parameters.AddWithValue("dir", signal.Direction.ToString());
        cmd.Parameters.AddWithValue("entry", signal.EntryPrice);
        cmd.Parameters.AddWithValue("tp", signal.TpPrice);
        cmd.Parameters.AddWithValue("sl", signal.SlPrice);
        cmd.Parameters.AddWithValue("rp", signal.RiskPercent);
        cmd.Parameters.AddWithValue("ru", signal.RiskUsd);
        cmd.Parameters.AddWithValue("score", signal.ConfidenceScore);
        cmd.Parameters.AddWithValue("regime", signal.Regime.ToString());
        cmd.Parameters.AddWithValue("sv", signal.StrategyVersion);
        cmd.Parameters.Add(new NpgsqlParameter("reasons", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(signal.Reasons) });
        cmd.Parameters.AddWithValue("status", signal.Status.ToString());
        cmd.Parameters.AddWithValue("mcc", (object?)signal.MarketConditionClass ?? DBNull.Value);
        cmd.Parameters.AddWithValue("evalId", (object?)signal.EvaluationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tp1", signal.Tp1Price);
        cmd.Parameters.AddWithValue("tp2", signal.Tp2Price);
        cmd.Parameters.AddWithValue("tp3", signal.Tp3Price);
        cmd.Parameters.AddWithValue("rr", signal.RiskRewardRatio);
        cmd.Parameters.AddWithValue("exitModel", (object?)signal.ExitModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("exitExpl", (object?)signal.ExitExplanation ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertOutcomeAsync(SignalOutcome outcome, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".signal_outcomes
                (signal_id, evaluated_at_utc, bars_observed, tp_hit, sl_hit, partial_win,
                 outcome_label, pnl_r, mfe_price, mae_price, mfe_r, mae_r, closed_at_utc)
            VALUES (@id, @evaluated, @bars, @tp, @sl, @partial, @label, @pnl, @mfe, @mae, @mfer, @maer, @closed)
            ON CONFLICT (signal_id) DO UPDATE SET
                evaluated_at_utc = EXCLUDED.evaluated_at_utc, bars_observed = EXCLUDED.bars_observed,
                tp_hit = EXCLUDED.tp_hit, sl_hit = EXCLUDED.sl_hit, partial_win = EXCLUDED.partial_win,
                outcome_label = EXCLUDED.outcome_label, pnl_r = EXCLUDED.pnl_r,
                mfe_price = EXCLUDED.mfe_price, mae_price = EXCLUDED.mae_price,
                mfe_r = EXCLUDED.mfe_r, mae_r = EXCLUDED.mae_r,
                closed_at_utc = EXCLUDED.closed_at_utc;", conn);

        cmd.Parameters.AddWithValue("id", outcome.SignalId);
        cmd.Parameters.AddWithValue("evaluated", outcome.EvaluatedAtUtc);
        cmd.Parameters.AddWithValue("bars", outcome.BarsObserved);
        cmd.Parameters.AddWithValue("tp", outcome.TpHit);
        cmd.Parameters.AddWithValue("sl", outcome.SlHit);
        cmd.Parameters.AddWithValue("partial", outcome.PartialWin);
        cmd.Parameters.AddWithValue("label", outcome.OutcomeLabel.ToString());
        cmd.Parameters.AddWithValue("pnl", outcome.PnlR);
        cmd.Parameters.AddWithValue("mfe", outcome.MfePrice);
        cmd.Parameters.AddWithValue("mae", outcome.MaePrice);
        cmd.Parameters.AddWithValue("mfer", outcome.MfeR);
        cmd.Parameters.AddWithValue("maer", outcome.MaeR);
        cmd.Parameters.AddWithValue("closed", (object?)outcome.ClosedAtUtc ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SignalRecommendation?> GetSignalByIdAsync(Guid signalId, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT symbol, signal_id, timeframe, signal_time_utc, direction,
                   entry_price, tp_price, sl_price, risk_percent, risk_usd,
                   confidence_score, regime, strategy_version, reasons_json, status,
                   market_condition_class, evaluation_id,
                   tp1_price, tp2_price, tp3_price, risk_reward_ratio, exit_model, exit_explanation
            FROM ""ETH"".signals
            WHERE signal_id = @id
            LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("id", signalId);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return ReadSignalFromRowWithSymbol(r);
    }

    public async Task<SignalRecommendation?> GetLatestSignalAsync(string symbol, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT signal_id, timeframe, signal_time_utc, direction,
                   entry_price, tp_price, sl_price, risk_percent, risk_usd,
                   confidence_score, regime, strategy_version, reasons_json, status,
                   market_condition_class, evaluation_id,
                   tp1_price, tp2_price, tp3_price, risk_reward_ratio, exit_model, exit_explanation
            FROM ""ETH"".signals
            WHERE symbol = @s ORDER BY signal_time_utc DESC LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("s", symbol);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return ReadSignal(symbol, r);
    }

    public async Task<SignalRecommendation?> GetLatestSignalBeforeAsync(
        string symbol,
        DateTimeOffset before,
        CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT signal_id, timeframe, signal_time_utc, direction,
                   entry_price, tp_price, sl_price, risk_percent, risk_usd,
                   confidence_score, regime, strategy_version, reasons_json, status,
                   market_condition_class, evaluation_id,
                   tp1_price, tp2_price, tp3_price, risk_reward_ratio, exit_model, exit_explanation
            FROM ""ETH"".signals
            WHERE symbol = @s AND signal_time_utc < @before
            ORDER BY signal_time_utc DESC
            LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("s", symbol);
        cmd.Parameters.AddWithValue("before", before);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return ReadSignal(symbol, r);
    }

    public async Task<SignalRecommendation?> GetLatestPrimaryTimeframeSignalAsync(
        string symbol, string primaryTimeframe, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            WITH preferred AS (
                SELECT signal_id, timeframe, signal_time_utc, direction,
                       entry_price, tp_price, sl_price, risk_percent, risk_usd,
                       confidence_score, regime, strategy_version, reasons_json, status,
                       market_condition_class, evaluation_id,
                       tp1_price, tp2_price, tp3_price, risk_reward_ratio, exit_model, exit_explanation
                FROM ""ETH"".signals
                WHERE symbol = @s AND timeframe = @tf
                ORDER BY signal_time_utc DESC
                LIMIT 1
            ),
            fallback AS (
                SELECT signal_id, timeframe, signal_time_utc, direction,
                       entry_price, tp_price, sl_price, risk_percent, risk_usd,
                       confidence_score, regime, strategy_version, reasons_json, status,
                       market_condition_class, evaluation_id,
                       tp1_price, tp2_price, tp3_price, risk_reward_ratio, exit_model, exit_explanation
                FROM ""ETH"".signals
                WHERE symbol = @s
                ORDER BY signal_time_utc DESC
                LIMIT 1
            )
            SELECT *
            FROM preferred
            UNION ALL
            SELECT *
            FROM fallback
            WHERE NOT EXISTS (SELECT 1 FROM preferred)
            LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("s", symbol);
        cmd.Parameters.AddWithValue("tf", primaryTimeframe);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return ReadSignal(symbol, r);
    }

    public async Task<IReadOnlyList<SignalRecommendation>> GetSignalHistoryAsync(string symbol, int limit, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT signal_id, timeframe, signal_time_utc, direction,
                   entry_price, tp_price, sl_price, risk_percent, risk_usd,
                   confidence_score, regime, strategy_version, reasons_json, status,
                   market_condition_class, evaluation_id,
                   tp1_price, tp2_price, tp3_price, risk_reward_ratio, exit_model, exit_explanation
            FROM ""ETH"".signals
            WHERE symbol = @s ORDER BY signal_time_utc DESC LIMIT @n;", conn);
        cmd.Parameters.AddWithValue("s", symbol);
        cmd.Parameters.AddWithValue("n", limit);

        var results = new List<SignalRecommendation>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            results.Add(ReadSignal(symbol, r));
        return results;
    }

    public async Task<IReadOnlyList<SignalRecommendation>> GetRecentSignalsAsync(
        string symbol,
        string timeframe,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT signal_id, timeframe, signal_time_utc, direction,
                   entry_price, tp_price, sl_price, risk_percent, risk_usd,
                   confidence_score, regime, strategy_version, reasons_json, status,
                   market_condition_class, evaluation_id,
                   tp1_price, tp2_price, tp3_price, risk_reward_ratio, exit_model, exit_explanation
            FROM ""ETH"".signals
            WHERE symbol = @s
              AND timeframe = @tf
              AND signal_time_utc >= @from
              AND signal_time_utc < @to
            ORDER BY signal_time_utc DESC
            LIMIT @n;", conn);
        cmd.Parameters.AddWithValue("s", symbol);
        cmd.Parameters.AddWithValue("tf", timeframe);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.AddWithValue("n", limit);

        var results = new List<SignalRecommendation>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            results.Add(ReadSignal(symbol, r));

        return results;
    }

    public async Task<IReadOnlyList<SignalOutcome>> GetOutcomesAsync(string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        => await GetOutcomesAsync(symbol, from, to, strategyVersion: null, ct);

    public async Task<IReadOnlyList<SignalOutcome>> GetOutcomesAsync(string symbol, DateTimeOffset from, DateTimeOffset to, string? strategyVersion, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = @"
            SELECT o.signal_id, o.bars_observed, o.tp_hit, o.sl_hit, o.partial_win,
                   o.outcome_label, o.pnl_r, o.mfe_price, o.mae_price, o.mfe_r, o.mae_r, o.closed_at_utc
            FROM ""ETH"".signal_outcomes o
            JOIN ""ETH"".signals s ON s.signal_id = o.signal_id
            WHERE s.symbol = @s AND s.signal_time_utc >= @from AND s.signal_time_utc < @to";
        if (strategyVersion != null)
            sql += " AND s.strategy_version = @ver";
        sql += " ORDER BY s.signal_time_utc;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("s", symbol);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);
        if (strategyVersion != null)
            cmd.Parameters.AddWithValue("ver", strategyVersion);

        var results = new List<SignalOutcome>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            results.Add(new SignalOutcome
            {
                SignalId = r.GetGuid(0),
                BarsObserved = r.GetInt32(1),
                TpHit = r.GetBoolean(2),
                SlHit = r.GetBoolean(3),
                PartialWin = r.GetBoolean(4),
                OutcomeLabel = Enum.Parse<OutcomeLabel>(r.GetString(5)),
                PnlR = r.GetDecimal(6),
                MfePrice = r.GetDecimal(7),
                MaePrice = r.GetDecimal(8),
                MfeR = r.GetDecimal(9),
                MaeR = r.GetDecimal(10),
                ClosedAtUtc = r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11)
            });
        }
        return results;
    }

    public async Task<IReadOnlyList<SignalRecommendation>> GetOpenSignalsAsync(string symbol, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT signal_id, timeframe, signal_time_utc, direction,
                   entry_price, tp_price, sl_price, risk_percent, risk_usd,
                   confidence_score, regime, strategy_version, reasons_json, status,
                   market_condition_class, evaluation_id,
                   tp1_price, tp2_price, tp3_price, risk_reward_ratio, exit_model, exit_explanation
            FROM ""ETH"".signals
            WHERE symbol = @s AND status = 'OPEN'
            ORDER BY signal_time_utc;", conn);
        cmd.Parameters.AddWithValue("s", symbol);

        var results = new List<SignalRecommendation>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            results.Add(ReadSignalWithCondition(symbol, r));
        return results;
    }

    public async Task UpdateSignalStatusAsync(Guid signalId, SignalStatus status, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            UPDATE ""ETH"".signals SET status = @status WHERE signal_id = @id;", conn);
        cmd.Parameters.AddWithValue("id", signalId);
        cmd.Parameters.AddWithValue("status", status.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertSignalFeaturesAsync(Guid signalId, Dictionary<string, decimal> features, CancellationToken ct = default)
    {
        if (features.Count == 0)
            return;

        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var sql = new StringBuilder(@"
            INSERT INTO ""ETH"".signal_features (signal_id, feature_name, feature_value_numeric)
            VALUES ");

        await using var cmd = new NpgsqlCommand { Connection = conn };
        cmd.Parameters.AddWithValue("id", signalId);

        var index = 0;
        foreach (var (name, value) in features)
        {
            if (index > 0)
                sql.Append(", ");

            sql.Append($"(@id, @name{index}, @val{index})");
            cmd.Parameters.AddWithValue($"name{index}", name);
            cmd.Parameters.AddWithValue($"val{index}", value);
            index++;
        }

        sql.Append(@"
            ON CONFLICT (signal_id, feature_name)
            DO UPDATE SET feature_value_numeric = EXCLUDED.feature_value_numeric;");
        cmd.CommandText = sql.ToString();

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SignalWithOutcome>> GetSignalHistoryWithOutcomesAsync(string symbol, int limit, int offset, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT s.signal_id, s.timeframe, s.signal_time_utc, s.direction,
                   s.entry_price, s.tp_price, s.sl_price, s.risk_percent, s.risk_usd,
                   s.confidence_score, s.regime, s.strategy_version, s.reasons_json, s.status,
                   s.market_condition_class, s.evaluation_id,
                   s.tp1_price, s.tp2_price, s.tp3_price, s.risk_reward_ratio, s.exit_model, s.exit_explanation,
                   o.outcome_label, o.pnl_r, o.bars_observed, o.tp_hit, o.sl_hit, o.partial_win,
                   o.mfe_price, o.mae_price, o.mfe_r, o.mae_r, o.closed_at_utc
            FROM ""ETH"".signals s
            LEFT JOIN ""ETH"".signal_outcomes o ON s.signal_id = o.signal_id
            WHERE s.symbol = @s
            ORDER BY s.signal_time_utc DESC
            LIMIT @n OFFSET @off;", conn);
        cmd.Parameters.AddWithValue("s", symbol);
        cmd.Parameters.AddWithValue("n", limit);
        cmd.Parameters.AddWithValue("off", offset);

        var results = new List<SignalWithOutcome>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var signal = ReadSignal(symbol, r);
            SignalOutcome? outcome = null;
            if (!r.IsDBNull(22))
            {
                outcome = new SignalOutcome
                {
                    SignalId = r.GetGuid(0),
                    OutcomeLabel = Enum.Parse<OutcomeLabel>(r.GetString(22)),
                    PnlR = r.GetDecimal(23),
                    BarsObserved = r.GetInt32(24),
                    TpHit = r.GetBoolean(25),
                    SlHit = r.GetBoolean(26),
                    PartialWin = r.GetBoolean(27),
                    MfePrice = r.GetDecimal(28),
                    MaePrice = r.GetDecimal(29),
                    MfeR = r.GetDecimal(30),
                    MaeR = r.GetDecimal(31),
                    ClosedAtUtc = r.IsDBNull(32) ? null : r.GetFieldValue<DateTimeOffset>(32)
                };
            }
            results.Add(new SignalWithOutcome { Signal = signal, Outcome = outcome });
        }
        return results;
    }

    public async Task<int> GetSignalCountAsync(string symbol, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*) FROM ""ETH"".signals WHERE symbol = @s;", conn);
        cmd.Parameters.AddWithValue("s", symbol);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>B-14: Parse reasons from JSONB (or legacy pipe-delimited text).</summary>
    private static IReadOnlyList<string> ParseReasons(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return [];
        if (raw.TrimStart().StartsWith('['))
            return JsonSerializer.Deserialize<string[]>(raw) ?? [];
        return raw.Split('|'); // legacy fallback
    }

    private static SignalRecommendation ReadSignal(string symbol, NpgsqlDataReader r) => new()
    {
        SignalId = r.GetGuid(0),
        Symbol = symbol,
        Timeframe = r.GetString(1),
        SignalTimeUtc = r.GetFieldValue<DateTimeOffset>(2),
        Direction = Enum.Parse<SignalDirection>(r.GetString(3)),
        EntryPrice = r.GetDecimal(4),
        TpPrice = r.GetDecimal(5),
        SlPrice = r.GetDecimal(6),
        RiskPercent = r.GetDecimal(7),
        RiskUsd = r.GetDecimal(8),
        ConfidenceScore = r.GetInt32(9),
        Regime = Enum.Parse<Regime>(r.GetString(10)),
        StrategyVersion = r.GetString(11),
        Reasons = ParseReasons(r.GetString(12)),
        Status = Enum.Parse<SignalStatus>(r.GetString(13)),
        MarketConditionClass = r.IsDBNull(14) ? null : r.GetString(14),
        EvaluationId = r.IsDBNull(15) ? null : r.GetGuid(15),
        Tp1Price = r.IsDBNull(16) ? 0m : r.GetDecimal(16),
        Tp2Price = r.IsDBNull(17) ? 0m : r.GetDecimal(17),
        Tp3Price = r.IsDBNull(18) ? 0m : r.GetDecimal(18),
        RiskRewardRatio = r.IsDBNull(19) ? 0m : r.GetDecimal(19),
        ExitModel = r.IsDBNull(20) ? null : r.GetString(20),
        ExitExplanation = r.IsDBNull(21) ? null : r.GetString(21)
    };

    private static SignalRecommendation ReadSignalFromRowWithSymbol(NpgsqlDataReader r) => new()
    {
        SignalId = r.GetGuid(1),
        Symbol = r.GetString(0),
        Timeframe = r.GetString(2),
        SignalTimeUtc = r.GetFieldValue<DateTimeOffset>(3),
        Direction = Enum.Parse<SignalDirection>(r.GetString(4)),
        EntryPrice = r.GetDecimal(5),
        TpPrice = r.GetDecimal(6),
        SlPrice = r.GetDecimal(7),
        RiskPercent = r.GetDecimal(8),
        RiskUsd = r.GetDecimal(9),
        ConfidenceScore = r.GetInt32(10),
        Regime = Enum.Parse<Regime>(r.GetString(11)),
        StrategyVersion = r.GetString(12),
        Reasons = ParseReasons(r.GetString(13)),
        Status = Enum.Parse<SignalStatus>(r.GetString(14)),
        MarketConditionClass = r.IsDBNull(15) ? null : r.GetString(15),
        EvaluationId = r.IsDBNull(16) ? null : r.GetGuid(16),
        Tp1Price = r.IsDBNull(17) ? 0m : r.GetDecimal(17),
        Tp2Price = r.IsDBNull(18) ? 0m : r.GetDecimal(18),
        Tp3Price = r.IsDBNull(19) ? 0m : r.GetDecimal(19),
        RiskRewardRatio = r.IsDBNull(20) ? 0m : r.GetDecimal(20),
        ExitModel = r.IsDBNull(21) ? null : r.GetString(21),
        ExitExplanation = r.IsDBNull(22) ? null : r.GetString(22)
    };

    private static SignalRecommendation ReadSignalWithCondition(string symbol, NpgsqlDataReader r) => new()
    {
        SignalId = r.GetGuid(0),
        Symbol = symbol,
        Timeframe = r.GetString(1),
        SignalTimeUtc = r.GetFieldValue<DateTimeOffset>(2),
        Direction = Enum.Parse<SignalDirection>(r.GetString(3)),
        EntryPrice = r.GetDecimal(4),
        TpPrice = r.GetDecimal(5),
        SlPrice = r.GetDecimal(6),
        RiskPercent = r.GetDecimal(7),
        RiskUsd = r.GetDecimal(8),
        ConfidenceScore = r.GetInt32(9),
        Regime = Enum.Parse<Regime>(r.GetString(10)),
        StrategyVersion = r.GetString(11),
        Reasons = ParseReasons(r.GetString(12)),
        Status = Enum.Parse<SignalStatus>(r.GetString(13)),
        MarketConditionClass = r.IsDBNull(14) ? null : r.GetString(14),
        EvaluationId = r.IsDBNull(15) ? null : r.GetGuid(15),
        Tp1Price = r.IsDBNull(16) ? 0m : r.GetDecimal(16),
        Tp2Price = r.IsDBNull(17) ? 0m : r.GetDecimal(17),
        Tp3Price = r.IsDBNull(18) ? 0m : r.GetDecimal(18),
        RiskRewardRatio = r.IsDBNull(19) ? 0m : r.GetDecimal(19),
        ExitModel = r.IsDBNull(20) ? null : r.GetString(20),
        ExitExplanation = r.IsDBNull(21) ? null : r.GetString(21)
    };
}
