using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class GpuPreferenceTests
{
    [Fact]
    public void OnlyAnExplicitTrueTurnsTheGpuOn()
    {
        var file = Path.Combine(Path.GetTempPath(), $"reframe-gpu-{Guid.NewGuid():N}.json");
        try
        {
            Assert.False(GpuPreference.Saved(file));
            foreach (var (text, on) in new[]
            {
                ("""{"preferences":{"gpuAcceleration":true}}""", true),
                ("""{"preferences":{"gpuAcceleration":false}}""", false),
                ("""{"preferences":{"gpuAcceleration":"true"}}""", false),
                ("""{"preferences":{}}""", false),
                ("[1]", false),
                ("not json", false),
            })
            {
                File.WriteAllText(file, text);
                Assert.Equal(on, GpuPreference.Saved(file));
            }
        }
        finally
        {
            File.Delete(file);
        }
    }
}
