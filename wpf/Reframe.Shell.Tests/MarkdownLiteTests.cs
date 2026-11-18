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
    public void Unclosed_markers_stay_plain_text()
    {
        var spans = MarkdownLite.Spans("a **b and `c");
        Assert.Equal("a **b and `c", string.Concat(spans.Select(s => s.Text)));
        Assert.All(spans, s => Assert.False(s.Bold || s.Code));
    }
}
