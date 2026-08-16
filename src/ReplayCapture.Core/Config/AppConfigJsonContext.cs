using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReplayCapture.Core.Config;

/// <summary>
/// Source-generated serialization for <see cref="AppConfig"/> and everything it composes, so
/// <see cref="ConfigStore"/> never falls back to reflection-based <c>JsonSerializer</c> — required
/// for Native AOT, where reflection-based (de)serialization is unsupported.
/// </summary>
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(DisplayConfig))]
[JsonSerializable(typeof(AudioTrackConfig))]
[JsonSerializable(typeof(IReadOnlyList<DisplayConfig>))]
[JsonSerializable(typeof(IReadOnlyList<AudioTrackConfig>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, IReadOnlyList<string>>))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
internal sealed partial class AppConfigJsonContext : JsonSerializerContext;
