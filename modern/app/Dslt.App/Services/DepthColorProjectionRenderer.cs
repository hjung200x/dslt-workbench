using Dslt.Managed.Core.Models;

namespace Dslt.App.Services;

internal static class DepthColorProjectionRenderer
{
    public static byte[] CreateRgb24(
        VolumeData volume,
        ReadOnlySpan<float> surface,
        ReadOnlySpan<float> depthMap,
        ReadOnlySpan<float> scalarProjection,
        OperationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(parameters);
        volume.Validate();
        if (parameters.Operation != ProcessingOperation.HeightProjection ||
            !parameters.DepthColorEnabled ||
            parameters.ProjectionMode != HeightProjectionMode.Z)
            throw new ArgumentException("Depth coloring requires an enabled Z height projection.", nameof(parameters));
        if (parameters.DepthColorRange is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(parameters), "Depth color range must be between 1 and 500.");

        var plane = checked(volume.Width * volume.Height);
        if (surface.Length != plane || scalarProjection.Length != plane)
            throw new ArgumentException("Height surface and scalar projection dimensions must match the XY plane.");
        if (depthMap.Length != volume.VoxelCount)
            throw new ArgumentException("Depth map dimensions must match the selected-channel volume.");

        var source = volume.Samples.AsSpan(
            checked(volume.SelectedChannel * volume.VoxelCount), volume.VoxelCount);
        var pixels = new byte[checked(plane * 3)];
        for (var index = 0; index < plane; index++)
        {
            var maximum = -1.0F;
            var projectionDepth = float.MaxValue;
            for (var step = 0; step <= parameters.ProjectionRange; step++)
            {
                var distance = parameters.ProjectionStartDepth + step;
                var sourceZ = surface[index] + distance + parameters.ProjectionOffset;
                if (sourceZ < 0) continue;
                if (sourceZ > volume.Depth - 1)
                {
                    if (maximum < 0) maximum = 0;
                    break;
                }

                var value = SampleZ(source, index, plane, volume.Depth, sourceZ);
                if (value <= maximum) continue;
                maximum = value;
                var depthZ = surface[index] + distance;
                projectionDepth = depthZ switch
                {
                    < 0 => 0,
                    _ when depthZ > volume.Depth - 1 => float.MaxValue,
                    _ => SampleZ(depthMap, index, plane, volume.Depth, depthZ),
                };
            }

            var brightness = Math.Clamp(scalarProjection[index], 0, 1);
            var hue = Math.Min(projectionDepth / parameters.DepthColorRange * 270.0F, 270.0F);
            var (red, green, blue) = HsvToRgb(hue, brightness);
            var destination = index * 3;
            pixels[destination] = red;
            pixels[destination + 1] = green;
            pixels[destination + 2] = blue;
        }
        return pixels;
    }

    private static float SampleZ(
        ReadOnlySpan<float> values,
        int xyIndex,
        int plane,
        int depth,
        float z)
    {
        var lower = Math.Clamp((int)MathF.Floor(z), 0, depth - 1);
        var upper = Math.Min(lower + 1, depth - 1);
        var fraction = z - lower;
        var lowerValue = values[checked(lower * plane + xyIndex)];
        var upperValue = values[checked(upper * plane + xyIndex)];
        return lowerValue + (upperValue - lowerValue) * fraction;
    }

    private static (byte Red, byte Green, byte Blue) HsvToRgb(float hue, float value)
    {
        var sector = Math.Clamp((int)(hue / 60.0F), 0, 5);
        var fraction = hue / 60.0F - sector;
        var p = 0.0F;
        var q = value * (1.0F - fraction);
        var t = value * fraction;
        var (red, green, blue) = sector switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q),
        };
        return (ToByte(red), ToByte(green), ToByte(blue));
    }

    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)(Math.Clamp(value, 0, 1) * 255.0F), 0, 255);
}
