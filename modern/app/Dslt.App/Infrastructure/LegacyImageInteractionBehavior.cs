using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Dslt.App.ViewModels;

namespace Dslt.App.Infrastructure;

public enum OrthogonalViewPlane
{
    Xy,
    Yz,
    Zx,
}

public sealed record OrthogonalPointerPosition(OrthogonalViewPlane Plane, int Horizontal, int Vertical);

public static class LegacyImageInteractionBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(LegacyImageInteractionBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty PlaneProperty = DependencyProperty.RegisterAttached(
        "Plane", typeof(OrthogonalViewPlane), typeof(LegacyImageInteractionBehavior),
        new PropertyMetadata(OrthogonalViewPlane.Xy));

    public static readonly DependencyProperty SelectLabelsProperty = DependencyProperty.RegisterAttached(
        "SelectLabels", typeof(bool), typeof(LegacyImageInteractionBehavior), new PropertyMetadata(false));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetPlane(DependencyObject element, OrthogonalViewPlane value) => element.SetValue(PlaneProperty, value);
    public static OrthogonalViewPlane GetPlane(DependencyObject element) =>
        (OrthogonalViewPlane)element.GetValue(PlaneProperty);
    public static void SetSelectLabels(DependencyObject element, bool value) => element.SetValue(SelectLabelsProperty, value);
    public static bool GetSelectLabels(DependencyObject element) => (bool)element.GetValue(SelectLabelsProperty);

    internal static bool TryMapToPixel(
        double pointerX,
        double pointerY,
        double elementWidth,
        double elementHeight,
        int pixelWidth,
        int pixelHeight,
        out int horizontal,
        out int vertical)
    {
        horizontal = vertical = -1;
        if (!double.IsFinite(pointerX) || !double.IsFinite(pointerY) ||
            !double.IsFinite(elementWidth) || !double.IsFinite(elementHeight) ||
            elementWidth <= 0 || elementHeight <= 0 || pixelWidth <= 0 || pixelHeight <= 0)
            return false;
        var scale = Math.Min(elementWidth / pixelWidth, elementHeight / pixelHeight);
        var renderedWidth = pixelWidth * scale;
        var renderedHeight = pixelHeight * scale;
        var offsetX = (elementWidth - renderedWidth) / 2;
        var offsetY = (elementHeight - renderedHeight) / 2;
        if (pointerX < offsetX || pointerY < offsetY ||
            pointerX >= offsetX + renderedWidth || pointerY >= offsetY + renderedHeight)
            return false;
        horizontal = Math.Clamp((int)((pointerX - offsetX) / scale), 0, pixelWidth - 1);
        vertical = Math.Clamp((int)((pointerY - offsetY) / scale), 0, pixelHeight - 1);
        return true;
    }

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not Image image) return;
        image.PreviewMouseDown -= OnPreviewMouseDown;
        if (args.NewValue is true) image.PreviewMouseDown += OnPreviewMouseDown;
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (sender is not Image { DataContext: MainWindowViewModel viewModel } image) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            if (args.ChangedButton == MouseButton.Left) viewModel.ZoomInCommand.Execute(null);
            else if (args.ChangedButton == MouseButton.Right) viewModel.ZoomOutCommand.Execute(null);
            else return;
            args.Handled = true;
            return;
        }
        if (args.ChangedButton != MouseButton.Middle || !GetSelectLabels(image) ||
            image.Source is not BitmapSource source) return;
        var pointer = args.GetPosition(image);
        if (!TryMapToPixel(pointer.X, pointer.Y, image.ActualWidth, image.ActualHeight,
                source.PixelWidth, source.PixelHeight, out var horizontal, out var vertical)) return;
        viewModel.SelectPlanePointCommand.Execute(new OrthogonalPointerPosition(
            GetPlane(image), horizontal, vertical));
        args.Handled = true;
    }
}
