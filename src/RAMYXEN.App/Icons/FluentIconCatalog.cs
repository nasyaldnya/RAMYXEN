using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using PathShape = Microsoft.UI.Xaml.Shapes.Path;

namespace RAMYXEN.App.Icons;

/// <summary>
/// Semantic icon identifiers mapped to locally-bundled offline vector paths inspired by
/// Microsoft Fluent System Icons (MIT License, 24x24 viewBox) plus the custom RAMYXEN BrandMark.
/// Zero runtime network calls, zero external font dependencies, sharp at 100%-200% DPI (F-039, F-046).
/// </summary>
public enum FluentGlyph
{
    BrandMark,
    Overview,
    LivePulse,
    Processes,
    ProcessDeepDive,
    VirtualMemory,
    PhysicalMemory,
    Alerts,
    Profiler,
    Snapshots,
    Forensics,
    Reports,
    Settings,
    HelpGlossary,
    Diagnostics,
    Search,
    CommandPalette,
    Play,
    Pause,
    CameraAdd,
    ThemeDark,
    ThemeLight,
    ThemeContrast,
    Globe,
    ChevronDown,
    ChevronRight,
    Dismiss,
    Copy,
    Save,
    Warning,
    ShieldCheck,
    AppFallback,
    BrowserFallback,
    SystemFallback,
    CodeFallback,
    Heap,
    Folder
}

/// <summary>
/// Vendored offline vector icon catalog (24x24 canonical viewBox preserved via Canvas inside Viewbox
/// so optical margins and aspect ratios never distort — F-039).
/// </summary>
public static class FluentIconCatalog
{
    private static readonly IReadOnlyDictionary<FluentGlyph, string> Paths = new Dictionary<FluentGlyph, string>
    {
        // RAMYXEN custom geometric hexagon/diamond core emblem (not Fluent)
        [FluentGlyph.BrandMark] = "M12 2L21 7V17L12 22L3 17V7L12 2ZM12 5.2L5.8 8.7V15.3L12 18.8L18.2 15.3V8.7L12 5.2ZM12 8.2L15.4 12L12 15.8L8.6 12L12 8.2Z",

        // Clean 2x2 / Board Overview grid (zero self-intersecting arcs — F-039)
        [FluentGlyph.Overview] = "M3 5.25A2.25 2.25 0 0 1 5.25 3h4.5A2.25 2.25 0 0 1 12 5.25v4.5A2.25 2.25 0 0 1 9.75 12h-4.5A2.25 2.25 0 0 1 3 9.75v-4.5ZM5.25 4.5a.75.75 0 0 0-.75.75v4.5c0 .41.34.75.75.75h4.5a.75.75 0 0 0 .75-.75v-4.5a.75.75 0 0 0-.75-.75h-4.5ZM14 5.25A2.25 2.25 0 0 1 16.25 3h2.5A2.25 2.25 0 0 1 21 5.25v4.5A2.25 2.25 0 0 1 18.75 12h-2.5A2.25 2.25 0 0 1 14 9.75v-4.5ZM16.25 4.5a.75.75 0 0 0-.75.75v4.5c0 .41.34.75.75.75h2.5a.75.75 0 0 0 .75-.75v-4.5a.75.75 0 0 0-.75-.75h-2.5ZM3 16.25A2.25 2.25 0 0 1 5.25 14h13.5A2.25 2.25 0 0 1 21 16.25v2.5A2.25 2.25 0 0 1 18.75 21H5.25A2.25 2.25 0 0 1 3 18.75v-2.5ZM5.25 15.5a.75.75 0 0 0-.75.75v2.5c0 .41.34.75.75.75h13.5a.75.75 0 0 0 .75-.75v-2.5a.75.75 0 0 0-.75-.75H5.25Z",

        // Fluent: Pulse 24 Regular
        [FluentGlyph.LivePulse] = "M9.04 3.43a.75.75 0 0 1 1.43.09l3.15 11.79 1.7-4.53a.75.75 0 0 1 .7-.48H21a.75.75 0 0 1 0 1.5h-4.46l-2.34 6.26a.75.75 0 0 1-1.43-.07L9.62 6.23 7.93 11.2a.75.75 0 0 1-.71.51H3a.75.75 0 0 1 0-1.5h3.68l2.36-6.78Z",

        // Fluent: AppsList 24 Regular
        [FluentGlyph.Processes] = "M3 5.75A1.75 1.75 0 0 1 4.75 4h2.5A1.75 1.75 0 0 1 9 5.75v2.5A1.75 1.75 0 0 1 7.25 10h-2.5A1.75 1.75 0 0 1 3 8.25v-2.5ZM4.75 5.5a.25.25 0 0 0-.25.25v2.5c0 .14.11.25.25.25h2.5a.25.25 0 0 0 .25-.25v-2.5a.25.25 0 0 0-.25-.25h-2.5ZM11.75 6a.75.75 0 0 0 0 1.5h8.5a.75.75 0 0 0 0-1.5h-8.5ZM3 15.75A1.75 1.75 0 0 1 4.75 14h2.5A1.75 1.75 0 0 1 9 15.75v2.5A1.75 1.75 0 0 1 7.25 20h-2.5A1.75 1.75 0 0 1 3 18.25v-2.5ZM4.75 15.5a.25.25 0 0 0-.25.25v2.5c0 .14.11.25.25.25h2.5a.25.25 0 0 0 .25-.25v-2.5a.25.25 0 0 0-.25-.25h-2.5ZM11.75 16a.75.75 0 0 0 0 1.5h8.5a.75.75 0 0 0 0-1.5h-8.5Z",

        // Fluent: DeveloperBoard / Focus Inspector 24 Regular
        [FluentGlyph.ProcessDeepDive] = "M5.25 3A2.25 2.25 0 0 0 3 5.25v13.5A2.25 2.25 0 0 0 5.25 21h13.5A2.25 2.25 0 0 0 21 18.75V5.25A2.25 2.25 0 0 0 18.75 3H5.25ZM4.5 5.25c0-.41.34-.75.75-.75h13.5c.41 0 .75.34.75.75v13.5a.75.75 0 0 1-.75.75H5.25a.75.75 0 0 1-.75-.75V5.25ZM8 8.75A.75.75 0 0 1 8.75 8h6.5a.75.75 0 0 1 .75.75v6.5a.75.75 0 0 1-.75.75h-6.5a.75.75 0 0 1-.75-.75v-6.5ZM9.5 9.5v5h5v-5h-5Z",

        // Fluent: Layer 24 Regular (Virtual Address Space)
        [FluentGlyph.VirtualMemory] = "M11.66 2.34a.75.75 0 0 1 .68 0l9 4.5a.75.75 0 0 1 0 1.34l-9 4.5a.75.75 0 0 1-.68 0l-9-4.5a.75.75 0 0 1 0-1.34l9-4.5ZM4.68 7.5 12 11.16 19.32 7.5 12 3.84 4.68 7.5ZM3.33 11.67a.75.75 0 0 1 1-.34L12 15.16l7.67-3.83a.75.75 0 1 1 .67 1.34l-8 4a.75.75 0 0 1-.68 0l-8-4a.75.75 0 0 1-.33-1ZM3.33 15.67a.75.75 0 0 1 1-.34L12 19.16l7.67-3.83a.75.75 0 1 1 .67 1.34l-8 4a.75.75 0 0 1-.68 0l-8-4a.75.75 0 0 1-.33-1Z",

        // Fluent: Memory / RAM Module 24 Regular
        [FluentGlyph.PhysicalMemory] = "M2 7.75A2.75 2.75 0 0 1 4.75 5h14.5A2.75 2.75 0 0 1 22 7.75v6.5A2.75 2.75 0 0 1 19.25 17H18v2.25a.75.75 0 0 1-1.5 0V17h-2v2.25a.75.75 0 0 1-1.5 0V17h-2v2.25a.75.75 0 0 1-1.5 0V17h-2v2.25a.75.75 0 0 1-1.5 0V17H4.75A2.75 2.75 0 0 1 2 14.25v-6.5ZM4.75 6.5c-.69 0-1.25.56-1.25 1.25v6.5c0 .69.56 1.25 1.25 1.25h14.5c.69 0 1.25-.56 1.25-1.25v-6.5c0-.69-.56-1.25-1.25-1.25H4.75ZM6 9h3v4H6V9Zm4.5 0h3v4h-3V9ZM15 9h3v4h-3V9Z",

        // Fluent: Alert / Bell 24 Regular (distinct from Warning triangle — F-039)
        [FluentGlyph.Alerts] = "M12 2a7 7 0 0 0-7 7v3.59l-1.3 2.6A1.25 1.25 0 0 0 4.82 17H8.5a3.5 3.5 0 0 0 7 0h3.68a1.25 1.25 0 0 0 1.12-1.81L19 12.59V9a7 7 0 0 0-7-7Zm0 1.5A5.5 5.5 0 0 1 17.5 9v3.76c0 .23.05.46.16.67l1.15 2.32H5.19l1.15-2.32c.11-.21.16-.44.16-.67V9A5.5 5.5 0 0 1 12 3.5Zm2 13.5a2 2 0 1 1-4 0h4Z",

        // Fluent: Flash / Profiler 24 Regular
        [FluentGlyph.Profiler] = "M12.97 2.25a.75.75 0 0 1 .69 1.03l-2.46 5.97h6.05a.75.75 0 0 1 .57 1.24l-8.25 9.75a.75.75 0 0 1-1.27-.78l2.46-6.21H5.75a.75.75 0 0 1-.6-1.2l7.25-9.5a.75.75 0 0 1 .57-.3Zm-5.7 9.5h4.6a.75.75 0 0 1 .7 1.03l-1.62 4.1 4.88-5.76h-5.72a.75.75 0 0 1-.69-1.04l1.58-3.83-3.73 5.5Z",

        // Fluent: Camera / Snapshot 24 Regular
        [FluentGlyph.Snapshots] = "M9.06 4.5a2.25 2.25 0 0 1 1.9-.99h2.08c.78 0 1.5.4 1.9 1l.98 1.5H19A2.75 2.75 0 0 1 21.75 8.75v9A2.75 2.75 0 0 1 19 20.5H5a2.75 2.75 0 0 1-2.75-2.75v-9A2.75 2.75 0 0 1 5 6h3.08l.98-1.5ZM10.96 5a.75.75 0 0 0-.63.35L9.14 7.16a.75.75 0 0 1-.63.34H5c-.69 0-1.25.56-1.25 1.25v9c0 .69.56 1.25 1.25 1.25h14c.69 0 1.25-.56 1.25-1.25v-9c0-.69-.56-1.25-1.25-1.25h-3.51a.75.75 0 0 1-.63-.34l-1.19-1.81a.75.75 0 0 0-.63-.35h-2.08ZM12 9.25a4 4 0 1 1 0 8 4 4 0 0 1 0-8Zm0 1.5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5Z",

        // Fluent: Shield / Forensics 24 Regular
        [FluentGlyph.Forensics] = "M12 2.25c.17 0 .34.06.48.17l7.5 5.25a.75.75 0 0 1 .27.58v4.5c0 4.88-3.32 8.27-7.95 9.48a.75.75 0 0 1-.6 0C7.07 21.02 3.75 17.63 3.75 12.75v-4.5c0-.24.11-.46.3-.6l7.5-5.23a.75.75 0 0 1 .45-.17ZM5.25 8.64v4.11c0 4.02 2.66 6.86 6.75 7.97 4.09-1.11 6.75-3.95 6.75-7.97V8.64L12 3.92 5.25 8.64ZM12 8.5a2.5 2.5 0 0 1 2.02 3.97l1.76 1.75a.75.75 0 1 1-1.06 1.06l-1.75-1.76A2.5 2.5 0 1 1 12 8.5Zm0 1.5a1 1 0 1 0 0 2 1 1 0 0 0 0-2Z",

        // Fluent: DocumentText 24 Regular
        [FluentGlyph.Reports] = "M6.75 2.5A2.25 2.25 0 0 0 4.5 4.75v14.5a2.25 2.25 0 0 0 2.25 2.25h10.5a2.25 2.25 0 0 0 2.25-2.25V8.56a2.25 2.25 0 0 0-.66-1.59l-3.81-3.81a2.25 2.25 0 0 0-1.59-.66H6.75ZM6 4.75c0-.41.34-.75.75-.75H13v3.25c0 1.24 1.01 2.25 2.25 2.25H18v9.75a.75.75 0 0 1-.75.75H6.75A.75.75 0 0 1 6 19.25V4.75ZM14.5 4.56 17.44 7.5H15.25a.75.75 0 0 1-.75-.75V4.56ZM8.75 12a.75.75 0 0 0 0 1.5h6.5a.75.75 0 0 0 0-1.5h-6.5Zm0 3.5a.75.75 0 0 0 0 1.5h4.5a.75.75 0 0 0 0-1.5h-4.5Z",

        // Canonical Fluent Settings 24 Regular (symmetric, closed contours — F-039)
        [FluentGlyph.Settings] = "M12 8.5a3.5 3.5 0 1 0 0 7 3.5 3.5 0 0 0 0-7ZM10 12a2 2 0 1 1 4 0 2 2 0 0 1-4 0Zm.22-9.5h3.56l.52 2.13c.56.19 1.09.42 1.59.69l1.86-1.14 2.52 2.52-1.14 1.86c.27.5.5 1.03.69 1.59L21.95 10.67v3.56l-2.13.52c-.19.56-.42 1.09-.69 1.59l1.14 1.86-2.52 2.52-1.86-1.14c-.5.27-1.03.5-1.59.69l-.52 2.13h-3.56l-.52-2.13c-.56-.19-1.09-.42-1.59-.69l-1.86 1.14-2.52-2.52 1.14-1.86c-.27-.5-.5-1.03-.69-1.59L2.05 14.23v-3.56l2.13-.52c.19-.56.42-1.09.69-1.59L3.73 6.7l2.52-2.52 1.86 1.14c.5-.27 1.03-.5 1.59-.69L10.22 2.5Zm1.16 1.5-.42 1.72-.57.17c-.61.18-1.18.43-1.72.73l-.52.3-1.51-.92-.88.88.92 1.51-.3.52c-.3.54-.55 1.11-.73 1.72l-.17.57L3.55 11.83v1.24l1.72.42.17.57c.18.61.43 1.18.73 1.72l.3.52-.92 1.51.88.88 1.51-.92.52.3c.54.3 1.11.55 1.72.73l.57.17.42 1.72h1.24l.42-1.72.57-.17c.61-.18 1.18-.43 1.72-.73l.52-.3 1.51.92.88-.88-.92-1.51.3-.52c.3-.54.55-1.11.73-1.72l.17-.57 1.72-.42v-1.24l-1.72-.42-.17-.57c-.18-.61-.43-1.18-.73-1.72l-.3-.52.92-1.51-.88-.88-1.51.92-.52-.3c-.54-.3-1.11-.55-1.72-.73l-.57-.17-.42-1.72h-1.24Z",

        // Fluent: QuestionCircle 24 Regular
        [FluentGlyph.HelpGlossary] = "M12 2.5a9.5 9.5 0 1 0 0 19 9.5 9.5 0 0 0 0-19ZM4 12a8 8 0 1 1 16 0 8 8 0 0 1-16 0Zm8 3.5a1 1 0 1 0 0 2 1 1 0 0 0 0-2ZM9.25 9.5a2.75 2.75 0 1 1 4.56 2.07c-.6.52-1.06.95-1.06 1.68a.75.75 0 0 1-1.5 0c0-1.38.86-2.16 1.56-2.78.28-.25.44-.58.44-.97a1.25 1.25 0 1 0-2.5 0 .75.75 0 0 1-1.5 0Z",

        // Distinct Diagnostics / HeartPulse Monitor 24 Regular (distinct from ShieldCheck — F-039)
        [FluentGlyph.Diagnostics] = "M4.75 3.5A2.25 2.25 0 0 0 2.5 5.75v12.5A2.25 2.25 0 0 0 4.75 20.5h14.5a2.25 2.25 0 0 0 2.25-2.25V5.75A2.25 2.25 0 0 0 19.25 3.5H4.75ZM4 5.75c0-.41.34-.75.75-.75h14.5c.41 0 .75.34.75.75v12.5a.75.75 0 0 1-.75.75H4.75a.75.75 0 0 1-.75-.75V5.75ZM9.7 7.45a.75.75 0 0 1 .72.52l1.85 6.08 1.23-2.9a.75.75 0 0 1 .69-.45h3.56a.75.75 0 0 1 0 1.5h-3.06l-1.88 4.44a.75.75 0 0 1-1.41-.07L9.65 10.8l-.87 2.18a.75.75 0 0 1-.7.47H6.25a.75.75 0 0 1 0-1.5h1.32l1.42-3.55a.75.75 0 0 1 .71-.95Z",

        // Fluent: Search 24 Regular
        [FluentGlyph.Search] = "M10 3a7 7 0 1 0 4.39 12.45l4.58 4.58a.75.75 0 1 0 1.06-1.06l-4.58-4.58A7 7 0 0 0 10 3Zm-5.5 7a5.5 5.5 0 1 1 11 0 5.5 5.5 0 0 1-11 0Z",

        // Fluent: WindowConsole / Command Palette 24 Regular
        [FluentGlyph.CommandPalette] = "M5.25 4A2.25 2.25 0 0 0 3 6.25v11.5A2.25 2.25 0 0 0 5.25 20h13.5A2.25 2.25 0 0 0 21 17.75V6.25A2.25 2.25 0 0 0 18.75 4H5.25ZM4.5 6.25c0-.41.34-.75.75-.75h13.5c.41 0 .75.34.75.75v11.5a.75.75 0 0 1-.75.75H5.25a.75.75 0 0 1-.75-.75V6.25Zm2.72 3.03a.75.75 0 0 1 1.06 0l2.25 2.25a.75.75 0 0 1 0 1.06l-2.25 2.25a.75.75 0 1 1-1.06-1.06L8.94 12 7.22 10.34a.75.75 0 0 1 0-1.06ZM12.25 14a.75.75 0 0 0 0 1.5h4.5a.75.75 0 0 0 0-1.5h-4.5Z",

        // Fluent: Play 24 Filled
        [FluentGlyph.Play] = "M6.5 5.65c0-1.36 1.48-2.2 2.65-1.5l10.22 6.1c1.14.68 1.14 2.32 0 3l-10.22 6.1A1.75 1.75 0 0 1 6.5 17.85V5.65Z",

        // Fluent: Pause 24 Filled
        [FluentGlyph.Pause] = "M6.75 4.5A1.75 1.75 0 0 1 8.5 6.25v11.5a1.75 1.75 0 1 1-3.5 0V6.25c0-.97.78-1.75 1.75-1.75Zm10.5 0A1.75 1.75 0 0 1 19 6.25v11.5a1.75 1.75 0 1 1-3.5 0V6.25c0-.97.78-1.75 1.75-1.75Z",

        // True Camera + Plus badge for Create Snapshot (F-039)
        [FluentGlyph.CameraAdd] = "M9 4.5a2 2 0 0 1 1.66-.89h2.68c.66 0 1.28.33 1.66.89l.86 1.25H18.5A2.75 2.75 0 0 1 21.25 8.5v3.25a.75.75 0 0 1-1.5 0V8.5c0-.69-.56-1.25-1.25-1.25h-3.44l-1.25-1.8a.5.5 0 0 0-.42-.2h-2.78a.5.5 0 0 0-.42.2L9.94 7.25H5.5c-.69 0-1.25.56-1.25 1.25v8.5c0 .69.56 1.25 1.25 1.25h6.25a.75.75 0 0 1 0 1.5H5.5A2.75 2.75 0 0 1 2.75 17V8.5A2.75 2.75 0 0 1 5.5 5.75h2.64L9 4.5ZM12 9.5a3.25 3.25 0 1 0 0 6.5 3.25 3.25 0 0 0 0-6.5Zm0 1.5a1.75 1.75 0 1 1 0 3.5 1.75 1.75 0 0 1 0-3.5Zm6.25 3.5a.75.75 0 0 1 .75.75v2h2a.75.75 0 0 1 0 1.5h-2v2a.75.75 0 0 1-1.5 0v-2h-2a.75.75 0 0 1 0-1.5h2v-2a.75.75 0 0 1 .75-.75Z",

        // Fluent: WeatherMoon 24 Regular
        [FluentGlyph.ThemeDark] = "M12.1 3.1a.75.75 0 0 1 .27.95 6.75 6.75 0 0 0 7.58 9.65.75.75 0 0 1 .93.96A8.75 8.75 0 1 1 11.15 2.83a.75.75 0 0 1 .95.27ZM10.42 4.9a7.25 7.25 0 1 0 8.68 10.7 8.25 8.25 0 0 1-8.68-10.7Z",

        // Full 8-ray symmetric WeatherSunny 24 Regular (exact 1.5px radial symmetry — F-039)
        [FluentGlyph.ThemeLight] = "M12 7a5 5 0 1 0 0 10 5 5 0 0 0 0-10Zm0 1.5a3.5 3.5 0 1 1 0 7 3.5 3.5 0 0 1 0-7Zm0-6a.75.75 0 0 1 .75.75v1.5a.75.75 0 0 1-1.5 0v-1.5A.75.75 0 0 1 12 2.5Zm0 16a.75.75 0 0 1 .75.75v1.5a.75.75 0 0 1-1.5 0v-1.5a.75.75 0 0 1 .75-.75ZM2.5 12a.75.75 0 0 1 .75-.75h1.5a.75.75 0 0 1 0 1.5h-1.5A.75.75 0 0 1 2.5 12Zm16 0a.75.75 0 0 1 .75-.75h1.5a.75.75 0 0 1 0 1.5h-1.5a.75.75 0 0 1-.75-.75ZM5.28 5.28a.75.75 0 0 1 1.06 0l1.06 1.06a.75.75 0 1 1-1.06 1.06L5.28 6.34a.75.75 0 0 1 0-1.06Zm11.32 11.32a.75.75 0 0 1 1.06 0l1.06 1.06a.75.75 0 1 1-1.06 1.06l-1.06-1.06a.75.75 0 0 1 0-1.06ZM18.72 5.28a.75.75 0 0 1 0 1.06l-1.06 1.06a.75.75 0 1 1-1.06-1.06l1.06-1.06a.75.75 0 0 1 1.06 0ZM7.4 16.6a.75.75 0 0 1 0 1.06l-1.06 1.06a.75.75 0 1 1-1.06-1.06l1.06-1.06a.75.75 0 0 1 1.06 0Z",

        // Fluent: DarkTheme / Contrast 24 Regular
        [FluentGlyph.ThemeContrast] = "M12 2.5a9.5 9.5 0 1 0 0 19 9.5 9.5 0 0 0 0-19ZM4 12a8 8 0 0 1 8-8v16a8 8 0 0 1-8-8Z",

        // Fluent: Globe 24 Regular
        [FluentGlyph.Globe] = "M12 2.5a9.5 9.5 0 1 0 0 19 9.5 9.5 0 0 0 0-19Zm6.94 6h-3.03a13.7 13.7 0 0 0-1.5-4.12A8.03 8.03 0 0 1 18.94 8.5ZM12 4.08c.98 1.15 1.75 2.68 2.18 4.42H9.82C10.25 6.76 11.02 5.23 12 4.08ZM4.25 14a7.9 7.9 0 0 1 0-4h3.58a15.3 15.3 0 0 0 0 4H4.25Zm.81 1.5h3.03c.32 1.52.84 2.93 1.5 4.12A8.03 8.03 0 0 1 5.06 15.5Zm3.03-7H5.06a8.03 8.03 0 0 1 4.53-4.12c-.66 1.19-1.18 2.6-1.5 4.12ZM12 19.92c-.98-1.15-1.75-2.68-2.18-4.42h4.36c-.43 1.74-1.2 3.27-2.18 4.42ZM14.52 14H9.48a13.7 13.7 0 0 1 0-4h5.04a13.7 13.7 0 0 1 0 4Zm-.11 5.62c.66-1.19 1.18-2.6 1.5-4.12h3.03a8.03 8.03 0 0 1-4.53 4.12Zm1.76-5.62a15.3 15.3 0 0 0 0-4h3.58a7.9 7.9 0 0 1 0 4h-3.58Z",

        // Fluent: ChevronDown 24 Regular
        [FluentGlyph.ChevronDown] = "M4.22 8.47a.75.75 0 0 1 1.06 0L12 15.19l6.72-6.72a.75.75 0 1 1 1.06 1.06l-7.25 7.25a.75.75 0 0 1-1.06 0L4.22 9.53a.75.75 0 0 1 0-1.06Z",

        // Fluent: ChevronRight 24 Regular
        [FluentGlyph.ChevronRight] = "M8.47 4.22a.75.75 0 0 1 1.06 0l7.25 7.25a.75.75 0 0 1 0 1.06l-7.25 7.25a.75.75 0 1 1-1.06-1.06L15.19 12 8.47 5.28a.75.75 0 0 1 0-1.06Z",

        // Fluent: Dismiss 24 Regular
        [FluentGlyph.Dismiss] = "M4.47 4.47a.75.75 0 0 1 1.06 0L12 10.94l6.47-6.47a.75.75 0 1 1 1.06 1.06L13.06 12l6.47 6.47a.75.75 0 1 1-1.06 1.06L12 13.06l-6.47 6.47a.75.75 0 0 1-1.06-1.06L10.94 12 4.47 5.53a.75.75 0 0 1 0-1.06Z",

        // Fluent: Copy 24 Regular
        [FluentGlyph.Copy] = "M5.5 4.75A2.25 2.25 0 0 1 7.75 2.5h9.5A2.25 2.25 0 0 1 19.5 4.75v12.5a2.25 2.25 0 0 1-2.25 2.25h-9.5a2.25 2.25 0 0 1-2.25-2.25V4.75ZM7.75 4a.75.75 0 0 0-.75.75v12.5c0 .41.34.75.75.75h9.5a.75.75 0 0 0 .75-.75V4.75a.75.75 0 0 0-.75-.75h-9.5ZM3 7.75a.75.75 0 0 1 1.5 0v10.5c0 .97.78 1.75 1.75 1.75h8.5a.75.75 0 0 1 0 1.5h-8.5A3.25 3.25 0 0 1 3 18.25V7.75Z",

        // Fluent: Save 24 Regular
        [FluentGlyph.Save] = "M5.25 3A2.25 2.25 0 0 0 3 5.25v13.5A2.25 2.25 0 0 0 5.25 21h13.5A2.25 2.25 0 0 0 21 18.75V7.81a2.25 2.25 0 0 0-.66-1.59l-2.56-2.56A2.25 2.25 0 0 0 16.19 3H5.25ZM4.5 5.25c0-.41.34-.75.75-.75h2.25v4c0 .97.78 1.75 1.75 1.75h5.5A1.75 1.75 0 0 0 16.5 8.5V4.52l3 3v11.23a.75.75 0 0 1-.75.75h-1.25v-5.75a1.75 1.75 0 0 0-1.75-1.75h-7.5a1.75 1.75 0 0 0-1.75 1.75V19.5H5.25a.75.75 0 0 1-.75-.75V5.25ZM9 4.5h6v4a.25.25 0 0 1-.25.25h-5.5A.25.25 0 0 1 9 8.5v-4Zm-1 9.25a.25.25 0 0 1 .25-.25h7.5a.25.25 0 0 1 .25.25V19.5H8v-5.75Z",

        // Fluent: Warning 24 Regular
        [FluentGlyph.Warning] = "M10.03 3.66a2.25 2.25 0 0 1 3.94 0l7.5 13.75A2.25 2.25 0 0 1 19.5 20.75h-15a2.25 2.25 0 0 1-1.97-3.34l7.5-13.75Zm2.62.72a.75.75 0 0 0-1.3 0l-7.5 13.75a.75.75 0 0 0 .65 1.12h15a.75.75 0 0 0 .65-1.12l-7.5-13.75ZM12 9a.75.75 0 0 1 .75.75v4.5a.75.75 0 0 1-1.5 0v-4.5A.75.75 0 0 1 12 9Zm0 8.5a1 1 0 1 1 0-2 1 1 0 0 1 0 2Z",

        // Fluent: ShieldCheckmark 24 Regular
        [FluentGlyph.ShieldCheck] = "M12 2.25c.17 0 .34.06.48.17l7.5 5.25a.75.75 0 0 1 .27.58v4.5c0 4.88-3.32 8.27-7.95 9.48a.75.75 0 0 1-.6 0C7.07 21.02 3.75 17.63 3.75 12.75v-4.5c0-.24.11-.46.3-.6l7.5-5.23a.75.75 0 0 1 .45-.17ZM5.25 8.64v4.11c0 4.02 2.66 6.86 6.75 7.97 4.09-1.11 6.75-3.95 6.75-7.97V8.64L12 3.92 5.25 8.64Zm10.28 1.58a.75.75 0 0 1 0 1.06l-4.25 4.25a.75.75 0 0 1-1.06 0l-2-2a.75.75 0 1 1 1.06-1.06l1.47 1.47 3.72-3.72a.75.75 0 0 1 1.06 0Z",

        // Process Fallback Category Icons (Fluent Window / Globe / System / Code)
        [FluentGlyph.AppFallback] = "M5.25 4A2.25 2.25 0 0 0 3 6.25v11.5A2.25 2.25 0 0 0 5.25 20h13.5A2.25 2.25 0 0 0 21 17.75V6.25A2.25 2.25 0 0 0 18.75 4H5.25ZM4.5 9h15v8.75a.75.75 0 0 1-.75.75H5.25a.75.75 0 0 1-.75-.75V9Zm0-2.75c0-.41.34-.75.75-.75h13.5c.41 0 .75.34.75.75V7.5h-15V6.25Z",
        [FluentGlyph.BrowserFallback] = "M12 2.5a9.5 9.5 0 1 0 0 19 9.5 9.5 0 0 0 0-19ZM4 12a8 8 0 1 1 16 0 8 8 0 0 1-16 0Zm8-5a5 5 0 1 0 0 10 5 5 0 0 0 0-10Z",
        [FluentGlyph.SystemFallback] = "M7.5 3A1.5 1.5 0 0 0 6 4.5V6H4.5A1.5 1.5 0 0 0 3 7.5v9A1.5 1.5 0 0 0 4.5 18H6v1.5A1.5 1.5 0 0 0 7.5 21h9a1.5 1.5 0 0 0 1.5-1.5V18h1.5a1.5 1.5 0 0 0 1.5-1.5v-9A1.5 1.5 0 0 0 19.5 6H18V4.5A1.5 1.5 0 0 0 16.5 3h-9ZM7.5 7.5h9v9h-9v-9Z",
        [FluentGlyph.CodeFallback] = "M8.28 6.47a.75.75 0 0 1 0 1.06L3.81 12l4.47 4.47a.75.75 0 1 1-1.06 1.06l-5-5a.75.75 0 0 1 0-1.06l5-5a.75.75 0 0 1 1.06 0Zm7.44 0a.75.75 0 0 1 1.06 0l5 5a.75.75 0 0 1 0 1.06l-5 5a.75.75 0 1 1-1.06-1.06L20.19 12l-4.47-4.47a.75.75 0 0 1 0-1.06Z",
        [FluentGlyph.Heap] = "M4 5.75A1.75 1.75 0 0 1 5.75 4h12.5A1.75 1.75 0 0 1 20 5.75v3.5A1.75 1.75 0 0 1 18.25 11H5.75A1.75 1.75 0 0 1 4 9.25v-3.5ZM4 14.75A1.75 1.75 0 0 1 5.75 13h12.5A1.75 1.75 0 0 1 20 14.75v3.5A1.75 1.75 0 0 1 18.25 20H5.75A1.75 1.75 0 0 1 4 18.25v-3.5Z",
        [FluentGlyph.Folder] = "M2.75 6A2.25 2.25 0 0 1 5 3.75h4.24c.6 0 1.17.24 1.59.66l1.09 1.09H19A2.25 2.25 0 0 1 21.25 7.75v10A2.25 2.25 0 0 1 19 20H5a2.25 2.25 0 0 1-2.25-2.25V6Z"
    };

    public static FrameworkElement Create(FluentGlyph glyph, double size, Brush foreground, bool mirrorInRtl = false)
    {
        string data = Paths.TryGetValue(glyph, out var pathData) ? pathData : Paths[FluentGlyph.AppFallback];

        Geometry geom = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data);
        var path = new PathShape
        {
            Data = geom,
            Fill = foreground,
            Stretch = Stretch.None
        };

        // Preserve canonical 24x24 viewBox optical margins inside a fixed 24x24 Canvas (F-039)
        var canvas = new Canvas
        {
            Width = 24,
            Height = 24
        };
        canvas.Children.Add(path);

        var viewbox = new Viewbox
        {
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = canvas
        };

        if (!mirrorInRtl)
        {
            viewbox.FlowDirection = FlowDirection.LeftToRight;
        }

        return viewbox;
    }

    public static FluentGlyph FallbackGlyphForProcess(string category, bool isRestricted)
    {
        if (isRestricted) return FluentGlyph.SystemFallback;
        string c = category.ToLowerInvariant();
        if (c.Contains("browser")) return FluentGlyph.BrowserFallback;
        if (c.Contains("ide") || c.Contains("toolchain")) return FluentGlyph.CodeFallback;
        if (c.Contains("windows") || c.Contains("service")) return FluentGlyph.SystemFallback;
        if (c.Contains("ramyxen")) return FluentGlyph.BrandMark;
        return FluentGlyph.AppFallback;
    }
}
