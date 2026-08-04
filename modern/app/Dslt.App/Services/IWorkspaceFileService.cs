using Dslt.Managed.Core.IO;
using Dslt.Managed.Core.Models;

namespace Dslt.App.Services;

public interface IWorkspaceFileService
{
    Task<VolumeData?> OpenVolumeAsync(CancellationToken cancellationToken);
    Task<LabelTiffVolume?> OpenLabelsAsync(CancellationToken cancellationToken);
    string? ChooseExportBasePath();
}
