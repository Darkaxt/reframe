using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class TranscriptImageTests
{
    [Fact]
    public void A_pasted_image_keeps_its_data()
    {
        var m = new TranscriptModel();
        m.Append(["""{"type":"user","uuid":"u1","message":{"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}},{"type":"text","text":"look"}]}}"""]);
        var user = Assert.IsType<UserMessageItem>(Assert.Single(m.Items));
        Assert.Equal(["AAAA"], user.Images);
        Assert.Contains("look", user.Text);
    }

    [Fact]
    public void A_system_line_without_content_is_bare()
    {
        var m = new TranscriptModel();
        m.Append(["""{"type":"system","subtype":"turn_duration","uuid":"s1"}""", """{"type":"system","content":"heads up","uuid":"s2"}"""]);
        Assert.True(Assert.IsType<SystemNoticeItem>(m.Items[0]).Bare);
        Assert.False(Assert.IsType<SystemNoticeItem>(m.Items[1]).Bare);
    }
}
