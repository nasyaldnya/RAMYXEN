using System.Net;
using System.Text;
using System.Text.Json;
using RAMYXEN.Core.Models;
using RAMYXEN.Localization.Services;

namespace RAMYXEN.Reporting;

public sealed class ReportExporterService
{
    private readonly ILocalizationService _loc;

    public ReportExporterService(ILocalizationService loc)
    {
        _loc = loc;
    }

    /// <summary>
    /// Neutralizes CSV Formula Injection (CWE-1236) triggers (=, +, -, @, \t, \r) and escapes embedded double quotes.
    /// </summary>
    public static string SanitizeCsvField(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        string cleaned = value;
        if (cleaned[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            cleaned = "'" + cleaned;
        }
        cleaned = cleaned.Replace("\"", "\"\"");
        return $"\"{cleaned}\"";
    }

    /// <summary>
    /// Redacts sensitive Windows user profile paths, NT device paths, UNC shares, credentials, tokens, emails, SIDs, IPv4, and machine names
    /// by delegating to the unified ReDoS-bounded <see cref="RAMYXEN.Diagnostics.DiagnosticPrivacyRedactor"/> (REL-NEW-007).
    /// </summary>
    public static string RedactSensitiveText(string? input, bool redact)
    {
        if (string.IsNullOrEmpty(input) || !redact) return input ?? string.Empty;
        string result = RAMYXEN.Diagnostics.DiagnosticPrivacyRedactor.Redact(input);
        return result
            .Replace("<REDACTED_USER>", "[REDACTED_USER]", StringComparison.Ordinal)
            .Replace("<REDACTED_HOST>", "[REDACTED_HOST]", StringComparison.Ordinal)
            .Replace("<REDACTED_EMAIL>", "[REDACTED_EMAIL]", StringComparison.Ordinal)
            .Replace("<REDACTED_SID>", "[REDACTED_SID]", StringComparison.Ordinal)
            .Replace("<REDACTED_SECRET>", "[REDACTED_SECRET]", StringComparison.Ordinal)
            .Replace("<REDACTED_TOKEN>", "[REDACTED_TOKEN]", StringComparison.Ordinal);
    }

    public string ExportCsv(
        string outputPath,
        SystemMemoryTelemetry sys,
        IReadOnlyList<ProcessMemoryInfo> processes,
        bool redactUsernames = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PID,ParentPID,ProcessName,Category,User,WorkingSetBytes,PrivateWorkingSetBytes,PrivateCommitBytes,VirtualSizeBytes,Handles,Threads,Provenance");
        foreach (var p in processes)
        {
            string user = redactUsernames ? "[REDACTED_USER]" : p.UserName;
            string procName = RedactSensitiveText(p.Name, redactUsernames);
            sb.AppendLine($"{p.Pid},{p.ParentPid},{SanitizeCsvField(procName)},{SanitizeCsvField(p.Category)},{SanitizeCsvField(user)},{p.WorkingSetBytes},{p.PrivateWorkingSetBytes},{p.PrivateCommitBytes},{p.VirtualSizeBytes},{p.HandleCount},{p.ThreadCount},{p.Provenance}");
        }
        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        return outputPath;
    }

    public string ExportJson(
        string outputPath,
        SystemMemoryTelemetry sys,
        IReadOnlyList<ProcessMemoryInfo> processes,
        IReadOnlyList<LeakInvestigationCase> investigations,
        bool redactPaths = true)
    {
        var sanitizedInvestigations = investigations.Select(c => redactPaths
            ? c with
            {
                TargetProcessName = RedactSensitiveText(c.TargetProcessName, true),
                ExecutablePath = RedactSensitiveText(c.ExecutablePath, true),
                SuspectedComponent = RedactSensitiveText(c.SuspectedComponent, true),
                EvidenceItems = c.EvidenceItems.Select(e => RedactSensitiveText(e, true)).ToList(),
                RecommendedNextSteps = c.RecommendedNextSteps.Select(e => RedactSensitiveText(e, true)).ToList(),
                KnownLimitations = c.KnownLimitations.Select(e => RedactSensitiveText(e, true)).ToList()
            }
            : c).ToList();

        var sanitizedProcesses = processes.Take(30).Select(p => redactPaths
            ? p with
            {
                Name = RedactSensitiveText(p.Name, true),
                UserName = "[REDACTED_USER]",
                ExecutablePath = RedactSensitiveText(p.ExecutablePath, true)
            }
            : p).ToList();

        var payload = new
        {
            Product = "RAMYXEN — Windows Memory Intelligence & Diagnostics Platform",
            GeneratedUtc = DateTimeOffset.UtcNow,
            Locale = _loc.CurrentLocale,
            Redacted = redactPaths,
            SystemMemory = sys,
            ActiveLeakInvestigations = sanitizedInvestigations,
            TopProcesses = sanitizedProcesses
        };
        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outputPath, json, Encoding.UTF8);
        return outputPath;
    }

    public string ExportHtml(
        string outputPath,
        SystemMemoryTelemetry sys,
        IReadOnlyList<ProcessMemoryInfo> processes,
        IReadOnlyList<LeakInvestigationCase> investigations,
        bool redactPaths = true)
    {
        string dir = _loc.IsRightToLeft ? "rtl" : "ltr";
        string Enc(string? s) => WebUtility.HtmlEncode(RedactSensitiveText(s, redactPaths));

        var sb = new StringBuilder();
        sb.AppendLine($"<!DOCTYPE html><html lang=\"{Enc(_loc.CurrentLocale)}\" dir=\"{dir}\"><head><meta charset=\"UTF-8\">");
        sb.AppendLine($"<title>RAMYXEN — {Enc(_loc.GetString("reports.title"))}</title>");
        sb.AppendLine("<style>body{background:#090C10;color:#F0F4F8;font-family:'Segoe UI',sans-serif;margin:24px;} table{width:100%;border-collapse:collapse;margin-top:12px;} th,td{border:1px solid #1D2635;padding:8px 10px;text-align:start;font-size:12.5px;} th{background:#111720;color:#94A3B8;} .mono{font-family:'Cascadia Code',Consolas,monospace;direction:ltr;unicode-bidi:isolate;}</style></head><body>");
        sb.AppendLine($"<h1>RAMYXEN — {Enc(_loc.GetString("reports.title"))}</h1>");
        sb.AppendLine($"<p class=\"mono\">Generated: {DateTimeOffset.UtcNow:u} | Locale: {Enc(_loc.CurrentLocale)} ({dir.ToUpperInvariant()}) | Redaction: {(redactPaths ? "ENABLED" : "OFF")}</p>");
        sb.AppendLine($"<h2>1. {Enc(_loc.GetString("navigation.overview"))}</h2>");
        sb.AppendLine($"<p><strong>{Enc(_loc.GetString("system.memory.inUse"))}:</strong> <span class=\"mono\">{UnitFormatter.FormatBytes(sys.InUsePhysicalBytes, true)} / {UnitFormatter.FormatBytes(sys.TotalPhysicalBytes, true)} ({sys.MemoryLoadPercent}%)</span> | <strong>{Enc(_loc.GetString("system.memory.available"))}:</strong> <span class=\"mono\">{UnitFormatter.FormatBytes(sys.AvailablePhysicalBytes, true)}</span> | <strong>{Enc(_loc.GetString("system.memory.committed"))}:</strong> <span class=\"mono\">{UnitFormatter.FormatBytes(sys.CommitTotalBytes, true)} / {UnitFormatter.FormatBytes(sys.CommitLimitBytes, true)}</span></p>");

        sb.AppendLine($"<h2>2. {Enc(_loc.GetString("alerts.title"))}</h2><table><thead><tr><th>ID</th><th>{Enc(_loc.GetString("process.column.name"))}</th><th>{Enc(_loc.GetString("process.column.leakStatus"))}</th><th>Confidence</th><th>Evidence</th></tr></thead><tbody>");
        foreach (var c in investigations.Take(10))
        {
            string ev = string.Join(" · ", c.EvidenceItems);
            sb.AppendLine($"<tr><td class=\"mono\">{Enc(c.CaseId)}</td><td class=\"mono\">{Enc(c.TargetProcessName)} (PID {c.TargetPid})</td><td>{Enc(c.Classification.ToString())}</td><td class=\"mono\">{c.ConfidencePercent}%</td><td>{Enc(ev)}</td></tr>");
        }
        sb.AppendLine("</tbody></table>");

        sb.AppendLine($"<h2>3. {Enc(_loc.GetString("process.title"))} (Top 25)</h2><table><thead><tr><th>{Enc(_loc.GetString("process.column.name"))}</th><th>{Enc(_loc.GetString("process.column.pid"))}</th><th>{Enc(_loc.GetString("process.column.workingSet"))}</th><th>{Enc(_loc.GetString("process.column.privateBytes"))}</th><th>{Enc(_loc.GetString("process.column.handles"))}</th><th>Path</th></tr></thead><tbody>");
        foreach (var p in processes.Take(25))
        {
            sb.AppendLine($"<tr><td>{Enc(p.Name)}</td><td class=\"mono\">{p.Pid}</td><td class=\"mono\">{UnitFormatter.FormatBytes(p.WorkingSetBytes, true)}</td><td class=\"mono\">{UnitFormatter.FormatBytes(p.PrivateCommitBytes, true)}</td><td class=\"mono\">{p.HandleCount:N0}</td><td class=\"mono\">{Enc(p.ExecutablePath)}</td></tr>");
        }
        sb.AppendLine("</tbody></table></body></html>");
        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        return outputPath;
    }

    /// <summary>
    /// Generates a valid PDF 1.4 diagnostic document with exact byte-offset xref table and startxref pointer,
    /// full PII path redaction, and safe Unicode codepoint escaping so non-ASCII / Arabic characters never
    /// corrupt into '?'.
    /// </summary>
    public string ExportPdf(
        string outputPath,
        SystemMemoryTelemetry sys,
        IReadOnlyList<ProcessMemoryInfo> processes,
        IReadOnlyList<LeakInvestigationCase> investigations,
        bool redactPaths = true)
    {
        var lines = new List<string>
        {
            $"RAMYXEN - Windows Memory Intelligence Report [Locale: {_loc.CurrentLocale}]",
            $"Generated UTC: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} | Redaction: {(redactPaths ? "ENABLED" : "OFF")}",
            $"Physical RAM In Use: {UnitFormatter.FormatBytes(sys.InUsePhysicalBytes, true)} / {UnitFormatter.FormatBytes(sys.TotalPhysicalBytes, true)} ({sys.MemoryLoadPercent}%)",
            $"Available (Reclaimable Standby + Free): {UnitFormatter.FormatBytes(sys.AvailablePhysicalBytes, true)}",
            $"Commit Charge / Limit: {UnitFormatter.FormatBytes(sys.CommitTotalBytes, true)} / {UnitFormatter.FormatBytes(sys.CommitLimitBytes, true)} ({sys.CommitRatioPercent}%)",
            $"Kernel Pools: Paged {UnitFormatter.FormatBytes(sys.KernelPagedBytes, true)} | Nonpaged {UnitFormatter.FormatBytes(sys.KernelNonpagedBytes, true)}",
            "------------------------------------------------------------------------",
            "Top Memory Consumers & Leak Triage:"
        };

        foreach (var c in investigations.Take(12))
        {
            string safeName = RedactSensitiveText(c.TargetProcessName, redactPaths);
            string safePath = RedactSensitiveText(c.ExecutablePath, redactPaths);
            lines.Add($"PID {c.TargetPid,-6} {safeName,-22} Commit: {UnitFormatter.FormatBytes(c.CurrentCommitBytes, true),-11} [{c.Classification} {c.ConfidencePercent}%] {safePath}");
        }

        foreach (var p in processes.Take(10))
        {
            string safeName = RedactSensitiveText(p.Name, redactPaths);
            string safePath = RedactSensitiveText(p.ExecutablePath, redactPaths);
            lines.Add($"PROC {p.Pid,-5} {safeName,-22} WS: {UnitFormatter.FormatBytes(p.WorkingSetBytes, true),-11} Path: {safePath}");
        }

        WriteValidPdf14Document(outputPath, lines);
        return outputPath;
    }

    private static string EncodePdfSafeAsciiOrHex(string input)
    {
        var sb = new StringBuilder(input.Length + 16);
        foreach (char ch in input)
        {
            if (ch == '\\') sb.Append("\\\\");
            else if (ch == '(') sb.Append("\\(");
            else if (ch == ')') sb.Append("\\)");
            else if (ch >= 32 && ch <= 126) sb.Append(ch);
            else if (ch > 126) sb.Append($"[U+{(int)ch:X4}]");
        }
        return sb.ToString();
    }

    private static void WriteValidPdf14Document(string path, IReadOnlyList<string> lines)
    {
        var content = new StringBuilder();
        content.Append("BT /F1 9 Tf 36 756 Td 13 TL\n");
        foreach (string line in lines)
        {
            string escaped = EncodePdfSafeAsciiOrHex(line);
            content.Append($"({escaped}) Tj T*\n");
        }
        content.Append("ET\n");
        byte[] streamBytes = Encoding.ASCII.GetBytes(content.ToString());

        using var ms = new MemoryStream();
        void WriteAscii(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        WriteAscii("%PDF-1.4\n");

        long[] offsets = new long[6];

        offsets[1] = ms.Position;
        WriteAscii("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");

        offsets[2] = ms.Position;
        WriteAscii("2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");

        offsets[3] = ms.Position;
        WriteAscii("3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >> endobj\n");

        offsets[4] = ms.Position;
        WriteAscii($"4 0 obj << /Length {streamBytes.Length} >> stream\n");
        ms.Write(streamBytes, 0, streamBytes.Length);
        WriteAscii("\nendstream endobj\n");

        offsets[5] = ms.Position;
        WriteAscii("5 0 obj << /Type /Font /Subtype /Type1 /BaseFont /Courier >> endobj\n");

        long xrefOffset = ms.Position;
        WriteAscii("xref\n0 6\n");
        WriteAscii("0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++)
        {
            WriteAscii($"{offsets[i]:D10} 00000 n \n");
        }
        WriteAscii($"trailer << /Size 6 /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");

        File.WriteAllBytes(path, ms.ToArray());
    }
}
