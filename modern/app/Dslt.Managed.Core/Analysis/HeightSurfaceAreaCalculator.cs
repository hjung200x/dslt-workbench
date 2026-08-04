using Dslt.Managed.Core.IO;

namespace Dslt.Managed.Core.Analysis;

public sealed record HeightSurfaceAreaMap(
    int Width,
    int Height,
    float[] ScaleFactors,
    byte[] PreviewGray8,
    double MaximumScaleFactor,
    int IntegrationResolution)
{
    public void Validate()
    {
        if (Width < 3 || Height < 3)
            throw new ArgumentOutOfRangeException(nameof(Width), "Height-surface area maps require dimensions of at least 3 x 3.");
        var expected = checked(Width * Height);
        if (ScaleFactors is null || ScaleFactors.Length != expected)
            throw new ArgumentException($"Expected {expected} surface-area scale factors.", nameof(ScaleFactors));
        if (PreviewGray8 is null || PreviewGray8.Length != expected)
            throw new ArgumentException($"Expected {expected} preview pixels.", nameof(PreviewGray8));
        if (ScaleFactors.Any(value => !float.IsFinite(value) || value < 0))
            throw new ArgumentException("Surface-area scale factors must be finite and non-negative.", nameof(ScaleFactors));
        if (!double.IsFinite(MaximumScaleFactor) || MaximumScaleFactor < 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumScaleFactor));
        if (IntegrationResolution <= 0 || (IntegrationResolution & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(IntegrationResolution), "Simpson integration resolution must be positive and even.");
    }
}

/// <summary>
/// Reproduces the legacy A-command surface-area scale calculation. Heights are
/// intentionally interpreted in voxel-index units, as in the original code.
/// </summary>
public static class HeightSurfaceAreaCalculator
{
    public const int LegacyIntegrationResolution = 10;

    public static HeightSurfaceAreaMap Calculate(
        LegacyHeightMap heightMap,
        int integrationResolution = LegacyIntegrationResolution,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(heightMap);
        heightMap.Validate();
        if (heightMap.Width < 3 || heightMap.Height < 3)
            throw new ArgumentOutOfRangeException(nameof(heightMap), "Height-surface area calculation requires dimensions of at least 3 x 3.");
        if (integrationResolution <= 0 || (integrationResolution & 1) != 0)
            throw new ArgumentOutOfRangeException(
                nameof(integrationResolution), "Simpson integration resolution must be positive and even.");

        cancellationToken.ThrowIfCancellationRequested();
        var width = heightMap.Width;
        var height = heightMap.Height;
        var normals = CalculateVertexNormals(heightMap, cancellationToken);
        var cellWidth = width - 1;
        var cellNormals = new Vector3[cellWidth * (height - 1)];
        for (var y = 0; y < height - 1; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width - 1; x++)
            {
                var sum = normals[y * width + x] +
                          normals[y * width + x + 1] +
                          normals[(y + 1) * width + x] +
                          normals[(y + 1) * width + x + 1];
                cellNormals[y * cellWidth + x] = sum / 4F;
            }
        }

        var samples = new double[integrationResolution + 1, integrationResolution + 1];
        var scaleFactors = new float[checked(width * height)];
        var maximum = 1.0;
        var outputRows = height - 2;
        for (var y = 0; y < outputRows; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width - 2; x++)
            {
                var p00 = cellNormals[y * cellWidth + x];
                var p10 = cellNormals[y * cellWidth + x + 1];
                var p01 = cellNormals[(y + 1) * cellWidth + x];
                var p11 = cellNormals[(y + 1) * cellWidth + x + 1];
                for (var sampleX = 0; sampleX <= integrationResolution; sampleX++)
                {
                    var sx = sampleX / (double)integrationResolution;
                    for (var sampleY = 0; sampleY <= integrationResolution; sampleY++)
                    {
                        var sy = sampleY / (double)integrationResolution;
                        samples[sampleX, sampleY] = SurfaceScale(sx, sy, p00, p10, p01, p11);
                    }
                }

                var area = Simpson2D(samples, integrationResolution);
                scaleFactors[(y + 1) * width + x + 1] = (float)area;
                maximum = Math.Max(maximum, area);
            }
            progress?.Report((y + 1) / (double)outputRows);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var preview = CreatePreview(scaleFactors, width, height, maximum);
        var result = new HeightSurfaceAreaMap(
            width, height, scaleFactors, preview, maximum, integrationResolution);
        result.Validate();
        return result;
    }

    private static Vector3[] CalculateVertexNormals(
        LegacyHeightMap heightMap,
        CancellationToken cancellationToken)
    {
        var width = heightMap.Width;
        var height = heightMap.Height;
        var values = heightMap.Values;
        var normals = new Vector3[checked(width * height)];

        for (var y = 0; y < height - 1; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width - 1; x++)
            {
                var center = values[y * width + x];
                normals[y * width + x] += new Vector3(
                    -(values[y * width + x + 1] - center),
                    -(values[(y + 1) * width + x] - center),
                    1);
            }
        }
        for (var y = 0; y < height - 1; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 1; x < width; x++)
            {
                var center = values[y * width + x];
                normals[y * width + x] += new Vector3(
                    values[y * width + x - 1] - center,
                    -(values[(y + 1) * width + x] - center),
                    1);
            }
        }
        for (var y = 1; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 1; x < width; x++)
            {
                var center = values[y * width + x];
                normals[y * width + x] += new Vector3(
                    values[y * width + x - 1] - center,
                    values[(y - 1) * width + x] - center,
                    1);
            }
        }
        for (var y = 1; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width - 1; x++)
            {
                var center = values[y * width + x];
                normals[y * width + x] += new Vector3(
                    values[y * width + x + 1] - center,
                    values[(y - 1) * width + x] - center,
                    1);
            }
        }

        for (var index = 0; index < normals.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normal = normals[index];
            var length = (float)Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
            normals[index] = normal / length;
        }
        return normals;
    }

    private static double SurfaceScale(
        double x,
        double y,
        Vector3 p00,
        Vector3 p10,
        Vector3 p01,
        Vector3 p11)
    {
        var nx = Bilinear(x, y, p00.X, p10.X, p01.X, p11.X);
        var ny = Bilinear(x, y, p00.Y, p10.Y, p01.Y, p11.Y);
        var nz = Bilinear(x, y, p00.Z, p10.Z, p01.Z, p11.Z);
        var dx = nx / nz;
        var dy = ny / nz;
        return Math.Sqrt(1 + dx * dx + dy * dy);
    }

    private static double Bilinear(double x, double y, float p00, float p10, float p01, float p11) =>
        p00 * (1 - x) * (1 - y) + p10 * x * (1 - y) + p01 * (1 - x) * y + p11 * x * y;

    private static double Simpson2D(double[,] samples, int resolution)
    {
        var temporary = new double[resolution + 1];
        for (var x = 0; x <= resolution; x++)
        {
            var sum = -samples[x, 0] + samples[x, resolution];
            for (var y = 0; y < resolution - 1; y += 2)
                sum += 2 * samples[x, y] + 4 * samples[x, y + 1];
            temporary[x] = sum;
        }

        var result = -temporary[0] + temporary[resolution];
        for (var x = 0; x < resolution - 1; x += 2)
            result += 2 * temporary[x] + 4 * temporary[x + 1];
        var spacing = 1.0 / resolution;
        return result * spacing * spacing / 9.0;
    }

    private static byte[] CreatePreview(float[] areas, int width, int height, double maximum)
    {
        var preview = new byte[areas.Length];
        if (maximum <= 1) return preview;
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var normalized = (areas[y * width + x] - 1.0) / (maximum - 1.0);
                preview[y * width + x] = (byte)Math.Clamp(Math.Round(normalized * 255.0), 0, 255);
            }
        }
        return preview;
    }

    private readonly record struct Vector3(float X, float Y, float Z)
    {
        public static Vector3 operator +(Vector3 left, Vector3 right) =>
            new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

        public static Vector3 operator /(Vector3 value, float divisor) =>
            new(value.X / divisor, value.Y / divisor, value.Z / divisor);
    }
}
