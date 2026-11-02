using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Waypoint;

partial class OutputPanel : UserControl
{
    readonly StringBuilder _output = new();
    const int OutputMax = 200_000;
    string _stage = "", _detail = "";

    public OutputPanel()
    {
        InitializeComponent();
        ViewTools.WhenShown(this, ShowOutput);
    }

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

    void ShowStage()
    {
        OutputHeading.Text = L.Text("Output");
        ClearButton.Content = L.Text("Clear");
        RunState.Text = _stage.Length == 0 ? L.Text("Ready.") : _detail.Length == 0 ? L.Text(_stage) + "…" : $"{L.Text(_stage)}… {L.Text(_detail)}";
    }

    void Append(string text)
    {
        _output.AppendLine(text);
        if (_output.Length > OutputMax) _output.Remove(0, _output.Length - OutputMax);
        if (IsEffectivelyVisible) ShowOutput();
    }

    void ShowOutput()
    {
        Output.Text = _output.ToString();
        Output.CaretIndex = _output.Length;
    }

    void ClearOutput(object? sender, RoutedEventArgs e)
    {
        _output.Clear();
        Output.Clear();
    }
}
