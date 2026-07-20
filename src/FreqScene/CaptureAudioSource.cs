namespace FreqScene;

public sealed unsafe class CaptureAudioSource : IDisposable
{
    private const uint SampleRate = 44100;
    private const int BufferFrames = (int)SampleRate / 4;
    private const int MinChunkFrames = 256;

    private readonly Action<short[]> _sink;
    private readonly CancellationTokenSource _cts = new();
    private readonly IntPtr _device;
    private Thread? _thread;

    public CaptureAudioSource(string? deviceName, Action<short[]> sink)
    {
        _sink = sink;
        _device = OpenAlCapture.alcCaptureOpenDevice(
            deviceName, SampleRate, OpenAlCapture.FormatStereo16, BufferFrames);
        if (_device == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Could not open capture device '{deviceName ?? "(default)"}'.");
        }
    }

    public void Start()
    {
        OpenAlCapture.alcCaptureStart(_device);
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioCapture" };
        _thread.Start();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(millisecondsTimeout: 500);
        OpenAlCapture.alcCaptureStop(_device);
        OpenAlCapture.alcCaptureCloseDevice(_device);
        _cts.Dispose();
    }

    private void Run()
    {
        while (!_cts.IsCancellationRequested)
        {
            int availableFrames;
            OpenAlCapture.alcGetIntegerv(_device, OpenAlCapture.CaptureSamplesParam, 1, &availableFrames);

            if (availableFrames >= MinChunkFrames)
            {
                var chunk = new short[availableFrames * 2];
                fixed (short* buffer = chunk)
                {
                    OpenAlCapture.alcCaptureSamples(_device, buffer, availableFrames);
                }

                _sink(chunk);
            }

            Thread.Sleep(5);
        }
    }
}
