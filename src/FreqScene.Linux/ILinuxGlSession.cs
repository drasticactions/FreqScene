namespace FreqScene;

public interface ILinuxGlSession : IDisposable
{
    uint EglPlatform { get; }

    IntPtr NativeDisplayHandle { get; }

    IntPtr NativeWindowHandle { get; }

    int? RequiredNativeVisualId { get; }

    int PixelWidth { get; }

    int PixelHeight { get; }

    bool Visible { get; }

    bool Closed { get; }

    double RefreshRate { get; }

    void PumpEvents();

    void ApplyPendingResize();

    void RequestShow();

    void AfterSwap(IntPtr eglDisplay, IntPtr eglSurface);
}
