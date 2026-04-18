using EthSignal.Domain.Models;
using Npgsql;

namespace EthSignal.Infrastructure.Db;

public sealed class OptimizerRepository : IOptimizerRepository
{
    private readonly string _connectionString;

    public OptimizerRepository(string connectionString) => _connectionString = connectionString;

    public async Task<long> InsertRunAsync(OptimizerRun run, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".optimizer_runs
                (symbol, strategy_version, baseline_parameter_set_id,
                 search_space_json, objective_function_version,
                 start_utc, end_utc, status, run_mode, fold_count, started_utc)
            VALUES (@sym, @sv, @bps, @ss::jsonb, @ofv, @start, @end, @st, @rm, @fc, NOW())
            RETURNING id;", conn);
        cmd.Parameters.AddWithValue("sym", run.Symbol);
        cmd.Parameters.AddWithValue("sv", run.StrategyVersion);
        cmd.Parameters.AddWithValue("bps", (object?)run.BaselineParameterSetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ss", (object?)run.SearchSpaceJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ofv", (object?)run.ObjectiveFunctionVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("start", run.StartUtc);
        cmd.Parameters.AddWithValue("end", run.EndUtc);
        cmd.Parameters.AddWithValue("st", run.Status.ToString());
        cmd.Parameters.AddWithValue("rm", run.RunMode);
        cmd.Parameters.AddWithValue("fc", run.FoldCount);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateRunStatusAsync(long id, RunStatus status, string? error, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE ""ETH"".optimizer_runs SET status = @s, error_text = @err WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("s", status.ToString());
        cmd.Parameters.AddWithValue("err", (object?)error ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateRunFinishedAsync(long id, RunStatus status, long? bestCandidateId,
        decimal? bestScore, int candidateCount, string? summaryJson, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE ""ETH"".optimizer_runs
            SET status = @s, finished_utc = NOW(), best_candidate_id = @bc,
                best_score = @bs, candidate_count = @cc, summary_json = @sj::jsonb
            WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("s", status.ToString());
        cmd.Parameters.AddWithValue("bc", (object?)bestCandidateId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("bs", (object?)bestScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cc", candidateCount);
        cmd.Parameters.AddWithValue("sj", (object?)summaryJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertCandidateAsync(OptimizerCandidate candidate, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".optimizer_candidates
                (optimizer_run_id, parameter_set_id, status,
                 train_score, validation_score, baseline_delta_pct,
                 trade_count, win_rate, expectancy_r, total_pnl_r,
                 profit_factor, max_drawdown_r, timeout_rate,
                 overfit_penalty, sparsity_penalty, rank)
            VALUES (@orid, @psid, @st, @ts, @vs, @bdp, @tc, @wr, @er, @tpr,
                    @pf, @mdr, @tr, @op, @sp, @rk)
            RETURNING id;", conn);
        cmd.Parameters.AddWithValue("orid", candidate.OptimizerRunId);
        cmd.Parameters.AddWithValue("psid", candidate.ParameterSetId);
        cmd.Parameters.AddWithValue("st", candidate.Status);
        cmd.Parameters.AddWithValue("ts", (object?)candidate.TrainScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("vs", (object?)candidate.ValidationScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("bdp", (object?)candidate.BaselineDeltaPct ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tc", (object?)candidate.TradeCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("wr", (object?)candidate.WinRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("er", (object?)candidate.ExpectancyR ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tpr", (object?)candidate.TotalPnlR ?? DBNull.Value);
        cmd.Parameters.AddWithValue("pf", (object?)candidate.ProfitFactor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("mdr", (object?)candidate.MaxDrawdownR ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tr", (object?)candidate.TimeoutRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("op", (object?)candidate.OverfitPenalty ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sp", (object?)candidate.SparsityPenalty ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rk", (object?)candidate.Rank ?? DBNull.Value);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task InsertCandidateFoldAsync(OptimizerCandidateFold fold, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".optimizer_candidate_folds
                (optimizer_candidate_id, fold_index,
                 train_start_utc, train_end_utc, val_start_utc, val_end_utc,
                 train_metrics_json, val_metrics_json, warnings_json)
            VALUES (@ocid, @fi, @ts, @te, @vs, @ve, @tmj::jsonb, @vmj::jsonb, @wj::jsonb);", conn);
        cmd.Parameters.AddWithValue("ocid", fold.OptimizerCandidateId);
        cmd.Parameters.AddWithValue("fi", fold.FoldIndex);
        cmd.Parameters.AddWithValue("ts", fold.TrainStartUtc);
        cmd.Parameters.AddWithValue("te", fold.TrainEndUtc);
        cmd.Parameters.AddWithValue("vs", fold.ValStartUtc);
        cmd.Parameters.AddWithValue("ve", fold.ValEndUtc);
        cmd.Parameters.AddWithValue("tmj", (object?)fold.TrainMetricsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("vmj", (object?)fold.ValMetricsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("wj", (object?)fold.WarningsJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<OptimizerRun?> GetRunAsync(long id, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, symbol, strategy_version, baseline_parameter_set_id,
                   start_utc, end_utc, status, run_mode, fold_count,
                   candidate_count, best_candidate_id, best_score,
                   started_utc, finished_utc, error_text
            FROM ""ETH"".optimizer_runs WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new OptimizerRun
        {
            Id = r.GetInt64(0), Symbol = r.GetString(1), StrategyVersion = r.GetString(2),
            BaselineParameterSetId = r.IsDBNull(3) ? null : r.GetInt64(3),
            StartUtc = r.GetFieldValue<DateTimeOffset>(4), EndUtc = r.GetFieldValue<DateTimeOffset>(5),
            Status = Enum.Parse<RunStatus>(r.GetString(6)), RunMode = r.GetString(7),
            FoldCount = r.GetInt32(8), CandidateCount = r.GetInt32(9),
            BestCandidateId = r.IsDBNull(10) ? null : r.GetInt64(10),
            BestScore = r.IsDBNull(11) ? null : r.GetDecimal(11),
            StartedUtc = r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12),
            FinishedUtc = r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13),
            ErrorText = r.IsDBNull(14) ? null : r.GetString(14)
        };
    }
}
