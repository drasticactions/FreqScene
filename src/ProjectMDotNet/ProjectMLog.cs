using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProjectMDotNet.Interop;

namespace ProjectMDotNet;

public static unsafe class ProjectMLog
{
    private static readonly object Sync = new();
    private static Action<string, LogLevel>? _message;

    /// <summary>
    /// Sets the minimum level of messages passed to the log callback.
    /// </summary>
    public static void SetLogLevel(LogLevel level, bool currentThreadOnly = false) =>
        NativeMethods.projectm_set_log_level((projectm_log_level)level, (byte)(currentThreadOnly ? 1 : 0));

    /// <summary>
    /// Receives projectM log messages. Handlers must be thread-safe.
    /// </summary>
    public static event Action<string, LogLevel>? Message
    {
        add
        {
            lock (Sync)
            {
                var register = _message is null;
                _message += value;
                if (register)
                {
                    NativeMethods.projectm_set_log_callback(&OnMessage, 0, null);
                }
            }
        }
        remove
        {
            lock (Sync)
            {
                _message -= value;
                if (_message is null)
                {
                    NativeMethods.projectm_set_log_callback(null, 0, null);
                }
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMessage(sbyte* message, projectm_log_level level, void* userData)
    {
        try
        {
            var text = Marshal.PtrToStringUTF8((IntPtr)message) ?? string.Empty;
            Volatile.Read(ref _message)?.Invoke(text, (LogLevel)level);
        }
        catch
        {
            // Exceptions must not cross the native boundary.
        }
    }
}
