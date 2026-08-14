using System.Globalization;
using Discord;

namespace Bot.Infrastructure;

public sealed class EmbedFactory(SettingsProvider settingsProvider)
{
    public Color GetThemeColor(string? customHex = null)
    {
        var hex = string.IsNullOrWhiteSpace(customHex)
            ? settingsProvider.Current.EmbedColorHex
            : customHex;

        return TryParseColor(hex, out var color) ? color : new Color(88, 101, 242);
    }

    public static bool TryParseColor(string hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
            return false;

        var clean = hex.Trim().TrimStart('#');
        if (clean.Length != 6 || !uint.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return false;

        color = new Color(rgb);
        return true;
    }

    public EmbedBuilder CreateBase(string? title = null, string? description = null, Color? colorOverride = null)
    {
        var builder = new EmbedBuilder
        {
            Color = colorOverride ?? GetThemeColor()
        };

        if (!string.IsNullOrWhiteSpace(settingsProvider.Current.FooterText))
        {
            builder.WithFooter(settingsProvider.Current.FooterText);
        }

        if (!string.IsNullOrWhiteSpace(title))
            builder.WithTitle(title);

        if (!string.IsNullOrWhiteSpace(description))
            builder.WithDescription(description);

        return builder;
    }

    public Embed Success(string title, string description) =>
        CreateBase(title, description).Build();

    public Embed Error(string description, string title = "Ошибка выполнения") =>
        CreateBase(title, description, new Color(237, 66, 69)).Build();

    public Embed Warning(string title, string description) =>
        CreateBase(title, description, new Color(254, 231, 92)).Build();

    public Embed Info(string title, string description, Action<EmbedBuilder>? configure = null)
    {
        var builder = CreateBase(title, description);
        configure?.Invoke(builder);
        return builder.Build();
    }
}
