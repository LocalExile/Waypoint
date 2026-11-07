using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;

namespace Waypoint;

/// <summary>A mod's README as controls, laid out like GitHub draws it: headings, paragraphs, lists, code, quotes, tables
/// and links. Images show their alt text; HTML is left out. Colors are the theme's brushes when it is drawn.</summary>
static class MarkdownView
{
    public static IEnumerable<Control> Render(string markdown, string? baseUrl, Control host)
    {
        var renderer = new Renderer(baseUrl, key => host.FindResource(key) as IBrush);
        var controls = new List<Control>();
        foreach (var block in ModsText.Readme(markdown)) renderer.Add(controls, block, tight: false);
        return controls;
    }

    sealed class Renderer(string? baseUrl, Func<string, IBrush?> brush)
    {
        static readonly FontFamily Mono = new("Cascadia Mono, DejaVu Sans Mono, Liberation Mono, monospace");

        public void Add(List<Control> into, MdBlock block, bool tight)
        {
            switch (block)
            {
                case HeadingBlock heading:
                {
                    var text = Text(heading.Inline);
                    text.FontSize = heading.Level switch { 1 => 24, 2 => 19, 3 => 16, _ => 14 };
                    text.FontWeight = FontWeight.SemiBold;
                    var border = new Border { Child = text, Margin = new Thickness(0, into.Count == 0 ? 0 : 16, 0, 8) };
                    if (heading.Level <= 2)
                    {
                        border.BorderThickness = new Thickness(0, 0, 0, 1);
                        border.BorderBrush = brush("Shell.Border");
                        border.Padding = new Thickness(0, 0, 0, 4);
                    }
                    into.Add(border);
                    break;
                }
                case ParagraphBlock paragraph:
                {
                    var text = Text(paragraph.Inline);
                    text.Margin = new Thickness(0, 0, 0, tight ? 2 : 10);
                    into.Add(text);
                    break;
                }
                case ListBlock list:
                {
                    var panel = new StackPanel { Margin = new Thickness(0, 0, 0, tight ? 2 : 10) };
                    int number = list.IsOrdered && int.TryParse(list.OrderedStart, out int start) ? start : 1;
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*") };
                        row.Children.Add(new TextBlock { Text = list.IsOrdered ? $"{number++}." : "•", FontSize = 13.5, HorizontalAlignment = HorizontalAlignment.Left });
                        var content = new StackPanel();
                        var inner = new List<Control>();
                        foreach (var child in item) Add(inner, child, tight: !list.IsLoose);
                        content.Children.AddRange(inner);
                        Grid.SetColumn(content, 1);
                        row.Children.Add(content);
                        panel.Children.Add(row);
                    }
                    into.Add(panel);
                    break;
                }
                case CodeBlock code:
                    into.Add(new Border
                    {
                        Background = brush("Shell.Panel"), CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 12),
                        Child = new SelectableTextBlock { Text = code.Lines.ToString().Replace("\r", "").TrimEnd('\n'), FontFamily = Mono, FontSize = 12.5, TextWrapping = TextWrapping.Wrap },
                    });
                    break;
                case QuoteBlock quote:
                {
                    var panel = new StackPanel();
                    var inner = new List<Control>();
                    foreach (var child in quote) Add(inner, child, tight);
                    panel.Children.AddRange(inner);
                    foreach (var text in inner.OfType<TextBlock>()) text.Foreground = brush("Shell.TextSecondary");
                    into.Add(new Border
                    {
                        BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = brush("Shell.Border"), Padding = new Thickness(12, 0, 0, 0), Margin = new Thickness(0, 0, 0, 10), Child = panel,
                    });
                    break;
                }
                case ThematicBreakBlock:
                    into.Add(new Border { Height = 1, Background = brush("Shell.Border"), Margin = new Thickness(0, 4, 0, 14) });
                    break;
                case MdTable table:
                    into.Add(Table(table));
                    break;
                case HtmlBlock or LinkReferenceDefinitionGroup:
                    break;
                case ContainerBlock container:
                    foreach (var child in container) Add(into, child, tight);
                    break;
            }
        }

        Control Table(MdTable table)
        {
            var rows = table.OfType<MdTableRow>().ToList();
            int columns = rows.Select(r => r.Count).DefaultIfEmpty(0).Max();
            var grid = new Grid();
            for (int i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            for (int r = 0; r < rows.Count; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                int c = 0;
                foreach (var cell in rows[r].OfType<MdTableCell>())
                {
                    var inner = new List<Control>();
                    foreach (var child in cell) Add(inner, child, tight: true);
                    var panel = new StackPanel();
                    panel.Children.AddRange(inner);
                    if (rows[r].IsHeader) foreach (var text in inner.OfType<TextBlock>()) text.FontWeight = FontWeight.SemiBold;
                    var border = new Border
                    {
                        Child = panel, Padding = new Thickness(8, 4, 8, 4), BorderBrush = brush("Shell.Border"),
                        BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1),
                        Background = rows[r].IsHeader ? brush("Shell.Panel") : null,
                    };
                    Grid.SetRow(border, r);
                    Grid.SetColumn(border, c++);
                    grid.Children.Add(border);
                }
            }
            return new Border { Child = grid, Margin = new Thickness(0, 0, 0, 12), HorizontalAlignment = HorizontalAlignment.Left };
        }

        SelectableTextBlock Text(ContainerInline? inlines)
        {
            var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Inlines = [] };
            if (inlines is not null) foreach (var inline in inlines) Add(text.Inlines, inline);
            return text;
        }

        void AddAll(InlineCollection into, ContainerInline container)
        {
            foreach (var inline in container) Add(into, inline);
        }

        void Add(InlineCollection into, MdInline inline)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    into.Add(new Run(literal.Content.ToString()));
                    break;
                case EmphasisInline emphasis:
                {
                    var span = new Span();
                    if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeight.SemiBold;
                    else span.FontStyle = FontStyle.Italic;
                    AddAll(span.Inlines, emphasis);
                    into.Add(span);
                    break;
                }
                case CodeInline code:
                    into.Add(new Run(code.Content) { FontFamily = Mono, FontSize = 12.5, Background = brush("Shell.Panel") });
                    break;
                case LinkInline { IsImage: true } image:
                {
                    // No images: their alt text, in italics.
                    var span = new Span { FontStyle = FontStyle.Italic, Foreground = brush("Shell.TextSecondary") };
                    AddAll(span.Inlines, image);
                    into.Add(span);
                    break;
                }
                case LinkInline link:
                {
                    var span = new Span();
                    AddAll(span.Inlines, link);
                    if (ModsText.Link(link.GetDynamicUrl?.Invoke() ?? link.Url, baseUrl) is { } url) into.Add(Hyperlink(Plain(link), url));
                    else into.Add(span);
                    break;
                }
                case AutolinkInline auto when !auto.IsEmail && ModsText.Link(auto.Url, baseUrl) is { } url:
                    into.Add(Hyperlink(auto.Url, url));
                    break;
                case AutolinkInline auto:
                    into.Add(new Run(auto.Url));
                    break;
                case LineBreakInline lineBreak:
                    into.Add(lineBreak.IsHard ? new LineBreak() : new Run(" "));
                    break;
                case HtmlEntityInline entity:
                    into.Add(new Run(entity.Transcoded.ToString()));
                    break;
                case HtmlInline html when html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase):
                    into.Add(new LineBreak());
                    break;
                case TaskList task:
                    into.Add(new Run(task.Checked ? "☑ " : "☐ "));
                    break;
                case ContainerInline container:
                    AddAll(into, container);
                    break;
            }
        }

        /// <summary>A link's text without its formatting (a link is drawn as one button).</summary>
        static string Plain(ContainerInline container) => string.Concat(container.Select(i => i switch
        {
            LiteralInline literal => literal.Content.ToString(),
            CodeInline code => code.Content,
            ContainerInline inner => Plain(inner),
            _ => "",
        }));

        Avalonia.Controls.Documents.Inline Hyperlink(string text, string url)
        {
            var link = new HyperlinkButton { Content = text, Padding = new Thickness(0), NavigateUri = new Uri(url), Foreground = brush("Shell.Accent") };
            ToolTip.SetTip(link, url);
            return new InlineUIContainer(link);
        }
    }
}
