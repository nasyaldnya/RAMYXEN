using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace RAMYXEN.Installer;

internal sealed record InstallManifest(
    string InstallDir,
    string InstalledExe,
    string UninstallerExe,
    string? DesktopShortcut,
    string? StartMenuShortcut,
    string RegistrySubKey,
    string Version,
    DateTimeOffset InstalledUtc);

public static class Program
{
    private const string DefaultUninstallRegSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RAMYXEN";
    private const string ManifestFileName = ".ramyxen-install-manifest.json";
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const int SHCNE_CREATE = 0x00000002;
    private const int SHCNE_DELETE = 0x00000004;
    private const uint SHCNF_IDLIST = 0x0000;
    private const uint SHCNF_PATHW = 0x0005;
    private const uint SHCNF_FLUSH = 0x1000;
    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, nint dwItem1, nint dwItem2);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileExW(string lpExistingFileName, string? lpNewFileName, uint dwFlags);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, nint pfd, int fFlags);
        void GetIDList(out nint ppidl);
        void SetIDList(nint pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(nint hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        bool silent = HasFlag(args, "--silent") || HasFlag(args, "/S");
        bool uninstall = HasFlag(args, "--uninstall");
        bool uninstallStage2 = HasFlag(args, "--uninstall-stage2");
        bool launchAfter = HasFlag(args, "--launch");
        bool noShortcuts = HasFlag(args, "--no-shortcuts");

        string? customDir = GetArgValue(args, "--install-dir");
        string? customShortcutDir = GetArgValue(args, "--shortcut-dir");
        string? customRegSubKey = GetArgValue(args, "--registry-subkey");
        int parentPid = int.TryParse(GetArgValue(args, "--parent-pid"), out int pp) ? pp : 0;

        string? selfExeForDefault = Environment.ProcessPath;
        string defaultDir;
        if (customDir is null && uninstall && !string.IsNullOrEmpty(selfExeForDefault) &&
            Path.GetFileName(selfExeForDefault).Equals("Uninstall-RAMYXEN.exe", StringComparison.OrdinalIgnoreCase))
        {
            defaultDir = Path.GetDirectoryName(selfExeForDefault) ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RAMYXEN");
        }
        else
        {
            defaultDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "RAMYXEN");
        }

        string rawInstallDir = customDir ?? defaultDir;
        if (IsUnsafeRootPath(rawInstallDir))
        {
            Console.Error.WriteLine($"[RAMYXEN-Setup] Refusing unsafe root or drive-relative path: {rawInstallDir}");
            return 4;
        }
        string installDir = Path.GetFullPath(rawInstallDir).TrimEnd('\\', '/');
        string regSubKey = string.IsNullOrWhiteSpace(customRegSubKey)
            ? DefaultUninstallRegSubKey
            : customRegSubKey.Trim().TrimStart('\\', '/').TrimEnd('\\', '/');

        try
        {
            if (uninstallStage2)
            {
                return RunUninstallStage2(installDir, parentPid, silent);
            }

            if (uninstall)
            {
                return RunUninstallStage1(installDir, regSubKey, silent);
            }

            return RunInstall(installDir, customShortcutDir, regSubKey, noShortcuts, launchAfter, silent);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RAMYXEN-Setup] Fatal error: {ex.Message}");
            return 1;
        }
    }

    private static int RunInstall(
        string installDir,
        string? customShortcutDir,
        string regSubKey,
        bool noShortcuts,
        bool launchAfter,
        bool silent)
    {
        string appVersion = GetAppVersionString();

        if (!silent)
        {
            Console.WriteLine("=================================================================");
            Console.WriteLine($"  RAMYXEN v{appVersion} — Windows Memory Intelligence Platform Setup");
            Console.WriteLine("  Tagline: Understand Every Byte.");
            Console.WriteLine("=================================================================");
            Console.WriteLine($"Target Installation Directory: {installDir}");
        }

        if (IsUnsafeRootPath(installDir))
        {
            Console.Error.WriteLine($"[RAMYXEN-Setup] Refusing unsafe installation path: {installDir}");
            return 4;
        }

        bool dirExistedBefore = Directory.Exists(installDir);
        if (dirExistedBefore)
        {
            // Close running RAMYXEN/HeapWorker instances inside installDir before in-place upgrade (REL-NEW-002)
            TerminateOwnedProcesses(installDir, silent);
        }
        else
        {
            Directory.CreateDirectory(installDir);
        }

        try
        {
            using Stream? payloadStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RAMYXEN-Portable-win-x64.zip");
            if (payloadStream is not null)
            {
                if (!silent) Console.WriteLine("[RAMYXEN-Setup] Extracting self-contained RAMYXEN runtime payload...");
                using var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    string destPath = Path.GetFullPath(Path.Combine(installDir, entry.FullName));
                    if (!IsPathUnderDirectory(destPath, installDir))
                        throw new InvalidOperationException("Zip Slip path traversal blocked.");
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }
            else
            {
                string adjacentFolder = Path.Combine(AppContext.BaseDirectory, "RAMYXEN-win-x64");
                if (Directory.Exists(adjacentFolder))
                {
                    CopyDirectory(adjacentFolder, installDir);
                }
                else if (!File.Exists(Path.Combine(installDir, "RAMYXEN.exe")))
                {
                    Console.Error.WriteLine("[RAMYXEN-Setup] Embedded payload not found and no adjacent RAMYXEN-win-x64 folder exists.");
                    if (!dirExistedBefore) TryRollbackFreshInstallDir(installDir);
                    return 2;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RAMYXEN-Setup] Extraction failed: {ex.Message}");
            if (!dirExistedBefore) TryRollbackFreshInstallDir(installDir);
            return 2;
        }

        string installedExe = Path.Combine(installDir, "RAMYXEN.exe");
        if (!File.Exists(installedExe))
        {
            Console.Error.WriteLine($"[RAMYXEN-Setup] Verification failed: {installedExe} not found after extraction.");
            return 3;
        }

        string installedIco = Path.Combine(installDir, "Assets", "RAMYXEN.ico");
        string iconPath = File.Exists(installedIco) ? installedIco : installedExe;
        string iconLocationWithIndex = $"{iconPath},0";

        string uninstallerPath = Path.Combine(installDir, "Uninstall-RAMYXEN.exe");
        string? selfExe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(selfExe) && File.Exists(selfExe))
        {
            if (!string.Equals(Path.GetFullPath(selfExe), Path.GetFullPath(uninstallerPath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(selfExe, uninstallerPath, overwrite: true);
            }
        }

        string? startMenuShortcut = null;
        string? desktopShortcut = null;
        if (!noShortcuts)
        {
            if (!string.IsNullOrWhiteSpace(customShortcutDir))
            {
                Directory.CreateDirectory(customShortcutDir);
                startMenuShortcut = Path.Combine(customShortcutDir, "RAMYXEN-StartMenu.lnk");
                desktopShortcut = Path.Combine(customShortcutDir, "RAMYXEN-Desktop.lnk");
            }
            else
            {
                startMenuShortcut = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    "Programs",
                    "RAMYXEN.lnk");
                desktopShortcut = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "RAMYXEN.lnk");
            }

            if (!CreateShortcutCom(startMenuShortcut, installedExe, "", installDir, "RAMYXEN — Understand Every Byte.", iconPath, 0, out string smErr))
            {
                Console.Error.WriteLine($"[RAMYXEN-Setup] Failed creating Start Menu shortcut: {smErr}");
                return 6;
            }
            if (!CreateShortcutCom(desktopShortcut, installedExe, "", installDir, "RAMYXEN — Understand Every Byte.", iconPath, 0, out string dtErr))
            {
                Console.Error.WriteLine($"[RAMYXEN-Setup] Failed creating Desktop shortcut: {dtErr}");
                return 6;
            }
            NotifyShellPath(SHCNE_CREATE, startMenuShortcut);
            NotifyShellPath(SHCNE_CREATE, desktopShortcut);
        }

        long estimatedSizeKb = ComputeDirectorySizeKb(installDir);
        RegisterAddRemovePrograms(regSubKey, installDir, uninstallerPath, iconLocationWithIndex, appVersion, estimatedSizeKb);

        // Persist install manifest so uninstaller always knows exact shortcuts & registry key (even for custom --install-dir)
        var manifest = new InstallManifest(
            InstallDir: installDir,
            InstalledExe: installedExe,
            UninstallerExe: uninstallerPath,
            DesktopShortcut: desktopShortcut,
            StartMenuShortcut: startMenuShortcut,
            RegistrySubKey: regSubKey,
            Version: appVersion,
            InstalledUtc: DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(installDir, ManifestFileName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);

        NotifyShellIconRefresh();

        if (!silent)
        {
            Console.WriteLine($"[RAMYXEN-Setup] Successfully installed RAMYXEN v{appVersion} to: {installDir}");
            Console.WriteLine($"[RAMYXEN-Setup] Executable    : {installedExe}");
            Console.WriteLine($"[RAMYXEN-Setup] Icon Location : {iconLocationWithIndex}");
            Console.WriteLine($"[RAMYXEN-Setup] User data in %LOCALAPPDATA%\\RAMYXEN is preserved across upgrades and uninstalls.");
        }

        if (launchAfter)
        {
            Process.Start(new ProcessStartInfo(installedExe) { WorkingDirectory = installDir, UseShellExecute = true });
        }

        return 0;
    }

    /// <summary>
    /// Stage 1 Uninstall (F-001): Validates install marker, removes shortcuts &amp; Add/Remove Programs registration FIRST,
    /// and if executing from inside installDir, copies itself to %TEMP% and spawns Stage 2 so the running EXE never tries
    /// to delete itself while locked!
    /// </summary>
    private static int RunUninstallStage1(string installDir, string defaultRegSubKey, bool silent)
    {
        SweepOrphanedTempHelpers();

        if (IsUnsafeRootPath(installDir))
        {
            Console.Error.WriteLine("[RAMYXEN-Setup] Refusing to uninstall a root or system drive path.");
            return 4;
        }

        // Load install manifest if present so custom shortcuts & registry subkeys are always cleaned accurately.
        // IMPORTANT (R2-NEW-005): When manifest exists and explicitly recorded null (--no-shortcuts), NEVER fall back to default user shortcuts!
        InstallManifest? manifest = TryLoadManifest(installDir);
        string effectiveRegSubKey = !string.IsNullOrWhiteSpace(manifest?.RegistrySubKey)
            ? manifest!.RegistrySubKey
            : defaultRegSubKey;
        string? desktopShortcut = manifest is not null
            ? manifest.DesktopShortcut
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "RAMYXEN.lnk");
        string? startMenuShortcut = manifest is not null
            ? manifest.StartMenuShortcut
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "RAMYXEN.lnk");

        if (Directory.Exists(installDir))
        {
            bool hasMarker = File.Exists(Path.Combine(installDir, "RAMYXEN.exe"))
                          || File.Exists(Path.Combine(installDir, ManifestFileName))
                          || File.Exists(Path.Combine(installDir, "Uninstall-RAMYXEN.exe"));
            if (!hasMarker)
            {
                Console.Error.WriteLine($"[RAMYXEN-Setup] Refusing to delete directory without RAMYXEN installation markers: {installDir}");
                return 5;
            }
        }

        // Close BOTH RAMYXEN.exe and RAMYXEN.HeapWorker.exe running inside this exact installDir (R2-NEW-005, R2-NEW-019)
        TerminateOwnedProcesses(installDir, silent);

        if (!Directory.Exists(installDir))
        {
            DeleteShortcutSafe(startMenuShortcut, installDir);
            DeleteShortcutSafe(desktopShortcut, installDir);
            UnregisterAddRemoveProgramsSafe(effectiveRegSubKey, installDir);
            NotifyShellIconRefresh();
            if (!silent) Console.WriteLine("[RAMYXEN-Setup] Installation directory already absent; shortcuts & registration cleaned.");
            return 0;
        }

        // Determine whether we are currently executing from inside installDir (Add/Remove Programs UninstallString case!)
        string? selfExe = Environment.ProcessPath;
        bool runningInsideInstallDir = !string.IsNullOrEmpty(selfExe) && IsPathUnderDirectory(selfExe, installDir);

        if (runningInsideInstallDir && !string.IsNullOrEmpty(selfExe))
        {
            string fullSelf = Path.GetFullPath(selfExe);
            string fullManifest = Path.GetFullPath(Path.Combine(installDir, ManifestFileName));

            // 1. Delete non-self, non-manifest payload files safely WITHOUT traversing NTFS junctions/symlinks (REL-NEW-001, REL-NEW-008)
            var (files, reparseDirs, _) = EnumerateTreeSafeNoReparse(installDir);
            var lockedRelPaths = new List<string>();
            bool allLockedScheduled = true;
            int lastWin32Err = 0;

            foreach (string file in files)
            {
                string fullFile = Path.GetFullPath(file);
                if (string.Equals(fullFile, fullSelf, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fullFile, fullManifest, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch
                {
                    lockedRelPaths.Add(Path.GetRelativePath(installDir, file));
                    if (!MoveFileExW(file, null, MOVEFILE_DELAY_UNTIL_REBOOT))
                    {
                        allLockedScheduled = false;
                        lastWin32Err = Marshal.GetLastWin32Error();
                    }
                }
            }

            // Unlink any reparse-point directories safely without recursing into their external targets (REL-NEW-008)
            foreach (string rpDir in reparseDirs)
            {
                try { Directory.Delete(rpDir, recursive: false); } catch { }
            }

            // 2. REL-NEW-001: If locked files remain AND cannot be scheduled for reboot (non-elevated Standard User),
            //    DO NOT orphan the installation! Preserve HKCU Uninstall registration, shortcuts, Uninstall-RAMYXEN.exe,
            //    and the install manifest so the user can close the locking app and cleanly retry uninstall.
            if (lockedRelPaths.Count > 0 && !allLockedScheduled)
            {
                string sampleList = string.Join(", ", lockedRelPaths.Take(5));
                if (lockedRelPaths.Count > 5) sampleList += $" (+{lockedRelPaths.Count - 5} more)";
                Console.Error.WriteLine($"[RAMYXEN-Setup] Uninstall incomplete (exit 1603): {lockedRelPaths.Count} file(s) are locked by another process: {sampleList} (Win32 error {lastWin32Err}).");
                Console.Error.WriteLine($"[RAMYXEN-Setup] Recovery: Close any application holding files in '{installDir}', then retry uninstall from Windows Settings (Apps & Features) or by running '{fullSelf} --uninstall'.");
                Console.Error.WriteLine("[RAMYXEN-Setup] Add/Remove Programs registration and Uninstall-RAMYXEN.exe have been preserved so retry remains available.");
                return 1603;
            }

            // 3. Payload removal succeeded (or elevated MoveFileExW scheduled all locked files for reboot):
            //    Now remove shortcuts, HKCU Uninstall registration, and manifest, then spawn Stage 2 to remove Uninstall-RAMYXEN.exe & installDir.
            DeleteShortcutSafe(startMenuShortcut, installDir);
            DeleteShortcutSafe(desktopShortcut, installDir);
            UnregisterAddRemoveProgramsSafe(effectiveRegSubKey, installDir);
            NotifyShellIconRefresh();
            try { if (File.Exists(fullManifest)) File.Delete(fullManifest); } catch { }

            string tempHelper = Path.Combine(Path.GetTempPath(), $"RAMYXEN-Uninstall-Helper-{Guid.NewGuid():N}.exe");
            File.Copy(selfExe, tempHelper, overwrite: true);

            var psi = new ProcessStartInfo
            {
                FileName = tempHelper,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            };
            psi.ArgumentList.Add("--uninstall-stage2");
            psi.ArgumentList.Add("--parent-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add("--install-dir");
            psi.ArgumentList.Add(installDir);
            if (silent) psi.ArgumentList.Add("--silent");

            var child = Process.Start(psi);
            if (child is null)
            {
                try { File.Delete(tempHelper); } catch { }
                Console.Error.WriteLine("[RAMYXEN-Setup] Failed to launch Stage 2 cleanup helper.");
                return 7;
            }

            if (lockedRelPaths.Count > 0)
            {
                Console.Error.WriteLine($"[RAMYXEN-Setup] Partial cleanup: {lockedRelPaths.Count} locked file(s) scheduled for deletion after restart.");
                return 3010; // Honest ERROR_SUCCESS_REBOOT_REQUIRED (only when MoveFileExW actually succeeded)
            }

            if (!silent)
                Console.WriteLine($"[RAMYXEN-Setup] Launched Stage 2 cleanup helper (PID {child.Id}); exiting Stage 1 to unlock binaries.");
            return 0;
        }

        // Running externally (e.g. RAMYXEN-Setup.exe --uninstall): delete with lock-aware verification FIRST;
        // only remove HKCU registration & shortcuts if cleanup succeeded (0) or scheduled reboot (3010) (REL-NEW-001).
        int extRc = DeleteInstallDirectoryWithVerification(installDir, silent, preserveManifestAndUninstallerOnFailure: true);
        if (extRc is 0 or 3010)
        {
            DeleteShortcutSafe(startMenuShortcut, installDir);
            DeleteShortcutSafe(desktopShortcut, installDir);
            UnregisterAddRemoveProgramsSafe(effectiveRegSubKey, installDir);
            NotifyShellIconRefresh();
        }
        return extRc;
    }

    private static int RunUninstallStage2(string installDir, int parentPid, bool silent)
    {
        if (parentPid > 0)
        {
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                parent.WaitForExit(8000);
            }
            catch
            {
                // Parent already exited
            }
        }

        int rc = DeleteInstallDirectoryWithVerification(installDir, silent);

        // Reliably delete temp helper EXE after Stage 2 exits even for non-elevated Standard Users (R2-NEW-004)
        string? selfExe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(selfExe) && File.Exists(selfExe))
        {
            ScheduleStandardUserTempSelfDelete(selfExe);
        }

        return rc;
    }

    private static void ScheduleStandardUserTempSelfDelete(string helperPath)
    {
        try
        {
            string fullHelper = Path.GetFullPath(helperPath);
            string tempDir = Path.GetFullPath(Path.GetTempPath());
            string fileName = Path.GetFileName(fullHelper);
            // Strict safety check: ONLY ever self-delete our exact GUID-named helper inside %TEMP%
            if (!IsPathUnderDirectory(fullHelper, tempDir) ||
                !fileName.StartsWith("RAMYXEN-Uninstall-Helper-", StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = $"/d /c \"ping -n 3 127.0.0.1 >nul & del /f /q \"{fullHelper}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = tempDir
            };
            Process.Start(psi);
        }
        catch { }
    }

    private static void SweepOrphanedTempHelpers()
    {
        try
        {
            string tempDir = Path.GetTempPath();
            string? selfFull = Environment.ProcessPath is not null ? Path.GetFullPath(Environment.ProcessPath) : null;
            DateTime cutoffUtc = DateTime.UtcNow.AddSeconds(-30);
            foreach (string f in Directory.GetFiles(tempDir, "RAMYXEN-Uninstall-Helper-*.exe"))
            {
                try
                {
                    if (selfFull is not null && string.Equals(Path.GetFullPath(f), selfFull, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (File.GetLastWriteTimeUtc(f) < cutoffUtc)
                        File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }

    private static void TerminateOwnedProcesses(string installDir, bool silent)
    {
        foreach (string procName in new[] { "RAMYXEN", "RAMYXEN.HeapWorker" })
        {
            foreach (var p in Process.GetProcessesByName(procName))
            {
                try
                {
                    string? procPath = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(procPath) && IsPathUnderDirectory(procPath, installDir))
                    {
                        if (!silent) Console.WriteLine($"[RAMYXEN-Setup] Closing running {procName} instance (PID {p.Id})...");
                        if (procName == "RAMYXEN") p.CloseMainWindow();
                        if (!p.WaitForExit(3000))
                            p.Kill(entireProcessTree: true);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
    }

    private static void TryRollbackFreshInstallDir(string installDir)
    {
        try
        {
            if (!IsUnsafeRootPath(installDir) && Directory.Exists(installDir))
                Directory.Delete(installDir, recursive: true);
        }
        catch { }
    }

    /// <summary>
    /// Walks installDir safely without following NTFS junctions or directory symlinks (REL-NEW-008).
    /// </summary>
    private static (List<string> Files, List<string> ReparseDirs, List<string> NormalDirs) EnumerateTreeSafeNoReparse(string rootDir)
    {
        var files = new List<string>();
        var reparseDirs = new List<string>();
        var normalDirs = new List<string>();
        var stack = new Stack<string>();
        stack.Push(rootDir);

        while (stack.Count > 0)
        {
            string current = stack.Pop();
            try
            {
                foreach (string f in Directory.GetFiles(current))
                    files.Add(f);
            }
            catch { }

            try
            {
                foreach (string sub in Directory.GetDirectories(current))
                {
                    try
                    {
                        var attrs = File.GetAttributes(sub);
                        if ((attrs & FileAttributes.ReparsePoint) != 0)
                        {
                            reparseDirs.Add(sub);
                            continue; // NEVER recurse into junctions or symlinks!
                        }
                    }
                    catch
                    {
                        continue;
                    }
                    normalDirs.Add(sub);
                    stack.Push(sub);
                }
            }
            catch { }
        }

        normalDirs.Sort((a, b) => b.Length.CompareTo(a.Length));
        return (files, reparseDirs, normalDirs);
    }

    private static int DeleteInstallDirectoryWithVerification(string installDir, bool silent, bool preserveManifestAndUninstallerOnFailure = false)
    {
        if (IsUnsafeRootPath(installDir) || !Directory.Exists(installDir))
            return 0;

        string fullUninstaller = Path.GetFullPath(Path.Combine(installDir, "Uninstall-RAMYXEN.exe"));
        string fullManifest = Path.GetFullPath(Path.Combine(installDir, ManifestFileName));

        // Safe reparse-aware sweep first so junctions/symlinks are unlinked before Directory.Delete
        var (files, reparseDirs, normalDirs) = EnumerateTreeSafeNoReparse(installDir);
        foreach (string rpDir in reparseDirs)
        {
            try { Directory.Delete(rpDir, recursive: false); } catch { }
        }

        var lockedRelPaths = new List<string>();
        bool allScheduled = true;
        int lastWin32Err = 0;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            lockedRelPaths.Clear();
            allScheduled = true;
            lastWin32Err = 0;
            (files, reparseDirs, normalDirs) = EnumerateTreeSafeNoReparse(installDir);

            foreach (string file in files)
            {
                string fullFile = Path.GetFullPath(file);
                if (preserveManifestAndUninstallerOnFailure &&
                    (string.Equals(fullFile, fullUninstaller, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(fullFile, fullManifest, StringComparison.OrdinalIgnoreCase)))
                    continue;

                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch
                {
                    lockedRelPaths.Add(Path.GetRelativePath(installDir, file));
                }
            }

            if (lockedRelPaths.Count == 0)
                break;

            Thread.Sleep(200 * (attempt + 1));
        }

        if (lockedRelPaths.Count > 0)
        {
            foreach (string rel in lockedRelPaths)
            {
                string lockedFull = Path.Combine(installDir, rel);
                if (!MoveFileExW(lockedFull, null, MOVEFILE_DELAY_UNTIL_REBOOT))
                {
                    allScheduled = false;
                    lastWin32Err = Marshal.GetLastWin32Error();
                }
            }

            if (!allScheduled)
            {
                string sampleList = string.Join(", ", lockedRelPaths.Take(5));
                if (lockedRelPaths.Count > 5) sampleList += $" (+{lockedRelPaths.Count - 5} more)";
                Console.Error.WriteLine($"[RAMYXEN-Setup] Uninstall incomplete (exit 1603): {lockedRelPaths.Count} locked file(s): {sampleList} (Win32 error {lastWin32Err}). Close locking processes and retry uninstall.");
                return 1603;
            }
        }

        // All payload files deleted or scheduled for reboot — now clean manifest, uninstaller, and directories
        if (preserveManifestAndUninstallerOnFailure)
        {
            foreach (string p in new[] { fullManifest, fullUninstaller })
            {
                if (!File.Exists(p)) continue;
                try { File.SetAttributes(p, FileAttributes.Normal); File.Delete(p); }
                catch
                {
                    if (!MoveFileExW(p, null, MOVEFILE_DELAY_UNTIL_REBOOT))
                    {
                        allScheduled = false;
                        lastWin32Err = Marshal.GetLastWin32Error();
                    }
                }
            }
        }

        foreach (string dir in normalDirs)
        {
            try { Directory.Delete(dir, recursive: false); }
            catch
            {
                if (!MoveFileExW(dir, null, MOVEFILE_DELAY_UNTIL_REBOOT))
                {
                    allScheduled = false;
                    lastWin32Err = Marshal.GetLastWin32Error();
                }
            }
        }

        try { Directory.Delete(installDir, recursive: false); }
        catch
        {
            if (!MoveFileExW(installDir, null, MOVEFILE_DELAY_UNTIL_REBOOT))
            {
                allScheduled = false;
                lastWin32Err = Marshal.GetLastWin32Error();
            }
        }

        if (!Directory.Exists(installDir))
        {
            if (!silent) Console.WriteLine($"[RAMYXEN-Setup] Cleanly removed {installDir}.");
            return 0;
        }

        if (allScheduled)
        {
            Console.Error.WriteLine($"[RAMYXEN-Setup] Partial cleanup: {lockedRelPaths.Count} locked file(s) scheduled for deletion after restart.");
            return 3010; // Honest ERROR_SUCCESS_REBOOT_REQUIRED
        }

        Console.Error.WriteLine($"[RAMYXEN-Setup] Partial cleanup: {lockedRelPaths.Count} locked file(s) could not be removed or scheduled for reboot (Win32 error {lastWin32Err}).");
        return 1603;
    }

    public static bool CreateShortcutCom(
        string shortcutPath,
        string targetExe,
        string arguments,
        string workingDir,
        string description,
        string iconPath,
        int iconIndex,
        out string errorMessage)
    {
        errorMessage = string.Empty;
        object? linkObj = null;
        try
        {
            string? parentDir = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrEmpty(parentDir))
                Directory.CreateDirectory(parentDir);

            linkObj = new ShellLink();
            var link = (IShellLinkW)linkObj;
            link.SetPath(targetExe);
            if (!string.IsNullOrEmpty(arguments))
                link.SetArguments(arguments);
            link.SetWorkingDirectory(workingDir);
            link.SetDescription(description);
            link.SetIconLocation(iconPath, iconIndex);

            var persist = (IPersistFile)linkObj;
            persist.Save(shortcutPath, true);
            return File.Exists(shortcutPath);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
        finally
        {
            if (linkObj is not null && Marshal.IsComObject(linkObj))
            {
                try { Marshal.FinalReleaseComObject(linkObj); } catch { }
            }
        }
    }

    private const string UninstallHivePrefix = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";

    private static bool IsPathUnderDirectory(string candidatePath, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(baseDir))
            return false;
        string fullCandidate = Path.GetFullPath(candidatePath);
        string fullBase = Path.GetFullPath(baseDir).TrimEnd('\\', '/') + "\\";
        return fullCandidate.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidUninstallSubKey(string? regSubKey)
    {
        if (string.IsNullOrWhiteSpace(regSubKey))
            return false;
        string normalized = regSubKey.Trim().TrimStart('\\', '/').TrimEnd('\\', '/');
        return normalized.StartsWith(UninstallHivePrefix, StringComparison.OrdinalIgnoreCase) &&
               normalized.Length > UninstallHivePrefix.Length &&
               !normalized.Contains("..", StringComparison.Ordinal) &&
               !normalized.Contains('"', StringComparison.Ordinal);
    }

    private static void RegisterAddRemovePrograms(
        string regSubKey,
        string installDir,
        string uninstallerPath,
        string iconLocationWithIndex,
        string appVersion,
        long estimatedSizeKb)
    {
        if (!IsValidUninstallSubKey(regSubKey))
            throw new InvalidOperationException($"Refusing invalid HKCU Uninstall registry subkey: {regSubKey}");

        string cleanDir = installDir.TrimEnd('\\', '/');
        string regArg = string.Equals(regSubKey, DefaultUninstallRegSubKey, StringComparison.OrdinalIgnoreCase)
            ? ""
            : $" --registry-subkey \"{regSubKey}\"";

        using var key = Registry.CurrentUser.CreateSubKey(regSubKey);
        if (key is null) return;
        key.SetValue("DisplayName", "RAMYXEN — Windows Memory Intelligence Platform");
        key.SetValue("DisplayVersion", appVersion);
        key.SetValue("Publisher", "RAMYXEN Project");
        key.SetValue("InstallLocation", cleanDir);
        key.SetValue("DisplayIcon", iconLocationWithIndex);
        key.SetValue("UninstallString", $"\"{uninstallerPath}\" --uninstall --install-dir \"{cleanDir}\"{regArg}");
        key.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" --uninstall --silent --install-dir \"{cleanDir}\"{regArg}");
        key.SetValue("EstimatedSize", (int)Math.Clamp(estimatedSizeKb, 1, int.MaxValue), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void UnregisterAddRemoveProgramsSafe(string regSubKey, string installDir)
    {
        if (!IsValidUninstallSubKey(regSubKey))
            return;
        try
        {
            using (var existing = Registry.CurrentUser.OpenSubKey(regSubKey))
            {
                if (existing is null) return;
                string? pub = existing.GetValue("Publisher") as string;
                string? loc = existing.GetValue("InstallLocation") as string;
                // REL-NEW-010: If InstallLocation is present, it MUST match installDir (never let Publisher override a different installation!)
                bool ownedByRamyxen = !string.IsNullOrWhiteSpace(loc)
                    ? string.Equals(
                        Path.GetFullPath(loc).TrimEnd('\\', '/'),
                        Path.GetFullPath(installDir).TrimEnd('\\', '/'),
                        StringComparison.OrdinalIgnoreCase)
                    : string.Equals(pub, "RAMYXEN Project", StringComparison.OrdinalIgnoreCase);
                if (!ownedByRamyxen) return;
            }
            Registry.CurrentUser.DeleteSubKeyTree(regSubKey, throwOnMissingSubKey: false);
        }
        catch { }
    }

    private static void DeleteShortcutSafe(string? path, string expectedInstallDir)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (!File.Exists(path)) return;
            // Verify shortcut actually points inside this RAMYXEN installDir before deleting (fail-closed - R2-NEW-005 / R3-NEW-007)
            string? target = TryReadShortcutTargetCom(path);
            if (string.IsNullOrWhiteSpace(target) || !IsPathUnderDirectory(target, expectedInstallDir))
                return;

            File.Delete(path);
            NotifyShellPath(SHCNE_DELETE, path);
        }
        catch { }
    }

    private static string? TryReadShortcutTargetCom(string shortcutPath)
    {
        object? linkObj = null;
        try
        {
            linkObj = new ShellLink();
            var persist = (IPersistFile)linkObj;
            persist.Load(shortcutPath, 0);
            var link = (IShellLinkW)linkObj;
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, nint.Zero, 0);
            return sb.ToString();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (linkObj is not null && Marshal.IsComObject(linkObj))
            {
                try { Marshal.FinalReleaseComObject(linkObj); } catch { }
            }
        }
    }

    private static InstallManifest? TryLoadManifest(string installDir)
    {
        try
        {
            string p = Path.Combine(installDir, ManifestFileName);
            if (File.Exists(p))
                return JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(p, Encoding.UTF8));
        }
        catch { }
        return null;
    }

    private static void NotifyShellIconRefresh()
    {
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, nint.Zero, nint.Zero); } catch { }
    }

    private static void NotifyShellPath(int eventId, string path)
    {
        nint ptr = nint.Zero;
        try
        {
            ptr = Marshal.StringToHGlobalUni(path);
            SHChangeNotify(eventId, SHCNF_PATHW | SHCNF_FLUSH, ptr, nint.Zero);
        }
        catch { }
        finally
        {
            if (ptr != nint.Zero) Marshal.FreeHGlobal(ptr);
        }
    }

    private static bool IsUnsafeRootPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;
        string trimmedInput = path.Trim();
        // Reject drive-relative paths like "C:" before Path.GetFullPath resolves them to CWD (REL-NEW-009)
        if (trimmedInput.Length == 2 && char.IsLetter(trimmedInput[0]) && trimmedInput[1] == ':')
            return true;

        string fullPath = Path.GetFullPath(trimmedInput);
        string? rawRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(rawRoot))
            return true;

        string normalized = fullPath.TrimEnd('\\', '/');
        string root = rawRoot.TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(normalized) || string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase))
            return true;

        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\', '/');
        if (string.Equals(normalized, winDir, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static long ComputeDirectorySizeKb(string dir)
    {
        long total = 0;
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return Math.Max(1, total / 1024);
    }

    private static string GetAppVersionString()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is not null ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.1.0";
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string? GetArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (string subDir in Directory.GetDirectories(sourceDir))
            CopyDirectory(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
    }
}
