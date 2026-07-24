using System.Runtime.InteropServices;

namespace FreqScene;

internal static unsafe partial class DrmInterop
{
    private const string Drm = "libdrm.so.2";

    public const int DrmModeConnected = 1;
    public const uint DrmModeTypePreferred = 1 << 3;
    public const uint DrmModePageFlipEvent = 0x01;
    public const uint DrmFormatArgb8888 = 0x34325241;

    [LibraryImport(Drm)]
    public static partial IntPtr drmModeGetResources(int fd);

    [LibraryImport(Drm)]
    public static partial void drmModeFreeResources(IntPtr resources);

    [LibraryImport(Drm)]
    public static partial IntPtr drmModeGetConnector(int fd, uint connectorId);

    [LibraryImport(Drm)]
    public static partial IntPtr drmModeGetConnectorCurrent(int fd, uint connectorId);

    [LibraryImport(Drm)]
    public static partial void drmModeFreeConnector(IntPtr connector);

    [LibraryImport(Drm)]
    public static partial IntPtr drmModeGetEncoder(int fd, uint encoderId);

    [LibraryImport(Drm)]
    public static partial void drmModeFreeEncoder(IntPtr encoder);

    [LibraryImport(Drm)]
    public static partial int drmModeSetCrtc(
        int fd, uint crtcId, uint bufferId, uint x, uint y, ref uint connectorId, int count, ref DrmModeModeInfo mode);

    [LibraryImport(Drm)]
    public static partial int drmModeAddFB2(
        int fd, uint width, uint height, uint pixelFormat, uint* handles, uint* pitches, uint* offsets,
        out uint bufferId, uint flags);

    [LibraryImport(Drm)]
    public static partial int drmModeRmFB(int fd, uint bufferId);

    [LibraryImport(Drm)]
    public static partial int drmModePageFlip(int fd, uint crtcId, uint fbId, uint flags, IntPtr userData);

    [LibraryImport(Drm)]
    public static partial int drmHandleEvent(int fd, ref DrmEventContext context);

    [LibraryImport(Drm)]
    public static partial int drmSetMaster(int fd);

    [LibraryImport(Drm)]
    public static partial int drmDropMaster(int fd);

    [StructLayout(LayoutKind.Sequential)]
    public struct DrmModeRes
    {
        public int CountFbs;
        public IntPtr Fbs;
        public int CountCrtcs;
        public IntPtr Crtcs;
        public int CountConnectors;
        public IntPtr Connectors;
        public int CountEncoders;
        public IntPtr Encoders;
        public uint MinWidth;
        public uint MaxWidth;
        public uint MinHeight;
        public uint MaxHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DrmModeModeInfo
    {
        public uint Clock;
        public ushort HDisplay;
        public ushort HSyncStart;
        public ushort HSyncEnd;
        public ushort HTotal;
        public ushort HSkew;
        public ushort VDisplay;
        public ushort VSyncStart;
        public ushort VSyncEnd;
        public ushort VTotal;
        public ushort VScan;
        public uint VRefresh;
        public uint Flags;
        public uint Type;
        public fixed byte Name[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DrmModeConnector
    {
        public uint ConnectorId;
        public uint EncoderId;
        public uint ConnectorType;
        public uint ConnectorTypeId;
        public int Connection;
        public uint MmWidth;
        public uint MmHeight;
        public int Subpixel;
        public int CountModes;
        public IntPtr Modes;
        public int CountProps;
        public IntPtr Props;
        public IntPtr PropValues;
        public int CountEncoders;
        public IntPtr Encoders;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DrmModeEncoder
    {
        public uint EncoderId;
        public uint EncoderType;
        public uint CrtcId;
        public uint PossibleCrtcs;
        public uint PossibleClones;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DrmEventContext
    {
        public int Version;
        public IntPtr VblankHandler;
        public IntPtr PageFlipHandler;
    }

    private static readonly string[] ConnectorTypeNames =
    [
        "Unknown", "VGA", "DVI-I", "DVI-D", "DVI-A", "Composite", "SVIDEO", "LVDS", "Component",
        "DIN", "DP", "HDMI-A", "HDMI-B", "TV", "eDP", "Virtual", "DSI", "DPI", "Writeback", "SPI", "USB",
    ];

    public static string ConnectorName(in DrmModeConnector connector)
    {
        var type = connector.ConnectorType < ConnectorTypeNames.Length
            ? ConnectorTypeNames[connector.ConnectorType]
            : "Unknown";
        return $"{type}-{connector.ConnectorTypeId}";
    }
}
