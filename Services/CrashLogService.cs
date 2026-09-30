using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhereUAtNative.Services;

/// <summary>
/// Append-only JSONL crash log under AppDataDirectory. Rotates at ~256KB.
/// Redacts secrets and precise lat/lon before writing.
/// </summary>
public sealed class CrashLogService : ICrashLogService
{
    private const long MaxFileBytes = 256 * 1024;
    private const int MaxStackChars = 2500;
    private const int MaxMessageChars = 800;
    private static readonly object Gate = new();

    private readonly string _path;
    private readonly string _appVersion;

    public CrashLogService()
    {
        _path = Path.Combine(FileSystem.AppDataDirectory, "crash.log");
        _appVersion = ResolveAppVersion();
    }

    public string LogFilePath => _path;

    public bool HasEntries
    {
        get
        {
            try
            {
                return File.Exists(_path) && new FileInfo(_path).Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public void LogFatal(string message, Exception? exception = null)
        => Log(CrashLogLevel.Fatal, message, exception);

    public void LogError(string message, Exception? exception = null)
        => Log(CrashLogLevel.Error, message, exception);

    public void Log(CrashLogLevel level, string message, Exception? exception = null)
    {
        try
        {
            var entry = new Dictionary<string, object?>
            {
                ["ts"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["level"] = level.ToString(),
                ["ver"] = _appVersion,
                ["msg"] = Truncate(Redact(message ?? string.Empty), MaxMessageChars)
            };

            if (exception is not null)
            {
                entry["exType"] = exception.GetType().FullName ?? exception.GetType().Name;
                entry["exMsg"] = Truncate(Redact(exception.Message ?? string.Empty), MaxMessageChars);
                var stack = exception.StackTrace;
                if (!string.IsNullOrEmpty(stack))
                    entry["stack"] = Truncate(Redact(stack), MaxStackChars);

                if (exception.InnerException is { } inner)
                {
                    entry["innerType"] = inner.GetType().FullName ?? inner.GetType().Name;
                    entry["innerMsg"] = Truncate(Redact(inner.Message ?? string.Empty), MaxMessageChars);
                }
            }

            var line = JsonSerializer.Serialize(entry) + "\n";
            AppendLine(line);
        }
        catch
        {
            // Logging must never throw into app code.
        }
    }

    public Task<string> ReadTailAsync(int maxBytes = 48 * 1024)
    {
        return Task.Run(() =>
        {
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(_path))
                        return string.Empty;

                    var info = new FileInfo(_path);
                    if (info.Length == 0)
                        return string.Empty;

                    using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var take = (int)Math.Min(info.Length, Math.Max(1024, maxBytes));
                    if (info.Length > take)
                        fs.Seek(info.Length - take, SeekOrigin.Begin);

                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    var text = reader.ReadToEnd();
                    // If we started mid-line, drop the partial first line.
                    if (info.Length > take)
                    {
                        var nl = text.IndexOf('\n');
                        if (nl >= 0 && nl + 1 < text.Length)
                            text = text[(nl + 1)..];
                    }

                    return text;
                }
                catch
                {
                    return string.Empty;
                }
            }
        });
    }

    public Task ClearAsync()
    {
        return Task.Run(() =>
        {
            lock (Gate)
            {
                try
                {
                    if (File.Exists(_path))
                        File.Delete(_path);
                }
                catch
                {
                    // ignore
                }
            }
        });
    }

    private void AppendLine(string line)
    {
        lock (Gate)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.AppendAllText(_path, line, Encoding.UTF8);
                RotateIfNeeded();
            }
            catch
            {
                // ignore disk errors
            }
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length <= MaxFileBytes)
                return;

            // Keep the newest half (~128KB) so we don't lose recent crashes.
            var keep = MaxFileBytes / 2;
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(info.Length - keep, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var tail = reader.ReadToEnd();
            var nl = tail.IndexOf('\n');
            if (nl >= 0 && nl + 1 < tail.Length)
                tail = tail[(nl + 1)..];

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, tail, Encoding.UTF8);
            File.Copy(tmp, _path, overwrite: true);
            File.Delete(tmp);
        }
        catch
        {
            // Rotation failure is non-fatal; next write may retry.
        }
    }

    private static string ResolveAppVersion()
    {
        try
        {
            var v = AppInfo.Current.VersionString;
            var b = AppInfo.Current.BuildString;
            return string.IsNullOrWhiteSpace(b) ? v : $"{v} ({b})";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
            return value;
        return value[..max] + "…";
    }

    /// <summary>
    /// Strip credentials/tokens/passwords and precise coordinate pairs from log text.
    /// </summary>
    internal static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var s = input;

        // password / passwd / pwd assignments
        s = Regex.Replace(s, @"(?i)(password|passwd|pwd|pass)\s*[=:]\s*\S+", "$1=[REDACTED]");
        // bearer / token / apikey / secret / credential
        s = Regex.Replace(s, @"(?i)(authorization|bearer|token|id[_-]?token|access[_-]?token|refresh[_-]?token|api[_-]?key|secret|credential)([""'=:\s]+)[^\s,""']+", "$1$2[REDACTED]");
        // Firebase / JWT-looking blobs
        s = Regex.Replace(s, @"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", "[REDACTED_JWT]");
        // RTDB ?auth= query values
        s = Regex.Replace(s, @"(?i)([?&]auth=)[^&\s]+", "$1[REDACTED]");
        // Precise lat/lon (5+ decimal places) — keep coarse rounded values untouched
        s = Regex.Replace(s, @"(-?\d{1,3}\.\d{5,})\s*[, ]\s*(-?\d{1,3}\.\d{5,})", "[COORDS_REDACTED]");
        s = Regex.Replace(s, @"(?i)\b(lat|latitude|lon|lng|longitude)\s*[=:]\s*-?\d+\.\d{5,}", "$1=[COORDS_REDACTED]");

        return s;
    }
}
