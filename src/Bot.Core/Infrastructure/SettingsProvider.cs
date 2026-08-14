using System.Text.Json;
using Bot.Models;

namespace Bot.Infrastructure;

public sealed class SettingsProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private BotSettings _current;

    public SettingsProvider(string filePath = "settings.json")
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src", "Bot", "settings.json")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Bot", "settings.json")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "settings.json")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "settings.json")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, filePath)),
            Path.GetFullPath(filePath)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                filePath = candidate;
                break;
            }
        }

        _filePath = Path.GetFullPath(filePath);
        _current = LoadOrCreateDefault();
    }

    public BotSettings Current => _current;

    public async Task UpdateAsync(Action<BotSettings> mutate, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            mutate(_current);
            var json = JsonSerializer.Serialize(_current, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json, ct);

            // Mirror to root settings.json if present so both files stay 100% in sync
            var rootSettings = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "settings.json"));
            if (!string.Equals(rootSettings, _filePath, StringComparison.OrdinalIgnoreCase) && File.Exists(rootSettings))
            {
                await File.WriteAllTextAsync(rootSettings, json, ct);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private BotSettings LoadOrCreateDefault()
    {
        if (!File.Exists(_filePath))
        {
            var template = new BotSettings();
            var serialized = JsonSerializer.Serialize(template, JsonOptions);
            File.WriteAllText(_filePath, serialized);
            return template;
        }

        var raw = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize<BotSettings>(raw, JsonOptions) ?? new BotSettings();
    }
}
