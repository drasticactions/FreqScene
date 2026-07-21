using System.Collections.Concurrent;

namespace ProjectMDotNet.Avalonia;

/// <summary>
/// Thread-safe buffer between audio producers (any thread) and the render
/// thread. projectM's PCM API is bound to the GL thread, so samples are queued
/// here and drained just before each frame. Oldest chunks are dropped when the
/// producer outpaces rendering.
/// </summary>
internal sealed class PcmBuffer
{
    private const int MaxChunks = 64;

    private readonly ConcurrentQueue<(float[] Samples, AudioChannels Channels)> _queue = new();
    private int _count;

    public void Add(ReadOnlySpan<float> interleavedSamples, AudioChannels channels)
    {
        if (interleavedSamples.IsEmpty)
        {
            return;
        }

        Enqueue(interleavedSamples.ToArray(), channels);
    }

    public void Add(ReadOnlySpan<short> interleavedSamples, AudioChannels channels)
    {
        if (interleavedSamples.IsEmpty)
        {
            return;
        }

        var samples = new float[interleavedSamples.Length];
        for (var i = 0; i < interleavedSamples.Length; i++)
        {
            samples[i] = interleavedSamples[i] / 32768f;
        }

        Enqueue(samples, channels);
    }

    public void Clear()
    {
        while (_queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
        }
    }

    /// <summary>Feeds all queued samples to the instance. Call on the GL thread.</summary>
    public void Drain(ProjectM instance)
    {
        var maxPerChannel = (int)ProjectM.MaxPcmSamples;
        while (_queue.TryDequeue(out var chunk))
        {
            Interlocked.Decrement(ref _count);
            var stride = chunk.Channels == AudioChannels.Stereo ? maxPerChannel * 2 : maxPerChannel;
            var span = chunk.Samples.AsSpan();
            for (var offset = 0; offset < span.Length; offset += stride)
            {
                instance.AddPcm(span.Slice(offset, Math.Min(stride, span.Length - offset)), chunk.Channels);
            }
        }
    }

    private void Enqueue(float[] samples, AudioChannels channels)
    {
        _queue.Enqueue((samples, channels));
        if (Interlocked.Increment(ref _count) > MaxChunks && _queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
        }
    }
}
