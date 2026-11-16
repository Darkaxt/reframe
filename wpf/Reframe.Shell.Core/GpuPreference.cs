using System.Text.Json.Nodes;

namespace Reframe.Shell.Core;

/// <summary>
/// The "Use GPU acceleration" setting, read from state.json before anything draws (as src/main/gpu.ts does): only an
/// explicit <c>true</c> turns it on; a missing or unreadable file means off. A change applies after a restart.
/// </summary>
public static class GpuPreference
{
    public static bool Saved(string stateFile)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(stateFile))?["preferences"]?["gpuAcceleration"]?.GetValueKind() == System.Text.Json.JsonValueKind.True;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}
