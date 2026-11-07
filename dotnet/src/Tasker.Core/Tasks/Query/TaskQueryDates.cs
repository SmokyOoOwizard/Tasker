using System.Globalization;
using System.Text.RegularExpressions;

namespace Tasker.Core.Tasks.Query;

internal static partial class TaskQueryDates
{
    public const string Formats = "\"2026-11-05\", \"2026-11-05T14:30\", \"today\", \"now\", \"-7d\"";

    [GeneratedRegex(@"^(?<base>now|today)?(?<sign>[+-])(?<count>\d{1,6})(?<unit>[mhdw])$|^(?<only>now|today)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Relative();

    public static (long Start, long End)? Interval(string text, DateTimeOffset now)
    {
        var value = text.Trim();
        if (RelativeInstant(value, now, out var moment, out var wholeDay))
            return moment is { } instant ? wholeDay ? Day(instant) : Second(instant) : null;

        if (value.Length == 10 && DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day))
            return (day.Ticks, day.Ticks + TimeSpan.TicksPerDay);

        var zone = value.EndsWith('Z') || value.EndsWith('z') ? value[..^1] : value;
        if (zone.Length == 16 && DateTime.TryParseExact(zone, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var minute))
            return (minute.Ticks, minute.Ticks + TimeSpan.TicksPerMinute);
        if (zone.Length == 19 && DateTime.TryParseExact(zone, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var second))
            return (second.Ticks, second.Ticks + TimeSpan.TicksPerSecond);
        return null;
    }

    public static string? DateOnlyText(string text, DateTimeOffset now)
    {
        if (!RelativeInstant(text.Trim(), now, out var moment, out _))
            return null;
        return moment?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public static bool IsRelative(string text) => Relative().IsMatch(text.Trim());

    private static bool RelativeInstant(string value, DateTimeOffset now, out DateTimeOffset? moment, out bool wholeDay)
    {
        moment = null;
        wholeDay = false;
        var match = Relative().Match(value);
        if (!match.Success)
            return false;

        var origin = match.Groups["only"].Success ? match.Groups["only"].Value : match.Groups["base"].Value;
        var today = origin.Equals("today", StringComparison.OrdinalIgnoreCase);
        var start = today ? new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero) : now.ToUniversalTime();
        if (match.Groups["only"].Success)
        {
            moment = start;
            wholeDay = today;
            return true;
        }

        var unit = char.ToLowerInvariant(match.Groups["unit"].Value[0]);
        if (today && unit is 'm' or 'h')
            return true;
        if (!long.TryParse(match.Groups["count"].Value, CultureInfo.InvariantCulture, out var count))
            return true;
        var span = unit switch
        {
            'm' => TimeSpan.FromMinutes(count),
            'h' => TimeSpan.FromHours(count),
            'd' => TimeSpan.FromDays(count),
            _ => TimeSpan.FromDays(count * 7)
        };
        var signed = match.Groups["sign"].Value == "-" ? -span : span;
        try
        {
            moment = start + signed;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
        wholeDay = today;
        return true;
    }

    private static (long, long) Day(DateTimeOffset moment) => (moment.UtcDateTime.Date.Ticks, moment.UtcDateTime.Date.Ticks + TimeSpan.TicksPerDay);

    private static (long, long) Second(DateTimeOffset moment)
    {
        var start = moment.UtcTicks - moment.UtcTicks % TimeSpan.TicksPerSecond;
        return (start, start + TimeSpan.TicksPerSecond);
    }
}
