using Dslt.App.Services;
using Dslt.Managed.Core.Models;
using Dslt.Managed.Core.Services;

namespace Dslt.App.Tests;

internal static class DepthColorProjectionTests
{
    public static async Task RunAsync()
    {
        PreservesLegacyHueAndBrightness();
        SamplesDepthWithoutSurfaceOffset();
        UsesScalarThresholdResultForBrightness();
        RejectsUndefinedOrUnsupportedParameters();
        await RunsWithNativeCpuWhenAvailable();
    }

    private static void PreservesLegacyHueAndBrightness()
    {
        var volume = Volume([0.1F, 0.2F, 0.8F, 0.4F]);
        var pixels = DepthColorProjectionRenderer.CreateRgb24(
            volume,
            surface: [0.0F],
            depthMap: [0.0F, 1.0F, 2.0F, 3.0F],
            scalarProjection: [0.8F],
            Parameters(projectionRange: 3, depthColorRange: 4));
        AssertPixels([0, 204, 51], pixels, "Legacy HSV hue/brightness mapping");
    }

    private static void SamplesDepthWithoutSurfaceOffset()
    {
        var volume = Volume([0.0F, 0.0F, 0.0F, 1.0F]);
        var pixels = DepthColorProjectionRenderer.CreateRgb24(
            volume,
            surface: [0.0F],
            depthMap: [0.0F, 1.0F, 2.0F, 3.0F],
            scalarProjection: [1.0F],
            Parameters(projectionRange: 2, depthColorRange: 4, projectionOffset: 1.0F));
        AssertPixels([0, 255, 63], pixels, "Offset-independent legacy depth sampling");
    }

    private static void UsesScalarThresholdResultForBrightness()
    {
        var volume = Volume([0.0F, 0.0F, 1.0F, 0.0F]);
        var pixels = DepthColorProjectionRenderer.CreateRgb24(
            volume,
            surface: [0.0F],
            depthMap: [0.0F, 1.0F, 2.0F, 3.0F],
            scalarProjection: [0.0F],
            Parameters(projectionRange: 3, depthColorRange: 4));
        AssertPixels([0, 0, 0], pixels, "Thresholded scalar brightness");
    }

    private static void RejectsUndefinedOrUnsupportedParameters()
    {
        var volume = Volume([1.0F, 0.0F, 0.0F, 0.0F]);
        AssertRejected(Parameters(projectionRange: 3, depthColorRange: 0));
        AssertRejected(Parameters(projectionRange: 3, depthColorRange: 4) with
        {
            ProjectionMode = HeightProjectionMode.Normal,
        });

        void AssertRejected(OperationParameters parameters)
        {
            try
            {
                _ = DepthColorProjectionRenderer.CreateRgb24(
                    volume, [0.0F], [0.0F, 1.0F, 2.0F, 3.0F], [1.0F], parameters);
                throw new InvalidOperationException("Invalid depth-color parameters were accepted.");
            }
            catch (ArgumentException)
            {
                // Expected.
            }
        }
    }

    private static async Task RunsWithNativeCpuWhenAvailable()
    {
        using var engine = ProcessingEngineFactory.Create();
        if (!engine.IsAvailable)
        {
            Console.WriteLine("Native depth-color integration test skipped: native core unavailable.");
            return;
        }

        var volume = Volume([0.6F, 0.2F, 1.0F, 0.4F]);
        var parameters = Parameters(projectionRange: 3, depthColorRange: 4) with
        {
            HeightMapXyRadius = 0,
            HeightMapZRadius = 0,
            HeightMapSmoothLevel = 0,
            Threshold = 0.5F,
        };
        var projection = await engine.RunAsync(
            volume, parameters, null, CancellationToken.None);
        var surface = await engine.RunAsync(
            volume,
            parameters with
            {
                Operation = ProcessingOperation.HeightMap,
                DepthColorEnabled = false,
            },
            null,
            CancellationToken.None);
        var depth = await engine.RunAsync(
            volume,
            parameters with
            {
                Operation = ProcessingOperation.DepthMap,
                DepthColorEnabled = false,
            },
            null,
            CancellationToken.None);
        var pixels = DepthColorProjectionRenderer.CreateRgb24(
            volume,
            surface.FloatData ?? throw new InvalidOperationException("Native height map is missing."),
            depth.FloatData ?? throw new InvalidOperationException("Native depth map is missing."),
            projection.FloatData ?? throw new InvalidOperationException("Native projection is missing."),
            parameters);
        AssertPixels([0, 255, 63], pixels, "Native CPU depth-color integration");
    }

    private static VolumeData Volume(float[] samples) => new(
        Width: 1,
        Height: 1,
        Depth: 4,
        Channels: 1,
        SelectedChannel: 0,
        Calibration: Calibration.Unit,
        Samples: samples);

    private static OperationParameters Parameters(
        int projectionRange,
        int depthColorRange,
        float projectionOffset = 0) => new(
            ProcessingOperation.HeightProjection,
            ProcessingBackend.Cpu,
            ProjectionMode: HeightProjectionMode.Z,
            ProjectionOffset: projectionOffset,
            ProjectionRange: projectionRange,
            DepthColorEnabled: true,
            DepthColorRange: depthColorRange);

    private static void AssertPixels(byte[] expected, byte[] actual, string message)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException(
                $"{message}: expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}].");
    }
}
