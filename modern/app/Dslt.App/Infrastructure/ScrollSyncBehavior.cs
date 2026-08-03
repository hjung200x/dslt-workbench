using System.Windows;
using System.Windows.Controls;

namespace Dslt.App.Infrastructure;

public static class ScrollSyncBehavior
{
    private static readonly Dictionary<string, HashSet<ScrollViewer>> Groups =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ScrollViewer> SynchronizationSources =
        new(StringComparer.Ordinal);

    public static readonly DependencyProperty GroupProperty = DependencyProperty.RegisterAttached(
        "Group",
        typeof(string),
        typeof(ScrollSyncBehavior),
        new PropertyMetadata(null, OnGroupChanged));

    public static string? GetGroup(DependencyObject element) =>
        (string?)element.GetValue(GroupProperty);

    public static void SetGroup(DependencyObject element, string? value) =>
        element.SetValue(GroupProperty, value);

    private static void OnGroupChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ScrollViewer viewer) return;
        if (args.OldValue is string oldGroup) Remove(oldGroup, viewer);
        viewer.ScrollChanged -= OnScrollChanged;
        viewer.Loaded -= OnLoaded;
        viewer.Unloaded -= OnUnloaded;
        if (args.NewValue is not string newGroup || string.IsNullOrWhiteSpace(newGroup)) return;
        viewer.Loaded += OnLoaded;
        Add(newGroup, viewer);
    }

    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is ScrollViewer viewer && GetGroup(viewer) is { } group)
            Add(group, viewer);
    }

    private static void Add(string group, ScrollViewer viewer)
    {
        if (!Groups.TryGetValue(group, out var viewers))
        {
            viewers = [];
            Groups.Add(group, viewers);
        }
        viewers.Add(viewer);
        viewer.ScrollChanged -= OnScrollChanged;
        viewer.ScrollChanged += OnScrollChanged;
        viewer.Unloaded -= OnUnloaded;
        viewer.Unloaded += OnUnloaded;
    }

    private static void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is not ScrollViewer viewer) return;
        var group = GetGroup(viewer);
        if (group is not null) Remove(group, viewer);
        viewer.ScrollChanged -= OnScrollChanged;
        viewer.Unloaded -= OnUnloaded;
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        if (sender is not ScrollViewer source ||
            GetGroup(source) is not { } group ||
            !Groups.TryGetValue(group, out var viewers)) return;
        var ownsSynchronization = false;
        if (SynchronizationSources.TryGetValue(group, out var activeSource))
        {
            if (!ReferenceEquals(activeSource, source)) return;
        }
        else
        {
            SynchronizationSources.Add(group, source);
            ownsSynchronization = true;
        }

        try
        {
            var horizontal = source.ScrollableWidth <= 0 ? 0 : source.HorizontalOffset / source.ScrollableWidth;
            var vertical = source.ScrollableHeight <= 0 ? 0 : source.VerticalOffset / source.ScrollableHeight;
            foreach (var target in viewers)
            {
                if (ReferenceEquals(target, source)) continue;
                var horizontalOffset = horizontal * target.ScrollableWidth;
                var verticalOffset = vertical * target.ScrollableHeight;
                if (Math.Abs(target.HorizontalOffset - horizontalOffset) >= 0.5)
                    target.ScrollToHorizontalOffset(horizontalOffset);
                if (Math.Abs(target.VerticalOffset - verticalOffset) >= 0.5)
                    target.ScrollToVerticalOffset(verticalOffset);
            }
        }
        finally
        {
            if (ownsSynchronization) SynchronizationSources.Remove(group);
        }
    }

    private static void Remove(string group, ScrollViewer viewer)
    {
        if (!Groups.TryGetValue(group, out var viewers)) return;
        viewers.Remove(viewer);
        if (SynchronizationSources.TryGetValue(group, out var source) && ReferenceEquals(source, viewer))
            SynchronizationSources.Remove(group);
        if (viewers.Count == 0) Groups.Remove(group);
    }
}
