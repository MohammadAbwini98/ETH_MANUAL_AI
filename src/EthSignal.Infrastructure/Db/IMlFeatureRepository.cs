using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Db;

public interface IMlFeatureRepository
{
    Task InsertAsync(
        MlFeatureVector features,
        Guid? signalId,
        string featureVersion,
        string linkStatus,
        CancellationToken ct = default);
    Task<MlFeatureVector?> GetByEvaluationIdAsync(Guid evaluationId, CancellationToken ct = default);
    Task LinkSignalAsync(Guid evaluationId, Guid signalId, CancellationToken ct = default);
    Task UpdateLinkStatusAsync(Guid evaluationId, string linkStatus, CancellationToken ct = default);
}
