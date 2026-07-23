using System.Security.Cryptography;
using System.Text;

namespace FreqScene.Remote.Server;

public enum PairFailure
{
    None,
    WindowClosed,
    WrongPin,
    TooManyAttempts,
}

public sealed class PairingManager(string serverId, string serverName, IEnumerable<PairedDevice>? devices = null)
{
    public static readonly TimeSpan PinLifetime = TimeSpan.FromMinutes(2);

    private const int MaxAttempts = 5;

    private readonly Lock _gate = new();
    private readonly List<PairedDevice> _devices = [.. devices ?? []];
    private string? _pin;
    private DateTime _pinDeadlineUtc;
    private int _attemptsLeft;

    public string ServerId { get; } = serverId;

    public string ServerName { get; set; } = serverName;

    public event Action? DevicesChanged;

    public event Action<PairedDevice>? DevicePaired;

    public IReadOnlyList<PairedDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return [.. _devices];
            }
        }
    }

    public string? ActivePin
    {
        get
        {
            lock (_gate)
            {
                return _pin is not null && DateTime.UtcNow < _pinDeadlineUtc ? _pin : null;
            }
        }
    }

    public DateTime PinDeadlineUtc
    {
        get
        {
            lock (_gate)
            {
                return _pinDeadlineUtc;
            }
        }
    }

    public string BeginPairing()
    {
        lock (_gate)
        {
            _pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            _pinDeadlineUtc = DateTime.UtcNow + PinLifetime;
            _attemptsLeft = MaxAttempts;
            return _pin;
        }
    }

    public void CancelPairing()
    {
        lock (_gate)
        {
            _pin = null;
        }
    }

    public PairingGrant? TryPair(string pin, string clientName, string deviceModel, out PairFailure failure)
    {
        PairedDevice device;
        string token;
        lock (_gate)
        {
            if (_pin is null || DateTime.UtcNow >= _pinDeadlineUtc)
            {
                failure = PairFailure.WindowClosed;
                return null;
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(pin), Encoding.UTF8.GetBytes(_pin)))
            {
                if (--_attemptsLeft <= 0)
                {
                    _pin = null;
                    failure = PairFailure.TooManyAttempts;
                }
                else
                {
                    failure = PairFailure.WrongPin;
                }

                return null;
            }

            _pin = null;
            token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            device = new PairedDevice
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(clientName) ? "Unnamed device" : clientName,
                DeviceModel = deviceModel,
                TokenHash = HashToken(token),
                PairedAt = DateTimeOffset.Now,
            };
            _devices.Add(device);
        }

        failure = PairFailure.None;
        DevicesChanged?.Invoke();
        DevicePaired?.Invoke(device);
        return new PairingGrant
        {
            DeviceId = device.Id,
            Token = token,
            ServerId = ServerId,
            ServerName = ServerName,
        };
    }

    public PairedDevice? ValidateToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var hash = Encoding.UTF8.GetBytes(HashToken(token));
        lock (_gate)
        {
            return _devices.FirstOrDefault(d =>
                CryptographicOperations.FixedTimeEquals(hash, Encoding.UTF8.GetBytes(d.TokenHash)));
        }
    }

    public bool RemoveDevice(string deviceId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _devices.RemoveAll(d => d.Id == deviceId) > 0;
        }

        if (removed)
        {
            DevicesChanged?.Invoke();
        }

        return removed;
    }

    private static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
