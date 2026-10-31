using System.Windows;
using System.Windows.Controls;

namespace Launcher.Desktop;

/// <summary>Retains finite, virtualized lists while allowing the whole task page to scroll at larger text sizes.</summary>
public sealed class WorkspaceScroll : ScrollViewer
{
    public static readonly DependencyProperty MinimumContentHeightProperty = DependencyProperty.Register(nameof(MinimumContentHeight), typeof(double), typeof(WorkspaceScroll), new FrameworkPropertyMetadata(380d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double MinimumContentHeight { get => (double)GetValue(MinimumContentHeightProperty); set => SetValue(MinimumContentHeightProperty, value); }
    public WorkspaceScroll() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto; HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled; }
    protected override Size MeasureOverride(Size constraint)
    {
        if (Content is FrameworkElement child && double.IsFinite(constraint.Height))
            child.Height = Math.Max(MinimumContentHeight, constraint.Height);
        return base.MeasureOverride(constraint);
    }
}
