using Dslt.Managed.Core.Models;

namespace Dslt.Managed.Core.Synthetic;

public static class SyntheticVolumes
{
    public static VolumeData Impulse(int width = 9, int height = 9, int depth = 9)
    {
        var samples = EmptySamples(width, height, depth, 1);
        samples[(depth / 2) * width * height + (height / 2) * width + width / 2] = 1;
        return new VolumeData(width, height, depth, 1, 0, Calibration.Unit, samples);
    }

    public static VolumeData Ramp(int width = 16, int height = 8, int depth = 6)
    {
        var samples = EmptySamples(width, height, depth, 1);
        for (var z = 0; z < depth; z++)
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            samples[z * width * height + y * width + x] = (x + y + z) / (float)(width + height + depth - 3);
        return new VolumeData(width, height, depth, 1, 0, Calibration.Unit, samples);
    }

    public static VolumeData Sphere(
        int width = 96,
        int height = 96,
        int depth = 48,
        float radius = 24,
        int channels = 1)
    {
        if (width <= 0 || height <= 0 || depth <= 0 || channels <= 0 || radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        var voxels = checked(width * height * depth);
        var samples = new float[checked(voxels * channels)];
        var cx = (width - 1) / 2f;
        var cy = (height - 1) / 2f;
        var cz = (depth - 1) / 2f;
        var radiusSquared = radius * radius;
        for (var channel = 0; channel < channels; channel++)
        {
            var offset = channel * voxels;
            var scale = 1f - channel * 0.2f;
            for (var z = 0; z < depth; z++)
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                var dz = (z - cz) * 2f;
                var distanceSquared = dx * dx + dy * dy + dz * dz;
                samples[offset + z * width * height + y * width + x] =
                    distanceSquared <= radiusSquared ? scale : 0f;
            }
        }
        return new VolumeData(
            width, height, depth, channels, 0,
            new Calibration(1, 1, 2, true), samples);
    }

    public static VolumeData TouchingObjects()
    {
        const int size = 9;
        var data = new float[size * size * size];
        void Set(int x, int y, int z) => data[z * size * size + y * size + x] = 1;
        Set(2, 2, 2);
        Set(3, 2, 2);
        Set(6, 6, 6);
        return new VolumeData(size, size, size, 1, 0, Calibration.Unit, data);
    }

    public static VolumeData Shell(int size = 33, float innerRadius = 8, float outerRadius = 12)
    {
        if (innerRadius <= 0 || outerRadius <= innerRadius)
            throw new ArgumentOutOfRangeException(nameof(innerRadius));
        var data = EmptySamples(size, size, size, 1);
        var center = (size - 1) / 2f;
        var innerSquared = innerRadius * innerRadius;
        var outerSquared = outerRadius * outerRadius;
        for (var z = 0; z < size; z++)
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var squared = MathF.Pow(x - center, 2) + MathF.Pow(y - center, 2) + MathF.Pow(z - center, 2);
            if (squared >= innerSquared && squared <= outerSquared)
                data[z * size * size + y * size + x] = 1;
        }
        return new VolumeData(size, size, size, 1, 0, Calibration.Unit, data);
    }

    public static VolumeData Noise(int width = 32, int height = 24, int depth = 12, int seed = 310)
    {
        var data = EmptySamples(width, height, depth, 1);
        var random = new Random(seed);
        for (var index = 0; index < data.Length; index++) data[index] = random.NextSingle();
        return new VolumeData(width, height, depth, 1, 0, Calibration.Unit, data);
    }

    public static VolumeData MultiChannelComposite(int width = 12, int height = 10, int depth = 8)
    {
        const int channels = 3;
        var voxels = checked(width * height * depth);
        var data = EmptySamples(width, height, depth, channels);
        for (var z = 0; z < depth; z++)
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = z * width * height + y * width + x;
            data[index] = x / (float)Math.Max(1, width - 1);
            data[voxels + index] = y / (float)Math.Max(1, height - 1);
            data[voxels * 2 + index] = z / (float)Math.Max(1, depth - 1);
        }
        return new VolumeData(width, height, depth, channels, 0, new Calibration(1, 1, 2.5, true), data);
    }

    private static float[] EmptySamples(int width, int height, int depth, int channels)
    {
        if (width <= 0 || height <= 0 || depth <= 0 || channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Fixture dimensions must be positive.");
        return new float[checked(width * height * depth * channels)];
    }
}
