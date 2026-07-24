using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreqScene.Remote.Client;

public sealed class ServerPairing
{
    public string ServerId { get; set; } = "";

    public string ServerName { get; set; } = "";

    public string Host { get; set; } = "";

    public string Token { get; set; } = "";
}

[JsonSerializable(typeof(List<ServerPairing>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class PairingJsonContext : JsonSerializerContext;

public class PairingStore(string filePath)
{
    private readonly Lock _gate = new();
    private List<ServerPairing>? _entries;

    protected PairingStore()
        : this("")
    {
    }

    public ServerPairing? FindByServerId(string serverId)
    {
        lock (_gate)
        {
            return Load().FirstOrDefault(e => e.ServerId == serverId);
        }
    }

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

    protected virtual string? ReadPayload() =>
        File.Exists(filePath) ? File.ReadAllText(filePath) : null;

    protected virtual void WritePayload(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, json);
    }

    private List<ServerPairing> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        try
        {
            if (ReadPayload() is { } json)
            {
                _entries = JsonSerializer.Deserialize(json, PairingJsonContext.Default.ListServerPairing);
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
            WritePayload(JsonSerializer.Serialize(entries, PairingJsonContext.Default.ListServerPairing));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
