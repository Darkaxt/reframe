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
    public void Each_provider_offers_its_own_models()
    {
        Assert.Contains("opus", ModelNames.Choices("anthropic"));
        Assert.Contains("gpt-6.1-sol", ModelNames.Choices("chatgpt"));
        Assert.DoesNotContain("opus", ModelNames.Choices("chatgpt"));
    }
}
