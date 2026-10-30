using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Text.RegularExpressions;

namespace Launcher.Desktop;

/// <summary>Displays full Git IDs as short inline code badges without changing the underlying identity.</summary>
public sealed class CommitText : TextBlock
{
    static readonly Regex Hash = new(@"(?<![A-Za-z0-9])(?:[a-fA-F0-9]{64}|[a-fA-F0-9]{40})(?![A-Za-z0-9])", RegexOptions.CultureInvariant);
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(CommitText),
        new PropertyMetadata("", (sender, _) => ((CommitText)sender).Render()));

    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    void Render()
    {
        Inlines.Clear();
        string value = Value ?? "";
        int position = 0;
        foreach (Match match in Hash.Matches(value))
        {
            if (match.Index > position) Inlines.Add(new Run(value[position..match.Index]));
            var badge = new Border
            {
                Style = (Style)FindResource("CommitBadge"), ToolTip = match.Value,
                Child = new TextBlock { Text = match.Value[..7], Style = (Style)FindResource("CommitHashText") }
            };
            Inlines.Add(new InlineUIContainer(badge) { BaselineAlignment = BaselineAlignment.Center });
            position = match.Index + match.Length;
        }
        if (position < value.Length) Inlines.Add(new Run(value[position..]));
    }
}
