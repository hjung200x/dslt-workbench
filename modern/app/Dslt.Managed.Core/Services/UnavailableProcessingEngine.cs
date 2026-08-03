using Dslt.Managed.Core.Models;

namespace Dslt.Managed.Core.Services;

public sealed class UnavailableProcessingEngine(string reason) : IProcessingEngine
{
    public bool IsAvailable => false;
    public string Status { get; } = reason;
    public BackendInformation Backend { get; } = new(true, false, false, 0, "Managed UI only");

    public Task<ProcessingResult> RunAsync(
        VolumeData volume,
        OperationParameters parameters,
        IProgress<double>? progress,
        CancellationToken cancellationToken) =>
        Task.FromException<ProcessingResult>(new InvalidOperationException(Status));

    public void Dispose()
    {
    }
}

