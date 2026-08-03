namespace Dslt.Managed.Core.Segmentation;

public sealed record CroppedLabels(
    int OriginX,
    int OriginY,
    int OriginZ,
    int Width,
    int Height,
    int Depth,
    int[] Labels);

public sealed class LabelEditingSession
{
    public const int Background = -1;

    private readonly Stack<Snapshot> _undo = new();
    private readonly HashSet<int> _selection = [];
    private int[] _labels;

    public LabelEditingSession(int width, int height, int depth, ReadOnlySpan<int> labels)
    {
        if (width <= 0 || height <= 0 || depth <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Label dimensions must be positive.");
        var expected = checked(width * height * depth);
        if (labels.Length != expected)
            throw new ArgumentException($"Expected {expected} labels but received {labels.Length}.", nameof(labels));
        Width = width;
        Height = height;
        Depth = depth;
        _labels = labels.ToArray();
    }

    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }
    public ReadOnlyMemory<int> Labels => _labels;
    public IReadOnlySet<int> Selection => _selection;
    public bool CanUndo => _undo.Count > 0;

    public void Select(IEnumerable<int> labels, bool replace = true)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (replace) _selection.Clear();
        foreach (var label in labels)
        {
            if (label >= 0 && _labels.Contains(label)) _selection.Add(label);
        }
    }

    public void ClearSelection() => _selection.Clear();

    public int MergeSelected(int? destinationLabel = null)
    {
        RequireSelection();
        var destination = destinationLabel ?? _selection.Min();
        if (!_selection.Contains(destination))
            throw new ArgumentException("The destination label must be selected.", nameof(destinationLabel));
        SaveUndo();
        for (var index = 0; index < _labels.Length; index++)
        {
            if (_selection.Contains(_labels[index])) _labels[index] = destination;
        }
        _selection.Clear();
        _selection.Add(destination);
        return destination;
    }

    public int SplitSelected(int connectivity = 6)
    {
        RequireConnectivity(connectivity);
        RequireSelection();
        SaveUndo();
        var nextLabel = _labels.Where(value => value >= 0).DefaultIfEmpty(-1).Max() + 1;
        var created = 0;
        var newSelection = new HashSet<int>();
        foreach (var originalLabel in _selection.Order())
        {
            var visited = new bool[_labels.Length];
            var componentIndex = 0;
            for (var seed = 0; seed < _labels.Length; seed++)
            {
                if (visited[seed] || _labels[seed] != originalLabel) continue;
                var assignedLabel = componentIndex == 0 ? originalLabel : nextLabel++;
                if (componentIndex > 0) created++;
                componentIndex++;
                newSelection.Add(assignedLabel);
                var queue = new Queue<int>();
                queue.Enqueue(seed);
                visited[seed] = true;
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    _labels[current] = assignedLabel;
                    foreach (var neighbor in Neighbors(current, connectivity))
                    {
                        if (!visited[neighbor] && _labels[neighbor] == originalLabel)
                        {
                            visited[neighbor] = true;
                            queue.Enqueue(neighbor);
                        }
                    }
                }
            }
        }
        _selection.Clear();
        _selection.UnionWith(newSelection);
        return created;
    }

    public void DilateSelected(int iterations = 1, int connectivity = 6)
    {
        RequireMorphologyArguments(iterations, connectivity);
        RequireSelection();
        SaveUndo();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var source = (int[])_labels.Clone();
            for (var index = 0; index < source.Length; index++)
            {
                if (source[index] != Background) continue;
                var candidate = Neighbors(index, connectivity)
                    .Select(neighbor => source[neighbor])
                    .Where(_selection.Contains)
                    .DefaultIfEmpty(Background)
                    .Min();
                if (candidate != Background) _labels[index] = candidate;
            }
        }
    }

    public void ErodeSelected(int iterations = 1, int connectivity = 6)
    {
        RequireMorphologyArguments(iterations, connectivity);
        RequireSelection();
        SaveUndo();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var source = (int[])_labels.Clone();
            for (var index = 0; index < source.Length; index++)
            {
                var label = source[index];
                if (!_selection.Contains(label)) continue;
                if (IsBoundary(index) || Neighbors(index, connectivity).Any(neighbor => source[neighbor] != label))
                    _labels[index] = Background;
            }
        }
    }

    public CroppedLabels CropToSelection()
    {
        RequireSelection();
        var selectedIndices = Enumerable.Range(0, _labels.Length)
            .Where(index => _selection.Contains(_labels[index]))
            .ToArray();
        if (selectedIndices.Length == 0) throw new InvalidOperationException("The selection contains no voxels.");

        var coordinates = selectedIndices.Select(Coordinates).ToArray();
        var minimumX = coordinates.Min(value => value.X);
        var minimumY = coordinates.Min(value => value.Y);
        var minimumZ = coordinates.Min(value => value.Z);
        var maximumX = coordinates.Max(value => value.X);
        var maximumY = coordinates.Max(value => value.Y);
        var maximumZ = coordinates.Max(value => value.Z);
        var width = maximumX - minimumX + 1;
        var height = maximumY - minimumY + 1;
        var depth = maximumZ - minimumZ + 1;
        var cropped = Enumerable.Repeat(Background, checked(width * height * depth)).ToArray();
        foreach (var index in selectedIndices)
        {
            var (x, y, z) = Coordinates(index);
            var destination = (z - minimumZ) * width * height + (y - minimumY) * width + x - minimumX;
            cropped[destination] = _labels[index];
        }
        return new CroppedLabels(minimumX, minimumY, minimumZ, width, height, depth, cropped);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var snapshot = _undo.Pop();
        _labels = snapshot.Labels;
        _selection.Clear();
        _selection.UnionWith(snapshot.Selection);
        return true;
    }

    private void SaveUndo()
    {
        _undo.Push(new Snapshot((int[])_labels.Clone(), [.. _selection]));
        const int maximumUndoDepth = 32;
        if (_undo.Count <= maximumUndoDepth) return;
        var retained = _undo.Take(maximumUndoDepth).Reverse().ToArray();
        _undo.Clear();
        foreach (var snapshot in retained) _undo.Push(snapshot);
    }

    private IEnumerable<int> Neighbors(int index, int connectivity)
    {
        var (x, y, z) = Coordinates(index);
        for (var deltaZ = -1; deltaZ <= 1; deltaZ++)
        for (var deltaY = -1; deltaY <= 1; deltaY++)
        for (var deltaX = -1; deltaX <= 1; deltaX++)
        {
            if (deltaX == 0 && deltaY == 0 && deltaZ == 0) continue;
            var distance = Math.Abs(deltaX) + Math.Abs(deltaY) + Math.Abs(deltaZ);
            if (connectivity == 6 && distance != 1) continue;
            if (connectivity == 18 && distance == 3) continue;
            var neighborX = x + deltaX;
            var neighborY = y + deltaY;
            var neighborZ = z + deltaZ;
            if (neighborX < 0 || neighborY < 0 || neighborZ < 0 ||
                neighborX >= Width || neighborY >= Height || neighborZ >= Depth) continue;
            yield return neighborZ * Width * Height + neighborY * Width + neighborX;
        }
    }

    private (int X, int Y, int Z) Coordinates(int index)
    {
        var z = index / (Width * Height);
        var remainder = index % (Width * Height);
        return (remainder % Width, remainder / Width, z);
    }

    private bool IsBoundary(int index)
    {
        var (x, y, z) = Coordinates(index);
        return x == 0 || y == 0 || z == 0 || x == Width - 1 || y == Height - 1 || z == Depth - 1;
    }

    private void RequireSelection()
    {
        if (_selection.Count == 0) throw new InvalidOperationException("At least one label must be selected.");
    }

    private static void RequireConnectivity(int connectivity)
    {
        if (connectivity is not (6 or 18 or 26))
            throw new ArgumentOutOfRangeException(nameof(connectivity), "Connectivity must be 6, 18, or 26.");
    }

    private static void RequireMorphologyArguments(int iterations, int connectivity)
    {
        if (iterations <= 0 || iterations > 64)
            throw new ArgumentOutOfRangeException(nameof(iterations), "Iterations must be between 1 and 64.");
        RequireConnectivity(connectivity);
    }

    private sealed record Snapshot(int[] Labels, int[] Selection);
}
