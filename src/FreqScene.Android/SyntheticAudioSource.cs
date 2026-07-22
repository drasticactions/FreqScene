using System.Diagnostics;

namespace FreqScene.Android;

public sealed class SyntheticAudioSource : IDisposable
{
    private const int SampleRate = 44100;
    private const int ChunkFrames = 441; // 10 ms

    private readonly Action<float[]> _sink;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    public SyntheticAudioSource(Action<float[]> sink) => _sink = sink;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "SyntheticAudio" };
        _thread.Start();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(millisecondsTimeout: 500);
        _cts.Dispose();
    }

    private void Run()
    {
        var stopwatch = Stopwatch.StartNew();
        long generatedFrames = 0;
        var phase = 0.0;

        while (!_cts.IsCancellationRequested)
        {
            var targetFrames = (long)(stopwatch.Elapsed.TotalSeconds * SampleRate);
            while (generatedFrames < targetFrames)
            {
                var chunk = new float[ChunkFrames * 2];
                for (var i = 0; i < ChunkFrames; i++)
                {
                    var t = (generatedFrames + i) / (double)SampleRate;

                    var frequency = 110.0 * Math.Pow(2, 1.5 * (1 + Math.Sin(t * 0.31)));
                    phase += 2 * Math.PI * frequency / SampleRate;
                    var tone = 0.25 * Math.Sin(phase) + 0.1 * Math.Sin(2 * phase);

                    var beatTime = t % (60.0 / 128.0);
                    var kick = Math.Exp(-beatTime * 24) * Math.Sin(2 * Math.PI * 55 * beatTime) * 0.9;

                    var sample = (float)Math.Clamp(tone + kick, -1.0, 1.0);
                    chunk[i * 2] = sample;
                    chunk[(i * 2) + 1] = sample;
                }

                generatedFrames += ChunkFrames;
                _sink(chunk);
            }

            Thread.Sleep(5);
        }
    }
}
