using Dslt.App.Infrastructure;

namespace Dslt.App.Tests;

internal static class LegacyImageInteractionBehaviorTests
{
    public static void Run()
    {
        if (!LegacyImageInteractionBehavior.TryMapToPixel(
                50, 0, 200, 100, 100, 100, out var left, out var top) || left != 0 || top != 0)
            throw new InvalidOperationException("Uniform-image mapping did not remove horizontal letterboxing.");
        if (!LegacyImageInteractionBehavior.TryMapToPixel(
                149.999, 99.999, 200, 100, 100, 100, out var right, out var bottom) ||
            right != 99 || bottom != 99)
            throw new InvalidOperationException("Uniform-image mapping did not preserve the final source pixel.");
        if (LegacyImageInteractionBehavior.TryMapToPixel(
                49.999, 50, 200, 100, 100, 100, out _, out _))
            throw new InvalidOperationException("A click in the image letterbox was mapped to a label voxel.");
        if (LegacyImageInteractionBehavior.TryMapToPixel(
                double.NaN, 0, 100, 100, 100, 100, out _, out _))
            throw new InvalidOperationException("A non-finite pointer coordinate was accepted.");
    }
}
