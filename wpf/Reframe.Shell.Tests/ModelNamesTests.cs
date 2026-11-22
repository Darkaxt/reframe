using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class ModelNamesTests
{
    [Theory]
    [InlineData(null, "Default")]
    [InlineData("default", "Default")]
    [InlineData("opus", "Claude Opus")]
    [InlineData("haiku", "Claude Haiku")]
    [InlineData("claude-opus-4-1", "Claude Opus 4.1")]
    [InlineData("gpt-6.1-sol", "GPT-6.1 Sol")]
    [InlineData("gpt-6.1-terra-fast", "GPT-6.1 Terra Fast")]
    [InlineData("gpt-6.2-astra", "GPT-6.2 Astra")]
    [InlineData("opus[1m]", "Claude Opus [1m]")]
    public void Models_have_their_commercial_name(string? id, string expected) => Assert.Equal(expected, ModelNames.Label(id));

    [Fact]
    public void A_model_select_offers_the_providers_models_and_keeps_one_set_before()
    {
        var (options, selected) = ModelNames.Options("anthropic", "opus");
        Assert.Equal(["", "fable", "opus", "sonnet", "haiku"], options.Select(o => o.Value));
        Assert.All(options, o => Assert.True(o.Known));
        Assert.Equal("opus", selected);
        Assert.Equal("", ModelNames.Options("chatgpt", "default").Selected);
        Assert.Equal("", ModelNames.Options("chatgpt", null).Selected);
        // A typo set by hand before: listed last, marked, until another is picked.
        (options, selected) = ModelNames.Options("chatgpt", "gpt-6.1-sool");
        Assert.Equal(("gpt-6.1-sool", false), (options[^1].Value, options[^1].Known));
        Assert.Equal("gpt-6.1-sool", selected);
        // A Gemini id with its effort is its family.
        (options, selected) = ModelNames.Options("gemini", "gemini-4-neon-xhigh");
        Assert.Equal("gemini-4-neon", selected);
        Assert.All(options, o => Assert.True(o.Known));
    }

    [Fact]
    public void Each_provider_offers_its_own_models()
    {
        Assert.Contains("opus", ModelNames.Choices("anthropic"));
        Assert.Contains("gpt-6.1-sol", ModelNames.Choices("chatgpt"));
        Assert.DoesNotContain("opus", ModelNames.Choices("chatgpt"));
    }
}
