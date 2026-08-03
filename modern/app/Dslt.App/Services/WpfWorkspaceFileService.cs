using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dslt.Managed.Core.Models;
using Microsoft.Win32;

namespace Dslt.App.Services;

public sealed class WpfWorkspaceFileService : IWorkspaceFileService
{
    public async Task<VolumeData?> OpenVolumeAsync(CancellationToken cancellationToken)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open confocal stack",
            Filter = "TIFF / LSM stack|*.tif;*.tiff;*.lsm|All files|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true) return null;
        return await Task.Run(() => ReadStack(dialog.FileName, cancellationToken), cancellationToken);
    }

    public string? ChooseExportBasePath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export result package",
            Filter = "DSLT result package|*.dslt-result",
            AddExtension = false,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog() == true
            ? Path.ChangeExtension(dialog.FileName, null)
            : null;
    }

    internal static VolumeData ReadStack(string path, CancellationToken cancellationToken)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = new TiffBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("TIFF stack contains no frames.");
        var width = decoder.Frames[0].PixelWidth;
        var height = decoder.Frames[0].PixelHeight;
        var depth = decoder.Frames.Count;
        var sliceLength = checked(width * height);
        var samples = new float[checked(sliceLength * depth)];
        var bytes = new byte[checked(sliceLength * sizeof(float))];

        for (var z = 0; z < depth; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = decoder.Frames[z];
            if (frame.PixelWidth != width || frame.PixelHeight != height)
                throw new InvalidDataException("All TIFF frames must have the same dimensions.");
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Gray32Float, null, 0);
            converted.CopyPixels(bytes, width * sizeof(float), 0);
            Buffer.BlockCopy(bytes, 0, samples, z * bytes.Length, bytes.Length);
        }

        return new VolumeData(width, height, depth, 1, 0, Calibration.Unit, samples);
    }
}
