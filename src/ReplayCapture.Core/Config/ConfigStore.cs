using System.Text.Json;
using System.Text.Json.Serialization;
using ReplayCapture.Core.Diagnostics;

namespace ReplayCapture.Core.Config;

/// <summary>Loads and saves <see cref="AppConfig"/> as JSON under %APPDATA%\ReplayCapture.</summary>
public sealed class ConfigStore
{
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReplayCapture");

    public static string FilePath { get; } = Path.Combine(DirectoryPath, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _writeLock = new();

    public AppConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                var fresh = new AppConfig();
                Save(fresh);
                Log.Info($"No config found; wrote defaults to {FilePath}");
                return fresh;
            }

            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Options)
                         ?? new AppConfig();
            Validate(config);
            return config;
        }
        catch (Exception ex)
        {
            // A corrupt config must never stop the buffer from running — keep the bad file for
            // inspection and carry on with defaults.
            Log.Error($"Failed to read {FilePath}, falling back to defaults", ex);
            TryQuarantine();
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        lock (_writeLock)
        {
            Directory.CreateDirectory(DirectoryPath);
            // Write-then-replace so a crash mid-write cannot leave a truncated config behind.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(config, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
    }

    private static void Validate(AppConfig config)
    {
        foreach (var track in config.AudioTracks)
        foreach (var source in track.Sources)
        {
            if (!AudioSourceSpec.TryParse(source, out _, out var error))
                Log.Warn($"Audio track '{track.Name}' has an unusable source '{source}': {error}");
        }

        if (config.BufferSeconds is < 5 or > 600)
            Log.Warn($"bufferSeconds={config.BufferSeconds} is outside the supported 5-600 range.");
    }

    private static void TryQuarantine()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Move(FilePath, $"{FilePath}.bad-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not quarantine the unreadable config: {ex.Message}");
        }
    }
}
