using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.EXT;

namespace FreqScene;

public sealed unsafe class CaptureAudioSource : IDisposable
{
    private const uint SampleRate = 44100;
    private const int BufferFrames = (int)SampleRate / 4;
    private const int MinChunkFrames = 256;

    private readonly Action<short[]> _sink;
    private readonly CancellationTokenSource _cts = new();
    private readonly Capture _capture;
    private readonly Device* _device;
    private Thread? _thread;

    public CaptureAudioSource(string? deviceName, Action<short[]> sink)
    {
        _sink = sink;
        _capture = OpenAlCapture.CaptureApi
            ?? throw new InvalidOperationException("OpenAL capture is not available on this system.");

        deviceName ??= OpenAlCapture.DefaultCaptureDevice;
        _device = _capture.CaptureOpenDevice(deviceName!, SampleRate, BufferFormat.Stereo16, BufferFrames);
        if (_device is null)
        {
            throw new InvalidOperationException($"Could not open capture device '{deviceName ?? "(default)"}'.");
        }
    }

    public void Start()
    {
        _capture.CaptureStart(_device);
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioCapture" };
        _thread.Start();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(millisecondsTimeout: 500);
        _capture.CaptureStop(_device);
        _capture.CaptureCloseDevice(_device);
        _cts.Dispose();
    }

    private void Run()
    {
        while (!_cts.IsCancellationRequested)
        {
            var availableFrames = _capture.GetAvailableSamples(_device);
            if (availableFrames >= MinChunkFrames)
            {
                var chunk = new short[availableFrames * 2];
                fixed (short* buffer = chunk)
                {
                    _capture.CaptureSamples(_device, buffer, availableFrames);
                }

                _sink(chunk);
            }

            Thread.Sleep(5);
        }
    }
}
