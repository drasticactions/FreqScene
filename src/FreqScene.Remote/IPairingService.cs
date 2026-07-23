using MagicOnion;

namespace FreqScene.Remote;

public interface IPairingService : IService<IPairingService>
{
    UnaryResult<PairingGrant> PairAsync(PairingAttempt attempt);
}
