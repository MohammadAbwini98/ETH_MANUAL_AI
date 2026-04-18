using System.Globalization;
using System.Text.Json;
using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Db;

internal static class MlModelMetadataSidecarReader
{
    public static MlModelMetadata Enrich(MlModelMetadata model)
    {
        if (!NeedsEnrichment(model))
            return model;

        if (!TryReadMetrics(model.FilePath, out var auc, out var brier, out var ece, out var logLoss))
            return model;

        return model with
        {
            AucRoc = model.AucRoc > 0 ? model.AucRoc : auc ?? 0m,
            BrierScore = model.BrierScore > 0 ? model.BrierScore : brier ?? 0m,
            ExpectedCalibrationError = model.ExpectedCalibrationError > 0 ? model.ExpectedCalibrationError : ece ?? 0m,
            LogLoss = model.LogLoss > 0 ? model.LogLoss : logLoss ?? 0m
        };
    }

    public static bool TryReadMetrics(
        string modelPath,
        out decimal? auc,
        out decimal? brier,
        out decimal? ece,
        out decimal? logLoss)
    {
        auc = null;
        brier = null;
        ece = null;
        logLoss = null;

        try
        {
            var directory = Path.GetDirectoryName(modelPath);
            var baseName = Path.GetFileNameWithoutExtension(modelPath);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseName))
                return false;

            var metaPath = Path.Combine(directory, $"{baseName}_meta.json");
            if (!File.Exists(metaPath))
                return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            var root = doc.RootElement;

            auc = ReadMetric(root, "avg_auc_roc") ?? AverageFoldMetric(root, "auc_roc");
            brier = ReadMetric(root, "avg_brier_score") ?? AverageFoldMetric(root, "brier_score");
            ece = ReadMetric(root, "avg_expected_calibration_error") ?? AverageFoldMetric(root, "expected_calibration_error");
            logLoss = ReadMetric(root, "avg_log_loss") ?? AverageFoldMetric(root, "log_loss");

            return auc.HasValue || brier.HasValue || ece.HasValue || logLoss.HasValue;
        }
        catch
        {
            return false;
        }
    }

    private static bool NeedsEnrichment(MlModelMetadata model)
    {
        return model.AucRoc <= 0m
            || model.BrierScore <= 0m
            || model.ExpectedCalibrationError <= 0m
            || model.LogLoss <= 0m;
    }

    private static decimal? ReadMetric(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var metric)
            ? ParseDecimal(metric)
            : null;
    }

    private static decimal? AverageFoldMetric(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty("fold_metrics", out var folds) || folds.ValueKind != JsonValueKind.Array)
            return null;

        decimal sum = 0m;
        var count = 0;
        foreach (var fold in folds.EnumerateArray())
        {
            if (fold.ValueKind != JsonValueKind.Object || !fold.TryGetProperty(propertyName, out var metric))
                continue;

            var value = ParseDecimal(metric);
            if (!value.HasValue)
                continue;

            sum += value.Value;
            count++;
        }

        return count > 0 ? sum / count : null;
    }

    private static decimal? ParseDecimal(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetDecimal(out var decimalValue))
                return decimalValue;
            if (element.TryGetDouble(out var doubleValue))
                return Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture);
        }

        if (element.ValueKind == JsonValueKind.String &&
            decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
