using System.Text.Json.Serialization;

namespace FreqScene;

[JsonSerializable(typeof(PlaylistState))]
[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class PlaylistJsonContext : JsonSerializerContext;
