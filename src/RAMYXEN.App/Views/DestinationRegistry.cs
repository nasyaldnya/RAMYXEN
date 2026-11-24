using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RAMYXEN.App.Icons;
using RAMYXEN.Localization.Services;

namespace RAMYXEN.App.Views;

public sealed record DestinationMeta(
    int Id,
    string RouteKey,
    string CamelKey,
    string CategoryKey,
    FluentGlyph Icon)
{
    public string TitleLocKey => $"dest.{CamelKey}.title";
    public string DescLocKey => $"dest.{CamelKey}.desc";
    public string CategoryLocKey => $"palette.cat.{CategoryKey}";
}

public static class DestinationRegistry
{
    public static readonly IReadOnlyList<DestinationMeta> All98 = BuildAll98();

    private static readonly Dictionary<string, DestinationMeta> ByRouteKey =
        All98.ToDictionary(d => d.RouteKey, StringComparer.OrdinalIgnoreCase);

    public static DestinationMeta? Find(string routeKey) =>
        ByRouteKey.TryGetValue(routeKey, out var meta) ? meta : null;

    public static string GetLocalizedTitle(ILocalizationService loc, string routeKey)
    {
        if (ByRouteKey.TryGetValue(routeKey, out var meta))
        {
            string raw = loc.GetString(meta.TitleLocKey);
            // Isolate leading #NN token in RTL so "#14 ..." renders with #14 intact rather than flipped (F-042)
            if (loc.IsRightToLeft && raw.StartsWith('#'))
            {
                int spaceIdx = raw.IndexOf(' ');
                if (spaceIdx > 1)
                {
                    return $"{loc.IsolateTechnicalToken(raw[..spaceIdx])}{raw[spaceIdx..]}";
                }
            }
            return raw;
        }
        return routeKey;
    }

    public static string GetLocalizedDescription(ILocalizationService loc, string routeKey)
    {
        if (ByRouteKey.TryGetValue(routeKey, out var meta))
            return loc.GetString(meta.DescLocKey);
        return string.Empty;
    }

    public static string ToCamelKey(string routeKey)
    {
        var sb = new StringBuilder(routeKey.Length);
        bool upperNext = false;
        foreach (char c in routeKey)
        {
            if (c == '-')
            {
                upperNext = true;
                continue;
            }
            sb.Append(upperNext ? char.ToUpperInvariant(c) : c);
            upperNext = false;
        }
        return sb.ToString();
    }

    private static IReadOnlyList<DestinationMeta> BuildAll98()
    {
        var raw = new (int Id, string Key, string Cat, FluentGlyph Icon)[]
        {
            (1,"splash","shell",FluentGlyph.BrandMark), (2,"first-launch","shell",FluentGlyph.BrandMark), (3,"welcome","shell",FluentGlyph.BrandMark),
            (4,"compat-check","shell",FluentGlyph.ShieldCheck), (5,"permissions-setup","shell",FluentGlyph.ShieldCheck), (6,"init-monitor","shell",FluentGlyph.LivePulse),
            (7,"shell-main","shell",FluentGlyph.Overview), (8,"shell-nav-expanded","shell",FluentGlyph.Overview), (9,"shell-nav-collapsed","shell",FluentGlyph.Overview),
            (10,"global-search","shell",FluentGlyph.Search), (11,"command-palette","shell",FluentGlyph.CommandPalette), (12,"notifications","shell",FluentGlyph.Alerts),
            (13,"status-bar-detail","shell",FluentGlyph.Diagnostics),

            (14,"overview","monitoring",FluentGlyph.Overview), (15,"live-monitor","monitoring",FluentGlyph.LivePulse),
            (16,"memory-usage-details","monitoring",FluentGlyph.Overview), (17,"memory-composition","monitoring",FluentGlyph.PhysicalMemory),

            (18,"process-explorer","processes",FluentGlyph.Processes), (19,"process-tree","processes",FluentGlyph.Processes), (20,"process-comparison","processes",FluentGlyph.Processes),
            (21,"process-deep-dive","processes",FluentGlyph.ProcessDeepDive), (22,"process-overview","processes",FluentGlyph.ProcessDeepDive), (23,"process-memory","processes",FluentGlyph.PhysicalMemory),
            (24,"process-memory-map","processes",FluentGlyph.VirtualMemory), (25,"heap-explorer","processes",FluentGlyph.Heap), (26,"stack-explorer","processes",FluentGlyph.ProcessDeepDive),
            (27,"process-modules","processes",FluentGlyph.CodeFallback), (28,"process-threads","processes",FluentGlyph.LivePulse), (29,"process-handles","processes",FluentGlyph.Processes),
            (30,"memory-strings","processes",FluentGlyph.Search),

            (31,"virtual-overview","memory",FluentGlyph.VirtualMemory), (32,"virtual-map","memory",FluentGlyph.VirtualMemory), (33,"virtual-region-details","memory",FluentGlyph.VirtualMemory),
            (34,"physical-overview","memory",FluentGlyph.PhysicalMemory), (35,"page-distribution","memory",FluentGlyph.PhysicalMemory),
            (36,"physical-pages","memory",FluentGlyph.PhysicalMemory), (37,"physical-ranges","memory",FluentGlyph.PhysicalMemory), (38,"file-summary","memory",FluentGlyph.Folder),
            (39,"file-details","memory",FluentGlyph.Folder), (40,"priority-summary","memory",FluentGlyph.PhysicalMemory), (41,"page-inspector","memory",FluentGlyph.PhysicalMemory),

            (42,"profiler-home","profiler",FluentGlyph.Profiler), (43,"profiler-new","profiler",FluentGlyph.Profiler),
            (44,"profiler-providers","profiler",FluentGlyph.Profiler), (45,"profiler-recording","profiler",FluentGlyph.LivePulse),
            (46,"profiler-timeline","profiler",FluentGlyph.LivePulse), (47,"profiler-hotspots","profiler",FluentGlyph.Profiler),
            (48,"profiler-callstacks","profiler",FluentGlyph.CodeFallback), (49,"profiler-gc","profiler",FluentGlyph.Profiler),
            (50,"profiler-comparison","profiler",FluentGlyph.Profiler),

            (51,"snapshot-library","profiler",FluentGlyph.Snapshots), (52,"snapshot-create","profiler",FluentGlyph.CameraAdd),
            (53,"snapshot-details","profiler",FluentGlyph.Snapshots), (54,"snapshot-comparison","profiler",FluentGlyph.Snapshots),
            (55,"snapshot-comparison-viz","profiler",FluentGlyph.Snapshots), (56,"snapshot-import","profiler",FluentGlyph.Folder),
            (57,"snapshot-export","profiler",FluentGlyph.Save), (58,"snapshot-management","profiler",FluentGlyph.Folder),

            (59,"forensics-home","forensics",FluentGlyph.Forensics), (60,"forensics-source","forensics",FluentGlyph.Forensics),
            (61,"forensics-import","forensics",FluentGlyph.Folder), (62,"forensics-acq-config","forensics",FluentGlyph.Forensics),
            (63,"forensics-acq-progress","forensics",FluentGlyph.Forensics), (64,"forensics-analysis","forensics",FluentGlyph.Forensics),
            (65,"forensics-pstree","forensics",FluentGlyph.Processes), (66,"forensics-vad","forensics",FluentGlyph.VirtualMemory),
            (67,"forensics-pte","forensics",FluentGlyph.PhysicalMemory), (68,"forensics-kernel","forensics",FluentGlyph.SystemFallback),
            (69,"forensics-drivers","forensics",FluentGlyph.SystemFallback), (70,"forensics-strings","forensics",FluentGlyph.Search),
            (71,"forensics-yara","forensics",FluentGlyph.Forensics), (72,"forensics-suspicious","forensics",FluentGlyph.Warning),
            (73,"forensics-artifacts","forensics",FluentGlyph.Forensics), (74,"forensics-timeline","forensics",FluentGlyph.LivePulse),
            (75,"forensics-report","forensics",FluentGlyph.Reports),

            (76,"alerts-center","forensics",FluentGlyph.Alerts), (77,"alert-details","forensics",FluentGlyph.Alerts),
            (78,"alert-rules","forensics",FluentGlyph.Alerts), (79,"alert-rule-create","forensics",FluentGlyph.Alerts),

            (80,"report-center","forensics",FluentGlyph.Reports), (81,"report-preview","forensics",FluentGlyph.Reports),
            (82,"export-config","forensics",FluentGlyph.Save), (83,"export-progress","forensics",FluentGlyph.Save),

            (84,"settings-general","settings",FluentGlyph.Settings), (85,"settings-appearance","settings",FluentGlyph.ThemeDark),
            (86,"settings-monitoring","settings",FluentGlyph.Settings), (87,"settings-collection","settings",FluentGlyph.Settings),
            (88,"settings-sampling","settings",FluentGlyph.LivePulse), (89,"settings-storage","settings",FluentGlyph.Folder),
            (90,"settings-privacy","settings",FluentGlyph.ShieldCheck), (91,"settings-permissions","settings",FluentGlyph.ShieldCheck),
            (92,"settings-notifications","settings",FluentGlyph.Alerts), (93,"settings-advanced","settings",FluentGlyph.Diagnostics),
            (94,"settings-shortcuts","settings",FluentGlyph.CommandPalette), (95,"settings-datasources","settings",FluentGlyph.SystemFallback),
            (96,"help-center","settings",FluentGlyph.HelpGlossary), (97,"glossary","settings",FluentGlyph.HelpGlossary),
            (98,"about","settings",FluentGlyph.BrandMark)
        };

        return raw.Select(r => new DestinationMeta(r.Id, r.Key, ToCamelKey(r.Key), r.Cat, r.Icon)).ToList();
    }
}
