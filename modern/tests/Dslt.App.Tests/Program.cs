using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dslt.App.Services;

namespace Dslt.App.Tests;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dslt-stack-{Guid.NewGuid():N}.tif");
        try
        {
            var encoder = new TiffBitmapEncoder();
            for (var z = 0; z < 3; z++)
            {
                var pixels = Enumerable.Repeat((byte)(64 + z * 64), 12).ToArray();
                var frame = BitmapFrame.Create(BitmapSource.Create(
                    4, 3, 96, 96, PixelFormats.Gray8, null, pixels, 4));
                encoder.Frames.Add(frame);
            }
            using (var stream = File.Create(path)) encoder.Save(stream);

            var volume = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
            volume.Validate();
            if (volume.Width != 4 || volume.Height != 3 || volume.Depth != 3)
                throw new InvalidOperationException("TIFF stack dimensions were not preserved.");
            var sliceLength = volume.Width * volume.Height;
            if (!(volume.Samples[0] < volume.Samples[sliceLength] &&
                  volume.Samples[sliceLength] < volume.Samples[sliceLength * 2]))
                throw new InvalidOperationException("TIFF frame intensity order was not preserved.");
            Console.WriteLine("DSLT WPF multi-frame TIFF smoke test passed.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
