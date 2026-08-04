using Dslt.Managed.Core.Analysis;
using Dslt.Managed.Core.IO;

internal static class HeightSurfaceAreaCalculatorTests
{
    public static void Run()
    {
        var flat = HeightSurfaceAreaCalculator.Calculate(
            new LegacyHeightMap(5, 4, new float[20]));
        Assert(flat.ScaleFactors.Count(value => value == 1F) == 6 &&
               flat.ScaleFactors.Count(value => value == 0F) == 14 &&
               flat.PreviewGray8.All(value => value == 0) &&
               flat.MaximumScaleFactor == 1.0 &&
               flat.IntegrationResolution == 10,
            "A flat surface did not produce the exact legacy unit-area interior and zero border.");

        var rampValues = new float[9 * 9];
        for (var y = 0; y < 9; y++)
            for (var x = 0; x < 9; x++)
                rampValues[y * 9 + x] = x;
        var ramp = HeightSurfaceAreaCalculator.Calculate(new LegacyHeightMap(9, 9, rampValues));
        // The fourth legacy quadrant contributes +dx while the other three contribute -dx.
        // Away from edges this yields nx/nz=-0.5 and therefore sqrt(1.25), not sqrt(2).
        var expectedLegacyInterior = MathF.Sqrt(1.25F);
        Assert(MathF.Abs(ramp.ScaleFactors[3 * 9 + 3] - expectedLegacyInterior) <= 1e-6F &&
               ramp.MaximumScaleFactor >= expectedLegacyInterior &&
               ramp.PreviewGray8.Contains(byte.MaxValue),
            "A z=x plane did not preserve the source-derived quadrant-normal accumulation.");

        var curved = HeightSurfaceAreaCalculator.Calculate(new LegacyHeightMap(5, 5,
        [
            0F, 1.25F, 4F, 9.25F, 16F,
            0.75F, 1.5F, 4.75F, 9.5F, 16.75F,
            2F, 3.25F, 6F, 11.25F, 18F,
            4.75F, 5.5F, 8.75F, 13.5F, 20.75F,
            8F, 9.25F, 12F, 17.25F, 24F,
        ]));
        var expectedCurvedInterior = new[]
        {
            1.73357224F, 2.33297610F, 3.54642153F,
            2.19283557F, 2.57227063F, 3.54767489F,
            2.99038243F, 3.14137435F, 3.80311966F,
        };
        var actualCurvedInterior = Enumerable.Range(1, 3)
            .SelectMany(y => Enumerable.Range(1, 3).Select(x => curved.ScaleFactors[y * 5 + x]))
            .ToArray();
        Assert(actualCurvedInterior.Zip(expectedCurvedInterior)
                   .All(pair => MathF.Abs(pair.First - pair.Second) <= 1e-5F),
            "The non-planar source oracle detected a bilinear or Simpson-integration regression.");

        Expect<ArgumentOutOfRangeException>(() => HeightSurfaceAreaCalculator.Calculate(
            new LegacyHeightMap(2, 3, new float[6])));
        Expect<ArgumentOutOfRangeException>(() => HeightSurfaceAreaCalculator.Calculate(
            new LegacyHeightMap(3, 3, new float[9]), integrationResolution: 9));
        Expect<ArgumentException>(() => HeightSurfaceAreaCalculator.Calculate(
            new LegacyHeightMap(3, 3, [0, 0, 0, 0, float.NaN, 0, 0, 0, 0])));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Expect<OperationCanceledException>(() => HeightSurfaceAreaCalculator.Calculate(
            new LegacyHeightMap(3, 3, new float[9]), cancellationToken: cancelled.Token));
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
