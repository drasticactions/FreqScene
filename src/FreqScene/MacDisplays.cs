using System.Runtime.InteropServices;

namespace FreqScene;

internal static class MacDisplays
{
    public static IReadOnlyList<DisplayInfo> List()
    {
        var result = new List<DisplayInfo>();
        ForEachScreen((screen, key, displayId) =>
        {
            var frame = MacInterop.MsgSendRect(screen, MacInterop.Sel("frame"));
            var name = ScreenName(screen) ?? $"Display {result.Count + 1}";
            var isPrimary = displayId == MacInterop.MainDisplayId();
            var label = $"{name} ({(int)frame.Size.Width}×{(int)frame.Size.Height})";
            result.Add(new DisplayInfo(key, isPrimary ? label + " — Primary" : label, isPrimary));
        });
        return result;
    }

    public static MacInterop.CgRect ResolveFrame(string? key)
    {
        MacInterop.CgRect? match = null;
        if (key is not null)
        {
            ForEachScreen((screen, screenKey, _) =>
            {
                if (screenKey == key && match is null)
                {
                    match = MacInterop.MsgSendRect(screen, MacInterop.Sel("frame"));
                }
            });
        }

        if (match is { } frame)
        {
            return frame;
        }

        var bounds = MacInterop.DisplayBounds(MacInterop.MainDisplayId());
        return new MacInterop.CgRect(0, 0, bounds.Size.Width, bounds.Size.Height);
    }

    private static void ForEachScreen(Action<IntPtr, string, uint> visit)
    {
        var screens = MacInterop.MsgSend(MacInterop.GetClass("NSScreen"), MacInterop.Sel("screens"));
        if (screens == IntPtr.Zero)
        {
            return;
        }

        var count = MacInterop.MsgSendLong(screens, MacInterop.Sel("count"));
        var seen = new Dictionary<string, int>();
        for (long i = 0; i < count; i++)
        {
            var screen = MacInterop.MsgSend(screens, MacInterop.Sel("objectAtIndex:"), (IntPtr)i);
            if (screen == IntPtr.Zero)
            {
                continue;
            }

            var id = DisplayId(screen);
            var key = $"{MacInterop.DisplayVendorNumber(id):X}-{MacInterop.DisplayModelNumber(id):X}-{MacInterop.DisplaySerialNumber(id):X}";
            seen[key] = seen.TryGetValue(key, out var duplicates) ? duplicates + 1 : 0;
            if (seen[key] > 0)
            {
                key = $"{key}#{seen[key]}";
            }

            visit(screen, key, id);
        }
    }

    private static uint DisplayId(IntPtr screen)
    {
        var description = MacInterop.MsgSend(screen, MacInterop.Sel("deviceDescription"));
        var numberKey = MacInterop.MsgSendUtf8(
            MacInterop.GetClass("NSString"), MacInterop.Sel("stringWithUTF8String:"), "NSScreenNumber");
        var number = MacInterop.MsgSend(description, MacInterop.Sel("objectForKey:"), numberKey);
        return number == IntPtr.Zero
            ? 0
            : (uint)MacInterop.MsgSendLong(number, MacInterop.Sel("unsignedIntegerValue"));
    }

    private static string? ScreenName(IntPtr screen)
    {
        var localizedName = MacInterop.Sel("localizedName");
        if (!MacInterop.MsgSendBool(screen, MacInterop.Sel("respondsToSelector:"), localizedName))
        {
            return null;
        }

        var name = MacInterop.MsgSend(screen, localizedName);
        if (name == IntPtr.Zero)
        {
            return null;
        }

        var utf8 = MacInterop.MsgSend(name, MacInterop.Sel("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }
}
