using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;

namespace Waypoint;

/// <summary>A mod's README as a FlowDocument, laid out like GitHub draws it: headings, paragraphs, lists, code, quotes,
/// tables and links. Images show their alt text; HTML is left out. Colors follow the theme's brushes.</summary>
static class MarkdownView
{
    public static FlowDocument Render(string markdown, string? baseUrl, Action<string> open, FontFamily font)
    {
        var document = new FlowDocument { PagePadding = new Thickness(16, 12, 16, 16), FontFamily = font, FontSize = 13.5, TextAlignment = TextAlignment.Left };
        document.SetResourceReference(TextElement.ForegroundProperty, "Shell.Text");
        var renderer = new Renderer(baseUrl, open);
        foreach (var block in ModsText.Readme(markdown)) renderer.Add(document.Blocks, block, tight: false);
        return document;
    }

    sealed class Renderer(string? baseUrl, Action<string> open)
    {
        static readonly FontFamily Mono = new("Consolas, Courier New");

        public void Add(BlockCollection into, MdBlock block, bool tight)
        {
            switch (block)
            {
                case HeadingBlock heading:
                {
                    var paragraph = new Paragraph
                    {
                        FontSize = heading.Level switch { 1 => 24, 2 => 19, 3 => 16, _ => 14 },
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, into.Count == 0 ? 0 : 16, 0, 8),
                    };
                    if (heading.Level <= 2)
                    {
                        paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
                        paragraph.Padding = new Thickness(0, 0, 0, 4);
                        paragraph.SetResourceReference(Paragraph.BorderBrushProperty, "Shell.Border");
                    }
                    AddInlines(paragraph.Inlines, heading.Inline);
                    into.Add(paragraph);
                    break;
                }
                case ParagraphBlock text:
                {
                    var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, tight ? 2 : 10) };
                    AddInlines(paragraph.Inlines, text.Inline);
                    into.Add(paragraph);
                    break;
                }
                case ListBlock list:
                {
                    var view = new List
                    {
                        MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(0, 0, 0, tight ? 2 : 10),
                        Padding = new Thickness(22, 0, 0, 0),
                    };
                    if (list.IsOrdered && int.TryParse(list.OrderedStart, out int start) && start > 0) view.StartIndex = start;
                    foreach (var child in list.OfType<ListItemBlock>())
                    {
                        var item = new ListItem();
                        foreach (var inner in child) Add(item.Blocks, inner, tight: !list.IsLoose);
                        view.ListItems.Add(item);
                    }
                    into.Add(view);
                    break;
                }
                case CodeBlock code:
                {
                    var paragraph = new Paragraph { FontFamily = Mono, FontSize = 12.5, Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 12) };
                    paragraph.SetResourceReference(Paragraph.BackgroundProperty, "Shell.Panel");
                    var lines = code.Lines.ToString().TrimEnd('\r', '\n').Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (i > 0) paragraph.Inlines.Add(new LineBreak());
                        paragraph.Inlines.Add(new Run(lines[i].TrimEnd('\r')));
                    }
                    into.Add(paragraph);
                    break;
                }
                case QuoteBlock quote:
                {
                    var section = new System.Windows.Documents.Section { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 0, 0, 0), Margin = new Thickness(0, 0, 0, 10) };
                    section.SetResourceReference(System.Windows.Documents.Section.BorderBrushProperty, "Shell.Border");
                    section.SetResourceReference(TextElement.ForegroundProperty, "Shell.TextSecondary");
                    foreach (var inner in quote) Add(section.Blocks, inner, tight);
                    into.Add(section);
                    break;
                }
                case ThematicBreakBlock:
                {
                    var line = new Paragraph { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 4, 0, 14), FontSize = 1 };
                    line.SetResourceReference(Paragraph.BorderBrushProperty, "Shell.Border");
                    into.Add(line);
                    break;
                }
                case MdTable table:
                    into.Add(Table(table));
                    break;
                case HtmlBlock or LinkReferenceDefinitionGroup:
                    break;
                case ContainerBlock container:
                    foreach (var inner in container) Add(into, inner, tight);
                    break;
            }
        }

        System.Windows.Documents.Table Table(MdTable table)
        {
            var view = new System.Windows.Documents.Table { CellSpacing = 0, BorderThickness = new Thickness(1, 1, 0, 0), Margin = new Thickness(0, 0, 0, 12) };
            view.SetResourceReference(System.Windows.Documents.Table.BorderBrushProperty, "Shell.Border");
            int columns = table.OfType<MdTableRow>().Select(r => r.Count).DefaultIfEmpty(0).Max();
            for (int i = 0; i < columns; i++) view.Columns.Add(new TableColumn { Width = GridLength.Auto });
            var group = new TableRowGroup();
            foreach (var row in table.OfType<MdTableRow>())
            {
                var viewRow = new System.Windows.Documents.TableRow();
                if (row.IsHeader)
                {
                    viewRow.FontWeight = FontWeights.SemiBold;
                    viewRow.SetResourceReference(System.Windows.Documents.TableRow.BackgroundProperty, "Shell.Panel");
                }
                foreach (var cell in row.OfType<MdTableCell>())
                {
                    var viewCell = new System.Windows.Documents.TableCell { BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(8, 4, 8, 4) };
                    viewCell.SetResourceReference(System.Windows.Documents.TableCell.BorderBrushProperty, "Shell.Border");
                    foreach (var inner in cell) Add(viewCell.Blocks, inner, tight: true);
                    viewRow.Cells.Add(viewCell);
                }
                group.Rows.Add(viewRow);
            }
            view.RowGroups.Add(group);
            return view;
        }

        void AddInlines(InlineCollection into, ContainerInline? container)
        {
            if (container is null) return;
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
                    else if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.SemiBold;
                    else span.FontStyle = FontStyles.Italic;
                    AddInlines(span.Inlines, emphasis);
                    into.Add(span);
                    break;
                }
                case CodeInline code:
                {
                    var run = new Run(code.Content) { FontFamily = Mono, FontSize = 12.5 };
                    run.SetResourceReference(TextElement.BackgroundProperty, "Shell.Panel");
                    into.Add(run);
                    break;
                }
                case LinkInline { IsImage: true } image:
                {
                    // No images: their alt text, in italics.
                    var span = new Span { FontStyle = FontStyles.Italic };
                    span.SetResourceReference(TextElement.ForegroundProperty, "Shell.TextSecondary");
                    AddInlines(span.Inlines, image);
                    into.Add(span);
                    break;
                }
                case LinkInline link:
                {
                    if (ModsText.Link(link.GetDynamicUrl?.Invoke() ?? link.Url, baseUrl) is not { } url)
                    {
                        var span = new Span();
                        AddInlines(span.Inlines, link);
                        into.Add(span);
                        break;
                    }
                    var hyperlink = Hyperlink(url);
                    AddInlines(hyperlink.Inlines, link);
                    into.Add(hyperlink);
                    break;
                }
                case AutolinkInline auto when !auto.IsEmail && ModsText.Link(auto.Url, baseUrl) is { } url:
                {
                    var hyperlink = Hyperlink(url);
                    hyperlink.Inlines.Add(new Run(auto.Url));
                    into.Add(hyperlink);
                    break;
                }
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
                    AddInlines(into, container);
                    break;
            }
        }

        Hyperlink Hyperlink(string url)
        {
            var hyperlink = new Hyperlink { ToolTip = url, Cursor = System.Windows.Input.Cursors.Hand };
            hyperlink.SetResourceReference(TextElement.ForegroundProperty, "Shell.Accent");
            hyperlink.Click += (_, _) => open(url);
            return hyperlink;
        }
    }
}
