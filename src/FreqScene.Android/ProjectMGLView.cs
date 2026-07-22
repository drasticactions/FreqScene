using Android.Content;
using Android.Opengl;
using Android.Util;
using Javax.Microedition.Khronos.Opengles;
using EGLConfig = Javax.Microedition.Khronos.Egl.EGLConfig;
using ProjectMDotNet;

namespace FreqScene.Android;

public sealed class ProjectMGLView : GLSurfaceView
{
    private readonly Renderer _renderer;

    static ProjectMGLView()
    {
        ProjectMLog.Message += (message, _) => Log.Info("projectM", message);
    }

    public ProjectMGLView(Context context)
        : base(context)
    {
        SetEGLContextClientVersion(3);
        PreserveEGLContextOnPause = true;
        _renderer = new Renderer(this);
        SetRenderer(_renderer);
        RenderMode = Rendermode.Continuously;
    }

    public void AddPcm(ReadOnlySpan<float> interleavedSamples, AudioChannels channels) =>
        _renderer.AddPcm(interleavedSamples, channels);

    public void LoadPresetData(string presetData, bool smoothTransition) =>
        QueueEvent(() => _renderer.LoadPresetData(presetData, smoothTransition));

    protected override void OnDetachedFromWindow()
    {
        QueueEvent(_renderer.Teardown);
        base.OnDetachedFromWindow();
    }

    private sealed class Renderer(ProjectMGLView view) : Java.Lang.Object, IRenderer
    {
        private readonly PcmBuffer _pcm = new();
        private ProjectM? _projectM;
        private (string Data, bool Smooth)? _pendingPreset;
        private bool _failed;
        private int _width;
        private int _height;

        public void AddPcm(ReadOnlySpan<float> interleavedSamples, AudioChannels channels) =>
            _pcm.Add(interleavedSamples, channels);

        // GL thread only (callers go through QueueEvent).
        public void LoadPresetData(string presetData, bool smoothTransition)
        {
            if (_projectM is { } instance)
            {
                instance.LoadPresetData(presetData, smoothTransition);
            }
            else
            {
                _pendingPreset = (presetData, smoothTransition);
            }
        }

        public void OnSurfaceCreated(IGL10? gl, EGLConfig? config)
        {
            if (_projectM is { } stale)
            {
                _projectM = null;
                stale.Abandon();
            }

            if (_failed)
            {
                return;
            }

            try
            {
                var instance = ProjectM.Create();

                instance.InGlScope = true;
                instance.GlWorkDispatcher = view.QueueEvent;

                instance.AspectCorrection = true;
                instance.PresetDuration = 30.0;
                if (_width > 0)
                {
                    instance.WindowSize = (_width, _height);
                }

                instance.LoadPresetFile("idle://", smoothTransition: false);
                if (_pendingPreset is { } pending)
                {
                    _pendingPreset = null;
                    instance.LoadPresetData(pending.Data, pending.Smooth);
                }

                _projectM = instance;
            }
            catch (Exception ex)
            {
                _failed = true;
                Log.Error("FreqScene", $"projectM init failed: {ex}");
            }
        }

        public void OnSurfaceChanged(IGL10? gl, int width, int height)
        {
            (_width, _height) = (width, height);
            if (_projectM is { } instance)
            {
                instance.WindowSize = (width, height);
            }
        }

        public void OnDrawFrame(IGL10? gl)
        {
            if (_projectM is not { } instance)
            {
                return;
            }

            _pcm.Drain(instance);
            instance.RenderFrame(0);
        }

        public void Teardown()
        {
            if (_projectM is { } instance)
            {
                _projectM = null;
                instance.Dispose();
            }
        }
    }
}
