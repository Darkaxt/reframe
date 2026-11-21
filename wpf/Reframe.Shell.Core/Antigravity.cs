namespace Reframe.Shell.Core;

/// <summary>Google's Antigravity CLI (agy), which Gemini threads run: where it keeps its conversations. Read only.</summary>
public static class Antigravity
{
    /// <summary>agy's home: <c>%USERPROFILE%\.gemini\antigravity-cli</c>.</summary>
    public static string Home() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli");

    /// <summary>The transcript agy writes for a conversation, if it is there.</summary>
    public static string? TranscriptFile(string conversationId, string? home = null)
    {
        if (string.IsNullOrEmpty(conversationId) || conversationId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        var file = Path.Combine(home ?? Home(), "brain", conversationId, ".system_generated", "logs", "transcript_full.jsonl");
        return File.Exists(file) ? file : null;
    }
}
