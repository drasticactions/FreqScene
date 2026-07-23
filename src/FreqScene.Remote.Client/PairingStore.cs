using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreqScene.Remote.Client;

public sealed class ServerPairing
{
    public string ServerId { get; set; } = "";

    public string ServerName { get; set; } = "";

    /// <summary>Last host the server was reached at; a fallback match when it was renamed.</summary>
    public string Host { get; set; } = "";

    public string Token { get; set; } = "";
}

[JsonSerializable(typeof(List<ServerPairing>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class PairingJsonContext : JsonSerializerContext;

/// <summary>Per-server pairing tokens, persisted as plain JSON next to the head's other state.</summary>
public sealed class PairingStore(string filePath)
{
    private readonly Lock _gate = new();
    private List<ServerPairing>? _entries;

    public ServerPairing? FindByServerId(string serverId)
    {
        lock (_gate)
        {
            return Load().FirstOrDefault(e => e.ServerId == serverId);
        }
    }

    /// <summary>Best-effort lookup before the server's id is known: by name, then by host.</summary>
    public ServerPairing? Find(string? serverName, string? host)
    {
        lock (_gate)
        {
            var entries = Load();
            return entries.FirstOrDefault(e =>
                    serverName is not null &&
                    string.Equals(e.ServerName, serverName, StringComparison.OrdinalIgnoreCase))
                ?? entries.FirstOrDefault(e =>
                    host is not null && string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Upsert(ServerPairing pairing)
    {
        lock (_gate)
        {
            var entries = Load();
            entries.RemoveAll(e => e.ServerId == pairing.ServerId);
            entries.Add(pairing);
            Save(entries);
        }
    }

    public void Remove(string serverId)
    {
        lock (_gate)
        {
            var entries = Load();
            if (entries.RemoveAll(e => e.ServerId == serverId) > 0)
            {
                Save(entries);
            }
        }
    }

    private List<ServerPairing> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        try
        {
            if (File.Exists(filePath))
            {
                using var stream = File.OpenRead(filePath);
                _entries = JsonSerializer.Deserialize(stream, PairingJsonContext.Default.ListServerPairing);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt store only costs a re-pair; never fail the head over it.
        }

        return _entries ??= [];
    }

    private void Save(List<ServerPairing> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            using var stream = File.Create(filePath);
            JsonSerializer.Serialize(stream, entries, PairingJsonContext.Default.ListServerPairing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
