using System.Text.Json;

namespace Reframe.Shell.Core;

/// <summary>A message for an ended thread (message_thread without wake), held until that thread next runs.</summary>
/// <param name="Text">The full text to type in, with its "[message from …]" header.</param>
public sealed record HeldMessage(string TargetId, string TargetName, string AskerId, string AskerName, string Text, long HeldAt);

/// <summary>The held messages, kept in one JSON file (the profile's <c>messages/held.json</c>); a missing or corrupt
/// file is empty.</summary>
public static class HeldMessages
{
    public static List<HeldMessage> Load(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<List<HeldMessage>>(File.ReadAllText(file))?.Where(m => m is { TargetId: not null, Text: not null }).ToList() ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>Written to a temporary file that then replaces it.</summary>
    public static void Save(string file, IReadOnlyList<HeldMessage> messages)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(messages));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>Removes and returns the messages for <paramref name="targetId"/>, oldest first.</summary>
    public static List<HeldMessage> TakeFor(List<HeldMessage> messages, string targetId)
    {
        var taken = messages.Where(m => m.TargetId == targetId).OrderBy(m => m.HeldAt).ToList();
        messages.RemoveAll(m => m.TargetId == targetId);
        return taken;
    }

    /// <summary>Removes and returns the messages held longer than <see cref="LongMessages.KeepFor"/>.</summary>
    public static List<HeldMessage> TakeExpired(List<HeldMessage> messages, DateTimeOffset now)
    {
        var limit = (now - LongMessages.KeepFor).ToUnixTimeMilliseconds();
        var expired = messages.Where(m => m.HeldAt < limit).ToList();
        messages.RemoveAll(m => m.HeldAt < limit);
        return expired;
    }
}
