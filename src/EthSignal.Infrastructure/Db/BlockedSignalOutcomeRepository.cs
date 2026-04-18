using EthSignal.Infrastructure.Engine;
using Npgsql;

namespace EthSignal.Infrastructure.Db;

public sealed class BlockedSignalOutcomeRepository : IBlockedSignalOutcomeRepository
{
    private readonly string _connectionString;

    public BlockedSignalOutcomeRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task UpsertManyAsync(
        IReadOnlyList<BlockedSignalWithOutcome> items,
        CancellationToken ct = default)
    {
        if (items.Count == 0)
            return;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".blocked_signal_outcomes
                (decision_id, evaluation_id, symbol, timeframe, signal_time_utc,
                 evaluated_at_utc, bars_observed, tp_hit, sl_hit, outcome_label,
                 pnl_r, mfe_price, mae_price, mfe_r, mae_r, closed_at_utc, updated_at_utc)
            VALUES
                (@decision_id, @evaluation_id, @symbol, @timeframe, @signal_time_utc,
                 @evaluated_at_utc, @bars_observed, @tp_hit, @sl_hit, @outcome_label,
                 @pnl_r, @mfe_price, @mae_price, @mfe_r, @mae_r, @closed_at_utc, NOW())
            ON CONFLICT (decision_id) DO UPDATE SET
                evaluation_id = EXCLUDED.evaluation_id,
                symbol = EXCLUDED.symbol,
                timeframe = EXCLUDED.timeframe,
                signal_time_utc = EXCLUDED.signal_time_utc,
                evaluated_at_utc = EXCLUDED.evaluated_at_utc,
                bars_observed = EXCLUDED.bars_observed,
                tp_hit = EXCLUDED.tp_hit,
                sl_hit = EXCLUDED.sl_hit,
                outcome_label = EXCLUDED.outcome_label,
                pnl_r = EXCLUDED.pnl_r,
                mfe_price = EXCLUDED.mfe_price,
                mae_price = EXCLUDED.mae_price,
                mfe_r = EXCLUDED.mfe_r,
                mae_r = EXCLUDED.mae_r,
                closed_at_utc = EXCLUDED.closed_at_utc,
                updated_at_utc = NOW();", conn, tx);

        cmd.Parameters.Add(new("decision_id", NpgsqlTypes.NpgsqlDbType.Uuid));
        cmd.Parameters.Add(new("evaluation_id", NpgsqlTypes.NpgsqlDbType.Uuid));
        cmd.Parameters.Add(new("symbol", NpgsqlTypes.NpgsqlDbType.Text));
        cmd.Parameters.Add(new("timeframe", NpgsqlTypes.NpgsqlDbType.Text));
        cmd.Parameters.Add(new("signal_time_utc", NpgsqlTypes.NpgsqlDbType.TimestampTz));
        cmd.Parameters.Add(new("evaluated_at_utc", NpgsqlTypes.NpgsqlDbType.TimestampTz));
        cmd.Parameters.Add(new("bars_observed", NpgsqlTypes.NpgsqlDbType.Integer));
        cmd.Parameters.Add(new("tp_hit", NpgsqlTypes.NpgsqlDbType.Boolean));
        cmd.Parameters.Add(new("sl_hit", NpgsqlTypes.NpgsqlDbType.Boolean));
        cmd.Parameters.Add(new("outcome_label", NpgsqlTypes.NpgsqlDbType.Text));
        cmd.Parameters.Add(new("pnl_r", NpgsqlTypes.NpgsqlDbType.Numeric));
        cmd.Parameters.Add(new("mfe_price", NpgsqlTypes.NpgsqlDbType.Numeric));
        cmd.Parameters.Add(new("mae_price", NpgsqlTypes.NpgsqlDbType.Numeric));
        cmd.Parameters.Add(new("mfe_r", NpgsqlTypes.NpgsqlDbType.Numeric));
        cmd.Parameters.Add(new("mae_r", NpgsqlTypes.NpgsqlDbType.Numeric));
        cmd.Parameters.Add(new("closed_at_utc", NpgsqlTypes.NpgsqlDbType.TimestampTz));

        foreach (var item in items)
        {
            if (item.Signal.EvaluationId == Guid.Empty)
                continue;

            cmd.Parameters["decision_id"].Value = item.Signal.SignalId;
            cmd.Parameters["evaluation_id"].Value = item.Signal.EvaluationId;
            cmd.Parameters["symbol"].Value = item.Signal.Symbol;
            cmd.Parameters["timeframe"].Value = item.Signal.Timeframe;
            cmd.Parameters["signal_time_utc"].Value = item.Signal.SignalTimeUtc;
            cmd.Parameters["evaluated_at_utc"].Value = item.Outcome.EvaluatedAtUtc;
            cmd.Parameters["bars_observed"].Value = item.Outcome.BarsObserved;
            cmd.Parameters["tp_hit"].Value = item.Outcome.TpHit;
            cmd.Parameters["sl_hit"].Value = item.Outcome.SlHit;
            cmd.Parameters["outcome_label"].Value = item.Outcome.OutcomeLabel.ToString();
            cmd.Parameters["pnl_r"].Value = item.Outcome.PnlR;
            cmd.Parameters["mfe_price"].Value = item.Outcome.MfePrice;
            cmd.Parameters["mae_price"].Value = item.Outcome.MaePrice;
            cmd.Parameters["mfe_r"].Value = item.Outcome.MfeR;
            cmd.Parameters["mae_r"].Value = item.Outcome.MaeR;
            cmd.Parameters["closed_at_utc"].Value = (object?)item.Outcome.ClosedAtUtc ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }
}
