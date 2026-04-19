using System.Diagnostics;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Engine;
using EthSignal.Infrastructure.Engine.ML;

namespace EthSignal.Web.BackgroundServices;

/// <summary>
/// Background service that automatically triggers ML training when readiness
/// conditions are met. Checks every 5 minutes. Retrains at least every 1 hour,
/// plus on drift or new-outcome thresholds. Uses ml/ directory for training.
/// Training state is exposed via MlTrainingState singleton.
/// </summary>
public sealed class MlTrainingService : BackgroundService
{
    private readonly IMlTrainingRunRepository _trainingRepo;
    private readonly IMlModelRepository _modelRepo;
    private readonly MlModelPromotionService _modelPromotionService;
    private readonly MlInferenceService _inferenceService;
    private readonly MlDriftDetector _driftDetector;
    private readonly IParameterProvider _paramProvider;
    private readonly IBlockedSignalOutcomeSyncService _blockedOutcomeSyncService;
    private readonly IGeneratedSignalOutcomeSyncService _generatedOutcomeSyncService;
    private readonly IBlockedMlFeatureBackfillService _blockedFeatureBackfillService;
    private readonly MlTrainingState _state;
    private readonly IConfiguration _config;
    private readonly ILogger<MlTrainingService> _logger;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetrainInterval = TimeSpan.FromHours(1);
    private const int MinSamplesRequired = 200;
    private const int MinSamplesPerClass = 30;

    public MlTrainingService(
        IMlTrainingRunRepository trainingRepo,
        IMlModelRepository modelRepo,
        MlModelPromotionService modelPromotionService,
        MlInferenceService inferenceService,
        MlDriftDetector driftDetector,
        IParameterProvider paramProvider,
        IBlockedSignalOutcomeSyncService blockedOutcomeSyncService,
        IGeneratedSignalOutcomeSyncService generatedOutcomeSyncService,
        IBlockedMlFeatureBackfillService blockedFeatureBackfillService,
        MlTrainingState state,
        IConfiguration config,
        ILogger<MlTrainingService> logger)
    {
        _trainingRepo = trainingRepo;
        _modelRepo = modelRepo;
        _modelPromotionService = modelPromotionService;
        _inferenceService = inferenceService;
        _driftDetector = driftDetector;
        _paramProvider = paramProvider;
        _blockedOutcomeSyncService = blockedOutcomeSyncService;
        _generatedOutcomeSyncService = generatedOutcomeSyncService;
        _blockedFeatureBackfillService = blockedFeatureBackfillService;
        _state = state;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[MLTrainer] Service started — checking every {Interval}m", CheckInterval.TotalMinutes);

        // Initial readiness snapshot on startup
        await RefreshReadinessAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(CheckInterval, stoppingToken);

            try
            {
                await RefreshReadinessAsync(stoppingToken);

                if (_state.IsRunning)
                {
                    _logger.LogDebug("[MLTrainer] Training already in progress — skipping check");
                    continue;
                }

                var trigger = await ShouldTrainAsync(stoppingToken);
                if (trigger != null)
                {
                    _logger.LogInformation("[MLTrainer] Auto-training triggered: {Reason}", trigger);
                    await RunTrainingAsync(trigger, stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MLTrainer] Unexpected error during training check — will retry next cycle");
            }
        }

        _logger.LogInformation("[MLTrainer] Service stopping");
    }

    /// <summary>Called externally (API endpoint) to force an immediate training run.</summary>
    public async Task TriggerManualAsync(CancellationToken ct = default)
    {
        if (_state.IsRunning)
        {
            _logger.LogWarning("[MLTrainer] Manual trigger ignored — training already in progress");
            return;
        }
        await RunTrainingAsync("manual", ct);
    }

    private async Task RefreshReadinessAsync(CancellationToken ct)
    {
        try
        {
            var symbol = _config["SYMBOL"]
                ?? Environment.GetEnvironmentVariable("SYMBOL")
                ?? "ETHUSD";
            await _blockedOutcomeSyncService.SyncAsync(symbol, ct);
            await _generatedOutcomeSyncService.SyncAsync(symbol, ct);
            await _blockedFeatureBackfillService.BackfillAsync(symbol, ct);

            var total = await _trainingRepo.GetLabeledSampleCountAsync(ct);
            var (wins, losses) = await _trainingRepo.GetWinLossCountsAsync(ct);
            _state.UpdateReadiness(total, wins, losses);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MLTrainer] Failed to refresh readiness counts");
        }
    }

    private async Task<string?> ShouldTrainAsync(CancellationToken ct)
    {
        var total = _state.LabeledSamples;
        var wins = _state.Wins;
        var losses = _state.Losses;

        if (total < MinSamplesRequired || wins < MinSamplesPerClass || losses < MinSamplesPerClass)
        {
            _logger.LogDebug("[MLTrainer] Not ready: total={Total} wins={Wins} losses={Losses} (need {MinTotal}+ with {MinClass}+ each class)",
                total, wins, losses, MinSamplesRequired, MinSamplesPerClass);
            return null;
        }

        var p = _paramProvider.GetActive();
        var lastCompleted = await _trainingRepo.GetLatestCompletedAsync(ct);
        if (lastCompleted == null)
            return "initial";

        var timeSinceLast = DateTimeOffset.UtcNow - lastCompleted.FinishedAtUtc!.Value;
        var newOutcomes = await _trainingRepo.GetNewOutcomesSinceAsync(lastCompleted.FinishedAtUtc!.Value, ct);

        // Drift-triggered retraining: only re-run when something materially changed
        // since the last completed attempt, otherwise the same drifting dataset can
        // trigger a failed training loop every 5 minutes.
        var modelVersion = _inferenceService.ActiveModelVersion ?? "none";
        var drift = _driftDetector.CheckDrift(p, modelVersion);
        if (drift.DriftDetected
            && (timeSinceLast >= RetrainInterval || newOutcomes >= p.MlRetrainSignalThreshold))
            return "drift";

        // Hourly periodic retraining
        if (timeSinceLast >= RetrainInterval)
            return $"hourly ({timeSinceLast.TotalMinutes:F0}m since last run)";

        // New outcomes threshold since the last completed run. Using the latest
        // completed attempt prevents immediate retraining on the same failed batch.
        if (newOutcomes >= p.MlRetrainSignalThreshold)
            return $"threshold ({newOutcomes} new outcomes)";

        return null;
    }

    private async Task RunTrainingAsync(string trigger, CancellationToken ct)
    {
        var configuredWorkspaceRoot = _config["WorkspaceRoot"];
        var projectRoot = !string.IsNullOrWhiteSpace(configuredWorkspaceRoot)
            ? Path.GetFullPath(configuredWorkspaceRoot)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var instrument = _config["INSTRUMENT"]
            ?? Environment.GetEnvironmentVariable("INSTRUMENT")
            ?? "eth";
        var symbol = _config["SYMBOL"]
            ?? Environment.GetEnvironmentVariable("SYMBOL")
            ?? "ETHUSD";
        var mlDir = "ml";
        var scriptPath = Path.Combine(projectRoot, mlDir, "train_pipeline.sh");

        if (!File.Exists(scriptPath))
        {
            _logger.LogError("[MLTrainer] train_pipeline.sh not found at {Path}", scriptPath);
            return;
        }

        await RefreshReadinessAsync(ct);

        var pgConnection = _config["PG_CONNECTION"]
            ?? Environment.GetEnvironmentVariable("PG_CONNECTION");
        if (string.IsNullOrWhiteSpace(pgConnection))
        {
            _logger.LogError(
                "[MLTrainer] PG_CONNECTION is not configured — cannot start training pipeline. " +
                "Set the PG_CONNECTION environment variable and restart.");
            return;
        }

        var runId = await _trainingRepo.InsertAsync(new MlTrainingRunRecord
        {
            ModelType = "outcome_predictor",
            Trigger = trigger,
            DataStartUtc = DateTimeOffset.UtcNow.AddDays(-90),
            DataEndUtc = DateTimeOffset.UtcNow,
            FoldCount = 5,
            EmbargoMars = 4,
            Status = "running"
        }, ct);

        _state.SetRunning(runId, trigger);
        _logger.LogInformation("[MLTrainer] Training started | RunId={RunId} | Trigger={Trigger}", runId, trigger);

        var sw = Stopwatch.StartNew();
        try
        {
            var psi = new ProcessStartInfo("bash", scriptPath)
            {
                WorkingDirectory = Path.Combine(projectRoot, mlDir),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.Environment["PG_CONNECTION"] = pgConnection;
            psi.Environment["ML_FEATURE_VERSION"] = MlFeatureExtractor.FeatureVersion;

            using var proc = Process.Start(psi)!;
            var stdOut = await proc.StandardOutput.ReadToEndAsync(ct);
            var stdErr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            sw.Stop();
            int exitCode = proc.ExitCode;

            if (!string.IsNullOrWhiteSpace(stdOut))
                _logger.LogInformation("[MLTrainer] Pipeline output:\n{Output}", stdOut);
            if (!string.IsNullOrWhiteSpace(stdErr))
                _logger.LogWarning("[MLTrainer] Pipeline stderr:\n{Err}", stdErr);

            if (exitCode == 0)
            {
                // FR-10: Validate that a training artifact was actually produced
                // and that we have meaningful sample counts before marking success.
                var candidates = await _modelRepo.GetAllAsync(ct);
                var newest = candidates
                    .Where(m => m.ModelType == "outcome_predictor"
                             && m.CreatedAtUtc >= _state.RunStartedAt!.Value.AddSeconds(-10))
                    .OrderByDescending(m => m.CreatedAtUtc)
                    .FirstOrDefault();

                var samples = _state.LabeledSamples;

                // FR-10: Artifact validation — must have a registered model with sample count > 0
                if (newest == null)
                {
                    await _trainingRepo.UpdateAsync(runId, "artifact_validation_failed", samples, null,
                        "Pipeline exited 0 but no model artifact was registered in the database",
                        (int)sw.Elapsed.TotalSeconds, ct);
                    _state.SetIdle("artifact_validation_failed", sw.Elapsed, null);
                    _logger.LogError(
                        "[MLTrainer] FR-10: Training pipeline exited 0 but NO model was registered | RunId={RunId}",
                        runId);
                }
                else if (samples <= 0)
                {
                    await _trainingRepo.UpdateAsync(runId, "artifact_validation_failed", samples, newest.Id,
                        $"Model {newest.ModelVersion} registered but sample count is {samples}",
                        (int)sw.Elapsed.TotalSeconds, ct);
                    _state.SetIdle("artifact_validation_failed", sw.Elapsed, newest.Id);
                    _logger.LogError(
                        "[MLTrainer] FR-10: Model registered but sample count is {Samples} | RunId={RunId} | ModelId={ModelId}",
                        samples, runId, newest.Id);
                }
                else
                {
                    // Promote global (ALL-scope) model
                    var promotion = await _modelPromotionService.PromoteBestModelAsync("outcome_predictor", ct);

                    await _trainingRepo.UpdateAsync(runId, "success", samples, newest.Id, null, (int)sw.Elapsed.TotalSeconds, ct);
                    _state.SetIdle("success", sw.Elapsed, newest.Id);
                    _logger.LogInformation("[MLTrainer] Training succeeded | RunId={RunId} | Duration={Dur}s | ModelId={ModelId} | Samples={Samples}",
                        runId, (int)sw.Elapsed.TotalSeconds, newest.Id, samples);

                    if (promotion.Activated && promotion.SelectedModel != null)
                    {
                        _logger.LogInformation(
                            "[MLTrainer] Auto-promoted global model {Version} after training | Reason={Reason}",
                            promotion.SelectedModel.ModelVersion, promotion.Reason);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "[MLTrainer] Promotion check kept the current global model after training | Reason={Reason}",
                            promotion.Reason);
                    }

                    // ENH-1: Auto-promote regime specialists (BEARISH / BULLISH / NEUTRAL).
                    // Non-fatal: a specialist may not exist yet if the regime had < 50 samples.
                    bool anySpecialistActivated = false;
                    foreach (var scope in new[] { "BEARISH", "BULLISH", "NEUTRAL" })
                    {
                        try
                        {
                            var regimePromotion = await _modelPromotionService.PromoteBestModelAsync(
                                "outcome_predictor", ct, regimeScope: scope);
                            if (regimePromotion.Activated && regimePromotion.SelectedModel != null)
                            {
                                anySpecialistActivated = true;
                                _logger.LogInformation(
                                    "[MLTrainer] Auto-promoted {Scope} specialist {Version} | Reason={Reason}",
                                    scope, regimePromotion.SelectedModel.ModelVersion, regimePromotion.Reason);
                            }
                            else
                            {
                                _logger.LogDebug(
                                    "[MLTrainer] No {Scope} specialist promoted after training | Reason={Reason}",
                                    scope, regimePromotion.Reason);
                            }
                        }
                        catch (Exception regEx)
                        {
                            _logger.LogWarning(regEx,
                                "[MLTrainer] Regime specialist promotion failed for scope={Scope} — continuing", scope);
                        }
                    }

                    // Reload inference service if global model or any specialist changed
                    if (promotion.Activated || anySpecialistActivated)
                    {
                        await _inferenceService.LoadActiveModelAsync(ct);
                        _logger.LogInformation("[MLTrainer] Inference service reloaded — global={GlobalActivated} specialists={SpecialistsActivated}",
                            promotion.Activated, anySpecialistActivated);
                    }
                }
            }
            else if (exitCode == 2)
            {
                await _trainingRepo.UpdateAsync(runId, "skipped", null, null, "Insufficient data", (int)sw.Elapsed.TotalSeconds, ct);
                _state.SetIdle("skipped", sw.Elapsed, null);
                _logger.LogInformation("[MLTrainer] Training skipped — insufficient data (exit 2)");
            }
            else
            {
                var err = stdErr.Length > 500 ? stdErr[..500] : stdErr;
                await _trainingRepo.UpdateAsync(runId, "failed", null, null, err, (int)sw.Elapsed.TotalSeconds, ct);
                _state.SetIdle("failed", sw.Elapsed, null);
                _logger.LogError("[MLTrainer] Training failed | RunId={RunId} | ExitCode={Code}", runId, exitCode);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            await _trainingRepo.UpdateAsync(runId, "failed", null, null, ex.Message, (int)sw.Elapsed.TotalSeconds, ct);
            _state.SetIdle("failed", sw.Elapsed, null);
            _logger.LogError(ex, "[MLTrainer] Training exception | RunId={RunId}", runId);
        }
    }
}
