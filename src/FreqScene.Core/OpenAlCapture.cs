using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.EXT;
using Silk.NET.OpenAL.Extensions.EXT.Enumeration;

namespace FreqScene;

internal static class OpenAlCapture
{
    private static readonly Lazy<Apis?> Loaded = new(Load);

    internal static bool IsAvailable => Loaded.Value is not null;

    internal static Capture? CaptureApi => Loaded.Value?.Capture;

    internal static string? DefaultCaptureDevice
    {
        get
        {
            unsafe
            {
                return Loaded.Value?.Enumeration?.GetString(
                    null, GetCaptureEnumerationContextString.DefaultCaptureDeviceSpecifier);
            }
        }
    }

    /// <summary>
    /// Names of all available capture devices, or empty when OpenAL is unavailable.
    /// </summary>
    internal static IReadOnlyList<string> GetCaptureDevices()
    {
        var devices = new List<string>();
        if (Loaded.Value?.Enumeration is { } enumeration)
        {
            devices.AddRange(enumeration.GetStringList(GetCaptureContextStringList.CaptureDeviceSpecifiers));
        }

        return devices;
    }

    private sealed record Apis(ALContext Alc, Capture Capture, CaptureEnumerationEnumeration? Enumeration);

    private static Apis? Load()
    {
        ALContext alc;
        try
        {
            alc = ALContext.GetApi();
        }
        catch (Exception)
        {
            return null;
        }

        unsafe
        {
            if (!alc.IsExtensionPresent(null, "ALC_EXT_CAPTURE"))
            {
                alc.Dispose();
                return null;
            }

            var enumeration = alc.IsExtensionPresent(null, "ALC_ENUMERATION_EXT")
                ? new CaptureEnumerationEnumeration(alc.Context)
                : null;
            return new Apis(alc, new Capture(alc.Context), enumeration);
        }
    }
}
