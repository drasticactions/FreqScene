using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FreqScene;

public sealed unsafe class LinuxKmsSession : ILinuxGlSession
{
    private const int HotplugPollSeconds = 2;

    private static bool s_flipCompleted;

    private readonly SeatSession _seat;
    private readonly string _devicePath;
    private readonly uint _connectorId;
    private readonly string _connectorName;
    private readonly uint _crtcId;
    private DrmInterop.DrmModeModeInfo _mode;

    private int _fd = -1;
    private IntPtr _gbmDevice;
    private IntPtr _gbmSurface;
    private IntPtr _previousBo;
    private readonly Dictionary<IntPtr, uint> _framebuffersByBo = [];

    private bool _needsModeset = true;
    private bool _connected = true;
    private long _nextHotplugPoll;

    public LinuxKmsSession(string? connectorName, string? modeSpec)
    {
        _seat = SeatSession.Open();
        try
        {
            (_devicePath, _fd, _connectorId, _connectorName, _crtcId, _mode) =
                PickOutput(_seat, connectorName, modeSpec);

            _gbmDevice = GbmInterop.gbm_create_device(_fd);
            if (_gbmDevice == IntPtr.Zero)
            {
                throw new InvalidOperationException($"gbm_create_device failed for {_devicePath}.");
            }

            _gbmSurface = GbmInterop.gbm_surface_create(
                _gbmDevice, _mode.HDisplay, _mode.VDisplay,
                GbmInterop.GbmFormatArgb8888,
                GbmInterop.GbmBoUseScanout | GbmInterop.GbmBoUseRendering);
            if (_gbmSurface == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"gbm_surface_create failed for {_mode.HDisplay}x{_mode.VDisplay} on {_connectorName}.");
            }

            Trace.TraceInformation(
                $"[kms] {_connectorName} on {_devicePath}: {_mode.HDisplay}x{_mode.VDisplay}@{_mode.VRefresh}");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public uint EglPlatform => LinuxInterop.EglPlatformGbmKhr;

    public IntPtr NativeDisplayHandle => _gbmDevice;

    public IntPtr NativeWindowHandle => _gbmSurface;

    public int? RequiredNativeVisualId => unchecked((int)GbmInterop.GbmFormatArgb8888);

    public int PixelWidth => _mode.HDisplay;

    public int PixelHeight => _mode.VDisplay;

    public bool Visible => _seat.Enabled && _connected;

    public bool Closed => false;

    public double RefreshRate => _mode.VRefresh > 0 ? _mode.VRefresh : 60;

    public string ConnectorName => _connectorName;

    public void RequestShow()
    {
        // A KMS output is always "shown" while connected and the seat is active.
    }

    public void PumpEvents()
    {
        var wasEnabled = _seat.Enabled;
        _seat.Dispatch();
        if (!wasEnabled && _seat.Enabled)
        {
            // Whoever held the seat in between changed the CRTC; take it back.
            _needsModeset = true;
        }

        var now = Stopwatch.GetTimestamp();
        if (now < _nextHotplugPoll)
        {
            return;
        }

        _nextHotplugPoll = now + Stopwatch.Frequency * HotplugPollSeconds;
        var connectorPtr = DrmInterop.drmModeGetConnectorCurrent(_fd, _connectorId);
        if (connectorPtr == IntPtr.Zero)
        {
            return;
        }

        var connected = ((DrmInterop.DrmModeConnector*)connectorPtr)->Connection == DrmInterop.DrmModeConnected;
        DrmInterop.drmModeFreeConnector(connectorPtr);
        if (connected == _connected)
        {
            return;
        }

        _connected = connected;
        if (connected)
        {
            _needsModeset = true;
            Trace.TraceInformation($"[kms] {_connectorName} reconnected; resuming.");
        }
        else
        {
            Trace.TraceWarning($"[kms] {_connectorName} disconnected; rendering pauses until it returns.");
        }
    }

    public void ApplyPendingResize()
    {
        // The mode is fixed for the lifetime of the session.
    }

    public void AfterSwap(IntPtr eglDisplay, IntPtr eglSurface)
    {
        var bo = GbmInterop.gbm_surface_lock_front_buffer(_gbmSurface);
        if (bo == IntPtr.Zero)
        {
            return;
        }

        var presented = false;
        try
        {
            var framebuffer = GetFramebuffer(bo);
            if (framebuffer == 0)
            {
                return;
            }

            if (_needsModeset)
            {
                var connectorId = _connectorId;
                if (DrmInterop.drmModeSetCrtc(_fd, _crtcId, framebuffer, 0, 0, ref connectorId, 1, ref _mode) == 0)
                {
                    _needsModeset = false;
                    presented = true;
                }
            }
            else if (DrmInterop.drmModePageFlip(
                _fd, _crtcId, framebuffer, DrmInterop.DrmModePageFlipEvent, IntPtr.Zero) == 0)
            {
                WaitForPageFlip();
                presented = true;
            }
            else
            {
                _needsModeset = true;
            }
        }
        finally
        {
            if (!presented)
            {
                GbmInterop.gbm_surface_release_buffer(_gbmSurface, bo);
            }
            else
            {
                if (_previousBo != IntPtr.Zero)
                {
                    GbmInterop.gbm_surface_release_buffer(_gbmSurface, _previousBo);
                }

                _previousBo = bo;
            }
        }
    }

    public void Dispose()
    {
        foreach (var framebuffer in _framebuffersByBo.Values)
        {
            DrmInterop.drmModeRmFB(_fd, framebuffer);
        }

        _framebuffersByBo.Clear();

        if (_previousBo != IntPtr.Zero && _gbmSurface != IntPtr.Zero)
        {
            GbmInterop.gbm_surface_release_buffer(_gbmSurface, _previousBo);
            _previousBo = IntPtr.Zero;
        }

        if (_gbmSurface != IntPtr.Zero)
        {
            GbmInterop.gbm_surface_destroy(_gbmSurface);
            _gbmSurface = IntPtr.Zero;
        }

        if (_gbmDevice != IntPtr.Zero)
        {
            GbmInterop.gbm_device_destroy(_gbmDevice);
            _gbmDevice = IntPtr.Zero;
        }

        if (_fd >= 0)
        {
            _seat.CloseDevice(_fd);
            _fd = -1;
        }

        _seat.Dispose();
    }

    public static IReadOnlyList<KmsOutputInfo> ListOutputs()
    {
        var outputs = new List<KmsOutputInfo>();
        foreach (var devicePath in EnumerateDevices())
        {
            var fd = SeatInterop.OpenReadWrite(devicePath);
            if (fd < 0)
            {
                continue;
            }

            try
            {
                ForEachConnector(fd, (in DrmInterop.DrmModeConnector connector) =>
                {
                    var modes = new List<string>(connector.CountModes);
                    var modeInfos = (DrmInterop.DrmModeModeInfo*)connector.Modes;
                    for (var i = 0; i < connector.CountModes; i++)
                    {
                        var mode = modeInfos[i];
                        var preferred = (mode.Type & DrmInterop.DrmModeTypePreferred) != 0 ? "*" : "";
                        modes.Add($"{mode.HDisplay}x{mode.VDisplay}@{mode.VRefresh}{preferred}");
                    }

                    outputs.Add(new KmsOutputInfo(
                        DrmInterop.ConnectorName(connector),
                        devicePath,
                        connector.Connection == DrmInterop.DrmModeConnected,
                        modes));
                    return true;
                });
            }
            finally
            {
                SeatInterop.close(fd);
            }
        }

        return outputs;
    }

    private static (string DevicePath, int Fd, uint ConnectorId, string Name, uint CrtcId, DrmInterop.DrmModeModeInfo Mode)
        PickOutput(SeatSession seat, string? connectorName, string? modeSpec)
    {
        var seen = new List<string>();
        foreach (var devicePath in EnumerateDevices())
        {
            var fd = seat.OpenDevice(devicePath);
            if (fd < 0)
            {
                continue;
            }

            uint pickedConnector = 0;
            var pickedName = string.Empty;
            uint pickedCrtc = 0;
            DrmInterop.DrmModeModeInfo pickedMode = default;
            var found = false;

            ForEachConnector(fd, (in DrmInterop.DrmModeConnector connector) =>
            {
                var name = DrmInterop.ConnectorName(connector);
                var connected = connector.Connection == DrmInterop.DrmModeConnected;
                seen.Add(connected ? $"{name} (connected)" : name);

                if (connector.CountModes == 0 || !connected)
                {
                    return true;
                }

                if (connectorName is not null &&
                    !string.Equals(name, connectorName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (FindCrtc(fd, connector) is not { } crtc)
                {
                    return true;
                }

                pickedConnector = connector.ConnectorId;
                pickedName = name;
                pickedCrtc = crtc;
                pickedMode = PickMode(connector, modeSpec);
                found = true;
                return false;
            });

            if (found)
            {
                return (devicePath, fd, pickedConnector, pickedName, pickedCrtc, pickedMode);
            }

            seat.CloseDevice(fd);
        }

        var available = seen.Count > 0 ? string.Join(", ", seen) : "none";
        throw new InvalidOperationException(connectorName is null
            ? $"No connected display was found on any DRM device (connectors: {available})."
            : $"Output '{connectorName}' is not a connected display (connectors: {available}).");
    }

    private static DrmInterop.DrmModeModeInfo PickMode(in DrmInterop.DrmModeConnector connector, string? modeSpec)
    {
        var modes = (DrmInterop.DrmModeModeInfo*)connector.Modes;
        if (modeSpec is not null && TryParseModeSpec(modeSpec, out var width, out var height, out var refresh))
        {
            for (var i = 0; i < connector.CountModes; i++)
            {
                var mode = modes[i];
                if (mode.HDisplay == width && mode.VDisplay == height &&
                    (refresh == 0 || mode.VRefresh == refresh))
                {
                    return mode;
                }
            }

            var wanted = refresh > 0 ? $"{width}x{height}@{refresh}" : $"{width}x{height}";
            throw new InvalidOperationException(
                $"Mode {wanted} is not offered by {DrmInterop.ConnectorName(connector)}; use --list-outputs to see modes.");
        }

        for (var i = 0; i < connector.CountModes; i++)
        {
            if ((modes[i].Type & DrmInterop.DrmModeTypePreferred) != 0)
            {
                return modes[i];
            }
        }

        return modes[0];
    }

    private static bool TryParseModeSpec(string spec, out ushort width, out ushort height, out uint refresh)
    {
        width = 0;
        height = 0;
        refresh = 0;
        var atSplit = spec.Split('@');
        var sizeSplit = atSplit[0].Split('x');
        return atSplit.Length <= 2 && sizeSplit.Length == 2 &&
            ushort.TryParse(sizeSplit[0], out width) &&
            ushort.TryParse(sizeSplit[1], out height) &&
            (atSplit.Length == 1 || uint.TryParse(atSplit[1], out refresh));
    }

    private static uint? FindCrtc(int fd, in DrmInterop.DrmModeConnector connector)
    {
        if (connector.EncoderId != 0)
        {
            var currentPtr = DrmInterop.drmModeGetEncoder(fd, connector.EncoderId);
            if (currentPtr != IntPtr.Zero)
            {
                var crtcId = ((DrmInterop.DrmModeEncoder*)currentPtr)->CrtcId;
                DrmInterop.drmModeFreeEncoder(currentPtr);
                if (crtcId != 0)
                {
                    return crtcId;
                }
            }
        }

        var resourcesPtr = DrmInterop.drmModeGetResources(fd);
        if (resourcesPtr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var resources = (DrmInterop.DrmModeRes*)resourcesPtr;
            var crtcs = (uint*)resources->Crtcs;
            var encoderIds = (uint*)connector.Encoders;
            for (var e = 0; e < connector.CountEncoders; e++)
            {
                var encoderPtr = DrmInterop.drmModeGetEncoder(fd, encoderIds[e]);
                if (encoderPtr == IntPtr.Zero)
                {
                    continue;
                }

                var possible = ((DrmInterop.DrmModeEncoder*)encoderPtr)->PossibleCrtcs;
                DrmInterop.drmModeFreeEncoder(encoderPtr);
                for (var c = 0; c < resources->CountCrtcs; c++)
                {
                    if ((possible & (1u << c)) != 0)
                    {
                        return crtcs[c];
                    }
                }
            }

            return null;
        }
        finally
        {
            DrmInterop.drmModeFreeResources(resourcesPtr);
        }
    }

    private delegate bool ConnectorVisitor(in DrmInterop.DrmModeConnector connector);

    private static void ForEachConnector(int fd, ConnectorVisitor visit)
    {
        var resourcesPtr = DrmInterop.drmModeGetResources(fd);
        if (resourcesPtr == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var resources = (DrmInterop.DrmModeRes*)resourcesPtr;
            var connectorIds = (uint*)resources->Connectors;
            for (var i = 0; i < resources->CountConnectors; i++)
            {
                var connectorPtr = DrmInterop.drmModeGetConnector(fd, connectorIds[i]);
                if (connectorPtr == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    if (!visit(in *(DrmInterop.DrmModeConnector*)connectorPtr))
                    {
                        return;
                    }
                }
                finally
                {
                    DrmInterop.drmModeFreeConnector(connectorPtr);
                }
            }
        }
        finally
        {
            DrmInterop.drmModeFreeResources(resourcesPtr);
        }
    }

    private static IEnumerable<string> EnumerateDevices()
    {
        if (!Directory.Exists("/dev/dri"))
        {
            return [];
        }

        return Directory.GetFiles("/dev/dri", "card*").Order(StringComparer.Ordinal);
    }

    private uint GetFramebuffer(IntPtr bo)
    {
        if (_framebuffersByBo.TryGetValue(bo, out var existing))
        {
            return existing;
        }

        var handle = (uint)GbmInterop.gbm_bo_get_handle(bo);
        var stride = GbmInterop.gbm_bo_get_stride(bo);
        var handles = stackalloc uint[4] { handle, 0, 0, 0 };
        var pitches = stackalloc uint[4] { stride, 0, 0, 0 };
        var offsets = stackalloc uint[4];
        if (DrmInterop.drmModeAddFB2(
                _fd, _mode.HDisplay, _mode.VDisplay, DrmInterop.DrmFormatArgb8888,
                handles, pitches, offsets, out var framebuffer, 0) != 0)
        {
            Trace.TraceError("[kms] drmModeAddFB2 failed; the frame cannot be presented.");
            return 0;
        }

        _framebuffersByBo[bo] = framebuffer;
        return framebuffer;
    }

    private void WaitForPageFlip()
    {
        s_flipCompleted = false;
        var context = new DrmInterop.DrmEventContext
        {
            Version = 2,
            VblankHandler = IntPtr.Zero,
            PageFlipHandler = (IntPtr)(delegate* unmanaged<int, uint, uint, uint, IntPtr, void>)&OnPageFlip,
        };

        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        while (!s_flipCompleted && Stopwatch.GetTimestamp() < deadline)
        {
            if (!LinuxInterop.PollReadable(_fd, 100))
            {
                continue;
            }

            if (DrmInterop.drmHandleEvent(_fd, ref context) != 0)
            {
                break;
            }
        }
    }

    [UnmanagedCallersOnly]
    private static void OnPageFlip(int fd, uint sequence, uint tvSec, uint tvUsec, IntPtr userData) =>
        s_flipCompleted = true;
}

public sealed record KmsOutputInfo(string Name, string DevicePath, bool Connected, IReadOnlyList<string> Modes);
