using System.Windows;

namespace Waypoint;

public static class SnapCaption
{
    public static readonly DependencyProperty IsHoveredProperty = DependencyProperty.RegisterAttached(
        "IsHovered", typeof(bool), typeof(SnapCaption), new PropertyMetadata(false));
    public static bool GetIsHovered(DependencyObject target) => (bool)target.GetValue(IsHoveredProperty);
    public static void SetIsHovered(DependencyObject target, bool value) => target.SetValue(IsHoveredProperty, value);
    public static readonly DependencyProperty IsPressedProperty = DependencyProperty.RegisterAttached(
        "IsPressed", typeof(bool), typeof(SnapCaption), new PropertyMetadata(false));
    public static bool GetIsPressed(DependencyObject target) => (bool)target.GetValue(IsPressedProperty);
    public static void SetIsPressed(DependencyObject target, bool value) => target.SetValue(IsPressedProperty, value);

    // Win32 packs signed screen coordinates, including monitors left of/above the primary.
    public static Point ScreenPoint(nint packed) => new(unchecked((short)(packed.ToInt64() & 0xffff)),
        unchecked((short)((packed.ToInt64() >> 16) & 0xffff)));

    public static bool Contains(Rect bounds, Point screen) => !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0
        && screen.X >= bounds.Left && screen.X < bounds.Right && screen.Y >= bounds.Top && screen.Y < bounds.Bottom;
}
