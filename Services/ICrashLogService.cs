namespace WhereUAtNative.Services;

/// <summary>
/// On-device crash/error log. Never uploads; no credentials, tokens, or precise coords.
/// </summary>
public interface ICrashLogService
{
    void Log(CrashLogLevel level, string message, Exception? exception = null);

    void LogFatal(string message, Exception? exception = null);

    void LogError(string message, Exception? exception = null);

    /// <summary>Returns the last <paramref name="maxBytes"/> of the log (or all if smaller).</summary>
    Task<string> ReadTailAsync(int maxBytes = 48 * 1024);

    Task ClearAsync();

    string LogFilePath { get; }

    bool HasEntries { get; }
}

public enum CrashLogLevel
{
    Error,
    Fatal
}
