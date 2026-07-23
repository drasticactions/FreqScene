using Grpc.Core;
using Grpc.Net.Client;
using MagicOnion.Client;

namespace FreqScene.Remote.Client;

public enum PairingFailureReason
{
    /// <summary>No pairing window is open on the server.</summary>
    WindowClosed,
    WrongPin,
    TooManyAttempts,
    VersionMismatch,
    Unreachable,
}

public sealed class PairingException(PairingFailureReason reason, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public PairingFailureReason Reason { get; } = reason;
}

public static class PairingClient
{
    /// <summary>Redeems the PIN shown on the server for a persistent per-device token.</summary>
    public static async Task<PairingGrant> PairAsync(
        Uri address, string pin, string clientName, string deviceModel, CancellationToken ct = default)
    {
        RemoteClientAotSupport.EnsureInitialized();
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) },
        });
        var service = MagicOnionClient.Create<IPairingService>(channel);
        try
        {
            return await service.PairAsync(new PairingAttempt
            {
                Pin = pin,
                ClientName = clientName,
                DeviceModel = deviceModel,
            }).ResponseAsync.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            throw new PairingException(ex.StatusCode switch
            {
                StatusCode.PermissionDenied => PairingFailureReason.WrongPin,
                StatusCode.ResourceExhausted => PairingFailureReason.TooManyAttempts,
                StatusCode.FailedPrecondition when ex.Status.Detail.Contains("version", StringComparison.OrdinalIgnoreCase)
                    => PairingFailureReason.VersionMismatch,
                StatusCode.FailedPrecondition => PairingFailureReason.WindowClosed,
                _ => PairingFailureReason.Unreachable,
            }, string.IsNullOrEmpty(ex.Status.Detail) ? "Could not reach the server." : ex.Status.Detail, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new PairingException(PairingFailureReason.Unreachable, "Could not reach the server.", ex);
        }
    }
}
