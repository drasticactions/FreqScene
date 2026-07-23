using System.Collections.Immutable;
using System.Text;
using Cysharp.Runtime.Multicast.InMemory;
using Cysharp.Runtime.Multicast.Remoting;

namespace FreqScene.Remote.Server.AotSupport;

public sealed class StaticVisualizerHubProxyFactory : IInMemoryProxyFactory, IRemoteProxyFactory
{
    public static StaticVisualizerHubProxyFactory Instance { get; } = new();

    public T Create<TKey, T>(IReceiverHolder<TKey, T> receivers, ImmutableArray<TKey> excludes, ImmutableArray<TKey>? targets)
        where TKey : IEquatable<TKey>
        => typeof(T) == typeof(IVisualizerHubReceiver)
            ? (T)(object)new InMemoryReceiverProxy<TKey>(
                (IReceiverHolder<TKey, IVisualizerHubReceiver>)(object)receivers, excludes, targets)
            : throw new NotSupportedException($"No static in-memory proxy for receiver type '{typeof(T)}'.");

    public T Create<T>(IRemoteReceiverWriter receiver, IRemoteSerializer serializer)
        => typeof(T) == typeof(IVisualizerHubReceiver)
            ? (T)(object)new RemoteReceiverProxy(receiver, serializer)
            : throw new NotSupportedException($"No static remote proxy for receiver type '{typeof(T)}'.");

    private sealed class InMemoryReceiverProxy<TKey>(
        IReceiverHolder<TKey, IVisualizerHubReceiver> receivers,
        ImmutableArray<TKey> excludes,
        ImmutableArray<TKey>? targets)
        : InMemoryProxyBase<TKey, IVisualizerHubReceiver>(receivers, excludes, targets), IVisualizerHubReceiver
        where TKey : IEquatable<TKey>
    {
        public void OnPresetChanged(PresetInfo preset, bool hardCut) =>
            Invoke(preset, hardCut, static (r, a1, a2) => r.OnPresetChanged(a1, a2));

        public void OnPlaybackSettingsChanged(PlaybackSettings settings) =>
            Invoke(settings, static (r, a1) => r.OnPlaybackSettingsChanged(a1));

        public void OnPcm(PcmChunk chunk) => Invoke(chunk, static (r, a1) => r.OnPcm(a1));

        public void OnServerShutdown() => Invoke(static r => r.OnServerShutdown());

        public void OnRevoked() => Invoke(static r => r.OnRevoked());
    }

    private sealed class RemoteReceiverProxy(IRemoteReceiverWriter writer, IRemoteSerializer serializer)
        : RemoteProxyBase(writer, serializer), IVisualizerHubReceiver
    {
        // Multicaster derives method ids as FNV1A32(method name) when no [MethodId] is present;
        // these must match what the dynamic factory (and the generated clients) would compute.
        private static readonly int OnPresetChangedId = Fnv1A32(nameof(IVisualizerHubReceiver.OnPresetChanged));
        private static readonly int OnPlaybackSettingsChangedId = Fnv1A32(nameof(IVisualizerHubReceiver.OnPlaybackSettingsChanged));
        private static readonly int OnPcmId = Fnv1A32(nameof(IVisualizerHubReceiver.OnPcm));
        private static readonly int OnServerShutdownId = Fnv1A32(nameof(IVisualizerHubReceiver.OnServerShutdown));
        private static readonly int OnRevokedId = Fnv1A32(nameof(IVisualizerHubReceiver.OnRevoked));

        public void OnPresetChanged(PresetInfo preset, bool hardCut) =>
            Invoke(nameof(IVisualizerHubReceiver.OnPresetChanged), OnPresetChangedId, preset, hardCut);

        public void OnPlaybackSettingsChanged(PlaybackSettings settings) =>
            Invoke(nameof(IVisualizerHubReceiver.OnPlaybackSettingsChanged), OnPlaybackSettingsChangedId, settings);

        public void OnPcm(PcmChunk chunk) => Invoke(nameof(IVisualizerHubReceiver.OnPcm), OnPcmId, chunk);

        public void OnServerShutdown() => Invoke(nameof(IVisualizerHubReceiver.OnServerShutdown), OnServerShutdownId);

        public void OnRevoked() => Invoke(nameof(IVisualizerHubReceiver.OnRevoked), OnRevokedId);

        private static int Fnv1A32(string name)
        {
            var hash = 2166136261u;
            foreach (var b in Encoding.UTF8.GetBytes(name))
            {
                hash = (b ^ hash) * 16777619;
            }

            return unchecked((int)hash);
        }
    }
}
