using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Reframe.Shell.Core;

/// <summary>
/// Scheduled tasks (the thread tools' <c>schedule_task</c>, reframe-host's tasks.rs): one folder per task under the
/// profile's <c>tasks</c> folder, with <c>automation.toml</c> in the format of Codex's automations and, beside it,
/// <c>runs.json</c>, what reframe last did with it. The shell works out when each is due (<see cref="Recurrence"/>)
/// and types its prompt into its thread then.
/// </summary>
public sealed record ScheduledTask(
    string Id, string Kind, string Name, string Prompt, string Status, string RRule, string TargetThreadId,
    string NotificationPolicy, long CreatedAt, long UpdatedAt, IReadOnlyDictionary<string, object> Fields)
{
    public bool Active => Status == "ACTIVE";
    public bool NewChat => Kind == "new_chat";
}

/// <summary>What reframe did with a task: when it last took a run, how many runs, and how the last one went.</summary>
public sealed record TaskRuns(long? LastRunAt, int Runs, string? LastResult, long? LastResultAt);

public static class ScheduledTasks
{
    public const string FileName = "automation.toml";
    public const string RunsFile = "runs.json";
    static readonly string[] Order =
        ["version", "id", "kind", "name", "prompt", "status", "rrule", "target_thread_id", "notification_policy", "created_at", "updated_at"];

    // ---------- the files ----------

    /// <summary>The flat <c>key = value</c> TOML Codex and reframe write (strings, integers, booleans); null when a
    /// line is anything else.</summary>
    public static Dictionary<string, object>? ParseToml(string text)
    {
        var fields = new Dictionary<string, object>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) return null;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Length == 0 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return null;
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                if (Unescape(value[1..^1]) is not { } s) return null;
                fields[key] = s;
            }
            else if (value is "true" or "false") fields[key] = value == "true";
            else if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) fields[key] = n;
            else return null;
        }
        return fields;
    }

    static string? Unescape(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            if (++i >= text.Length) return null;
            switch (text[i])
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'u' when i + 4 < text.Length && int.TryParse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                    sb.Append((char)code);
                    i += 4;
                    break;
                default: return null;
            }
        }
        return sb.ToString();
    }

    static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 2);
        foreach (var c in text)
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when c < 0x20 || c == 0x7f => $"\\u{(int)c:X4}",
                _ => c.ToString(),
            });
        return sb.ToString();
    }

    public static string WriteToml(IReadOnlyDictionary<string, object> fields)
    {
        var sb = new StringBuilder();
        foreach (var key in Order.Where(fields.ContainsKey).Concat(fields.Keys.Where(k => !Order.Contains(k)).Order(StringComparer.Ordinal)))
        {
            var value = fields[key] switch
            {
                string s => $"\"{Escape(s)}\"",
                bool b => b ? "true" : "false",
                long n => n.ToString(CultureInfo.InvariantCulture),
                int n => n.ToString(CultureInfo.InvariantCulture),
                var other => $"\"{Escape(other.ToString() ?? "")}\"",
            };
            sb.Append(key).Append(" = ").Append(value).Append('\n');
        }
        return sb.ToString();
    }

    public static ScheduledTask? FromFields(IReadOnlyDictionary<string, object> f)
    {
        string S(string key) => f.TryGetValue(key, out var v) && v is string s ? s : "";
        long L(string key) => f.TryGetValue(key, out var v) && v is long n ? n : 0;
        if (S("id") is not { Length: > 0 } id || S("prompt").Length == 0 || S("rrule").Length == 0) return null;
        return new ScheduledTask(id, S("kind") is { Length: > 0 } k ? k : "heartbeat", S("name") is { Length: > 0 } n ? n : id, S("prompt"),
            S("status") is { Length: > 0 } st ? st : "ACTIVE", S("rrule"), S("target_thread_id"),
            S("notification_policy") is { Length: > 0 } np ? np : "important", L("created_at"), L("updated_at"), f);
    }

    /// <summary>The tasks under <paramref name="dir"/> (unreadable ones left out).</summary>
    public static List<ScheduledTask> Load(string dir)
    {
        var tasks = new List<ScheduledTask>();
        if (!Directory.Exists(dir)) return tasks;
        foreach (var folder in Directory.EnumerateDirectories(dir))
        {
            try
            {
                var file = Path.Combine(folder, FileName);
                if (File.Exists(file) && ParseToml(File.ReadAllText(file)) is { } fields && FromFields(fields) is { } task) tasks.Add(task);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return tasks.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Saves a task's fields (written beside, then moved over the old file).</summary>
    public static void Save(string dir, IReadOnlyDictionary<string, object> fields)
    {
        var folder = Path.Combine(dir, (string)fields["id"]);
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, FileName + ".tmp");
        File.WriteAllText(temp, WriteToml(fields), new UTF8Encoding(false));
        File.Move(temp, Path.Combine(folder, FileName), true);
    }

    /// <summary>The task changed: some fields replaced and updated_at now.</summary>
    public static void Update(string dir, ScheduledTask task, IReadOnlyDictionary<string, object> changes, long now)
    {
        var fields = new Dictionary<string, object>(task.Fields);
        foreach (var (k, v) in changes) fields[k] = v;
        fields["updated_at"] = now;
        Save(dir, fields);
    }

    public static void Delete(string dir, string id)
    {
        if (!ValidId(id)) return;
        var folder = Path.Combine(dir, id);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    public static bool ValidId(string id) =>
        id.Length is > 0 and <= 80 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static TaskRuns ReadRuns(string dir, string id)
    {
        try
        {
            var file = Path.Combine(dir, id, RunsFile);
            if (!File.Exists(file)) return new TaskRuns(null, 0, null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var r = doc.RootElement;
            long? Long(string k) => r.TryGetProperty(k, out var v) && v.TryGetInt64(out var n) ? n : null;
            return new TaskRuns(Long("lastRunAt"), (int)(Long("runs") ?? 0),
                r.TryGetProperty("lastResult", out var res) && res.ValueKind == JsonValueKind.String ? res.GetString() : null, Long("lastResultAt"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new TaskRuns(null, 0, null, null);
        }
    }

    public static void WriteRuns(string dir, string id, TaskRuns runs)
    {
        var folder = Path.Combine(dir, id);
        if (!Directory.Exists(folder)) return;
        File.WriteAllText(Path.Combine(folder, RunsFile), JsonSerializer.Serialize(new
        {
            lastRunAt = runs.LastRunAt, runs = runs.Runs, lastResult = runs.LastResult, lastResultAt = runs.LastResultAt,
        }));
    }

    // ---------- when ----------

    /// <summary>A few seconds after the minute, the same for a task each time, so that tasks due together do not
    /// all start at once.</summary>
    public static int JitterSeconds(string id) => SHA256.HashData(Encoding.UTF8.GetBytes(id))[0] % 45;

    /// <summary>When the task is due next: the first time of its rule after its last run (or after it was made),
    /// plus its jitter. A run missed while reframe was closed is due at once, and only once. Null: never again.</summary>
    public static DateTimeOffset? NextDue(ScheduledTask task, TaskRuns runs)
    {
        if (Recurrence.Parse(task.RRule) is not { } rule) return null;
        var created = DateTimeOffset.FromUnixTimeMilliseconds(task.CreatedAt > 0 ? task.CreatedAt : task.UpdatedAt);
        var after = runs.LastRunAt is { } last ? DateTimeOffset.FromUnixTimeMilliseconds(last) : created;
        return rule.Next(created, after) is { } next ? next.AddSeconds(JitterSeconds(task.Id)) : null;
    }

    /// <summary>What the thread is told at a run: which task it is, then its prompt.</summary>
    public static string RunText(ScheduledTask task) => $"[Reframe scheduled task: {task.Name} ({task.Id})]\n{task.Prompt}";

    /// <summary>A readable form of the common rules ("Mondays at 09:15"), else the rule itself.</summary>
    public static string Describe(string rrule)
    {
        if (Recurrence.Parse(rrule) is not { } r) return rrule;
        string Time() => r.ByHour.Length == 1 ? $" at {r.ByHour[0]:00}:{(r.ByMinute.Length == 1 ? r.ByMinute[0] : 0):00}" : "";
        var every = r.Interval == 1 ? "" : $" {r.Interval}";
        var text = r.Freq switch
        {
            "MINUTELY" => $"Every{every} minutes",
            "HOURLY" => r.Interval == 1 ? "Hourly" : $"Every {r.Interval} hours",
            "DAILY" when r.ByDay.Length == 0 => (r.Interval == 1 ? "Daily" : $"Every {r.Interval} days") + Time(),
            "WEEKLY" or "DAILY" when r.ByDay.Length > 0 => string.Join(", ", r.ByDay.Select(d => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(d)))
                + (r.Interval == 1 ? "" : $", every {r.Interval} weeks") + Time(),
            "WEEKLY" => (r.Interval == 1 ? "Weekly" : $"Every {r.Interval} weeks") + Time(),
            "MONTHLY" => (r.ByMonthDay.Length > 0 ? $"Monthly on day {string.Join(", ", r.ByMonthDay)}" : "Monthly") + Time(),
            _ => rrule,
        };
        return r.Count is { } c ? $"{text} ({c} times)" : text;
    }

    // ---------- Codex ----------

    /// <summary>A Codex automation that can come over: its target thread's name is the name of one of reframe's.</summary>
    public sealed record CodexCandidate(ScheduledTask Task, string ThreadName, string SessionId);

    /// <summary>Codex's thread names (session_index.jsonl: the newest line of each id wins).</summary>
    public static Dictionary<string, string> CodexThreadNames(IEnumerable<string> indexLines)
    {
        var names = new Dictionary<string, string>();
        foreach (var line in indexLines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    && doc.RootElement.TryGetProperty("thread_name", out var name) && name.ValueKind == JsonValueKind.String
                    && name.GetString() is { Length: > 0 } n)
                    names[id.GetString()!] = n;
            }
            catch (JsonException) { }
        }
        return names;
    }

    /// <summary>The Codex tasks whose thread has the name of one of reframe's threads (<paramref name="sessions"/>: id
    /// and shown name), less those imported already.</summary>
    public static List<CodexCandidate> CodexCandidates(IEnumerable<ScheduledTask> codex, IReadOnlyDictionary<string, string> codexNames,
        IEnumerable<(string Id, string Name)> sessions, IEnumerable<ScheduledTask> own)
    {
        var byName = sessions.Where(s => s.Name.Trim().Length > 0)
            .GroupBy(s => s.Name.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.CurrentCultureIgnoreCase);
        var imported = own.Select(t => t.Fields.TryGetValue("imported_from", out var v) ? v as string : null).Where(v => v is not null).ToHashSet();
        return codex
            .Where(t => !imported.Contains("codex:" + t.Id))
            .Select(t => codexNames.TryGetValue(t.TargetThreadId, out var name) && byName.TryGetValue(name.Trim(), out var id)
                ? new CodexCandidate(t, name, id) : null)
            .OfType<CodexCandidate>().ToList();
    }

    /// <summary>The fields of a Codex task brought over to reframe's thread (a new id if its own is taken).</summary>
    public static Dictionary<string, object> Imported(CodexCandidate candidate, ISet<string> takenIds, long now)
    {
        var fields = new Dictionary<string, object>(candidate.Task.Fields)
        {
            ["target_thread_id"] = candidate.SessionId,
            ["imported_from"] = "codex:" + candidate.Task.Id,
            ["updated_at"] = now,
        };
        var id = candidate.Task.Id;
        for (var n = 2; takenIds.Contains(id); n++) id = $"{candidate.Task.Id}-{n}";
        fields["id"] = id;
        if (!fields.ContainsKey("created_at")) fields["created_at"] = now;
        return fields;
    }

    // ---------- waking reframe ----------

    /// <summary>The Task Scheduler task that starts reframe (<c>--autostart</c>: in the tray, sessions resumed) when
    /// the next task is due while it is closed: one start at <paramref name="at"/> (or as soon as the user is logged on
    /// after it), never a second copy, no time limit (the action is reframe itself, which keeps running).</summary>
    public static string WakeTaskXml(string executable, DateTime at, string userId) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Starts Reframe in the tray for its scheduled tasks (Settings: Wake Reframe for scheduled tasks). Removed while Reframe runs.</Description>
          </RegistrationInfo>
          <Triggers>
            <TimeTrigger>
              <StartBoundary>{at.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}</StartBoundary>
              <Enabled>true</Enabled>
            </TimeTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{System.Security.SecurityElement.Escape(userId)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>LeastPrivilege</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <StartWhenAvailable>true</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{System.Security.SecurityElement.Escape(executable)}</Command>
              <Arguments>{Autostart.Argument}</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    /// <summary>The wake-up task's name: one per profile, as the login entry's (<see cref="Autostart.ValueName"/>).</summary>
    public static string WakeTaskName(string profileDir) => Autostart.ValueName(profileDir).Replace("Reframe Shell", "Reframe wake-up");
}

/// <summary>
/// The part of RFC 5545 recurrence rules the scheduler follows (what Codex's automations and the thread tools
/// write): FREQ MINUTELY to MONTHLY, INTERVAL, COUNT, UNTIL, BYDAY (plain days), BYMONTHDAY, BYHOUR, BYMINUTE,
/// BYSECOND, and an optional DTSTART line with TZID. Times are in that zone, else the local one; what a rule leaves
/// open comes from its start (DTSTART, else when the task was made).
/// </summary>
public sealed record Recurrence(string Freq, int Interval, int? Count, DateTime? Until, int[] ByHour, int[] ByMinute, int[] BySecond,
    DayOfWeek[] ByDay, int[] ByMonthDay, DateTime? Start, TimeZoneInfo Zone)
{
    static readonly Dictionary<string, DayOfWeek> Days = new()
    {
        ["MO"] = DayOfWeek.Monday, ["TU"] = DayOfWeek.Tuesday, ["WE"] = DayOfWeek.Wednesday, ["TH"] = DayOfWeek.Thursday,
        ["FR"] = DayOfWeek.Friday, ["SA"] = DayOfWeek.Saturday, ["SU"] = DayOfWeek.Sunday,
    };

    public static Recurrence? Parse(string text, TimeZoneInfo? local = null)
    {
        var zone = local ?? TimeZoneInfo.Local;
        DateTime? start = null;
        var lines = text.Replace("\\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (lines.Count > 0 && lines[0].StartsWith("DTSTART", StringComparison.Ordinal))
        {
            var line = lines[0];
            lines.RemoveAt(0);
            var colon = line.LastIndexOf(':');
            if (colon < 0) return null;
            var head = line[..colon];
            if (head.IndexOf("TZID=", StringComparison.Ordinal) is var tz and >= 0)
            {
                if (!TimeZoneInfo.TryFindSystemTimeZoneById(head[(tz + 5)..], out var found)) return null;
                zone = found;
            }
            var value = line[(colon + 1)..];
            var utc = value.EndsWith('Z');
            if (!DateTime.TryParseExact(value.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var s)) return null;
            start = utc ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(s, DateTimeKind.Utc), zone) : s;
        }
        if (lines.Count != 1) return null;
        var rule = lines[0].StartsWith("RRULE:", StringComparison.Ordinal) ? lines[0][6..] : lines[0];
        string? freq = null;
        int interval = 1;
        int? count = null;
        DateTime? until = null;
        int[] hours = [], minutes = [], seconds = [], monthDays = [];
        DayOfWeek[] days = [];
        foreach (var part in rule.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) return null;
            var (key, value) = (part[..eq], part[(eq + 1)..]);
            int[]? Numbers(int min, int max)
            {
                var list = new List<int>();
                foreach (var n in value.Split(','))
                {
                    if (!int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var x) || x < min || x > max) return null;
                    list.Add(x);
                }
                return [.. list.Distinct().Order()];
            }
            switch (key)
            {
                case "FREQ" when value is "MINUTELY" or "HOURLY" or "DAILY" or "WEEKLY" or "MONTHLY": freq = value; break;
                case "INTERVAL" when Numbers(1, 1000) is [var everyN]: interval = everyN; break;
                case "COUNT" when Numbers(1, 100_000) is [var times]: count = times; break;
                case "BYHOUR" when Numbers(0, 23) is { } byHour: hours = byHour; break;
                case "BYMINUTE" when Numbers(0, 59) is { } byMinute: minutes = byMinute; break;
                case "BYSECOND" when Numbers(0, 59) is { } bySecond: seconds = bySecond; break;
                case "BYMONTHDAY" when Numbers(1, 31) is { } byMonthDay: monthDays = byMonthDay; break;
                case "BYDAY" when value.Split(',').All(Days.ContainsKey): days = [.. value.Split(',').Select(name => Days[name]).Distinct()]; break;
                case "WKST" when value == "MO": break;
                case "UNTIL":
                    var v = value.TrimEnd('Z');
                    if (DateTime.TryParseExact(v, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var untilTime)) until = value.EndsWith('Z')
                        ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(untilTime, DateTimeKind.Utc), zone) : untilTime;
                    else if (DateTime.TryParseExact(v, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var untilDay)) until = untilDay.AddDays(1).AddTicks(-1);
                    else return null;
                    break;
                default: return null;
            }
        }
        if (freq is null || (freq == "MINUTELY" && interval < 5)) return null;
        return new Recurrence(freq, interval, count, until, hours, minutes, seconds, days, monthDays, start, zone);
    }

    /// <summary>The first time of the rule after <paramref name="after"/>; <paramref name="made"/> is its start when the
    /// rule has no DTSTART. Null when the rule has ended (COUNT, UNTIL) or nothing comes within ten years.</summary>
    public DateTimeOffset? Next(DateTimeOffset made, DateTimeOffset after)
    {
        var start = Start ?? Truncate(TimeZoneInfo.ConvertTime(made, Zone).DateTime);
        var afterLocal = TimeZoneInfo.ConvertTime(after, Zone).DateTime;
        var limit = (afterLocal > start ? afterLocal : start).AddYears(10);
        var seen = 0;
        foreach (var time in Times(start, limit))
        {
            if (time > limit || (Until is { } u && time > u)) return null;
            if (Zone.IsInvalidTime(time)) continue;
            seen++;
            if (Count is { } c && seen > c) return null;
            if (time > afterLocal) return new DateTimeOffset(time, Zone.GetUtcOffset(time));
        }
        return null;
    }

    static DateTime Truncate(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, DateTimeKind.Unspecified);

    /// <summary>The rule's times from its start on, in order (local to the zone).</summary>
    IEnumerable<DateTime> Times(DateTime start, DateTime limit)
    {
        int[] Or(int[] given, int fallback) => given.Length > 0 ? given : [fallback];
        var seconds = Or(BySecond, start.Second);
        var minutes = Or(ByMinute, start.Minute);
        var hours = Or(ByHour, start.Hour);
        bool DayFits(DateTime d) => (ByDay.Length == 0 || ByDay.Contains(d.DayOfWeek)) && (ByMonthDay.Length == 0 || ByMonthDay.Contains(d.Day));
        IEnumerable<DateTime> OnDay(DateTime day) =>
            from h in hours from m in minutes from s in seconds select day.Date.AddHours(h).AddMinutes(m).AddSeconds(s);

        switch (Freq)
        {
            case "MINUTELY":
            case "HOURLY":
                {
                    var step = Freq == "MINUTELY" ? TimeSpan.FromMinutes(Interval) : TimeSpan.FromHours(Interval);
                    var period = Freq == "MINUTELY"
                        ? new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0)
                        : new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0);
                    for (; period <= limit; period += step)
                    {
                        if (!DayFits(period) || (ByHour.Length > 0 && !ByHour.Contains(period.Hour))) continue;
                        var at = period;
                        IEnumerable<DateTime> times = Freq == "MINUTELY"
                            ? (ByMinute.Length > 0 && !ByMinute.Contains(at.Minute) ? Enumerable.Empty<DateTime>() : seconds.Select(sec => at.AddSeconds(sec)))
                            : from min in minutes from sec in seconds select at.AddMinutes(min).AddSeconds(sec);
                        foreach (var t in times)
                            if (t >= start) yield return t;
                    }
                    yield break;
                }
            case "DAILY":
                for (var day = start.Date; day <= limit; day = day.AddDays(Interval))
                    if (DayFits(day))
                        foreach (var t in OnDay(day))
                            if (t >= start) yield return t;
                yield break;
            case "WEEKLY":
                {
                    var weekdays = ByDay.Length > 0 ? ByDay : [start.DayOfWeek];
                    var monday = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7));
                    for (var week = monday; week <= limit; week = week.AddDays(7 * Interval))
                        for (var i = 0; i < 7; i++)
                        {
                            var day = week.AddDays(i);
                            if (!weekdays.Contains(day.DayOfWeek) || (ByMonthDay.Length > 0 && !ByMonthDay.Contains(day.Day))) continue;
                            foreach (var t in OnDay(day))
                                if (t >= start) yield return t;
                        }
                    yield break;
                }
            default: // MONTHLY
                {
                    var monthDays = ByMonthDay.Length > 0 || ByDay.Length > 0 ? ByMonthDay : [start.Day];
                    for (var month = new DateTime(start.Year, start.Month, 1); month <= limit; month = month.AddMonths(Interval))
                        for (var d = 1; d <= DateTime.DaysInMonth(month.Year, month.Month); d++)
                        {
                            var day = month.AddDays(d - 1);
                            if ((monthDays.Length > 0 && !monthDays.Contains(d)) || (ByDay.Length > 0 && !ByDay.Contains(day.DayOfWeek))) continue;
                            foreach (var t in OnDay(day))
                                if (t >= start) yield return t;
                        }
                    yield break;
                }
        }
    }
}
