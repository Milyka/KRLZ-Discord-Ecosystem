using System.Text.RegularExpressions;

namespace Bot.Infrastructure;

public static partial class TimeParser
{
    [GeneratedRegex(@"^(?<val>\d+)(?<unit>[smhdwмчднc])$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DurationRegex();

    public static bool TryParseDuration(string? input, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var match = DurationRegex().Match(input.Trim());
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups["val"].Value, out var value) || value <= 0)
            return false;

        var unit = match.Groups["unit"].Value.ToLowerInvariant();
        duration = unit switch
        {
            "s" or "c" => TimeSpan.FromSeconds(value),
            "m" or "м" => TimeSpan.FromMinutes(value),
            "h" or "ч" => TimeSpan.FromHours(value),
            "d" or "д" => TimeSpan.FromDays(value),
            "w" or "н" => TimeSpan.FromDays(value * 7),
            _ => TimeSpan.Zero
        };

        return duration > TimeSpan.Zero && duration <= TimeSpan.FromDays(365);
    }

    public static string FormatDuration(TimeSpan span)
    {
        var parts = new List<string>();
        if (span.Days > 0)
            parts.Add($"{span.Days} д.");
        if (span.Hours > 0)
            parts.Add($"{span.Hours} ч.");
        if (span.Minutes > 0)
            parts.Add($"{span.Minutes} мин.");
        if (span.Seconds > 0 && parts.Count == 0)
            parts.Add($"{span.Seconds} сек.");

        return parts.Count > 0 ? string.Join(' ', parts) : "0 сек.";
    }

    public static string FormatVoiceSeconds(long totalSeconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        var hours = (int)span.TotalHours;
        return $"{hours} ч. {span.Minutes} мин.";
    }
}
