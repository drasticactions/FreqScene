using System.Diagnostics.CodeAnalysis;
using MagicOnion;
using MagicOnion.Internal;
using MagicOnion.Server.Binder;
using MagicOnion.Server.Hubs;
using MessagePack;

namespace FreqScene.Remote.Server.AotSupport;

public sealed class StaticMagicOnionMethodProvider : IMagicOnionGrpcMethodProvider
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(StreamingHubBase<IVisualizerHub, IVisualizerHubReceiver>))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(VisualizerHub))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PresetService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PairingService))]
    public StaticMagicOnionMethodProvider()
    {
    }

    public void MapAllSupportedServiceTypes(MagicOnionGrpcServiceMappingContext context)
    {
        context.Map<VisualizerHub>();
        context.Map<PresetService>();
        context.Map<PairingService>();
    }

    public IReadOnlyList<IMagicOnionGrpcMethod> GetGrpcMethods<TService>() where TService : class
    {
        if (typeof(TService) == typeof(VisualizerHub))
        {
            return [new MagicOnionStreamingHubConnectMethod<VisualizerHub>(nameof(IVisualizerHub))];
        }

        if (typeof(TService) == typeof(PresetService))
        {
            return
            [
                new MagicOnionUnaryMethod<PresetService, DynamicArgumentTuple<string, string>, PresetPayload,
                    Box<DynamicArgumentTuple<string, string>>, PresetPayload>(
                    nameof(IPresetService), nameof(IPresetService.GetPresetAsync),
                    static (service, _, request) => service.GetPresetAsync(request.Item1, request.Item2)),
            ];
        }

        if (typeof(TService) == typeof(PairingService))
        {
            return
            [
                new MagicOnionUnaryMethod<PairingService, PairingAttempt, PairingGrant, PairingAttempt, PairingGrant>(
                    nameof(IPairingService), nameof(IPairingService.PairAsync),
                    static (service, _, request) => service.PairAsync(request)),
            ];
        }

        return [];
    }

    public IReadOnlyList<IMagicOnionStreamingHubMethod> GetStreamingHubMethods<TService>() where TService : class
    {
        if (typeof(TService) == typeof(VisualizerHub))
        {
            return
            [
                new MagicOnionStreamingHubMethod<VisualizerHub, JoinRequest, SessionSnapshot>(
                    nameof(IVisualizerHub), nameof(IVisualizerHub.JoinAsync),
                    static (service, _, request) => service.JoinAsync(request)),
                new MagicOnionStreamingHubMethod<VisualizerHub, Nil>(
                    nameof(IVisualizerHub), nameof(IVisualizerHub.LeaveAsync),
                    static (service, _, _) => service.LeaveAsync()),
            ];
        }

        return [];
    }
}
