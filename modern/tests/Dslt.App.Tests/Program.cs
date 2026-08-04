using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using Dslt.App.Services;
using Dslt.Managed.Core.Models;

namespace Dslt.App.Tests;

internal static class Program
{
    private static readonly nint DpiAwarenessContextPerMonitorV2 = new(-4);

    [STAThread]
    private static async Task Main()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dslt-stack-{Guid.NewGuid():N}.tif");
        try
        {
            RunMainWindowStartupSmokeTest();
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
            if (volume.Source?.VoxelType != VolumeVoxelType.UnsignedInt8)
                throw new InvalidOperationException("Gray8 source voxel type was not preserved.");
            if (volume.Source.ChannelPlanarRawSamples[0] != 64 || volume.Source.ChannelPlanarRawSamples[^1] != 192)
                throw new InvalidOperationException("Gray8 decoded samples were not preserved.");

            RunGray16RoundTripTest();
            RunFileBackedLargeVolumeTest();
            RunGray32FloatRoundTripTest();
            RunUnsignedInt32RoundTripTest();
            RunUnsignedInt32MinIsWhiteTest();
            RunSignedInt32BigEndianRoundTripTest();
            Int32TiffCompressionTests.Run();
            RunMalformedInt32StripTest();
            RunImageJHyperStackTest();
            LsmMetadataTests.Run();
            ReferenceLabelImporterTests.Run();
            await RealDataManifestAssemblerTests.RunAsync();
            await DepthColorProjectionTests.RunAsync();
            await WorkflowViewModelTests.RunAsync();
            Console.WriteLine("DSLT WPF TIFF, metadata, hyperstack, and workflow tests passed.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void RunMainWindowStartupSmokeTest()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunMainWindowStartupSmokeTestOnSta();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("The WPF main-window startup smoke test failed.", failure);
    }

    private static void RunMainWindowStartupSmokeTestOnSta()
    {
        var application = new global::Dslt.App.App();
        application.InitializeComponent();
        Exception? inspectionFailure = null;
        application.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var window = application.MainWindow as global::Dslt.App.MainWindow ??
                    throw new InvalidOperationException("The application did not create its main window.");
                if (!window.IsLoaded)
                    throw new InvalidOperationException("The WPF main window did not reach the loaded state.");

                var windowHandle = new WindowInteropHelper(window).Handle;
                if (windowHandle == nint.Zero ||
                    !AreDpiAwarenessContextsEqual(
                        GetWindowDpiAwarenessContext(windowHandle),
                        DpiAwarenessContextPerMonitorV2))
                {
                    throw new InvalidOperationException(
                        "The WPF main window must run with PerMonitorV2 DPI awareness.");
                }

                var progress = FindVisualChild<ProgressBar>(window) ??
                    throw new InvalidOperationException("The WPF main window has no progress indicator.");
                var binding = BindingOperations.GetBindingExpression(progress, ProgressBar.ValueProperty);
                if (binding?.ParentBinding.Mode != BindingMode.OneWay)
                    throw new InvalidOperationException("The read-only progress property must use a OneWay binding.");

                AssertNamedInputControls(window);
                AssertMinimumHighDpiLayout(window);
            }
            catch (Exception error)
            {
                inspectionFailure = error;
            }
            finally
            {
                application.MainWindow?.Close();
                if (!application.Dispatcher.HasShutdownStarted)
                    application.Shutdown(inspectionFailure is null ? 0 : 1);
            }
        }, DispatcherPriority.ApplicationIdle);

        _ = application.Run();
        if (inspectionFailure is not null)
            throw inspectionFailure;
    }

    private static void AssertMinimumHighDpiLayout(global::Dslt.App.MainWindow window)
    {
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        window.UpdateLayout();

        foreach (var scale in new[] { 1.25, 1.5, 2.0 })
        {
            if (window.MinWidth * scale > 1_920 || window.MinHeight * scale > 1_080)
            {
                throw new InvalidOperationException(
                    $"The minimum window size does not fit a 1920 x 1080 display at {scale:P0} scaling.");
            }
        }

        var navigation = FindLogicalChild<ScrollViewer>(window, element =>
            AutomationProperties.GetName(element) == "Navigation and volume controls") ??
            throw new InvalidOperationException("The navigation controls are not scrollable.");
        var processing = FindLogicalChild<ScrollViewer>(window, element =>
            AutomationProperties.GetName(element) == "Processing and editing controls") ??
            throw new InvalidOperationException("The processing controls are not scrollable.");
        if (navigation.ScrollableHeight <= 0 || processing.ScrollableHeight <= 0)
            throw new InvalidOperationException("The minimum-height layout did not expose vertical scrolling.");

        var open = FindLogicalChild<Button>(window, element =>
            Equals(element.Content, "Open TIFF / LSM")) ??
            throw new InvalidOperationException("The open command is missing from the minimum-size layout.");
        var run = FindLogicalChild<Button>(window, element => Equals(element.Content, "Run")) ??
            throw new InvalidOperationException("The run command is missing from the minimum-size layout.");
        var cancel = FindLogicalChild<Button>(window, element => Equals(element.Content, "Cancel")) ??
            throw new InvalidOperationException("The cancel command is missing from the minimum-size layout.");
        var export = FindLogicalChild<Button>(window, element =>
            Equals(element.Content, "Export result + provenance")) ??
            throw new InvalidOperationException("The export command is missing from the minimum-size layout.");

        open.BringIntoView();
        run.BringIntoView();
        cancel.BringIntoView();
        export.BringIntoView();
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
        if (!IsWithinWindow(open, window) ||
            !IsWithinWindow(run, window) ||
            !IsWithinWindow(cancel, window) ||
            !IsWithinWindow(export, window))
        {
            throw new InvalidOperationException(
                "A primary workflow command could not be scrolled into the minimum-size window.");
        }
    }

    private static bool IsWithinWindow(FrameworkElement element, Window window)
    {
        var bounds = element.TransformToAncestor(window).TransformBounds(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Width > 0 && bounds.Height > 0 &&
            new Rect(0, 0, window.ActualWidth, window.ActualHeight).IntersectsWith(bounds);
    }

    private static T? FindLogicalChild<T>(
        DependencyObject parent,
        Func<T, bool> predicate) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is T match && predicate(match)) return match;
            if (child is DependencyObject dependencyObject)
            {
                var nested = FindLogicalChild(dependencyObject, predicate);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    private static void AssertNamedInputControls(DependencyObject root)
    {
        var unnamed = new List<string>();
        Visit(root, unnamed);
        if (unnamed.Count > 0)
            throw new InvalidOperationException(
                $"Focusable WPF controls require explicit accessible names: {string.Join(", ", unnamed)}");

        static void Visit(DependencyObject node, ICollection<string> unnamed)
        {
            if (node is Slider or ComboBox or TextBox or ListBox or ScrollViewer or ProgressBar)
            {
                var name = AutomationProperties.GetName(node);
                if (string.IsNullOrWhiteSpace(name))
                    unnamed.Add(node.GetType().Name);
            }

            foreach (var child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is DependencyObject dependencyObject)
                    Visit(dependencyObject, unnamed);
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    [DllImport("user32.dll")]
    private static extern nint GetWindowDpiAwarenessContext(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);

    private static void RunGray16RoundTripTest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dslt-gray16-{Guid.NewGuid():N}.tif");
        try
        {
            var values = new ushort[] { 0, 1, 1024, ushort.MaxValue };
            var bytes = new byte[values.Length * sizeof(ushort)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            var encoder = new TiffBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(
                2, 2, 96, 96, PixelFormats.Gray16, null, bytes, 2 * sizeof(ushort))));
            using (var stream = File.Create(path)) encoder.Save(stream);

            var volume = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
            volume.Validate();
            if (volume.Source?.VoxelType != VolumeVoxelType.UnsignedInt16)
                throw new InvalidOperationException("Gray16 source voxel type was not preserved.");
            if (!bytes.SequenceEqual(volume.Source.ChannelPlanarRawSamples))
                throw new InvalidOperationException("Gray16 decoded bytes were not bit-exact.");
            if (volume.Samples[^1] != 1f)
                throw new InvalidOperationException("Gray16 processing samples were not channel-normalized.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void RunFileBackedLargeVolumeTest()
    {
        const int width = 512;
        const int height = 512;
        const int depth = 32;
        var path = Path.Combine(Path.GetTempPath(), $"dslt-large-gray16-{Guid.NewGuid():N}.tif");
        try
        {
            var encoder = new TiffBitmapEncoder();
            for (var z = 0; z < depth; z++)
            {
                var pixels = new byte[checked(width * height * sizeof(ushort))];
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var value = checked((ushort)(x + y + z * 257));
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        pixels.AsSpan((y * width + x) * sizeof(ushort), sizeof(ushort)),
                        value);
                }
                var bitmap = BitmapSource.Create(
                    width,
                    height,
                    96,
                    96,
                    PixelFormats.Gray16,
                    null,
                    pixels,
                    checked(width * sizeof(ushort)));
                bitmap.Freeze();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
            }
            using (var stream = File.Create(path)) encoder.Save(stream);

            var timer = Stopwatch.StartNew();
            var volume = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
            timer.Stop();
            volume.Validate();

            if (volume.Width != width || volume.Height != height || volume.Depth != depth || volume.Channels != 1)
                throw new InvalidOperationException("The file-backed large TIFF dimensions were not preserved.");
            if (volume.Source is not
                {
                    VoxelType: VolumeVoxelType.UnsignedInt16,
                    Container: "TIFF",
                } source ||
                source.ChannelPlanarRawSamples.Length != checked(width * height * depth * sizeof(ushort)))
            {
                throw new InvalidOperationException("The file-backed large TIFF source bytes were not preserved.");
            }

            var lastRaw = BinaryPrimitives.ReadUInt16LittleEndian(source.ChannelPlanarRawSamples.AsSpan(^2));
            const ushort expectedLast = 8_989;
            if (lastRaw != expectedLast || Math.Abs(volume.Samples[^1] - 1) > 1e-6F)
                throw new InvalidOperationException("The file-backed large TIFF voxel order or normalization changed.");
            if (timer.Elapsed >= TimeSpan.FromSeconds(30))
                throw new InvalidOperationException(
                    $"The file-backed large TIFF decode exceeded 30 seconds: {timer.Elapsed}.");
            Console.WriteLine(
                $"File-backed 512 x 512 x 32 Gray16 TIFF decoded in {timer.Elapsed.TotalSeconds:F2} seconds.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void RunGray32FloatRoundTripTest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dslt-gray32f-{Guid.NewGuid():N}.tif");
        try
        {
            var values = new[] { -2f, -0.5f, 0.25f, 1f };
            var bytes = new byte[values.Length * sizeof(float)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            WriteGray32FloatTiff(path, 2, 2, values);

            var volume = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
            volume.Validate();
            if (volume.Source?.VoxelType != VolumeVoxelType.Float32)
                throw new InvalidOperationException($"Gray32Float source voxel type was not preserved: {volume.Source?.VoxelType}.");
            if (!bytes.SequenceEqual(volume.Source.ChannelPlanarRawSamples))
                throw new InvalidOperationException("Gray32Float decoded bytes were not bit-exact.");
            if (volume.Samples[0] != -1f || volume.Samples[^1] != 0.5f)
                throw new InvalidOperationException("Gray32Float processing samples were not maximum-absolute normalized.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void WriteGray32FloatTiff(string path, int width, int height, float[] values)
    {
        const ushort entryCount = 14;
        const uint ifdOffset = 8;
        const uint xResolutionOffset = ifdOffset + 2 + entryCount * 12 + 4;
        const uint yResolutionOffset = xResolutionOffset + 8;
        const uint pixelOffset = yResolutionOffset + 8;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'I');
        writer.Write((byte)'I');
        writer.Write((ushort)42);
        writer.Write(ifdOffset);
        writer.Write(entryCount);
        WriteLongEntry(writer, 256, checked((uint)width));
        WriteLongEntry(writer, 257, checked((uint)height));
        WriteShortEntry(writer, 258, 32);
        WriteShortEntry(writer, 259, 1);
        WriteShortEntry(writer, 262, 1);
        WriteLongEntry(writer, 273, pixelOffset);
        WriteShortEntry(writer, 277, 1);
        WriteLongEntry(writer, 278, checked((uint)height));
        WriteLongEntry(writer, 279, checked((uint)(values.Length * sizeof(float))));
        WriteOffsetEntry(writer, 282, 5, xResolutionOffset);
        WriteOffsetEntry(writer, 283, 5, yResolutionOffset);
        WriteShortEntry(writer, 284, 1);
        WriteShortEntry(writer, 296, 1);
        WriteShortEntry(writer, 339, 3);
        writer.Write(0u);
        writer.Write(1u);
        writer.Write(1u);
        writer.Write(1u);
        writer.Write(1u);
        foreach (var value in values) writer.Write(value);
    }

    private static void RunUnsignedInt32RoundTripTest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dslt-gray32u-{Guid.NewGuid():N}.tif");
        try
        {
            var values = new uint[]
            {
                0, 1, 0x80000000, uint.MaxValue,
                uint.MaxValue, 0x80000000, 1, 0,
            };
            WriteInt32Tiff(path, values, signed: false, littleEndian: true);
            var volume = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
            volume.Validate();
            if (volume.Depth != 2) throw new InvalidOperationException("Unsigned 32-bit multi-page depth was not preserved.");
            if (volume.Source?.VoxelType != VolumeVoxelType.UnsignedInt32)
                throw new InvalidOperationException("Unsigned 32-bit TIFF voxel type was not preserved.");
            var expectedBytes = new byte[values.Length * sizeof(uint)];
            Buffer.BlockCopy(values, 0, expectedBytes, 0, expectedBytes.Length);
            if (!expectedBytes.SequenceEqual(volume.Source.ChannelPlanarRawSamples))
                throw new InvalidOperationException("Unsigned 32-bit TIFF decoded bytes were not bit-exact.");
            if (volume.Samples[0] != 0 || Math.Abs(volume.Samples[2] - 0.5F) > 1e-6F || volume.Samples[3] != 1)
                throw new InvalidOperationException("Unsigned 32-bit TIFF processing samples were not normalized.");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                _ = WpfWorkspaceFileService.ReadStack(path, cancellation.Token);
                throw new InvalidOperationException("Cancelled 32-bit TIFF load was not interrupted.");
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void RunSignedInt32BigEndianRoundTripTest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dslt-gray32s-be-{Guid.NewGuid():N}.tif");
        try
        {
            var values = new[] { int.MinValue, -1, 0, int.MaxValue };
            WriteInt32Tiff(path, values.Select(value => unchecked((uint)value)).ToArray(), signed: true, littleEndian: false);
            var volume = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
            volume.Validate();
            if (volume.Source?.VoxelType != VolumeVoxelType.SignedInt32)
                throw new InvalidOperationException("Signed 32-bit TIFF voxel type was not preserved.");
            var expectedBytes = new byte[values.Length * sizeof(int)];
            Buffer.BlockCopy(values, 0, expectedBytes, 0, expectedBytes.Length);
            if (!expectedBytes.SequenceEqual(volume.Source.ChannelPlanarRawSamples))
                throw new InvalidOperationException("Big-endian signed 32-bit TIFF was not canonicalized bit-exactly.");
            if (volume.Samples[0] != -1 || volume.Samples[2] != 0 || Math.Abs(volume.Samples[3] - 1) > 1e-6F)
                throw new InvalidOperationException("Signed 32-bit TIFF processing samples were not normalized.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void RunUnsignedInt32MinIsWhiteTest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dslt-gray32u-white-{Guid.NewGuid():N}.tif");
        try
        {
            WriteInt32Tiff(
                path, [0, uint.MaxValue, 1, uint.MaxValue - 1],
                signed: false, littleEndian: true, photometric: 0);
            var volume = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
            var decoded = new uint[4];
            Buffer.BlockCopy(volume.Source!.ChannelPlanarRawSamples, 0, decoded, 0, sizeof(uint) * decoded.Length);
            if (!decoded.SequenceEqual(new[] { uint.MaxValue, 0u, uint.MaxValue - 1, 1u }))
                throw new InvalidOperationException("Unsigned 32-bit MinIsWhite samples were not inverted.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void RunMalformedInt32StripTest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dslt-gray32-invalid-{Guid.NewGuid():N}.tif");
        try
        {
            WriteInt32Tiff(path, [1, 2, 3, 4], signed: false, littleEndian: true, corruptSecondStripCount: true);
            try
            {
                _ = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
                throw new InvalidOperationException("Malformed 32-bit TIFF strip was accepted.");
            }
            catch (InvalidDataException)
            {
                // Expected: malformed input must fail without producing a volume.
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void WriteInt32Tiff(
        string path,
        uint[] values,
        bool signed,
        bool littleEndian,
        bool corruptSecondStripCount = false,
        ushort photometric = 1)
    {
        if (values.Length == 0 || values.Length % 4 != 0)
            throw new ArgumentException("The fixture requires one or more 2x2 pages.", nameof(values));
        const ushort entryCount = 12;
        const uint ifdOffset = 8;
        const uint ifdByteCount = 2 + entryCount * 12 + 4;
        const uint pageByteCount = ifdByteCount + 4 * sizeof(uint) + 4 * sizeof(uint);
        const uint rowByteCount = 2 * sizeof(uint);
        using var stream = File.Create(path);
        stream.WriteByte(littleEndian ? (byte)'I' : (byte)'M');
        stream.WriteByte(littleEndian ? (byte)'I' : (byte)'M');
        WriteUInt16(stream, 42, littleEndian);
        WriteUInt32(stream, ifdOffset, littleEndian);
        var pageCount = values.Length / 4;
        for (var page = 0; page < pageCount; page++)
        {
            var currentIfdOffset = checked(ifdOffset + (uint)page * pageByteCount);
            var stripOffsetsOffset = checked(currentIfdOffset + ifdByteCount);
            var stripByteCountsOffset = checked(stripOffsetsOffset + 2 * sizeof(uint));
            var pixelOffset = checked(stripByteCountsOffset + 2 * sizeof(uint));
            var nextIfdOffset = page + 1 < pageCount ? checked(currentIfdOffset + pageByteCount) : 0;
            WriteUInt16(stream, entryCount, littleEndian);
            WriteLongEntry(stream, 256, 2, littleEndian);
            WriteLongEntry(stream, 257, 2, littleEndian);
            WriteShortEntry(stream, 258, 32, littleEndian);
            WriteShortEntry(stream, 259, 1, littleEndian);
            WriteShortEntry(stream, 262, photometric, littleEndian);
            WriteArrayOffsetEntry(stream, 273, 2, stripOffsetsOffset, littleEndian);
            WriteShortEntry(stream, 274, 1, littleEndian);
            WriteShortEntry(stream, 277, 1, littleEndian);
            WriteLongEntry(stream, 278, 1, littleEndian);
            WriteArrayOffsetEntry(stream, 279, 2, stripByteCountsOffset, littleEndian);
            WriteShortEntry(stream, 284, 1, littleEndian);
            WriteShortEntry(stream, 339, signed ? (ushort)2 : (ushort)1, littleEndian);
            WriteUInt32(stream, nextIfdOffset, littleEndian);
            WriteUInt32(stream, pixelOffset, littleEndian);
            WriteUInt32(stream, pixelOffset + rowByteCount, littleEndian);
            WriteUInt32(stream, rowByteCount, littleEndian);
            WriteUInt32(stream, corruptSecondStripCount && page == pageCount - 1 ? rowByteCount - 1 : rowByteCount, littleEndian);
            for (var sample = 0; sample < 4; sample++)
                WriteUInt32(stream, values[page * 4 + sample], littleEndian);
        }
    }

    private static void WriteLongEntry(Stream stream, ushort tag, uint value, bool littleEndian)
    {
        WriteUInt16(stream, tag, littleEndian);
        WriteUInt16(stream, 4, littleEndian);
        WriteUInt32(stream, 1, littleEndian);
        WriteUInt32(stream, value, littleEndian);
    }

    private static void WriteShortEntry(Stream stream, ushort tag, ushort value, bool littleEndian)
    {
        WriteUInt16(stream, tag, littleEndian);
        WriteUInt16(stream, 3, littleEndian);
        WriteUInt32(stream, 1, littleEndian);
        WriteUInt16(stream, value, littleEndian);
        WriteUInt16(stream, 0, littleEndian);
    }

    private static void WriteArrayOffsetEntry(
        Stream stream,
        ushort tag,
        uint count,
        uint offset,
        bool littleEndian,
        ushort type = 4)
    {
        WriteUInt16(stream, tag, littleEndian);
        WriteUInt16(stream, type, littleEndian);
        WriteUInt32(stream, count, littleEndian);
        WriteUInt32(stream, offset, littleEndian);
    }

    private static void WriteUInt16(Stream stream, ushort value, bool littleEndian)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        else BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value, bool littleEndian)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        else BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteLongEntry(BinaryWriter writer, ushort tag, uint value)
    {
        writer.Write(tag);
        writer.Write((ushort)4);
        writer.Write(1u);
        writer.Write(value);
    }

    private static void WriteShortEntry(BinaryWriter writer, ushort tag, ushort value)
    {
        writer.Write(tag);
        writer.Write((ushort)3);
        writer.Write(1u);
        writer.Write(value);
        writer.Write((ushort)0);
    }

    private static void WriteOffsetEntry(BinaryWriter writer, ushort tag, ushort type, uint offset)
    {
        writer.Write(tag);
        writer.Write(type);
        writer.Write(1u);
        writer.Write(offset);
    }

    private static void RunImageJHyperStackTest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dslt-imagej-{Guid.NewGuid():N}.tif");
        try
        {
            const string description = "ImageJ=1.54\nimages=4\nchannels=2\nslices=2\nframes=1\nhyperstack=true\nunit=um\npixel_width=0.25\npixel_height=0.5\nspacing=1.5\n";
            var pageValues = new byte[] { 10, 100, 20, 200 };
            var encoder = new TiffBitmapEncoder();
            for (var page = 0; page < pageValues.Length; page++)
            {
                var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Gray8, null, new[] { pageValues[page] }, 1);
                if (page == 0)
                {
                    var metadata = new BitmapMetadata("tiff");
                    metadata.SetQuery("/ifd/{ushort=270}", description);
                    encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
                }
                else
                {
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                }
            }
            using (var stream = File.Create(path)) encoder.Save(stream);

            var volume = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
            volume.Validate();
            if (volume.Channels != 2 || volume.Depth != 2)
                throw new InvalidOperationException("ImageJ C/Z dimensions were not reconstructed.");
            var expectedPlanar = new byte[] { 10, 20, 100, 200 };
            if (!expectedPlanar.SequenceEqual(volume.Source!.ChannelPlanarRawSamples))
                throw new InvalidOperationException("ImageJ XYCZ page order was not converted to channel-planar storage.");
            if (volume.Samples[0] != 0.5f || volume.Samples[1] != 1f ||
                volume.Samples[2] != 0.5f || volume.Samples[3] != 1f)
                throw new InvalidOperationException("ImageJ channels were not normalized independently.");
            if (volume.Calibration.SpacingX != 0.25 || volume.Calibration.SpacingY != 0.5 ||
                volume.Calibration.SpacingZ != 1.5 || volume.Calibration.UnitName != "um")
                throw new InvalidOperationException("ImageJ voxel calibration was not preserved.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

}
