using ProjectMDotNet.Interop;

namespace ProjectMDotNet;

/// <summary>Severity of a projectM log message.</summary>
public enum LogLevel
{
    /// <summary>No level set; logs everything.</summary>
    NotSet = (int)projectm_log_level.PROJECTM_LOG_LEVEL_NOTSET,

    /// <summary>Trace-level diagnostics.</summary>
    Trace = (int)projectm_log_level.PROJECTM_LOG_LEVEL_TRACE,

    /// <summary>Debug-level diagnostics.</summary>
    Debug = (int)projectm_log_level.PROJECTM_LOG_LEVEL_DEBUG,

    /// <summary>Informational messages.</summary>
    Info = (int)projectm_log_level.PROJECTM_LOG_LEVEL_INFO,

    /// <summary>Warnings.</summary>
    Warning = (int)projectm_log_level.PROJECTM_LOG_LEVEL_WARN,

    /// <summary>Recoverable errors (e.g. preset shader compilation failures).</summary>
    Error = (int)projectm_log_level.PROJECTM_LOG_LEVEL_ERROR,

    /// <summary>Fatal errors.</summary>
    Fatal = (int)projectm_log_level.PROJECTM_LOG_LEVEL_FATAL,
}
