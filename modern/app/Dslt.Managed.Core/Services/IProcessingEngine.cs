using Dslt.Managed.Core.Models;

namespace Dslt.Managed.Core.Services;

public interface IProcessingEngine : IDisposable
{
    bool IsAvailable { get; }
    string Status { get; }
    BackendInformation Backend { get; }

    Task<ProcessingWorkEstimate> EstimateAsync(
        VolumeData volume,
        OperationParameters parameters,
        CancellationToken cancellationToken);

    Task<ProcessingResult> RunAsync(
        VolumeData volume,
        OperationParameters parameters,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}
