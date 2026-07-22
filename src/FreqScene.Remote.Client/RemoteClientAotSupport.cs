using System.Runtime.CompilerServices;
using MagicOnion.Client;
using MessagePack;
using MessagePack.Resolvers;

namespace FreqScene.Remote.Client;

[MagicOnionClientGeneration(typeof(IVisualizerHub))]
internal partial class RemoteMagicOnionInitializer;

public static class RemoteClientAotSupport
{
    private static int _initialized;

    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1 || RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        StaticCompositeResolver.Instance.Register(
            BuiltinResolver.Instance,
            PrimitiveObjectResolver.Instance,
            RemoteMagicOnionInitializer.Resolver,
            StandardResolver.Instance);
        MessagePackSerializer.DefaultOptions =
            MessagePackSerializer.DefaultOptions.WithResolver(StaticCompositeResolver.Instance);
    }
}
