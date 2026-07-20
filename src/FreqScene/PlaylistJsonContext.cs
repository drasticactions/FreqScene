using System.Text.Json.Serialization;

namespace FreqScene;

[JsonSerializable(typeof(PlaylistState))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class PlaylistJsonContext : JsonSerializerContext;
