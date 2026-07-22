using System.Net;
using System.Net.Sockets;
using Foundation;
using FreqScene.Remote;

namespace FreqScene.iOS;

/// <summary>Resolves a browsed _freqscene._tcp service to the http address the gRPC channel dials.</summary>
public static class BonjourResolver
{
    public static Task<Uri?> ResolveAsync(NSNetService service, double timeoutSeconds = 5)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnResolved(object? sender, EventArgs e) => completion.TrySetResult(true);
        void OnFailed(object? sender, NSNetServiceErrorEventArgs e) => completion.TrySetResult(false);

        service.AddressResolved += OnResolved;
        service.ResolveFailure += OnFailed;
        service.Resolve(timeoutSeconds);

        return completion.Task.ContinueWith(task =>
        {
            service.AddressResolved -= OnResolved;
            service.ResolveFailure -= OnFailed;
            return task.Result ? SelectReachableAsync(service) : Task.FromResult<Uri?>(null);
        }).Unwrap();
    }

    /// <summary>Re-resolves a server by its Bonjour instance name, for reconnect after an address change.</summary>
    public static async Task<Uri?> ResolveByNameAsync(string serviceName, CancellationToken cancellationToken)
    {
        var resolve = MainThread(async () =>
        {
            using var service = new NSNetService("local.", RemoteProtocol.BonjourServiceType, serviceName);
            return await ResolveAsync(service).ConfigureAwait(false);
        });

        try
        {
            return await resolve.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<Uri?> SelectReachableAsync(NSNetService service)
    {
        var port = (int)service.Port;
        if (port <= 0)
        {
            return null;
        }

        var candidates = CollectAddresses(service);
        if (await ProbeAsync(candidates, port).ConfigureAwait(false) is { } reachable)
        {
            return MakeUri(reachable, port);
        }

        if (candidates.Count > 0)
        {
            return MakeUri(candidates[0], port);
        }

        var hostName = service.HostName;
        return string.IsNullOrEmpty(hostName) ? null : new Uri($"http://{hostName.TrimEnd('.')}:{port}");
    }

    private static List<IPAddress> CollectAddresses(NSNetService service)
    {
        var candidates = new List<IPAddress>();
        var v6 = new List<IPAddress>();
        foreach (var data in service.Addresses ?? [])
        {
            // BSD sockaddr layout: [0]=length, [1]=family, [2..3]=port (big-endian), then the address.
            var bytes = data.ToArray();
            if (bytes.Length >= 8 && bytes[1] == 2 /* AF_INET */)
            {
                var candidate = new IPAddress(bytes.AsSpan(4, 4));
                if (!candidates.Contains(candidate))
                {
                    candidates.Add(candidate);
                }
            }
            else if (bytes.Length >= 24 && bytes[1] == 30 /* AF_INET6 (Darwin) */)
            {
                var candidate = new IPAddress(bytes.AsSpan(8, 16));
                if (!candidate.IsIPv6LinkLocal && !v6.Contains(candidate))
                {
                    v6.Add(candidate);
                }
            }
        }

        candidates.AddRange(v6);
        return candidates;
    }

    private static async Task<IPAddress?> ProbeAsync(List<IPAddress> candidates, int port)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var attempts = candidates.Select(async address =>
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(address, port, cts.Token).ConfigureAwait(false);
            return address;
        }).ToList();

        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(finished);
            try
            {
                var winner = await finished.ConfigureAwait(false);
                await cts.CancelAsync().ConfigureAwait(false);
                return winner;
            }
            catch (Exception)
            {
                // Unreachable candidate; keep racing the rest.
            }
        }

        return null;
    }

    private static Uri MakeUri(IPAddress address, int port)
    {
        var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        return new Uri($"http://{host}:{port}");
    }

    private static Task<Uri?> MainThread(Func<Task<Uri?>> work)
    {
        // NSNetService needs a run loop; the main loop is the only one guaranteed to spin.
        var completion = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CoreFoundation.DispatchQueue.MainQueue.DispatchAsync(async () =>
        {
            try
            {
                completion.TrySetResult(await work().ConfigureAwait(false));
            }
            catch (Exception)
            {
                completion.TrySetResult(null);
            }
        });
        return completion.Task;
    }
}
