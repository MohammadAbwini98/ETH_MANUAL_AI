using System.Text.Json;
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Engine.ML;

/// <summary>
/// Chooses whether a candidate model should replace the current active model.
/// Keeps startup and post-training promotion behavior aligned so runtime
/// inference uses the strongest validated model available in the DB.
/// </summary>
public sealed class MlModelPromotionService
{
    // Accuracy-first gates. Tighter than rolling-drift thresholds — a model
    // must clear these before it can be promoted to ACTIVE.
    private const int MinTrainingSamples = 200;
    private const int MinWinSamples = 30;
    private const int MinLossSamples = 30;
    private const decimal MinAucRoc = 0.58m;
    private const decimal MaxBrierScore = 0.30m;
    private const decimal MaxExpectedCalibrationError = 0.25m;

    private readonly IMlModelRepository _modelRepo;
    private readonly ILogger<MlModelPromotionService> _logger;
    private readonly Func<string, bool> _fileExists;

    /// <summary>
    /// Reason string describing the most recent promotion block (set even when
    /// a candidate is rejected for failing any of the accuracy-first gates).
    /// Exposed via /api/admin/ml/health for operator visibility.
    /// </summary>
    public string? LastPromotionBlockReason { get; private set; }

    public MlModelPromotionService(
        IMlModelRepository modelRepo,
        ILogger<MlModelPromotionService> logger,
        Func<string, bool>? fileExists = null)
    {
        _modelRepo = modelRepo;
        _logger = logger;
        _fileExists = fileExists ?? File.Exists;
    }

    public async Task<MlModelPromotionResult> PromoteBestModelAsync(
        string modelType,
        CancellationToken ct = default,
        MlPromotionContext? context = null)
    {
        var models = await _modelRepo.GetAllAsync(ct);
        var decision = EvaluatePromotion(modelType, models, context);
        LastPromotionBlockReason = decision.ShouldActivate ? null : decision.Reason;
        if (!decision.ShouldActivate || decision.SelectedModel is null)
        {
            _logger.LogInformation(
                "[MLPromotion] No model activation needed for {ModelType}: {Reason}",
                modelType, decision.Reason);
            return new MlModelPromotionResult
            {
                Activated = false,
                SelectedModel = decision.SelectedModel,
                PreviousActiveModel = decision.CurrentActiveModel,
                Reason = decision.Reason
            };
        }

        if (decision.CurrentActiveModel is { } current && current.Id != decision.SelectedModel.Id)
        {
            await _modelRepo.UpdateStatusAsync(
                current.Id,
                MlModelStatus.Retired,
                $"Auto-promoted {decision.SelectedModel.ModelVersion}: {decision.Reason}",
                ct);
        }

        await _modelRepo.UpdateStatusAsync(decision.SelectedModel.Id, MlModelStatus.Active, reason: null, ct: ct);

        _logger.LogInformation(
            "[MLPromotion] Activated model {Version} (AUC={Auc:F4}, Brier={Brier:F4}, Samples={Samples}) | Reason={Reason}",
            decision.SelectedModel.ModelVersion,
            decision.SelectedModel.AucRoc,
            decision.SelectedModel.BrierScore,
            decision.SelectedModel.TrainingSampleCount,
            decision.Reason);

        return new MlModelPromotionResult
        {
            Activated = true,
            SelectedModel = decision.SelectedModel,
            PreviousActiveModel = decision.CurrentActiveModel,
            Reason = decision.Reason
        };
    }

    public MlModelPromotionDecision EvaluatePromotion(
        string modelType,
        IReadOnlyList<MlModelMetadata> models,
        MlPromotionContext? context = null)
    {
        var relevant = models
            .Where(m => string.Equals(m.ModelType, modelType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var currentActive = relevant
            .Where(m => m.Status == MlModelStatus.Active)
            .OrderByDescending(m => m.ActivatedAtUtc ?? m.CreatedAtUtc)
            .FirstOrDefault();

        // Capture why candidates fail so operators see the real reason, not
        // just "no promotable candidate". Sorted by AUC so the strongest
        // candidate's block reasons show first.
        var candidateBlockReasons = new List<string>();
        MlModelMetadata? bestCandidate = null;
        foreach (var m in relevant
                     .Where(m => m.Status is MlModelStatus.Candidate or MlModelStatus.Shadow)
                     .OrderByDescending(m => m.AucRoc)
                     .ThenBy(m => m.BrierScore)
                     .ThenBy(m => m.ExpectedCalibrationError <= 0 ? decimal.MaxValue : m.ExpectedCalibrationError)
                     .ThenByDescending(m => m.TrainingSampleCount)
                     .ThenByDescending(m => m.CreatedAtUtc))
        {
            var blockers = GetPromotionBlockers(m, context);
            if (blockers.Count == 0)
            {
                bestCandidate = m;
                break;
            }
            if (candidateBlockReasons.Count == 0)
            {
                candidateBlockReasons.Add($"{m.ModelVersion}: {string.Join("; ", blockers)}");
            }
        }

        if (bestCandidate == null)
        {
            var blockReason = candidateBlockReasons.Count > 0
                ? $"No promotable candidate passed gates — {candidateBlockReasons[0]}"
                : "No promotable candidate model passed the quality gates";
            return new MlModelPromotionDecision
            {
                CurrentActiveModel = currentActive,
                Reason = blockReason,
                BlockReasons = candidateBlockReasons
            };
        }

        if (currentActive == null)
        {
            return new MlModelPromotionDecision
            {
                ShouldActivate = true,
                SelectedModel = bestCandidate,
                Reason = "No active model exists"
            };
        }

        if (currentActive.Id == bestCandidate.Id)
        {
            return new MlModelPromotionDecision
            {
                CurrentActiveModel = currentActive,
                SelectedModel = bestCandidate,
                Reason = "Best model is already active"
            };
        }

        if (GetPromotionBlockers(currentActive, context).Count > 0)
        {
            return new MlModelPromotionDecision
            {
                ShouldActivate = true,
                CurrentActiveModel = currentActive,
                SelectedModel = bestCandidate,
                Reason = "Current active model no longer passes readiness checks"
            };
        }

        if (IsMateriallyBetter(bestCandidate, currentActive, out var reason))
        {
            return new MlModelPromotionDecision
            {
                ShouldActivate = true,
                CurrentActiveModel = currentActive,
                SelectedModel = bestCandidate,
                Reason = reason
            };
        }

        return new MlModelPromotionDecision
        {
            CurrentActiveModel = currentActive,
            SelectedModel = bestCandidate,
            Reason = $"Current active model remains preferred over {bestCandidate.ModelVersion}"
        };
    }

    /// <summary>
    /// Enumerate every reason a model fails the accuracy-first promotion gates.
    /// Returning a list (instead of a single bool) lets callers surface
    /// explicit block reasons in logs, health endpoints, and tests.
    /// </summary>
    public List<string> GetPromotionBlockers(MlModelMetadata model, MlPromotionContext? context)
    {
        var blockers = new List<string>();

        // 1. Heuristic fallback is never a promotable model
        if (model.FileFormat.Equals("heuristic", StringComparison.OrdinalIgnoreCase))
            blockers.Add("heuristic fallback cannot be promoted to ACTIVE");
        else if (!model.FileFormat.Equals("onnx", StringComparison.OrdinalIgnoreCase))
            blockers.Add($"unsupported file format '{model.FileFormat}' (expected onnx)");

        // 2. Artifact on disk
        if (string.IsNullOrWhiteSpace(model.FilePath))
            blockers.Add("model file path is empty");
        else if (!_fileExists(model.FilePath))
            blockers.Add($"model file missing on disk: {model.FilePath}");

        // 3. Sample count gates
        if (model.TrainingSampleCount < MinTrainingSamples)
            blockers.Add($"training samples {model.TrainingSampleCount} < {MinTrainingSamples}");

        // 4. Accuracy metrics
        if (model.AucRoc < MinAucRoc)
            blockers.Add($"AUC {model.AucRoc:F4} < {MinAucRoc:F2}");
        if (model.BrierScore > MaxBrierScore)
            blockers.Add($"Brier {model.BrierScore:F4} > {MaxBrierScore:F2}");
        if (model.ExpectedCalibrationError > 0m && model.ExpectedCalibrationError > MaxExpectedCalibrationError)
            blockers.Add($"ECE {model.ExpectedCalibrationError:F4} > {MaxExpectedCalibrationError:F2}");

        // 5. Fold metrics must be present (null/empty/{}/[] all count as incomplete)
        if (!HasFoldMetrics(model.FoldMetricsJson))
            blockers.Add("fold metrics missing or empty in metadata");

        // 6. Context-driven gates (only enforced when caller supplied them)
        if (context != null)
        {
            if (context.LabeledSamples.HasValue && context.LabeledSamples.Value < MinTrainingSamples)
                blockers.Add($"clean labeled samples {context.LabeledSamples.Value} < {MinTrainingSamples}");
            if (context.Wins.HasValue && context.Wins.Value < MinWinSamples)
                blockers.Add($"WIN samples {context.Wins.Value} < {MinWinSamples}");
            if (context.Losses.HasValue && context.Losses.Value < MinLossSamples)
                blockers.Add($"LOSS samples {context.Losses.Value} < {MinLossSamples}");

            if (context.CalibrationSampleCount.HasValue && context.CalibrationSampleCount.Value < 50)
                blockers.Add($"calibration samples {context.CalibrationSampleCount.Value} < 50");
            if (context.CalibrationBrier.HasValue && context.CalibrationBrier.Value > 0.30m)
                blockers.Add($"live calibration Brier {context.CalibrationBrier.Value:F4} > 0.30");
            if (context.ThresholdLift.HasValue && context.ThresholdLift.Value <= 0m)
                blockers.Add($"threshold lift {context.ThresholdLift.Value:F4} not positive");
        }

        return blockers;
    }

    private static bool HasFoldMetrics(string? foldMetricsJson)
    {
        if (string.IsNullOrWhiteSpace(foldMetricsJson))
            return false;
        var trimmed = foldMetricsJson.Trim();
        if (trimmed is "[]" or "{}")
            return false;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return false;
            var count = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                // Require at least one fold with an auc_roc field present
                if (item.TryGetProperty("auc_roc", out var auc)
                    && auc.ValueKind == JsonValueKind.Number)
                {
                    count++;
                }
            }
            return count >= 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMateriallyBetter(
        MlModelMetadata candidate,
        MlModelMetadata current,
        out string reason)
    {
        var aucGain = candidate.AucRoc - current.AucRoc;
        var brierGain = current.BrierScore - candidate.BrierScore;
        var eceGain = NormalizeEce(current.ExpectedCalibrationError) - NormalizeEce(candidate.ExpectedCalibrationError);
        var sampleGain = candidate.TrainingSampleCount - current.TrainingSampleCount;

        if (aucGain >= 0.010m && candidate.BrierScore <= current.BrierScore + 0.010m)
        {
            reason = $"candidate AUC improved by {aucGain:F4} with comparable Brier score";
            return true;
        }

        if (brierGain >= 0.010m && candidate.AucRoc >= current.AucRoc - 0.010m)
        {
            reason = $"candidate Brier improved by {brierGain:F4} with comparable AUC";
            return true;
        }

        if (eceGain >= 0.020m
            && candidate.AucRoc >= current.AucRoc - 0.010m
            && candidate.BrierScore <= current.BrierScore + 0.010m)
        {
            reason = $"candidate calibration improved by {eceGain:F4} ECE with comparable outcome metrics";
            return true;
        }

        if (aucGain >= 0.005m && brierGain >= 0.005m)
        {
            reason = "candidate improved both AUC and Brier score";
            return true;
        }

        if (Math.Abs(aucGain) <= 0.002m && Math.Abs(brierGain) <= 0.002m && sampleGain >= 100)
        {
            reason = $"candidate matched quality metrics with {sampleGain} more samples";
            return true;
        }

        reason = "candidate did not materially outperform the current active model";
        return false;
    }

    private static decimal NormalizeEce(decimal value) => value > 0m ? value : 1m;
}

public sealed record MlModelPromotionDecision
{
    public bool ShouldActivate { get; init; }
    public MlModelMetadata? SelectedModel { get; init; }
    public MlModelMetadata? CurrentActiveModel { get; init; }
    public string Reason { get; init; } = "";
    public IReadOnlyList<string> BlockReasons { get; init; } = Array.Empty<string>();
}

public sealed record MlModelPromotionResult
{
    public bool Activated { get; init; }
    public MlModelMetadata? SelectedModel { get; init; }
    public MlModelMetadata? PreviousActiveModel { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// Optional runtime context used by the promotion gate in accuracy-first mode.
/// Populated from MlDataDiagnosticsService so the promotion decision reflects
/// current drift / calibration / class balance — not just the trained-at
/// metrics baked into metadata.
/// </summary>
public sealed record MlPromotionContext
{
    public string? FeatureDriftStatus { get; init; }
    public int? LabeledSamples { get; init; }
    public int? Wins { get; init; }
    public int? Losses { get; init; }
    public int? CalibrationSampleCount { get; init; }
    public decimal? CalibrationBrier { get; init; }
    public decimal? ThresholdLift { get; init; }
}
