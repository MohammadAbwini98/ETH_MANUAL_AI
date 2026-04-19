using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Db;

public interface IMlModelRepository
{
    Task<MlModelMetadata?> GetActiveModelAsync(string modelType, CancellationToken ct = default);
    Task<MlModelMetadata?> GetActiveModelAsync(string modelType, string regimeScope, CancellationToken ct = default);
    Task<MlModelMetadata?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<MlModelMetadata>> GetAllAsync(CancellationToken ct = default);
    Task<long> InsertAsync(MlModelMetadata model, CancellationToken ct = default);
    Task UpdateStatusAsync(long id, MlModelStatus status, string? reason = null, CancellationToken ct = default);
}
