using System.Diagnostics;
using Drm;
using Drm.Native;

namespace FreqScene;

public sealed class LinuxKmsSession : ILinuxGlSession
{
    private const int HotplugPollSeconds = 2;

    private readonly SeatSession _seat;
    private readonly string _devicePath;
    private readonly uint _connectorId;
    private readonly string _connectorName;
    private readonly uint _crtcId;
    private readonly DrmModeInfo _mode;
    private readonly DrmDevice _device = null!;

    private int _fd = -1;
    private IntPtr _gbmDevice;
    private IntPtr _gbmSurface;
    private IntPtr _previousBo;
    private readonly Dictionary<IntPtr, DrmFramebuffer> _framebuffersByBo = [];

    private bool _needsModeset = true;
    private bool _connected = true;
    private bool _flipCompleted;
    private DrmEventHandlers? _eventHandlers;
    private long _nextHotplugPoll;

    public LinuxKmsSession(string? connectorName, string? modeSpec)
    {
        _seat = SeatSession.Open();
        try
        {
            (_devicePath, _fd, _connectorId, _connectorName, _crtcId, _mode) =
                PickOutput(_seat, connectorName, modeSpec);
            _device = DrmDevice.FromFd(_fd);

            _gbmDevice = GbmInterop.gbm_create_device(_fd);
            if (_gbmDevice == IntPtr.Zero)
            {
                throw new InvalidOperationException($"gbm_create_device failed for {_devicePath}.");
            }

            _gbmSurface = GbmInterop.gbm_surface_create(
                _gbmDevice, _mode.HorizontalDisplay, _mode.VerticalDisplay,
                GbmInterop.GbmFormatArgb8888,
                GbmInterop.GbmBoUseScanout | GbmInterop.GbmBoUseRendering);
            if (_gbmSurface == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"gbm_surface_create failed for {_mode.HorizontalDisplay}x{_mode.VerticalDisplay} on {_connectorName}.");
            }

            Trace.TraceInformation(
                $"[kms] {_connectorName} on {_devicePath}: {_mode.HorizontalDisplay}x{_mode.VerticalDisplay}@{_mode.VerticalRefresh}");
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

    public int PixelWidth => _mode.HorizontalDisplay;

    public int PixelHeight => _mode.VerticalDisplay;

    public bool Visible => _seat.Enabled && _connected;

    public bool Closed => false;

    public double RefreshRate => _mode.VerticalRefresh > 0 ? _mode.VerticalRefresh : 60;

    public string ConnectorName => _connectorName;

    public bool UsesLibseat => _seat.UsesLibseat;

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
        bool connected;
        try
        {
            // forceProbe would block on a full re-detect; the cached state is
            // enough to notice hotplug because the kernel probes on its own.
            connected = _device.GetConnector(_connectorId, forceProbe: false).Status == DrmConnectionStatus.Connected;
        }
        catch (DrmException)
        {
            return;
        }

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
            if (GetFramebuffer(bo) is not { } framebuffer)
            {
                return;
            }

            if (_needsModeset)
            {
                try
                {
                    _device.SetCrtc(_crtcId, framebuffer.Id, 0, 0, [_connectorId], _mode);
                    _needsModeset = false;
                    presented = true;
                }
                catch (DrmException)
                {
                    // Not seat-active yet (or another master holds the CRTC);
                    // retry on a later frame.
                }
            }
            else
            {
                try
                {
                    _device.PageFlip(_crtcId, framebuffer.Id);
                    WaitForPageFlip();
                    presented = true;
                }
                catch (DrmException)
                {
                    _needsModeset = true;
                }
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
            framebuffer.Dispose();
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

        _device?.Dispose();

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
                using var device = DrmDevice.FromFd(fd);
                foreach (var connector in EnumerateConnectors(device))
                {
                    var modes = new List<string>(connector.Modes.Count);
                    foreach (var mode in connector.Modes)
                    {
                        var preferred = mode.IsPreferred ? "*" : "";
                        modes.Add($"{mode.HorizontalDisplay}x{mode.VerticalDisplay}@{mode.VerticalRefresh}{preferred}");
                    }

                    outputs.Add(new KmsOutputInfo(
                        connector.Name,
                        devicePath,
                        connector.Status == DrmConnectionStatus.Connected,
                        modes));
                }
            }
            finally
            {
                SeatInterop.close(fd);
            }
        }

        return outputs;
    }

    private static (string DevicePath, int Fd, uint ConnectorId, string Name, uint CrtcId, DrmModeInfo Mode)
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

            using var device = DrmDevice.FromFd(fd);
            foreach (var connector in EnumerateConnectors(device))
            {
                var connected = connector.Status == DrmConnectionStatus.Connected;
                seen.Add(connected ? $"{connector.Name} (connected)" : connector.Name);

                if (connector.Modes.Count == 0 || !connected)
                {
                    continue;
                }

                if (connectorName is not null &&
                    !string.Equals(connector.Name, connectorName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (FindCrtc(device, connector) is not { } crtc)
                {
                    continue;
                }

                return (devicePath, fd, connector.ConnectorId, connector.Name, crtc, PickMode(connector, modeSpec));
            }

            seat.CloseDevice(fd);
        }

        var available = seen.Count > 0 ? string.Join(", ", seen) : "none";
        throw new InvalidOperationException(connectorName is null
            ? $"No connected display was found on any DRM device (connectors: {available})."
            : $"Output '{connectorName}' is not a connected display (connectors: {available}).");
    }

    private static DrmModeInfo PickMode(DrmConnector connector, string? modeSpec)
    {
        if (modeSpec is not null && TryParseModeSpec(modeSpec, out var width, out var height, out var refresh))
        {
            foreach (var mode in connector.Modes)
            {
                if (mode.HorizontalDisplay == width && mode.VerticalDisplay == height &&
                    (refresh == 0 || mode.VerticalRefresh == refresh))
                {
                    return mode;
                }
            }

            var wanted = refresh > 0 ? $"{width}x{height}@{refresh}" : $"{width}x{height}";
            throw new InvalidOperationException(
                $"Mode {wanted} is not offered by {connector.Name}; use --list-outputs to see modes.");
        }

        foreach (var mode in connector.Modes)
        {
            if (mode.IsPreferred)
            {
                return mode;
            }
        }

        return connector.Modes[0];
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

    private static uint? FindCrtc(DrmDevice device, DrmConnector connector)
    {
        if (connector.CurrentEncoderId != 0)
        {
            try
            {
                var crtcId = device.GetEncoder(connector.CurrentEncoderId).CrtcId;
                if (crtcId != 0)
                {
                    return crtcId;
                }
            }
            catch (DrmException)
            {
            }
        }

        IReadOnlyList<uint> crtcs;
        try
        {
            crtcs = device.GetResources().CrtcIds;
        }
        catch (DrmException)
        {
            return null;
        }

        foreach (var encoderId in connector.EncoderIds)
        {
            uint possibleCrtcs;
            try
            {
                possibleCrtcs = device.GetEncoder(encoderId).PossibleCrtcs;
            }
            catch (DrmException)
            {
                continue;
            }

            for (var c = 0; c < crtcs.Count; c++)
            {
                if ((possibleCrtcs & (1u << c)) != 0)
                {
                    return crtcs[c];
                }
            }
        }

        return null;
    }

    private static IEnumerable<DrmConnector> EnumerateConnectors(DrmDevice device)
    {
        IReadOnlyList<uint> connectorIds;
        try
        {
            connectorIds = device.GetResources().ConnectorIds;
        }
        catch (DrmException)
        {
            // Render-only nodes have no modesetting resources; skip them.
            yield break;
        }

        foreach (var connectorId in connectorIds)
        {
            DrmConnector connector;
            try
            {
                connector = device.GetConnector(connectorId);
            }
            catch (DrmException)
            {
                continue;
            }

            yield return connector;
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

    private DrmFramebuffer? GetFramebuffer(IntPtr bo)
    {
        if (_framebuffersByBo.TryGetValue(bo, out var existing))
        {
            return existing;
        }

        var handle = (uint)GbmInterop.gbm_bo_get_handle(bo);
        var stride = GbmInterop.gbm_bo_get_stride(bo);
        try
        {
            var framebuffer = _device.AddFramebuffer(
                _mode.HorizontalDisplay, _mode.VerticalDisplay, Libdrm.DRM_FORMAT_ARGB8888,
                [handle], [stride], [0u]);
            _framebuffersByBo[bo] = framebuffer;
            return framebuffer;
        }
        catch (DrmException ex)
        {
            Trace.TraceError($"[kms] {ex.Message}; the frame cannot be presented.");
            return null;
        }
    }

    private void WaitForPageFlip()
    {
        _flipCompleted = false;
        _eventHandlers ??= new DrmEventHandlers
        {
            PageFlip = (_, _, _, _, _) => _flipCompleted = true,
        };

        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        while (!_flipCompleted && Stopwatch.GetTimestamp() < deadline)
        {
            if (!LinuxInterop.PollReadable(_fd, 100))
            {
                continue;
            }

            try
            {
                _device.DispatchEvents(_eventHandlers);
            }
            catch (DrmException)
            {
                break;
            }
        }
    }
}

public sealed record KmsOutputInfo(string Name, string DevicePath, bool Connected, IReadOnlyList<string> Modes);
