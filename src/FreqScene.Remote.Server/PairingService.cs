using Grpc.Core;
using MagicOnion;
using MagicOnion.Server;

namespace FreqScene.Remote.Server;

public sealed class PairingService(PairingManager pairing) : ServiceBase<IPairingService>, IPairingService
{
    public UnaryResult<PairingGrant> PairAsync(PairingAttempt attempt)
    {
        if (attempt.ProtocolVersion != RemoteProtocol.Version)
        {
            throw new ReturnStatusException(
                StatusCode.FailedPrecondition,
                $"Protocol version mismatch: server={RemoteProtocol.Version}, client={attempt.ProtocolVersion}.");
        }

        var grant = pairing.TryPair(attempt.Pin, attempt.ClientName, attempt.DeviceModel, out var failure);
        return grant is not null
            ? UnaryResult.FromResult(grant)
            : throw (failure switch
            {
                PairFailure.WrongPin => new ReturnStatusException(StatusCode.PermissionDenied, "Wrong PIN."),
                PairFailure.TooManyAttempts => new ReturnStatusException(
                    StatusCode.ResourceExhausted, "Too many wrong PINs; start pairing again on the server."),
                _ => new ReturnStatusException(StatusCode.FailedPrecondition, "Pairing is not active on the server."),
            });
    }
}
