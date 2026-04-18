using System.Globalization;
using System.Text.Json;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EthSignal.Infrastructure.Engine.ML;

/// <summary>
/// Loads and runs ML model inference for outcome prediction.
/// Supports ONNX models via Microsoft.ML.OnnxRuntime.
/// When no trained model is in DB, a heuristic fallback is used in SHADOW mode so predictions
/// still flow for logging/dashboard — the Python pipeline trains a real model later.
/// Thread-safe: model loading uses a write lock, inference uses a read lock.
/// </summary>
public sealed class MlInferenceService : IDisposable
{
    private readonly IMlModelRepository _modelRepo;
    private readonly ILogger<MlInferenceService> _logger;
    private readonly ReaderWriterLockSlim _lock = new();

    private MlModelMetadata? _activeModel;
    private InferenceSession? _onnxSession;
    private Func<float[], (float winProb, float calibrated)>? _inferFunc;
    private List<CalibrationPoint>? _calibrationCurve;
    private string? _calibrationVersion;
    private string? _thresholdLookupVersion;
    private SignalFrequencyManager? _frequencyManager;
    private IReadOnlyList<string>? _activeFeatureList;

    // Sentinel metadata used when running heuristic fallback (no trained model yet)
    private static readonly MlModelMetadata HeuristicSentinel = new()
    {
        Id = 0,
        ModelType = "outcome_predictor",
        ModelVersion = "heuristic-v1",
        FileFormat = "heuristic",
        FilePath = "",
        TrainStartUtc = DateTimeOffset.MinValue,
        TrainEndUtc = DateTimeOffset.MinValue,
        TrainingSampleCount = 0,
        FeatureCount = 0,
        FeatureListJson = "[]",
        FoldMetricsJson = "{}",
        FeatureImportanceJson = "{}",
        Status = MlModelStatus.Shadow
    };

    public MlInferenceService(IMlModelRepository modelRepo, ILogger<MlInferenceService> logger)
    {
        _modelRepo = modelRepo;
        _logger = logger;
    }

    /// <summary>Whether a model (ONNX or heuristic) is loaded and ready for inference.</summary>
    public bool IsReady
    {
        get
        {
            _lock.EnterReadLock();
            try { return _activeModel != null && _inferFunc != null; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>Current active model version, or null.</summary>
    public string? ActiveModelVersion
    {
        get
        {
            _lock.EnterReadLock();
            try { return _activeModel?.ModelVersion; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// File format of the currently loaded model — "onnx" for a real trained
    /// model, "heuristic" when the heuristic fallback is active, or null when
    /// nothing is loaded. Surfaced via /api/admin/ml/health.
    /// </summary>
    public string? ActiveModelFormat
    {
        get
        {
            _lock.EnterReadLock();
            try { return _activeModel?.FileFormat; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// True when the currently loaded "model" is the heuristic fallback rather
    /// than a trained ONNX artifact. Used by health reporting and runtime gate
    /// enforcement to reject accuracy-critical paths.
    /// </summary>
    public bool IsHeuristicFallback
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _activeModel != null
                    && _activeModel.FileFormat.Equals("heuristic", StringComparison.OrdinalIgnoreCase);
            }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Load or reload the active model from the DB + disk.
    /// If no DB model is registered, activates heuristic fallback so SHADOW mode
    /// still produces predictions (useful before first Python training run).
    /// Called at startup and on retrain events.
    /// </summary>
    public async Task LoadActiveModelAsync(CancellationToken ct = default)
    {
        try
        {
            var model = await _modelRepo.GetActiveModelAsync("outcome_predictor", ct);

            if (model == null)
            {
                _logger.LogInformation(
                    "[ML] No active model in DB — activating heuristic fallback. " +
                    "Run ml/train_pipeline.sh to train a real model.");
                SetFallback(HeuristicSentinel);
                EnforceNoHeuristicInActiveMode();
                return;
            }

            if (!File.Exists(model.FilePath))
            {
                _logger.LogError(
                    "[ML][ALERT] ML_FALLBACK_ACTIVATED — Model file not found at {Path}. " +
                    "DB entry {Version} exists but ONNX file is missing on disk. " +
                    "Heuristic fallback is now active. Trade gating is degraded. " +
                    "Re-run ml/train_pipeline.sh and register a new model.",
                    model.FilePath, model.ModelVersion);
                SetFallback(HeuristicSentinel);
                EnforceNoHeuristicInActiveMode();
                return;
            }

            if (!model.FileFormat.Equals("onnx", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "[ML][ALERT] ML_FALLBACK_ACTIVATED — Unsupported model format '{Format}' for version {Version}. " +
                    "Only 'onnx' is supported. Heuristic fallback is now active. " +
                    "Re-export the model as ONNX and re-register.",
                    model.FileFormat, model.ModelVersion);
                SetFallback(HeuristicSentinel);
                EnforceNoHeuristicInActiveMode();
                return;
            }

            _logger.LogInformation("[ML] Loading ONNX model: {Version} from {Path}",
                model.ModelVersion, model.FilePath);

            var artifacts = LoadCompanionArtifacts(model);
            var (session, infer) = LoadOnnxModel(model.FilePath);

            _lock.EnterWriteLock();
            try
            {
                _onnxSession?.Dispose();
                _onnxSession = session;
                _inferFunc = infer;
                _activeModel = model;
                _calibrationCurve = artifacts.CalibrationCurve;
                _calibrationVersion = artifacts.CalibrationVersion;
                _thresholdLookupVersion = artifacts.ThresholdLookupVersion;
                _activeFeatureList = ParseFeatureList(model.FeatureListJson);

                // If the DB metadata has no feature list, infer feature count from the ONNX
                // session's input shape and take the first N canonical features. This handles
                // models where feature_names were not stored in the meta JSON.
                if (_activeFeatureList == null && session != null)
                {
                    var sessionInputName = session.InputNames[0];
                    if (session.InputMetadata.TryGetValue(sessionInputName, out var inputMeta)
                        && inputMeta.Dimensions.Length >= 2
                        && inputMeta.Dimensions[1] > 0)
                    {
                        int expectedCount = (int)inputMeta.Dimensions[1];
                        if (expectedCount <= MlFeatureVector.FeatureNames.Count)
                        {
                            _activeFeatureList = MlFeatureVector.FeatureNames.Take(expectedCount).ToList();
                            _logger.LogInformation(
                                "[ML] Feature list derived from ONNX session input shape: {Count} features " +
                                "(DB FeatureListJson was empty for version {Version})",
                                expectedCount, model.ModelVersion);
                        }
                    }
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }

            if (_frequencyManager != null)
            {
                if (artifacts.ThresholdLookup != null && artifacts.ThresholdLookup.Count > 0)
                    _frequencyManager.LoadLookupTable(artifacts.ThresholdLookup);
                else
                    _frequencyManager.ResetToDefaults();
            }

            // Log ONNX model inputs/outputs for diagnostics
            if (session != null)
            {
                var inputNames = string.Join(", ", session.InputNames);
                var outputNames = string.Join(", ", session.OutputNames);
                _logger.LogInformation(
                    "[ML] ONNX session ready. Inputs: [{Inputs}] | Outputs: [{Outputs}]",
                    inputNames, outputNames);
            }

            _logger.LogInformation("[ML] Model active: {Version} ({Format})",
                model.ModelVersion, model.FileFormat);
            _logger.LogInformation(
                "[ML] Companion artifacts | Calibration={CalibrationVersion} | ThresholdLookup={ThresholdLookupVersion}",
                _calibrationVersion ?? "none",
                _thresholdLookupVersion ?? "none");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[ML][ALERT] ML_FALLBACK_ACTIVATED — Unexpected exception during model load. " +
                "Heuristic fallback is now active. Trade gating is degraded.");
            SetFallback(HeuristicSentinel);
            EnforceNoHeuristicInActiveMode();
        }
    }

    /// <summary>
    /// Run inference. Returns null only if mode is DISABLED.
    /// In SHADOW/ACTIVE mode always returns a prediction (ONNX or heuristic).
    /// </summary>
    public MlPrediction? Predict(MlFeatureVector features, MlMode mode)
    {
        if (mode == MlMode.DISABLED)
        {
            _logger.LogDebug("[ML] Predict() skipped — MlMode=DISABLED");
            return null;
        }

        _lock.EnterReadLock();
        try
        {
            if (_activeModel == null || _inferFunc == null)
            {
                _logger.LogWarning(
                    "[ML] Predict() called but no model loaded (IsReady=false). " +
                    "LoadActiveModelAsync may not have been called yet.");
                return null;
            }

            var isHeuristic = _activeModel.FileFormat.Equals("heuristic", StringComparison.OrdinalIgnoreCase);
            var sw = Stopwatch.StartNew();
            var input = features.ToFloatArray(_activeFeatureList);

            _logger.LogDebug(
                "[ML] Running inference | Model={Version} | IsHeuristic={IsHeuristic} | Features={Count} | EvalId={EvalId}",
                _activeModel.ModelVersion, isHeuristic, input.Length, features.EvaluationId);

            var (winProb, calibrated) = _inferFunc(input);
            var threshold = ComputeRecommendedThreshold(features);
            sw.Stop();

            _logger.LogInformation(
                "[ML] Inference complete | Model={Version} | WinProb={WinProb:P1} | Calibrated={Calibrated:P1} | " +
                "Threshold={Threshold:F0} | LatencyUs={LatencyUs} | Mode={Mode} | IsHeuristic={IsHeuristic}",
                _activeModel.ModelVersion, winProb, calibrated, threshold,
                (int)sw.Elapsed.TotalMicroseconds, mode, isHeuristic);

            var rawProb = Math.Clamp(winProb, 0f, 1f);
            var effectiveProb = Math.Clamp(calibrated, 0f, 1f);
            var predictionConfidence = Math.Clamp((int)Math.Round(Math.Max(effectiveProb, 1f - effectiveProb) * 100f), 0, 100);
            decimal targetRMultiple = _paramProvider?.GetActive().TargetRMultiple ?? 1.5m;
            decimal expectedValueR = (decimal)effectiveProb * targetRMultiple - (1m - (decimal)effectiveProb) * 1.0m;

            return new MlPrediction
            {
                PredictionId = Guid.NewGuid(),
                EvaluationId = features.EvaluationId,
                ModelVersion = _activeModel.ModelVersion,
                ModelType = _activeModel.ModelType,
                Timeframe = features.Timeframe,
                RawWinProbability = Math.Clamp((decimal)rawProb, 0, 1),
                CalibratedWinProbability = Math.Clamp((decimal)effectiveProb, 0, 1),
                PredictionConfidence = predictionConfidence,
                RecommendedThreshold = Math.Clamp((int)threshold, 40, 90),
                ExpectedValueR = expectedValueR,
                InferenceLatencyUs = (int)sw.Elapsed.TotalMicroseconds,
                IsActive = mode == MlMode.ACTIVE,
                Mode = mode,
                // FR-9: Explicit inference mode distinguishing heuristic from trained
                InferenceMode = isHeuristic
                    ? MlInferenceMode.HEURISTIC_FALLBACK
                    : mode == MlMode.SHADOW
                        ? MlInferenceMode.TRAINED_SHADOW
                        : MlInferenceMode.TRAINED_ACTIVE
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[ML] Inference threw exception — returning null (rule-based fallback). " +
                "Model={Version}", _activeModel?.ModelVersion);
            return null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    // ─── Private helpers ─────────────────────────────────────────────────────

    private IParameterProvider? _paramProvider;

    public void SetParameterProvider(IParameterProvider paramProvider)
    {
        _paramProvider = paramProvider;
    }

    public void SetFrequencyManager(SignalFrequencyManager frequencyManager)
    {
        _frequencyManager = frequencyManager;
    }

    private void EnforceNoHeuristicInActiveMode()
    {
        if (_paramProvider == null) return;
        var p = _paramProvider.GetActive();
        if (p.MlMode == MlMode.ACTIVE)
        {
            _logger.LogError(
                "[ML][ALERT] ML_MODE_DOWNGRADED — MlMode was ACTIVE but no trained ONNX model is available. " +
                "Force-downgrading to SHADOW to prevent unvalidated heuristic predictions from gating live trades. " +
                "Train and register a real ONNX model (ml/train_pipeline.sh) then set MlMode=ACTIVE.");
            _paramProvider.ForceOverrideMlMode(MlMode.SHADOW);
        }
    }

    private void SetFallback(MlModelMetadata sentinel)
    {
        _lock.EnterWriteLock();
        try
        {
            _onnxSession?.Dispose();
            _onnxSession = null;
            _inferFunc = CreateFallbackInference();
            _activeModel = sentinel;
            _calibrationCurve = null;
            _calibrationVersion = null;
            _thresholdLookupVersion = null;
            _activeFeatureList = null;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        _frequencyManager?.ResetToDefaults();
        _logger.LogError(
            "[ML][ALERT] ML_HEURISTIC_ACTIVE — Running on heuristic fallback (version={Version}). " +
            "Win-probability predictions are NOT from a trained model. Dashboard may show 'ML active' — this is misleading. " +
            "Check /api/admin/ml/health for IsHeuristicFallback=true to detect this state.",
            sentinel.ModelVersion);
    }

    private (InferenceSession? session, Func<float[], (float, float)> infer) LoadOnnxModel(string filePath)
    {
        try
        {
            var options = new SessionOptions();
            options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;

            var session = new InferenceSession(filePath, options);
            var inputName = session.InputNames[0];

            _logger.LogDebug("[ML] ONNX session created for {Path} | InputName={InputName}", filePath, inputName);

            Func<float[], (float, float)> infer = (float[] features) =>
            {
                // Build input tensor [1, N]
                var tensor = new DenseTensor<float>(features, [1, features.Length]);
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(inputName, tensor)
                };

                using var results = session.Run(inputs);

                // skl2onnx models emit [0]=class-label (DenseTensor<long>), [1]=probabilities.
                // Single-output models emit [0]=probabilities directly.
                // Always read WIN probability (classIndex=1) from the probability output.
                var probOutput = results.Count >= 2 ? results[1] : results[0];

                float winProb = ExtractScalar(probOutput, 1);
                float calibrated = ApplyCalibration(winProb);
                return (Math.Clamp(winProb, 0f, 1f), Math.Clamp(calibrated, 0f, 1f));
            };

            return (session, infer);
        }
        catch (OnnxRuntimeException ex)
        {
            _logger.LogError(ex, "[ML] OnnxRuntime failed to load model from {Path}", filePath);
            return (null, CreateFallbackInference());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ML] Unexpected error loading ONNX model from {Path}", filePath);
            return (null, CreateFallbackInference());
        }
    }

    /// <summary>
    /// Extract a scalar float from an ONNX output — handles both plain float tensors
    /// and probability_map outputs (list of dict produced when zipmap=True in skl2onnx).
    /// </summary>
    private float ExtractScalar(DisposableNamedOnnxValue output, int classIndex)
    {
        // Case 0: class-label tensor (skl2onnx results[0]) — should not reach here after fix,
        // but guard defensively. Return binary class score, not a probability.
        if (output.Value is DenseTensor<long> lt)
        {
            _logger.LogDebug("[ML] ExtractScalar received class-label tensor — returning binary class score");
            return lt.Length > 0 && lt[0] == 1L ? 1.0f : 0.0f;
        }

        // Case 1: plain float tensor (zipmap=False or pipeline with predict_proba)
        if (output.Value is DenseTensor<float> ft)
        {
            try
            {
                var flat = ft.ToArray();
                if (flat.Length == 0)
                    return 0.5f;

                if (flat.Length == 1)
                    return flat[0];

                var dims = ft.Dimensions.ToArray();
                var rank = dims.Length;

                // Rank-1 tensor (e.g. [2])
                if (rank <= 1)
                {
                    var idx = Math.Clamp(classIndex, 0, flat.Length - 1);
                    return flat[idx];
                }

                // Rank-2+ tensor (e.g. [1,2], [1,1,2]) — use first batch/row and class on last axis.
                var classAxisLen = Convert.ToInt32(dims[rank - 1]);
                var safeClassIndex = Math.Clamp(classIndex, 0, Math.Max(0, classAxisLen - 1));

                var indices = new int[rank];
                indices[rank - 1] = safeClassIndex;

                return ft[indices];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[ML] Float tensor extraction failed (Rank={Rank}, Length={Length}) — using first value fallback",
                    ft.Rank, ft.Length);

                var flat = ft.ToArray();
                return flat.Length > 0 ? flat[0] : 0.5f;
            }
        }

        // Case 2: sequence of maps (ZipMap output — list of {label→prob} dicts)
        if (output.Value is IEnumerable<IDictionary<long, float>> maps)
        {
            var first = maps.FirstOrDefault();
            if (first != null && first.TryGetValue(1L, out var p1)) return p1;
        }
        if (output.Value is IEnumerable<IDictionary<string, float>> smaps)
        {
            var first = smaps.FirstOrDefault();
            if (first != null && first.TryGetValue("1", out var ps1)) return ps1;
        }

        _logger.LogWarning("[ML] Unknown ONNX output type {Type} — defaulting to 0.5",
            output.Value?.GetType().Name ?? "null");
        return 0.5f;
    }

    /// <summary>
    /// Heuristic-based inference used before a real model is trained.
    /// Produces plausible shadow predictions based on key feature indices.
    /// Features follow MlFeatureVector.ToFloatArray() ordering:
    ///   [0]=ema20 (raw price), [1]=ema50, [2]=rsi14, [3]=macd_hist,
    ///   [4]=adx14, [5]=plus_di, [6]=minus_di, ... [35]=rule_based_score
    /// NOTE: This heuristic only uses [2] rsi14, [3] macd_hist, [4] adx14, [35] rule_based_score.
    /// It is intended for SHADOW mode annotation only — not for ACTIVE trade gating (see S-09).
    /// </summary>
    private static Func<float[], (float winProb, float calibrated)> CreateFallbackInference()
    {
        return (float[] features) =>
        {
            float ruleScore = features.Length > 35 ? features[35] / 100f : 0.5f;
            float adx = features.Length > 4 ? features[4] : 20f;
            float rsi = features.Length > 2 ? features[2] : 50f;
            float macdHist = features.Length > 3 ? features[3] : 0f;

            // Base probability proportional to rule score
            float winProb = ruleScore * 0.55f + 0.22f;

            // Trend strength bonus
            if (adx > 25) winProb += 0.04f;
            if (adx > 35) winProb += 0.04f;

            // MACD confirmation
            if (macdHist > 0) winProb += 0.03f;

            // RSI extremes penalty
            if (rsi < 25 || rsi > 75) winProb -= 0.05f;

            winProb = Math.Clamp(winProb, 0.10f, 0.88f);
            float calibrated = winProb;
            return (winProb, calibrated);
        };
    }

    private float ApplyCalibration(float rawProbability)
    {
        if (_calibrationCurve == null || _calibrationCurve.Count == 0)
            return Math.Clamp(rawProbability, 0f, 1f);

        var clamped = Math.Clamp(rawProbability, 0f, 1f);
        if (clamped <= _calibrationCurve[0].Raw)
            return Math.Clamp(_calibrationCurve[0].Calibrated, 0f, 1f);

        var last = _calibrationCurve[^1];
        if (clamped >= last.Raw)
            return Math.Clamp(last.Calibrated, 0f, 1f);

        for (var i = 1; i < _calibrationCurve.Count; i++)
        {
            var lower = _calibrationCurve[i - 1];
            var upper = _calibrationCurve[i];
            if (clamped > upper.Raw)
                continue;

            if (Math.Abs(upper.Raw - lower.Raw) < 1e-6f)
                return Math.Clamp(upper.Calibrated, 0f, 1f);

            var t = (clamped - lower.Raw) / (upper.Raw - lower.Raw);
            var interpolated = lower.Calibrated + t * (upper.Calibrated - lower.Calibrated);
            return Math.Clamp(interpolated, 0f, 1f);
        }

        return Math.Clamp(last.Calibrated, 0f, 1f);
    }

    private CompanionArtifacts LoadCompanionArtifacts(MlModelMetadata model)
    {
        var directory = Path.GetDirectoryName(model.FilePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return CompanionArtifacts.Empty;

        var calibrationPath = FindNearestArtifactPath(directory, "recalibrator_*.json", model.ModelVersion);
        var thresholdPath = FindNearestArtifactPath(directory, "threshold_lookup_*.json", model.ModelVersion);

        return new CompanionArtifacts(
            LoadCalibrationCurve(calibrationPath),
            LoadLookupTable(thresholdPath),
            ExtractArtifactVersion(calibrationPath),
            ExtractArtifactVersion(thresholdPath));
    }

    private List<CalibrationPoint>? LoadCalibrationCurve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            var artifact = JsonSerializer.Deserialize<CalibrationArtifact>(stream, JsonOptions);
            if (artifact?.CalibrationTable == null || artifact.CalibrationTable.Count == 0)
                return null;

            return artifact.CalibrationTable
                .Select(kv => new CalibrationPoint(
                    float.Parse(kv.Key, CultureInfo.InvariantCulture),
                    kv.Value))
                .OrderBy(p => p.Raw)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ML] Failed to load calibration artifact from {Path}", path);
            return null;
        }
    }

    private int ComputeRecommendedThreshold(MlFeatureVector features)
    {
        var p = _paramProvider?.GetActive();
        if (p == null)
            return 65;

        var directionIsBuy = features.DirectionEncoded >= 0;

        // Accuracy-first guard: when MlDisableDynamicThresholdsWhenUnhealthy is on
        // and the loaded model is the heuristic fallback, refuse dynamic threshold
        // relaxation. Running on heuristic is already a degraded state; lowering
        // the bar in that state would add signals the ML can't actually validate.
        bool dynamicAllowed = p.MlDynamicThresholdsEnabled;
        if (dynamicAllowed && p.MlDisableDynamicThresholdsWhenUnhealthy && IsHeuristicFallback)
        {
            dynamicAllowed = false;
        }

        if (_frequencyManager != null && dynamicAllowed)
        {
            var recommendation = _frequencyManager.GetDynamicThreshold(
                DecodeRegime(features.RegimeLabel),
                features.Adx14,
                features.Atr14Pct,
                features.HourOfDay,
                0m,
                features.Timeframe,
                p);
            return directionIsBuy ? recommendation.BuyThreshold : recommendation.SellThreshold;
        }

        return directionIsBuy ? p.ConfidenceBuyThreshold : p.ConfidenceSellThreshold;
    }

    private static Regime DecodeRegime(int regimeLabel) => regimeLabel switch
    {
        1 => Regime.BULLISH,
        2 => Regime.BEARISH,
        _ => Regime.NEUTRAL
    };

    private static IReadOnlyList<string>? ParseFeatureList(string? featureListJson)
    {
        if (string.IsNullOrWhiteSpace(featureListJson))
            return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(featureListJson, JsonOptions);
            return parsed is { Count: > 0 } ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    private Dictionary<string, int>? LoadLookupTable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var rawJson = File.ReadAllText(path);
            var sanitizedJson = Regex.Replace(rawJson, @"\bNaN\b", "0");
            var artifact = JsonSerializer.Deserialize<ThresholdLookupArtifact>(sanitizedJson, JsonOptions);
            return artifact?.LookupTable;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ML] Failed to load threshold lookup artifact from {Path}", path);
            return null;
        }
    }

    private static string? FindNearestArtifactPath(string directory, string pattern, string modelVersion)
    {
        var files = Directory.GetFiles(directory, pattern);
        if (files.Length == 0)
            return null;

        if (!TryParseVersionTimestamp(modelVersion, out var modelTimestamp))
            return files.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

        return files
            .Select(path => new
            {
                Path = path,
                Timestamp = TryParseVersionTimestamp(ExtractArtifactVersion(path), out var ts)
                    ? ts
                    : (DateTimeOffset?)null
            })
            .Where(x => x.Timestamp.HasValue)
            .OrderBy(x => Math.Abs((x.Timestamp!.Value - modelTimestamp).Ticks))
            .ThenBy(x => x.Timestamp!.Value)
            .Select(x => x.Path)
            .FirstOrDefault()
            ?? files.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private static string? ExtractArtifactVersion(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var name = Path.GetFileNameWithoutExtension(path);
        var marker = name.IndexOf("_v", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? name[(marker + 1)..] : null;
    }

    private static bool TryParseVersionTimestamp(string? version, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(version))
            return false;

        var normalized = version.StartsWith('v') ? version[1..] : version;
        return DateTimeOffset.TryParseExact(
            normalized,
            "yyyyMMdd_HHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    private sealed record CompanionArtifacts(
        List<CalibrationPoint>? CalibrationCurve,
        Dictionary<string, int>? ThresholdLookup,
        string? CalibrationVersion,
        string? ThresholdLookupVersion)
    {
        public static CompanionArtifacts Empty { get; } = new(null, null, null, null);
    }

    private sealed record CalibrationPoint(float Raw, float Calibrated);

    private sealed record CalibrationArtifact
    {
        [JsonPropertyName("calibration_table")]
        public Dictionary<string, float>? CalibrationTable { get; init; }
    }

    private sealed record ThresholdLookupArtifact
    {
        [JsonPropertyName("lookup_table")]
        public Dictionary<string, int>? LookupTable { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public void Dispose()
    {
        _lock.EnterWriteLock();
        try
        {
            _onnxSession?.Dispose();
            _onnxSession = null;
            _inferFunc = null;
            _activeModel = null;
            _activeFeatureList = null;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        _lock.Dispose();
    }
}
