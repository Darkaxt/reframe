using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class MarkdownLiteTests
{
    [Fact]
    public void Headings_lists_code_and_paragraphs()
    {
        var blocks = MarkdownLite.Parse("# Title\nSome **bold** and `code`.\n\n- one\n2. two\n\n```cs\nvar x = 1;\n```\nend");
        Assert.Equal([MdBlockKind.Heading, MdBlockKind.Paragraph, MdBlockKind.Bullet, MdBlockKind.Bullet, MdBlockKind.Code, MdBlockKind.Paragraph], blocks.Select(b => b.Kind).ToArray());
        Assert.Equal(1, blocks[0].Level);
        Assert.Equal(["Some ", "bold", " and ", "code", "."], blocks[1].Spans.Select(s => s.Text).ToArray());
        Assert.True(blocks[1].Spans[1].Bold);
        Assert.True(blocks[1].Spans[3].Code);
        Assert.Equal(["•", "2."], blocks.Skip(2).Take(2).Select(b => b.Marker).ToArray());
        Assert.Equal("var x = 1;", blocks[4].Raw);
    }

    [Fact]
    public void Tables_and_links()
    {
        var blocks = MarkdownLite.Parse("Before\n| Name | Size |\n| --- | ---: |\n| a | **1** |\n| b |\nafter");
        Assert.Equal([MdBlockKind.Paragraph, MdBlockKind.Table, MdBlockKind.Paragraph], blocks.Select(b => b.Kind).ToArray());
        var cells = blocks[1].Cells!;
        Assert.Equal(3, cells.Count);
        Assert.Equal(new[] { "Name", "Size" }, cells[0].ToArray());
        Assert.Equal(new[] { "a", "**1**" }, cells[1].ToArray());
        Assert.Equal(new[] { "b" }, cells[2].ToArray());
        // A line starting with | without the separator line stays text.
        Assert.Equal(MdBlockKind.Paragraph, MarkdownLite.Parse("| not a table |")[0].Kind);

        var spans = MarkdownLite.Spans("See [the docs](https://example.com/a) or https://example.com/b. Done");
        Assert.Equal(new[] { "See ", "the docs", " or ", "https://example.com/b", ". Done" }, spans.Select(s => s.Text).ToArray());
        Assert.Equal("https://example.com/a", spans[1].Link);
        Assert.Equal("https://example.com/b", spans[3].Link);
        Assert.Null(spans[0].Link);
        Assert.Equal("(https://x.org/p)", string.Concat(MarkdownLite.Spans("(https://x.org/p)").Select(s => s.Text)));
        Assert.Equal("https://x.org/p", MarkdownLite.Spans("(https://x.org/p)")[1].Link);
        Assert.All(MarkdownLite.Spans("[a b](not a url)"), s => Assert.Null(s.Link));
    }

    [Fact]
    public void Unclosed_markers_stay_plain_text()
    {
        var spans = MarkdownLite.Spans("a **b and `c");
        Assert.Equal("a **b and `c", string.Concat(spans.Select(s => s.Text)));
        Assert.All(spans, s => Assert.False(s.Bold || s.Code));
    }
}
