using Dslt.Managed.Core.Models;

namespace Dslt.App.Services;

public interface IWorkspaceFileService
{
    Task<VolumeData?> OpenVolumeAsync(CancellationToken cancellationToken);
    string? ChooseExportBasePath();
}

