using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Waypoint;

partial class OutputPanel : UserControl
{
    readonly StringBuilder _output = new();
    const int OutputMax = 200_000;
    string _stage = "", _detail = "";

    public OutputPanel() => InitializeComponent();

    public void Attach(Shared shared)
    {
        shared.Workspace.Output += text => Dispatcher.UIThread.Post(() => Append(text));
        shared.Workspace.Stage += (stage, detail) => Dispatcher.UIThread.Post(() =>
        {
            (_stage, _detail) = (stage, detail);
            ShowStage();
        });
        L.Current.PropertyChanged += (_, _) => ShowStage();
        ShowStage();
    }

    void ShowStage() =>
        RunState.Text = _stage.Length == 0 ? L.Text("Ready.") : _detail.Length == 0 ? L.Text(_stage) + "…" : $"{L.Text(_stage)}… {L.Text(_detail)}";

    void Append(string text)
    {
        _output.AppendLine(text);
        if (_output.Length > OutputMax) _output.Remove(0, _output.Length - OutputMax);
        // Written while hidden too: nothing here tells this panel when a section around it comes back into view.
        Output.Text = _output.ToString();
        Output.ScrollToEnd();
    }

    void ClearOutput(object? sender, RoutedEventArgs e)
    {
        _output.Clear();
        Output.Clear();
    }
}

static class LogBox
{
    /// <summary>Keeps a log's newest line in view. The caret carries a box that is hidden, which has no scroll viewer yet, to its
    /// end. It stays put once a log is at its length limit, so the box's scroll viewer goes there too, after the new text's layout.</summary>
    public static void ScrollToEnd(this TextBox box)
    {
        box.CaretIndex = box.Text?.Length ?? 0;
        Dispatcher.UIThread.Post(() => box.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd(), DispatcherPriority.Background);
    }
}
