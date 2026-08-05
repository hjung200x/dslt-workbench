using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Dslt.App.Services;

internal static class CziImportSelectionDialog
{
    internal static CziImportSelection? Show(CziDocumentDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Scenes.Count == 0)
            throw new InvalidDataException("CZI contains no selectable scenes.");
        var supportedChannels = descriptor.Channels.Where(channel => channel.IsSupported).ToArray();
        if (supportedChannels.Length == 0)
            throw new NotSupportedException("CZI contains no Gray8, Gray16, or Gray32Float channels.");
        if (descriptor.HasMultipleTiles)
            throw new NotSupportedException("Mosaic or multi-tile CZI input is not supported in phase one.");
        if (descriptor.UnsupportedDimensionsMask != 0)
            throw new NotSupportedException(
                $"CZI contains a non-unit acquisition dimension (mask 0x{descriptor.UnsupportedDimensionsMask:x8}).");

        var window = new Window
        {
            Title = "Select CZI volume",
            Width = 560,
            Height = 520,
            MinWidth = 480,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = false,
        };
        if (Application.Current?.MainWindow is { IsLoaded: true } owner) window.Owner = owner;

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var summary = new TextBlock
        {
            Text = $"Z={descriptor.ZStart}..{descriptor.ZStart + descriptor.ZCount - 1}; " +
                   $"spacing {FormatSpacing(descriptor)}; pyramid layer 0",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        };
        Grid.SetRow(summary, 0);
        root.Children.Add(summary);

        var scenePanel = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var sceneLabel = new TextBlock { Text = "Scene", Width = 90, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(sceneLabel, Dock.Left);
        scenePanel.Children.Add(sceneLabel);
        var sceneBox = new ComboBox
        {
            ItemsSource = descriptor.Scenes,
            DisplayMemberPath = nameof(CziSceneDescriptor.DisplayName),
            SelectedIndex = 0,
            MinWidth = 280,
        };
        AutomationProperties.SetName(sceneBox, "CZI scene");
        scenePanel.Children.Add(sceneBox);
        Grid.SetRow(scenePanel, 1);
        root.Children.Add(scenePanel);

        var timePanel = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var timeLabel = new TextBlock { Text = "Time point", Width = 90, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(timeLabel, Dock.Left);
        timePanel.Children.Add(timeLabel);
        var times = Enumerable.Range(descriptor.TimeStart, descriptor.TimeCount).ToArray();
        var timeBox = new ComboBox { ItemsSource = times, SelectedIndex = 0, MinWidth = 120 };
        AutomationProperties.SetName(timeBox, "CZI time point");
        timePanel.Children.Add(timeBox);
        Grid.SetRow(timePanel, 2);
        root.Children.Add(timePanel);

        var channelPanel = new DockPanel();
        var channelLabel = new TextBlock
        {
            Text = "Channels (select one or more channels with the same sample type)",
            Margin = new Thickness(0, 0, 0, 6),
        };
        DockPanel.SetDock(channelLabel, Dock.Top);
        channelPanel.Children.Add(channelLabel);
        var channelList = new ListBox
        {
            ItemsSource = descriptor.Channels,
            DisplayMemberPath = nameof(CziChannelDescriptor.DisplayName),
            SelectionMode = SelectionMode.Multiple,
            MinHeight = 160,
        };
        AutomationProperties.SetName(channelList, "CZI channels");
        channelPanel.Children.Add(channelList);
        Grid.SetRow(channelPanel, 3);
        root.Children.Add(channelPanel);

        var defaultType = supportedChannels[0].VoxelType;
        foreach (var channel in supportedChannels.Where(channel => channel.VoxelType == defaultType))
            channelList.SelectedItems.Add(channel);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 92,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var open = new Button { Content = "Open volume", IsDefault = true, MinWidth = 112 };
        buttons.Children.Add(cancel);
        buttons.Children.Add(open);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);
        window.Content = root;

        CziImportSelection? result = null;
        open.Click += (_, _) =>
        {
            var channels = channelList.SelectedItems.Cast<CziChannelDescriptor>().ToArray();
            if (sceneBox.SelectedItem is not CziSceneDescriptor scene || timeBox.SelectedItem is not int time)
            {
                MessageBox.Show(window, "Select a scene and time point.", "CZI selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (channels.Length == 0)
            {
                MessageBox.Show(window, "Select at least one supported channel.", "CZI selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (channels.Any(channel => !channel.IsSupported))
            {
                MessageBox.Show(window, "The selection contains an unsupported channel.", "CZI selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (channels.Select(channel => channel.VoxelType).Distinct().Count() != 1)
            {
                MessageBox.Show(window, "Selected channels must use the same sample type.", "CZI selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            result = new CziImportSelection(scene, time, channels);
            window.DialogResult = true;
        };

        return window.ShowDialog() == true ? result : null;
    }

    private static string FormatSpacing(CziDocumentDescriptor descriptor)
    {
        const uint allSpacing = 0b111;
        return (descriptor.SpacingFlags & allSpacing) == allSpacing
            ? $"{descriptor.SpacingXUm:0.######} x {descriptor.SpacingYUm:0.######} x {descriptor.SpacingZUm:0.######} um"
            : "incomplete metadata (uncalibrated)";
    }
}
