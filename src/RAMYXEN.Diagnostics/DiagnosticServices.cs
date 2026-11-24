using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RAMYXEN.Diagnostics;

public enum DiagnosticSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
    Fatal = 3
}

public enum WatchdogState
{
    Stopped = 0,
    Running = 1,
    SuspendedPower = 2,
    SuspendedDebugger = 3,
    Degraded = 4,
    HangDetected = 5
}

public sealed record DiagnosticLogEntry(
    DateTimeOffset TimestampUtc,
    DiagnosticSeverity Level,
    string Category,
    string Message,
    int ThreadId,
    string? ExceptionType = null,
    string? ExceptionMessage = null,
    string? StackTrace = null,
    int? NativeStatus = null,
    uint? Win32Error = null,
    string? NativeFunction = null,
    IReadOnlyDictionary<string, string>? Context = null);

public sealed record DiagnosticReportRecord(
    string ReportId,
    string Kind,
    DateTimeOffset TimestampUtc,
    bool IsFatal,
    bool Acknowledged,
    string Source,
    string Summary,
    string AppVersion,
    string OsVersion,
    string RuntimeVersion,
    string ActiveViewId,
    string ActiveLocale,
    string ActiveTheme,
    int? SelectedPid,
    string DisplayMetrics,
    string? ExceptionType,
    string? StackTrace,
    string? NativeContext,
    long? HangDurationMs,
    string JsonFilePath,
    string TextFilePath,
    IReadOnlyList<string> RecentBreadcrumbs);

public sealed record MiniDumpCaptureResult(
    bool Success,
    string? DumpFilePath,
    int Win32ErrorCode,
    string? ErrorMessage);

public sealed class DiagnosticRuntimeState
{
    private readonly object _sync = new();
    private string _appVersion = ResolveDefaultAppVersion();
    private string _activeViewId = "01";
    private string _activeLocale = "en-US";
    private string _activeTheme = "Dark";
    private int? _selectedPid;
    private string _displayMetrics = "Unknown";
    private int _nativeAbiVersion;

    public string AppVersion { get { lock (_sync) return _appVersion; } set { lock (_sync) _appVersion = value; } }
    public string ActiveViewId { get { lock (_sync) return _activeViewId; } set { lock (_sync) _activeViewId = value; } }
    public string ActiveLocale { get { lock (_sync) return _activeLocale; } set { lock (_sync) _activeLocale = value; } }
    public string ActiveTheme { get { lock (_sync) return _activeTheme; } set { lock (_sync) _activeTheme = value; } }
    public int? SelectedPid { get { lock (_sync) return _selectedPid; } set { lock (_sync) _selectedPid = value; } }
    public string DisplayMetrics { get { lock (_sync) return _displayMetrics; } set { lock (_sync) _displayMetrics = value; } }
    public int NativeAbiVersion { get { lock (_sync) return _nativeAbiVersion; } set { lock (_sync) _nativeAbiVersion = value; } }

    private static string ResolveDefaultAppVersion()
    {
        try
        {
            var ver = Assembly.GetEntryAssembly()?.GetName().Version;
            if (ver is not null && (ver.Major > 0 || ver.Minor > 0 || ver.Build > 0))
                return $"{ver.Major}.{ver.Minor}.{Math.Max(0, ver.Build)}";
        }
        catch { }
        return "1.1.0";
    }
}

/// <summary>
/// Comprehensive local privacy redactor for logs, crash reports, and exported ZIP bundles (F-005, F-016).
/// Handles short usernames (including 1-2 character usernames like "PC"), Windows/Unix/JSON-escaped profile paths,
/// hostnames, UNC shares, domains, Windows SIDs, non-loopback IPv4 addresses, emails, Bearer/JWT tokens, and secrets.
/// </summary>
public static class DiagnosticPrivacyRedactor
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex UsersPathRegex = new(
        @"(?i)([A-Za-z]:(?:\\\\|\\|/)Users(?:\\\\|\\|/))([^\\/\r\n""'<>|]+)",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex NtDeviceUsersPathRegex = new(
        @"(?i)((?:\\\\|\\)Device(?:\\\\|\\)HarddiskVolume\d+(?:\\\\|\\)Users(?:\\\\|\\)|(?:\\\\\\\\|\\\\)\?(?:\\\\|\\)[A-Za-z]:(?:\\\\|\\)Users(?:\\\\|\\)|(?<![A-Za-z0-9:])/Users/)([^\\/\r\n""'<>|\s]+)",
        RegexOptions.Compiled, RegexTimeout);

    // Match JSON-escaped UNC (\\\\server\\share) only when not preceded by drive/path/tag chars
    private static readonly Regex JsonUncShareRegex = new(
        @"(?<![A-Za-z0-9:<>\\])\\\\\\\\([A-Za-z0-9._-]{2,})\\\\([^\\/\r\n""'<>|\s]+)",
        RegexOptions.Compiled, RegexTimeout);

    // Match raw UNC (\\server\share) only when not preceded by drive/path/backslash/tag chars (prevents matching JSON-escaped C:\\Users\\...)
    private static readonly Regex UncShareRegex = new(
        @"(?<![A-Za-z0-9:<>\\])\\\\([A-Za-z0-9._-]{2,})\\([^\\/\r\n""'<>|\s]+)",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex EmailRegex = new(
        @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex JwtRegex = new(
        @"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(?![A-Za-z0-9_-])",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex AuthSchemeRegex = new(
        @"(?i)\b(Authorization\s*[:=]\s*""?)(Bearer|Basic|Token|ApiKey)\s+([^\s,;\""'}\]\r\n]+)",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex BearerRegex = new(
        @"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex UrlCredsRegex = new(
        @"([a-zA-Z][a-zA-Z0-9+.-]*://)([^/\s:@""']{1,64}):([^/\s@""']{1,128})@",
        RegexOptions.Compiled, RegexTimeout);

    // Supports quoted JSON ("api_key": "val"), quoted assignments (password="val"), underscore prefixes (access_token=, db_password=), and unquoted key=val
    private static readonly Regex QuotedSecretKeyValueRegex = new(
        @"(?i)([""']?\b[A-Za-z0-9_-]*(?:api[_-]?key|password|passwd|pwd|secret|token|client[_-]?secret|access[_-]?key|authorization|private[_-]?key)[""']?\s*[:=]\s*)([""'])([^""'\r\n]+)\2",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex UnquotedSecretKeyValueRegex = new(
        @"(?i)(\b[A-Za-z0-9_-]*(?:api[_-]?key|password|passwd|pwd|secret|token|client[_-]?secret|access[_-]?key|authorization|private[_-]?key)\s*[:=]\s*)([^\s,;\""'}\]\r\n]+)",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex ConnectionStringCredRegex = new(
        @"(?i)\b(User\s*ID|UID|Password|Pwd)\s*=\s*(?:""[^""\r\n]*""|'[^'\r\n]*'|[^;'\""\r\n]+)",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex WindowsSidRegex = new(
        @"\bS-1-\d+(?:-\d+){2,}\b",
        RegexOptions.Compiled, RegexTimeout);

    // Matches IPv4 while preserving loopback (0.0.0.0, 127.0.0.1) and 4-part .NET/SDK/OS versions (e.g. 10.0.0.0, 2.7.0.0, 8.0.12.0, 1.0.0.0)
    private static readonly Regex Ipv4Regex = new(
        @"(?i)(?<!(?:version|ver|nt|sdk|net|build|assembly)[\s:=/-])(?<!\d\.)\b((?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\b(?!\.\d)",
        RegexOptions.Compiled, RegexTimeout);

    private static bool ShouldPreserveAsVersionOrLoopback(string quad)
    {
        if (quad is "0.0.0.0" or "127.0.0.1" or "1.0.0.0")
            return true;

        var parts = quad.Split('.');
        if (parts.Length == 4 &&
            int.TryParse(parts[0], out int a) &&
            int.TryParse(parts[1], out int b) &&
            int.TryParse(parts[2], out int c) &&
            int.TryParse(parts[3], out int d))
        {
            // Common 4-part .NET / Windows SDK / assembly version shapes (e.g. 10.0.0.0, 2.7.0.0, 8.0.12.0, 4.0.0.0)
            if (d == 0 && a <= 15 && b <= 30 && c <= 99 && !(a == 10 && b >= 1))
                return true;
        }
        return false;
    }

    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        string result = input;

        // 1. Exact UserProfile path replacement (handles ANY username length including 1-2 chars like "A", "AB", "PC")
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            string trimmed = userProfile.TrimEnd('\\', '/');
            string driveRoot = Path.GetPathRoot(trimmed) ?? @"C:\";
            string replacement = driveRoot.TrimEnd('\\', '/') + @"\Users\<REDACTED_USER>";
            string replacementJson = replacement.Replace(@"\", @"\\");
            string replacementFwd = replacement.Replace('\\', '/');

            result = result.Replace(trimmed, replacement, StringComparison.OrdinalIgnoreCase);
            result = result.Replace(trimmed.Replace(@"\", @"\\"), replacementJson, StringComparison.OrdinalIgnoreCase);
            result = result.Replace(trimmed.Replace('\\', '/'), replacementFwd, StringComparison.OrdinalIgnoreCase);
        }

        // 2. General Users\<name>, NT device paths (\Device\HarddiskVolume3\Users\...), \\?\ paths, and /Users/ paths
        result = UsersPathRegex.Replace(result, "$1<REDACTED_USER>");
        result = NtDeviceUsersPathRegex.Replace(result, "$1<REDACTED_USER>");

        // 3. UNC network paths (JSON-escaped \\\\server\\share first, then raw \\server\share)
        result = JsonUncShareRegex.Replace(result, @"\\\\<REDACTED_HOST>\\<REDACTED_SHARE>");
        result = UncShareRegex.Replace(result, @"\\<REDACTED_HOST>\<REDACTED_SHARE>");

        // 4. Credentials, tokens, URLs, emails, SIDs, IPv4 addresses (preserving .NET/SDK version quads)
        result = JwtRegex.Replace(result, "<REDACTED_TOKEN>");
        result = AuthSchemeRegex.Replace(result, "$1$2 <REDACTED_SECRET>");
        result = BearerRegex.Replace(result, "Bearer <REDACTED_TOKEN>");
        result = UrlCredsRegex.Replace(result, "$1<REDACTED_USER>:<REDACTED_SECRET>@");
        result = ConnectionStringCredRegex.Replace(result, "$1=<REDACTED_SECRET>");
        result = QuotedSecretKeyValueRegex.Replace(result, "$1$2<REDACTED_SECRET>$2");
        result = UnquotedSecretKeyValueRegex.Replace(result, "$1<REDACTED_SECRET>");
        result = EmailRegex.Replace(result, "<REDACTED_EMAIL>");
        result = WindowsSidRegex.Replace(result, "<REDACTED_SID>");
        result = Ipv4Regex.Replace(result, m => ShouldPreserveAsVersionOrLoopback(m.Value) ? m.Value : "<REDACTED_IP>");

        // 5. Domain\Username explicit match regardless of username length (handles DOMAIN\PC and JSON-escaped DOMAIN\\PC)
        string userName = Environment.UserName;
        string domainName = Environment.UserDomainName;
        if (!string.IsNullOrWhiteSpace(userName))
        {
            if (!string.IsNullOrWhiteSpace(domainName))
            {
                result = Regex.Replace(
                    result,
                    $@"(?i)\b{Regex.Escape(domainName)}(\\\\|\\){Regex.Escape(userName)}\b",
                    "<REDACTED_DOMAIN>$1<REDACTED_USER>",
                    RegexOptions.None,
                    RegexTimeout);
            }

            if (userName.Length >= 3)
            {
                result = Regex.Replace(
                    result,
                    $@"(?i)\b{Regex.Escape(userName)}\b",
                    "<REDACTED_USER>",
                    RegexOptions.None,
                    RegexTimeout);
            }
            else
            {
                // Short usernames (1-2 chars like "A", "AB", "PC"): redact all contextual forms without mangling unrelated prose
                result = Regex.Replace(
                    result,
                    $@"(?i)(\\|/){Regex.Escape(userName)}(\\|/|$|\s|""|')",
                    "$1<REDACTED_USER>$2",
                    RegexOptions.None,
                    RegexTimeout);
                result = Regex.Replace(
                    result,
                    $@"(?i)([""']?\b(?:user(?:name)?|owner|account)\b[""']?\s*[:=]\s*|\b(?:for|by|as)\s+user\s+)([""']?){Regex.Escape(userName)}\b\2",
                    "$1$2<REDACTED_USER>$2",
                    RegexOptions.None,
                    RegexTimeout);
            }
        }

        // 6. Machine / Host name (unqualified for length >= 3; contextual for 1-2 char hostnames to avoid mangling OS/UI/VM/IO)
        string machineName = Environment.MachineName;
        if (!string.IsNullOrWhiteSpace(machineName))
        {
            if (machineName.Length >= 3)
            {
                result = Regex.Replace(
                    result,
                    $@"(?i)\b{Regex.Escape(machineName)}\b",
                    "<REDACTED_HOST>",
                    RegexOptions.None,
                    RegexTimeout);
            }
            else if (machineName.Length >= 2)
            {
                result = Regex.Replace(
                    result,
                    $@"(?i)([""']?\b(?:host(?:name)?|machine(?:name)?|computer|server)\b[""']?\s*[:=]\s*|\bon\s+)([""']?){Regex.Escape(machineName)}\b\2",
                    "$1$2<REDACTED_HOST>$2",
                    RegexOptions.None,
                    RegexTimeout);
            }
        }

        // 7. Domain name if distinct from machine name
        if (!string.IsNullOrWhiteSpace(domainName) &&
            domainName.Length >= 3 &&
            !domainName.Equals(machineName, StringComparison.OrdinalIgnoreCase) &&
            !domainName.Equals("WORKGROUP", StringComparison.OrdinalIgnoreCase) &&
            !domainName.Equals("NT AUTHORITY", StringComparison.OrdinalIgnoreCase))
        {
            result = Regex.Replace(
                result,
                $@"(?i)\b{Regex.Escape(domainName)}\b",
                "<REDACTED_DOMAIN>",
                RegexOptions.None,
                RegexTimeout);
        }

        return result;
    }

    public static IReadOnlyDictionary<string, string>? RedactDictionary(IReadOnlyDictionary<string, string>? source)
    {
        if (source is null || source.Count == 0)
            return null;

        var clean = new Dictionary<string, string>(source.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in source)
        {
            string safeKey = Redact(kv.Key);
            bool sensitiveKey =
                safeKey.Contains("user", StringComparison.OrdinalIgnoreCase) ||
                safeKey.Contains("pass", StringComparison.OrdinalIgnoreCase) ||
                safeKey.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                safeKey.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                safeKey.Contains("key", StringComparison.OrdinalIgnoreCase);

            clean[safeKey] = sensitiveKey ? "<REDACTED_VALUE>" : Redact(kv.Value);
        }
        return clean;
    }

    public static string SanitizeLocalFilePathForExport(string? filePath, string tokenPrefix = "<LOCAL_DIAGNOSTICS_DIR>")
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return string.Empty;
        string fileName = Path.GetFileName(filePath);
        return string.IsNullOrWhiteSpace(fileName) ? tokenPrefix : $"{tokenPrefix}/{fileName}";
    }
}

internal static class AtomicFileWriter
{
    public static void WriteAllTextAtomic(string destinationPath, string content)
    {
        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        string tempPath = $"{destinationPath}.tmp.{Guid.NewGuid():N}";
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }
}

public sealed class StructuredDiagnosticLogger : IDisposable
{
    private const long MaxLogFileBytes = 5 * 1024 * 1024; // 5 MB
    private const int MaxRetainedLogFiles = 5;
    private const int MaxRingBufferEntries = 250;
    private const int MaxBreadcrumbs = 40;

    private static readonly JsonSerializerOptions JsonLineOptions = new()
    {
        WriteIndented = false
    };

    private readonly object _fileLock = new();
    private readonly ConcurrentQueue<DiagnosticLogEntry> _ringBuffer = new();
    private readonly ConcurrentQueue<string> _breadcrumbs = new();
    private volatile bool _disposed;

    public string RootDirectory { get; }
    public string LogsDirectory { get; }

    public StructuredDiagnosticLogger(string? rootDirectory = null)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RAMYXEN")
            : rootDirectory;
        LogsDirectory = Path.Combine(RootDirectory, "logs");
        Directory.CreateDirectory(LogsDirectory);
        PruneOldLogFiles();
    }

    public void Info(string category, string message, IReadOnlyDictionary<string, string>? context = null) =>
        Log(DiagnosticSeverity.Info, category, message, context: context);

    public void Warning(string category, string message, IReadOnlyDictionary<string, string>? context = null) =>
        Log(DiagnosticSeverity.Warning, category, message, context: context);

    public void Error(string category, string message, Exception? exception = null, IReadOnlyDictionary<string, string>? context = null) =>
        Log(DiagnosticSeverity.Error, category, message, exception: exception, context: context);

    public void Fatal(string category, string message, Exception? exception = null, IReadOnlyDictionary<string, string>? context = null) =>
        Log(DiagnosticSeverity.Fatal, category, message, exception: exception, context: context);

    public void NativeError(string category, string functionName, int nativeStatus, uint win32Error, string message) =>
        Log(
            DiagnosticSeverity.Warning,
            category,
            message,
            nativeStatus: nativeStatus,
            win32Error: win32Error,
            nativeFunction: functionName);

    public void Breadcrumb(string category, string message)
    {
        string line = $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} [{category}] {DiagnosticPrivacyRedactor.Redact(message)}";
        _breadcrumbs.Enqueue(line);
        while (_breadcrumbs.Count > MaxBreadcrumbs && _breadcrumbs.TryDequeue(out _)) { }
    }

    public IReadOnlyList<string> GetRecentBreadcrumbs() => _breadcrumbs.ToArray();

    public IReadOnlyList<DiagnosticLogEntry> GetRecentEntries(int maxCount = 120)
    {
        var all = _ringBuffer.ToArray();
        if (all.Length <= maxCount)
            return all;
        return all[(all.Length - maxCount)..];
    }

    public string GetCurrentLogFilePath() =>
        Path.Combine(LogsDirectory, $"ramyxen-{DateTime.UtcNow:yyyyMMdd}.jsonl");

    /// <summary>
    /// Safely reads active and rotated JSONL logs using FileShare.ReadWrite under lock,
    /// returning redacted contents for diagnostic bundle export (F-031).
    /// </summary>
    public IReadOnlyList<(string FileName, string RedactedContent)> ReadRecentLogFilesForExport(int maxFiles = 3)
    {
        var results = new List<(string, string)>();
        lock (_fileLock)
        {
            try
            {
                if (!Directory.Exists(LogsDirectory))
                    return results;

                var files = Directory.GetFiles(LogsDirectory, "ramyxen-*.jsonl")
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(Math.Clamp(maxFiles, 1, MaxRetainedLogFiles));

                foreach (var fi in files)
                {
                    try
                    {
                        using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var sr = new StreamReader(fs, Encoding.UTF8);
                        var sb = new StringBuilder();
                        string? line;
                        while ((line = sr.ReadLine()) is not null)
                        {
                            try { sb.AppendLine(DiagnosticPrivacyRedactor.Redact(line)); }
                            catch (RegexMatchTimeoutException) { sb.AppendLine("<REDACTED_LINE_TIMEOUT>"); }
                        }
                        results.Add((fi.Name, sb.ToString()));
                    }
                    catch { }
                }
            }
            catch { }
        }
        return results;
    }

    public void Log(
        DiagnosticSeverity level,
        string category,
        string message,
        Exception? exception = null,
        int? nativeStatus = null,
        uint? win32Error = null,
        string? nativeFunction = null,
        IReadOnlyDictionary<string, string>? context = null)
    {
        if (_disposed)
            return;

        var entry = new DiagnosticLogEntry(
            TimestampUtc: DateTimeOffset.UtcNow,
            Level: level,
            Category: string.IsNullOrWhiteSpace(category) ? "General" : category,
            Message: DiagnosticPrivacyRedactor.Redact(message),
            ThreadId: Environment.CurrentManagedThreadId,
            ExceptionType: exception?.GetType().FullName,
            ExceptionMessage: exception is not null ? DiagnosticPrivacyRedactor.Redact(exception.Message) : null,
            StackTrace: exception?.StackTrace is not null ? DiagnosticPrivacyRedactor.Redact(exception.StackTrace) : null,
            NativeStatus: nativeStatus,
            Win32Error: win32Error,
            NativeFunction: nativeFunction,
            Context: DiagnosticPrivacyRedactor.RedactDictionary(context));

        _ringBuffer.Enqueue(entry);
        while (_ringBuffer.Count > MaxRingBufferEntries && _ringBuffer.TryDequeue(out _)) { }

        if (level >= DiagnosticSeverity.Warning)
        {
            Breadcrumb(entry.Category, $"{level}: {entry.Message}");
        }

        try
        {
            string jsonLine = JsonSerializer.Serialize(entry, JsonLineOptions);
            lock (_fileLock)
            {
                Directory.CreateDirectory(LogsDirectory);
                string logPath = GetCurrentLogFilePath();
                bool rotated = RotateIfNeeded(logPath);
                using (var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var sw = new StreamWriter(fs, Encoding.UTF8))
                {
                    sw.WriteLine(jsonLine);
                }
                if (rotated)
                {
                    PruneOldLogFiles();
                }
            }
        }
        catch
        {
            // Logging must never crash the host process.
        }
    }

    private bool RotateIfNeeded(string currentPath)
    {
        try
        {
            if (File.Exists(currentPath))
            {
                var info = new FileInfo(currentPath);
                if (info.Length >= MaxLogFileBytes)
                {
                    string rotated = Path.Combine(
                        LogsDirectory,
                        $"ramyxen-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.jsonl");
                    File.Move(currentPath, rotated, overwrite: true);
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private void PruneOldLogFiles()
    {
        try
        {
            if (!Directory.Exists(LogsDirectory))
                return;

            var files = Directory.GetFiles(LogsDirectory, "ramyxen-*.jsonl")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            for (int i = MaxRetainedLogFiles; i < files.Count; i++)
            {
                try { files[i].Delete(); } catch { }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}

public sealed class CrashAndDiagnosticReportService
{
    private const int MaxRetainedCrashReports = 30;
    private const int MaxRetainedDumps = 5;

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _sync = new();
    private readonly StructuredDiagnosticLogger _logger;
    private readonly DiagnosticRuntimeState _runtimeState;

    public string RootDirectory { get; }
    public string CrashReportsDirectory { get; }
    public string DumpsDirectory { get; }

    public event Action<DiagnosticReportRecord>? ReportRecorded;

    public CrashAndDiagnosticReportService(
        StructuredDiagnosticLogger logger,
        DiagnosticRuntimeState runtimeState,
        string? rootDirectory = null)
    {
        _logger = logger;
        _runtimeState = runtimeState;
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory) ? logger.RootDirectory : rootDirectory;
        CrashReportsDirectory = Path.Combine(RootDirectory, "crash-reports");
        DumpsDirectory = Path.Combine(RootDirectory, "diagnostic-dumps");
        Directory.CreateDirectory(CrashReportsDirectory);
        Directory.CreateDirectory(DumpsDirectory);
    }

    public DiagnosticReportRecord CaptureManagedException(
        Exception exception,
        string source,
        bool isFatal,
        string? nativeContext = null)
    {
        string safeSummary = DiagnosticPrivacyRedactor.Redact($"{exception.GetType().Name}: {exception.Message}");
        string safeStack = DiagnosticPrivacyRedactor.Redact(exception.ToString());
        string? safeNative = nativeContext is not null ? DiagnosticPrivacyRedactor.Redact(nativeContext) : null;

        _logger.Log(
            isFatal ? DiagnosticSeverity.Fatal : DiagnosticSeverity.Error,
            source,
            safeSummary,
            exception);

        return PersistReport(
            kind: isFatal ? "Crash" : "Error",
            isFatal: isFatal,
            source: source,
            summary: safeSummary,
            exceptionType: exception.GetType().FullName,
            stackTrace: safeStack,
            nativeContext: safeNative,
            hangDurationMs: null);
    }

    public DiagnosticReportRecord CaptureHangReport(
        long stallDurationMs,
        string source = "UiHangWatchdog",
        string? detail = null)
    {
        string safeSummary = DiagnosticPrivacyRedactor.Redact(
            detail ?? $"UI thread heartbeat stalled for {stallDurationMs} ms on view #{_runtimeState.ActiveViewId}.");

        string metadataNote = DiagnosticPrivacyRedactor.Redact(
            $"Heartbeat unacknowledged for {stallDurationMs} ms | View=#{_runtimeState.ActiveViewId} | SelectedPID={_runtimeState.SelectedPid?.ToString() ?? "None"} | Display={_runtimeState.DisplayMetrics}");

        _logger.Log(
            DiagnosticSeverity.Error,
            source,
            safeSummary,
            context: new Dictionary<string, string>
            {
                ["stallMs"] = stallDurationMs.ToString(),
                ["activeView"] = _runtimeState.ActiveViewId,
                ["selectedPid"] = _runtimeState.SelectedPid?.ToString() ?? "none"
            });

        // Do NOT fabricate a fake StackTrace string for UI hang reports (F-019): leave StackTrace null and put metadata in NativeContext
        return PersistReport(
            kind: "UIHang",
            isFatal: false,
            source: source,
            summary: safeSummary,
            exceptionType: "RAMYXEN.Diagnostics.UiThreadHangDetected",
            stackTrace: null,
            nativeContext: metadataNote,
            hangDurationMs: stallDurationMs);
    }

    public DiagnosticReportRecord CaptureNativeBoundaryError(
        string functionName,
        int nativeStatus,
        uint win32Error,
        string message)
    {
        string safeMsg = DiagnosticPrivacyRedactor.Redact(message);
        string nativeCtx = $"Function={functionName}, NativeStatus={nativeStatus}, Win32Error={win32Error}";
        _logger.NativeError("NativeBoundary", functionName, nativeStatus, win32Error, safeMsg);

        return PersistReport(
            kind: "NativeError",
            isFatal: false,
            source: "NativeBoundary",
            summary: $"{functionName} failed (status={nativeStatus}, win32={win32Error}): {safeMsg}",
            exceptionType: "RAMYXEN.Native.NativeBoundaryFailure",
            stackTrace: null,
            nativeContext: nativeCtx,
            hangDurationMs: null);
    }

    public IReadOnlyList<DiagnosticReportRecord> GetAllReports(int maxCount = 30)
    {
        var results = new List<DiagnosticReportRecord>();
        try
        {
            if (!Directory.Exists(CrashReportsDirectory))
                return results;

            var files = Directory.GetFiles(CrashReportsDirectory, "*.json")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .Take(maxCount);

            foreach (string file in files)
            {
                try
                {
                    string json = File.ReadAllText(file, Encoding.UTF8);
                    var record = JsonSerializer.Deserialize<DiagnosticReportRecord>(json);
                    if (record is not null)
                    {
                        // Resolve local paths at load time while keeping persisted JSON free of raw host paths (F-005)
                        string resolvedTxt = Path.ChangeExtension(file, ".txt");
                        results.Add(record with { JsonFilePath = file, TextFilePath = resolvedTxt });
                    }
                }
                catch { }
            }
        }
        catch { }
        return results;
    }

    public IReadOnlyList<DiagnosticReportRecord> GetUnacknowledgedReports() =>
        GetAllReports(int.MaxValue).Where(r => !r.Acknowledged).ToList();

    public void AcknowledgeAllReports()
    {
        lock (_sync)
        {
            foreach (var report in GetUnacknowledgedReports())
            {
                try
                {
                    string targetPath = report.JsonFilePath;
                    var sanitized = SanitizeForDiskAndExport(report with { Acknowledged = true });
                    string json = JsonSerializer.Serialize(sanitized, PrettyJsonOptions);
                    AtomicFileWriter.WriteAllTextAtomic(targetPath, json);
                }
                catch { }
            }
        }
    }

    public string BuildCopyableReportText(DiagnosticReportRecord? report = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== RAMYXEN DIAGNOSTIC REPORT (LOCAL, REDACTED, ZERO AUTO-UPLOAD) ===");
        sb.AppendLine($"Generated UTC   : {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"App Version     : {_runtimeState.AppVersion}");
        sb.AppendLine($"OS Version      : {Environment.OSVersion}");
        sb.AppendLine($"Runtime         : {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
        sb.AppendLine($"Active View     : #{_runtimeState.ActiveViewId}");
        sb.AppendLine($"Locale / Theme  : {_runtimeState.ActiveLocale} / {_runtimeState.ActiveTheme}");
        sb.AppendLine($"Display Metrics : {_runtimeState.DisplayMetrics}");
        sb.AppendLine($"Native ABI      : v{_runtimeState.NativeAbiVersion}");
        sb.AppendLine();

        if (report is not null)
        {
            sb.AppendLine("--- REPORT DETAILS ---");
            sb.AppendLine($"Report ID       : {report.ReportId}");
            sb.AppendLine($"Kind            : {report.Kind} (Fatal={report.IsFatal})");
            sb.AppendLine($"Timestamp UTC   : {report.TimestampUtc:O}");
            sb.AppendLine($"Source          : {report.Source}");
            sb.AppendLine($"Summary         : {report.Summary}");
            if (report.HangDurationMs.HasValue)
                sb.AppendLine($"Hang Duration   : {report.HangDurationMs.Value} ms");
            if (!string.IsNullOrWhiteSpace(report.NativeContext))
                sb.AppendLine($"Context Detail  : {report.NativeContext}");
            if (!string.IsNullOrWhiteSpace(report.StackTrace))
            {
                sb.AppendLine("Stack Trace     :");
                sb.AppendLine(report.StackTrace);
            }
            sb.AppendLine();
            if (report.RecentBreadcrumbs.Count > 0)
            {
                sb.AppendLine("--- BREADCRUMBS AT TIME OF EVENT ---");
                foreach (var b in report.RecentBreadcrumbs)
                    sb.AppendLine("  " + b);
                sb.AppendLine();
            }
        }

        var recentLogs = _logger.GetRecentEntries(40);
        if (recentLogs.Count > 0)
        {
            sb.AppendLine($"--- RECENT STRUCTURED LOGS ({recentLogs.Count}) ---");
            foreach (var l in recentLogs)
            {
                sb.AppendLine($"{l.TimestampUtc:HH:mm:ss.fff} [{l.Level}] [{l.Category}] {l.Message}");
            }
        }

        return DiagnosticPrivacyRedactor.Redact(sb.ToString());
    }

    public Task<string> ExportDiagnosticBundleAsync(
        string destinationFilePath,
        bool includeConsentGatedMinidump = false,
        DiagnosticReportRecord? specificReport = null,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return ExportDiagnosticBundle(destinationFilePath, includeConsentGatedMinidump, specificReport);
        }, ct);

    public string ExportDiagnosticBundle(
        string destinationFilePath,
        bool includeConsentGatedMinidump = false,
        DiagnosticReportRecord? specificReport = null)
    {
        string targetDir = Path.GetDirectoryName(destinationFilePath) ?? RootDirectory;
        Directory.CreateDirectory(targetDir);

        string reportText = BuildCopyableReportText(specificReport);

        if (destinationFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            string tempZip = $"{destinationFilePath}.tmp.{Guid.NewGuid():N}";
            try
            {
                using (var archive = ZipFile.Open(tempZip, ZipArchiveMode.Create))
                {
                    var summaryEntry = archive.CreateEntry("diagnostic-summary.txt");
                    using (var writer = new StreamWriter(summaryEntry.Open(), Encoding.UTF8))
                        writer.Write(reportText);

                    // Always serialize sanitized & redacted report JSON into ZIP (never copy raw paths! - F-005)
                    var reportsToInclude = specificReport is not null
                        ? new[] { specificReport }
                        : GetAllReports(5).ToArray();

                    foreach (var rep in reportsToInclude)
                    {
                        var sanitized = SanitizeForDiskAndExport(rep);
                        string safeJson = JsonSerializer.Serialize(sanitized, PrettyJsonOptions);
                        string entryName = specificReport is not null
                            ? $"{rep.ReportId}.json"
                            : $"reports/{rep.ReportId}.json";
                        var repEntry = archive.CreateEntry(entryName);
                        using var rw = new StreamWriter(repEntry.Open(), Encoding.UTF8);
                        rw.Write(safeJson);
                    }

                    // Include active & rotated logs read safely with FileShare.ReadWrite and fully redacted (F-031)
                    foreach (var (logName, redactedLog) in _logger.ReadRecentLogFilesForExport(3))
                    {
                        var logEntry = archive.CreateEntry($"logs/{logName}");
                        using var lw = new StreamWriter(logEntry.Open(), Encoding.UTF8);
                        lw.Write(redactedLog);
                    }

                    if (includeConsentGatedMinidump)
                    {
                        var dumpRes = CaptureConsentGatedMinidump();
                        if (dumpRes.Success && !string.IsNullOrEmpty(dumpRes.DumpFilePath) && File.Exists(dumpRes.DumpFilePath))
                        {
                            archive.CreateEntryFromFile(dumpRes.DumpFilePath, $"dumps/{Path.GetFileName(dumpRes.DumpFilePath)}");
                        }
                        else
                        {
                            throw new IOException(dumpRes.ErrorMessage ?? $"MiniDump capture failed (Win32={dumpRes.Win32ErrorCode}).");
                        }
                    }
                }

                File.Move(tempZip, destinationFilePath, overwrite: true);
                PruneOldExports(targetDir, 10);
            }
            finally
            {
                if (File.Exists(tempZip))
                {
                    try { File.Delete(tempZip); } catch { }
                }
            }

            _logger.Info("Diagnostics", $"Exported redacted diagnostic bundle ({Path.GetFileName(destinationFilePath)}, dump={includeConsentGatedMinidump}).");
            return destinationFilePath;
        }

        AtomicFileWriter.WriteAllTextAtomic(destinationFilePath, reportText);
        _logger.Info("Diagnostics", $"Saved redacted diagnostic report text ({Path.GetFileName(destinationFilePath)}).");
        return destinationFilePath;
    }

    public Task<MiniDumpCaptureResult> CaptureConsentGatedMinidumpAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return CaptureConsentGatedMinidump();
        }, ct);

    public MiniDumpCaptureResult CaptureConsentGatedMinidump()
    {
        try
        {
            Directory.CreateDirectory(DumpsDirectory);
            string dumpFile = Path.Combine(DumpsDirectory, $"ramyxen-consent-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.dmp");
            using var currentProc = Process.GetCurrentProcess();
            bool ok;
            int win32Err = 0;

            using (var fs = new FileStream(dumpFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                ok = MiniDumpWriteDump(
                    currentProc.Handle,
                    (uint)currentProc.Id,
                    fs.SafeFileHandle,
                    0x00000000, // MiniDumpNormal (minimal footprint, no full heap dump)
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);
                if (!ok)
                {
                    win32Err = Marshal.GetLastWin32Error();
                }
            }

            if (ok)
            {
                PruneOldDumps();
                _logger.Info("Diagnostics", $"Captured consent-gated MiniDumpNormal file ({Path.GetFileName(dumpFile)}).");
                return new MiniDumpCaptureResult(true, dumpFile, 0, null);
            }

            try { File.Delete(dumpFile); } catch { }
            string errMsg = $"MiniDumpWriteDump failed with Win32 error {win32Err}.";
            _logger.Warning("Diagnostics", errMsg);
            return new MiniDumpCaptureResult(false, null, win32Err, errMsg);
        }
        catch (Exception ex)
        {
            _logger.Warning("Diagnostics", $"Optional minidump capture unavailable: {ex.Message}");
            return new MiniDumpCaptureResult(false, null, -1, DiagnosticPrivacyRedactor.Redact(ex.Message));
        }
    }

    public string? TryCaptureConsentGatedMinidump()
    {
        var res = CaptureConsentGatedMinidump();
        return res.Success ? res.DumpFilePath : null;
    }

    private static DiagnosticReportRecord SanitizeForDiskAndExport(DiagnosticReportRecord record)
    {
        return record with
        {
            ReportId = DiagnosticPrivacyRedactor.Redact(record.ReportId),
            Kind = DiagnosticPrivacyRedactor.Redact(record.Kind),
            Source = DiagnosticPrivacyRedactor.Redact(record.Source),
            ActiveViewId = DiagnosticPrivacyRedactor.Redact(record.ActiveViewId),
            ExceptionType = record.ExceptionType is not null ? DiagnosticPrivacyRedactor.Redact(record.ExceptionType) : null,
            Summary = DiagnosticPrivacyRedactor.Redact(record.Summary),
            StackTrace = record.StackTrace is not null ? DiagnosticPrivacyRedactor.Redact(record.StackTrace) : null,
            NativeContext = record.NativeContext is not null ? DiagnosticPrivacyRedactor.Redact(record.NativeContext) : null,
            DisplayMetrics = DiagnosticPrivacyRedactor.Redact(record.DisplayMetrics),
            JsonFilePath = DiagnosticPrivacyRedactor.SanitizeLocalFilePathForExport(record.JsonFilePath, "<LOCAL_REPORTS_DIR>"),
            TextFilePath = DiagnosticPrivacyRedactor.SanitizeLocalFilePathForExport(record.TextFilePath, "<LOCAL_REPORTS_DIR>"),
            RecentBreadcrumbs = record.RecentBreadcrumbs.Select(DiagnosticPrivacyRedactor.Redact).ToList()
        };
    }

    private DiagnosticReportRecord PersistReport(
        string kind,
        bool isFatal,
        string source,
        string summary,
        string? exceptionType,
        string? stackTrace,
        string? nativeContext,
        long? hangDurationMs)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        string id = $"{kind.ToLowerInvariant()}_{stamp}_{Guid.NewGuid().ToString("N")[..6]}";
        string jsonPath = Path.Combine(CrashReportsDirectory, $"{id}.json");
        string txtPath = Path.Combine(CrashReportsDirectory, $"{id}.txt");

        var localRecord = new DiagnosticReportRecord(
            ReportId: id,
            Kind: kind,
            TimestampUtc: DateTimeOffset.UtcNow,
            IsFatal: isFatal,
            Acknowledged: false,
            Source: source,
            Summary: DiagnosticPrivacyRedactor.Redact(summary),
            AppVersion: _runtimeState.AppVersion,
            OsVersion: Environment.OSVersion.ToString(),
            RuntimeVersion: $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})",
            ActiveViewId: _runtimeState.ActiveViewId,
            ActiveLocale: _runtimeState.ActiveLocale,
            ActiveTheme: _runtimeState.ActiveTheme,
            SelectedPid: _runtimeState.SelectedPid,
            DisplayMetrics: DiagnosticPrivacyRedactor.Redact(_runtimeState.DisplayMetrics),
            ExceptionType: exceptionType,
            StackTrace: stackTrace is not null ? DiagnosticPrivacyRedactor.Redact(stackTrace) : null,
            NativeContext: nativeContext is not null ? DiagnosticPrivacyRedactor.Redact(nativeContext) : null,
            HangDurationMs: hangDurationMs,
            JsonFilePath: jsonPath,
            TextFilePath: txtPath,
            RecentBreadcrumbs: _logger.GetRecentBreadcrumbs().Select(DiagnosticPrivacyRedactor.Redact).ToList());

        var diskRecord = SanitizeForDiskAndExport(localRecord);

        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(CrashReportsDirectory);
                string serializedJson = JsonSerializer.Serialize(diskRecord, PrettyJsonOptions);
                AtomicFileWriter.WriteAllTextAtomic(jsonPath, serializedJson);
                AtomicFileWriter.WriteAllTextAtomic(txtPath, BuildCopyableReportText(localRecord));
                PruneOldCrashReports();
            }
            catch
            {
                // Best-effort persistence even under low-memory / fatal conditions.
            }
        }

        try { ReportRecorded?.Invoke(localRecord); } catch { }
        return localRecord;
    }

    private void PruneOldCrashReports()
    {
        try
        {
            var files = Directory.GetFiles(CrashReportsDirectory, "*.json")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            for (int i = MaxRetainedCrashReports; i < files.Count; i++)
            {
                try { files[i].Delete(); } catch { }
                try
                {
                    string txt = Path.ChangeExtension(files[i].FullName, ".txt");
                    if (File.Exists(txt)) File.Delete(txt);
                }
                catch { }
            }
        }
        catch { }
    }

    private void PruneOldDumps()
    {
        try
        {
            var files = Directory.GetFiles(DumpsDirectory, "ramyxen-consent-*.dmp")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            for (int i = MaxRetainedDumps; i < files.Count; i++)
            {
                try { files[i].Delete(); } catch { }
            }
        }
        catch { }
    }

    private static void PruneOldExports(string exportsDir, int maxExports)
    {
        try
        {
            if (!Directory.Exists(exportsDir)) return;
            var files = Directory.GetFiles(exportsDir, "RAMYXEN_Diagnostics_*.zip")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            for (int i = maxExports; i < files.Count; i++)
            {
                try { files[i].Delete(); } catch { }
            }
        }
        catch { }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        SafeHandle hFile,
        uint dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);
}

/// <summary>
/// Bounded, power-aware, debugger-aware UI responsiveness watchdog (F-019, F-033).
/// - Guarantees at most ONE pending heartbeat in the UI dispatcher queue (no queue flooding).
/// - Measures stall duration strictly from when an unacknowledged heartbeat was dispatched.
/// - Suspends and re-baselines cleanly across OS Sleep/Hibernate transitions and attached debuggers.
/// - Reports explicit WatchdogState (Stopped, Running, SuspendedPower, SuspendedDebugger, Degraded, HangDetected).
/// </summary>
public sealed class UiHangWatchdog : IDisposable
{
    private readonly Func<Action, bool> _tryPostToUi;
    private readonly CrashAndDiagnosticReportService _reportService;
    private readonly StructuredDiagnosticLogger _logger;
    private readonly int _heartbeatIntervalMs;
    private readonly int _hangThresholdMs;
    private readonly ManualResetEventSlim _stopEvent = new(false);

    private Thread? _watchdogThread;
    private long _lastAckTicks;
    private long _pendingDispatchedTicks;
    private int _pendingHeartbeat; // 0 = idle, 1 = pending UI ack
    private int _hangCount;
    private volatile bool _hangReportedForCurrentStall;
    private volatile bool _powerSuspended;
    private int _disposed;
    private volatile WatchdogState _state = WatchdogState.Stopped;
    private IntPtr _powerNotifyHandle;
    private PowerNotifyCallback? _powerCallbackDelegate;

    private const uint DEVICE_NOTIFY_CALLBACK = 2;
    private const uint PBT_APMSUSPEND = 0x0004;
    private const uint PBT_APMRESUMESUSPEND = 0x0007;
    private const uint PBT_APMRESUMEAUTOMATIC = 0x0012;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint PowerNotifyCallback(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll")]
    private static extern uint PowerRegisterSuspendResumeNotification(
        uint flags,
        ref DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS recipient,
        out IntPtr registrationHandle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll")]
    private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);

    public int HangCount => Volatile.Read(ref _hangCount);
    public WatchdogState State => _state;
    public bool IgnoreAttachedDebuggerForTests { get; set; }
    public event Action<DiagnosticReportRecord>? HangDetected;

    public UiHangWatchdog(
        Func<Action, bool> tryPostToUi,
        CrashAndDiagnosticReportService reportService,
        StructuredDiagnosticLogger logger,
        int heartbeatIntervalMs = 1500,
        int hangThresholdMs = 5000)
    {
        _tryPostToUi = tryPostToUi;
        _reportService = reportService;
        _logger = logger;
        _heartbeatIntervalMs = Math.Max(40, heartbeatIntervalMs);
        _hangThresholdMs = Math.Max(_heartbeatIntervalMs * 2, hangThresholdMs);
        _lastAckTicks = Stopwatch.GetTimestamp();
    }

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || _watchdogThread is not null)
            return;

        long now = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _lastAckTicks, now);
        Interlocked.Exchange(ref _pendingDispatchedTicks, 0);
        Interlocked.Exchange(ref _pendingHeartbeat, 0);
        _hangReportedForCurrentStall = false;
        _state = WatchdogState.Running;

        TryRegisterPowerNotifications();

        _watchdogThread = new Thread(WatchdogLoop)
        {
            IsBackground = true,
            Name = "RAMYXEN.UiHangWatchdog",
            Priority = ThreadPriority.BelowNormal
        };
        _watchdogThread.Start();
        _logger.Info("UiHangWatchdog", $"Started UI responsiveness watchdog (heartbeat={_heartbeatIntervalMs}ms, threshold={_hangThresholdMs}ms).");
    }

    private void TryRegisterPowerNotifications()
    {
        if (_powerNotifyHandle != IntPtr.Zero) return;
        try
        {
            _powerCallbackDelegate = OnWindowsPowerEvent;
            var parms = new DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
            {
                Callback = Marshal.GetFunctionPointerForDelegate(_powerCallbackDelegate),
                Context = IntPtr.Zero
            };
            if (PowerRegisterSuspendResumeNotification(DEVICE_NOTIFY_CALLBACK, ref parms, out IntPtr handle) == 0)
            {
                _powerNotifyHandle = handle;
            }
        }
        catch
        {
            // Non-fatal: fallback clock-jump detection remains active
        }
    }

    private void UnregisterPowerNotifications()
    {
        IntPtr handle = Interlocked.Exchange(ref _powerNotifyHandle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            try { PowerUnregisterSuspendResumeNotification(handle); } catch { }
        }
        _powerCallbackDelegate = null;
    }

    private uint OnWindowsPowerEvent(IntPtr context, uint type, IntPtr setting)
    {
        if (type == PBT_APMSUSPEND)
            NotifyPowerSuspend();
        else if (type == PBT_APMRESUMESUSPEND || type == PBT_APMRESUMEAUTOMATIC)
            NotifyPowerResume();
        return 0;
    }

    public void NotifyPowerSuspend()
    {
        _powerSuspended = true;
        _state = WatchdogState.SuspendedPower;
        Interlocked.Exchange(ref _pendingHeartbeat, 0);
        Interlocked.Exchange(ref _pendingDispatchedTicks, 0);
        _hangReportedForCurrentStall = false;
    }

    public void NotifyPowerResume()
    {
        long now = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _lastAckTicks, now);
        Interlocked.Exchange(ref _pendingDispatchedTicks, 0);
        Interlocked.Exchange(ref _pendingHeartbeat, 0);
        _hangReportedForCurrentStall = false;
        _powerSuspended = false;
        if (Volatile.Read(ref _disposed) == 0 && _watchdogThread is not null)
            _state = WatchdogState.Running;
    }

    public void Stop()
    {
        UnregisterPowerNotifications();
        try { _stopEvent.Set(); } catch (ObjectDisposedException) { }
        var thread = _watchdogThread;
        bool exitedCleanly = true;
        if (thread is not null && thread.IsAlive)
        {
            exitedCleanly = thread.Join(2000);
        }
        if (exitedCleanly)
        {
            _watchdogThread = null;
        }
        _state = WatchdogState.Stopped;
    }

    private void WatchdogLoop()
    {
        long prevWallUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long prevMonoTicks = Stopwatch.GetTimestamp();
        double modernStandbyJumpMs = Math.Max(_heartbeatIntervalMs * 4.0, 15_000.0);

        while (!_stopEvent.IsSet)
        {
            long nowWallUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long nowMonoTicks = Stopwatch.GetTimestamp();

            double wallDeltaMs = nowWallUtcMs - prevWallUtcMs;
            double monoDeltaMs = (nowMonoTicks - prevMonoTicks) * 1000.0 / Stopwatch.Frequency;
            prevWallUtcMs = nowWallUtcMs;
            prevMonoTicks = nowMonoTicks;

            // Detect both S3/Hibernate wall-vs-mono divergence AND Windows 10/11 Modern Standby (S0ix) where QPC advances with wall clock (R2-NEW-011)
            if (Math.Abs(wallDeltaMs - monoDeltaMs) > 15_000.0 || monoDeltaMs > modernStandbyJumpMs)
            {
                Interlocked.Exchange(ref _lastAckTicks, nowMonoTicks);
                Interlocked.Exchange(ref _pendingDispatchedTicks, 0);
                Interlocked.Exchange(ref _pendingHeartbeat, 0);
                _hangReportedForCurrentStall = false;
                if (!_powerSuspended) _state = WatchdogState.Running;
                _logger.Info("UiHangWatchdog", "Detected OS suspend/resume or Modern Standby clock jump; re-baselined UI heartbeat watchdog.");
                if (_stopEvent.Wait(_heartbeatIntervalMs)) break;
                continue;
            }

            if (_powerSuspended)
            {
                _state = WatchdogState.SuspendedPower;
                if (_stopEvent.Wait(_heartbeatIntervalMs)) break;
                continue;
            }

            if (!IgnoreAttachedDebuggerForTests && Debugger.IsAttached)
            {
                _state = WatchdogState.SuspendedDebugger;
                Interlocked.Exchange(ref _lastAckTicks, nowMonoTicks);
                Interlocked.Exchange(ref _pendingDispatchedTicks, 0);
                Interlocked.Exchange(ref _pendingHeartbeat, 0);
                _hangReportedForCurrentStall = false;
                if (_stopEvent.Wait(_heartbeatIntervalMs)) break;
                continue;
            }

            // CRITICAL FIX (R2-NEW-001): Check for recovery BEFORE flipping _pendingHeartbeat back to 1!
            if (Volatile.Read(ref _pendingHeartbeat) == 0)
            {
                if (_hangReportedForCurrentStall)
                {
                    _hangReportedForCurrentStall = false;
                    _state = WatchdogState.Running;
                    _logger.Info("UiHangWatchdog", "UI thread heartbeat recovered and is responsive again.");
                }
                else if (_state == WatchdogState.HangDetected || _state == WatchdogState.SuspendedDebugger || _state == WatchdogState.SuspendedPower)
                {
                    _state = WatchdogState.Running;
                }
            }

            // Only enqueue a new heartbeat when NO previous heartbeat is pending (ZERO queue flooding!)
            if (Interlocked.CompareExchange(ref _pendingHeartbeat, 1, 0) == 0)
            {
                Interlocked.Exchange(ref _pendingDispatchedTicks, nowMonoTicks);
                bool posted = false;
                try
                {
                    posted = _tryPostToUi(() =>
                    {
                        Interlocked.Exchange(ref _lastAckTicks, Stopwatch.GetTimestamp());
                        Interlocked.Exchange(ref _pendingDispatchedTicks, 0);
                        Interlocked.Exchange(ref _pendingHeartbeat, 0);
                    });
                }
                catch
                {
                    posted = false;
                }

                if (!posted)
                {
                    // Dispatcher unavailable or shutting down: mark Degraded honestly, do NOT fabricate hang report
                    Interlocked.Exchange(ref _pendingHeartbeat, 0);
                    Interlocked.Exchange(ref _pendingDispatchedTicks, 0);
                    if (_state != WatchdogState.Degraded)
                    {
                        _state = WatchdogState.Degraded;
                        _logger.Warning("UiHangWatchdog", "UI dispatcher rejected heartbeat enqueue; watchdog transitioned to Degraded state.");
                    }
                    if (_stopEvent.Wait(_heartbeatIntervalMs)) break;
                    continue;
                }

                if (_state == WatchdogState.Degraded)
                {
                    _state = WatchdogState.Running;
                }
            }
            else
            {
                // A previously dispatched heartbeat is still unacknowledged: measure stall strictly from dispatch time
                long dispatchedTicks = Interlocked.Read(ref _pendingDispatchedTicks);
                if (dispatchedTicks > 0)
                {
                    long stallMs = (long)((nowMonoTicks - dispatchedTicks) * 1000.0 / Stopwatch.Frequency);
                    if (stallMs >= _hangThresholdMs)
                    {
                        _state = WatchdogState.HangDetected;
                        if (!_hangReportedForCurrentStall)
                        {
                            _hangReportedForCurrentStall = true;
                            Interlocked.Increment(ref _hangCount);
                            var report = _reportService.CaptureHangReport(
                                stallMs,
                                detail: $"UI thread did not acknowledge heartbeat within {stallMs} ms (threshold {_hangThresholdMs} ms).");
                            try { HangDetected?.Invoke(report); } catch { }
                        }
                    }
                }
            }

            if (_stopEvent.Wait(_heartbeatIntervalMs))
                break;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        UnregisterPowerNotifications();
        try { _stopEvent.Set(); } catch { }
        var thread = _watchdogThread;
        bool exitedCleanly = true;
        if (thread is not null && thread.IsAlive)
        {
            exitedCleanly = thread.Join(2000);
        }
        if (exitedCleanly)
        {
            _watchdogThread = null;
        }
        _state = WatchdogState.Stopped;
        if (exitedCleanly)
        {
            try { _stopEvent.Dispose(); } catch { }
        }
    }
}
