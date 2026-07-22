using Android.Content;
using Android.Net.Nsd;
using Android.OS;
using Android.Util;
using FreqScene.Remote;

namespace FreqScene.Android;

public sealed class NsdServerBrowser(Context context) : IDisposable
{
    private readonly NsdManager _nsd = (NsdManager)context.GetSystemService(Context.NsdService)!;
    private readonly Handler _mainHandler = new(Looper.MainLooper!);
    private readonly List<string> _services = [];
    private readonly SemaphoreSlim _resolveGate = new(1, 1);
    private DiscoveryListener? _discovery;

    public event Action? ServicesChanged;

    public IReadOnlyList<string> Services => _services;

    public void StartDiscovery()
    {
        if (_discovery is not null)
        {
            return;
        }

        _discovery = new DiscoveryListener(this);
        _nsd.DiscoverServices(RemoteProtocol.BonjourServiceType, NsdProtocol.DnsSd, _discovery);
    }

    public void StopDiscovery()
    {
        if (_discovery is { } discovery)
        {
            _discovery = null;
            try
            {
                _nsd.StopServiceDiscovery(discovery);
            }
            catch (Exception ex)
            {
                Log.Warn("FreqScene", $"stopServiceDiscovery failed: {ex.Message}");
            }
        }

        _services.Clear();
        ServicesChanged?.Invoke();
    }

    public async Task<Uri?> ResolveAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        var info = new NsdServiceInfo
        {
            ServiceName = serviceName,
            ServiceType = RemoteProtocol.BonjourServiceType,
        };

        await _resolveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _nsd.ResolveService(info, new ResolveListener(completion));
            return await completion.Task
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            _resolveGate.Release();
        }
    }

    public void Dispose() => StopDiscovery();

    private void OnServiceFound(NsdServiceInfo info)
    {
        var name = info.ServiceName;
        _mainHandler.Post(() =>
        {
            if (name is not null && !_services.Contains(name))
            {
                _services.Add(name);
                ServicesChanged?.Invoke();
            }
        });
    }

    private void OnServiceLost(NsdServiceInfo info)
    {
        var name = info.ServiceName;
        _mainHandler.Post(() =>
        {
            if (name is not null && _services.Remove(name))
            {
                ServicesChanged?.Invoke();
            }
        });
    }

    private static Uri? ToUri(NsdServiceInfo? info)
    {
        var host = info?.Host?.HostAddress;
        if (info is null || string.IsNullOrEmpty(host) || info.Port <= 0)
        {
            return null;
        }

        if (host.Contains(':'))
        {
            // IPv6: strip any scope suffix and bracket for the authority.
            var scope = host.IndexOf('%');
            if (scope >= 0)
            {
                host = host[..scope];
            }

            host = $"[{host}]";
        }

        return new Uri($"http://{host}:{info.Port}");
    }

    private sealed class DiscoveryListener(NsdServerBrowser owner) : Java.Lang.Object, NsdManager.IDiscoveryListener
    {
        public void OnDiscoveryStarted(string? serviceType)
        {
        }

        public void OnDiscoveryStopped(string? serviceType)
        {
        }

        public void OnServiceFound(NsdServiceInfo? serviceInfo)
        {
            if (serviceInfo is not null)
            {
                owner.OnServiceFound(serviceInfo);
            }
        }

        public void OnServiceLost(NsdServiceInfo? serviceInfo)
        {
            if (serviceInfo is not null)
            {
                owner.OnServiceLost(serviceInfo);
            }
        }

        public void OnStartDiscoveryFailed(string? serviceType, NsdFailure errorCode) =>
            Log.Warn("FreqScene", $"NSD discovery failed to start: {errorCode}");

        public void OnStopDiscoveryFailed(string? serviceType, NsdFailure errorCode)
        {
        }
    }

    private sealed class ResolveListener(TaskCompletionSource<Uri?> completion) : Java.Lang.Object, NsdManager.IResolveListener
    {
        public void OnServiceResolved(NsdServiceInfo? serviceInfo) =>
            completion.TrySetResult(ToUri(serviceInfo));

        public void OnResolveFailed(NsdServiceInfo? serviceInfo, NsdFailure errorCode) =>
            completion.TrySetResult(null);
    }
}
