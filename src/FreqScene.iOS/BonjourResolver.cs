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
        var completion = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnResolved(object? sender, EventArgs e) => completion.TrySetResult(ToUri(service));
        void OnFailed(object? sender, NSNetServiceErrorEventArgs e) => completion.TrySetResult(null);

        service.AddressResolved += OnResolved;
        service.ResolveFailure += OnFailed;
        service.Resolve(timeoutSeconds);

        return completion.Task.ContinueWith(task =>
        {
            service.AddressResolved -= OnResolved;
            service.ResolveFailure -= OnFailed;
            return task.Result;
        });
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

    private static Uri? ToUri(NSNetService service)
    {
        var port = (int)service.Port;
        if (port <= 0)
        {
            return null;
        }

        // Prefer the numeric address Bonjour already resolved; dialing the .local hostname
        // again through the managed socket stack is a second, independent point of failure.
        if (PickAddress(service) is { } address)
        {
            var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
            return new Uri($"http://{host}:{port}");
        }

        var hostName = service.HostName;
        return string.IsNullOrEmpty(hostName) ? null : new Uri($"http://{hostName.TrimEnd('.')}:{port}");
    }

    private static IPAddress? PickAddress(NSNetService service)
    {
        if (service.Addresses is not { } addresses)
        {
            return null;
        }

        IPAddress? v6 = null;
        foreach (var data in addresses)
        {
            // BSD sockaddr layout: [0]=length, [1]=family, [2..3]=port (big-endian), then the address.
            var bytes = data.ToArray();
            if (bytes.Length >= 8 && bytes[1] == 2 /* AF_INET */)
            {
                return new IPAddress(bytes.AsSpan(4, 4));
            }

            if (bytes.Length >= 24 && bytes[1] == 30 /* AF_INET6 (Darwin) */)
            {
                var candidate = new IPAddress(bytes.AsSpan(8, 16));
                if (!candidate.IsIPv6LinkLocal)
                {
                    v6 ??= candidate;
                }
            }
        }

        return v6;
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
