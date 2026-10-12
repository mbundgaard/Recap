using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Recap;

/// <summary>Renders Markdown locally; links and images never load external content.</summary>
internal static class MarkdownPreview
{
    public static FlowDocument Render(string markdown)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"), FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(34, 39, 46)),
            PagePadding = new Thickness(4),
            ColumnWidth = double.PositiveInfinity
        };
        AddBlocks(document.Blocks, Markdown.Parse(markdown));
        return document;
    }

    private static void AddBlocks(BlockCollection target, ContainerBlock source)
    {
        foreach (var block in source)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var title = new Paragraph { FontSize = heading.Level == 1 ? 24 : heading.Level == 2 ? 20 : 16,
                        FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 8) };
                    AddInlines(title.Inlines, heading.Inline);
                    target.Add(title);
                    break;
                case ParagraphBlock paragraph:
                    var text = new Paragraph { Margin = new Thickness(0, 0, 0, 10), LineHeight = 22 };
                    AddInlines(text.Inlines, paragraph.Inline);
                    target.Add(text);
                    break;
                case ListBlock list:
                    var renderedList = new System.Windows.Documents.List
                    {
                        MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(0, 4, 0, 12), Padding = new Thickness(22, 0, 0, 0)
                    };
                    foreach (var item in list)
                    {
                        if (item is not ListItemBlock listItem) continue;
                        var renderedItem = new ListItem();
                        AddBlocks(renderedItem.Blocks, listItem);
                        renderedList.ListItems.Add(renderedItem);
                    }
                    target.Add(renderedList);
                    break;
                case CodeBlock code:
                    target.Add(new Paragraph(new Run(code.Lines.ToString()))
                    {
                        FontFamily = new FontFamily("Consolas"), FontSize = 13,
                        Background = Brushes.WhiteSmoke, Padding = new Thickness(10)
                    });
                    break;
                case QuoteBlock quote:
                    var section = new Section { BorderThickness = new Thickness(3, 0, 0, 0),
                        BorderBrush = Brushes.Gray, Padding = new Thickness(12, 0, 0, 0) };
                    AddBlocks(section.Blocks, quote);
                    target.Add(section);
                    break;
                case ThematicBreakBlock:
                    target.Add(new Paragraph { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 0, 0, 1) });
                    break;
            }
        }
    }

    private static void AddInlines(InlineCollection target, ContainerInline? source)
    {
        if (source is null) return;
        foreach (var inline in source)
        {
            switch (inline)
            {
                case LiteralInline literal: target.Add(new Run(literal.Content.ToString())); break;
                case CodeInline code:
                    target.Add(new Run(code.Content) { FontFamily = new FontFamily("Consolas"), Background = Brushes.WhiteSmoke });
                    break;
                case LineBreakInline: target.Add(new LineBreak()); break;
                case EmphasisInline emphasis:
                    Span span = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    AddInlines(span.Inlines, emphasis);
                    target.Add(span);
                    break;
                case AutolinkInline link: target.Add(new Run(link.Url)); break;
                case ContainerInline container: AddInlines(target, container); break;
            }
        }
    }
}
