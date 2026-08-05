using Dslt.Managed.Core.IO;
using Dslt.Managed.Core.Models;
using Dslt.Managed.Core.Analysis;
using System.Windows.Media.Imaging;

namespace Dslt.App.Services;

public interface IWorkspaceFileService
{
    Task<VolumeData?> OpenVolumeAsync(IProgress<double>? progress, CancellationToken cancellationToken);
    Task<LabelTiffVolume?> OpenLabelsAsync(CancellationToken cancellationToken);
    Task<LegacyHeightMap?> OpenHeightMapAsync(CancellationToken cancellationToken);
    Task<string?> SaveHeightMapAsync(LegacyHeightMap heightMap, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>?> SaveHeightSurfaceAreaAsync(
        HeightSurfaceAreaMap areaMap,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<string>?> SaveOrthogonalViewsAsync(
        string viewName,
        BitmapSource xy,
        BitmapSource yz,
        BitmapSource zx,
        CancellationToken cancellationToken);
    string? ChooseExportBasePath();
}
