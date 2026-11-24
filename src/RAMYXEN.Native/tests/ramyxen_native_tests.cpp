#include <stdio.h>
#include <string.h>
#include <windows.h>
#include "../include/ramyxen_native.h"

static int g_passed = 0;
static int g_failed = 0;

#define ASSERT_TRUE(cond, msg) do { \
    if (cond) { printf("  [PASS] %s\n", msg); g_passed++; } \
    else { printf("  [FAIL] %s (line %d)\n", msg, __LINE__); g_failed++; } \
} while(0)

int main(int argc, char** argv) {
    // Helper child mode for testing external-process heap & region inspection honestly
    if (argc >= 2 && strcmp(argv[1], "--sleep-child") == 0) {
        void* p = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 131072);
        Sleep(15000);
        if (p) HeapFree(GetProcessHeap(), 0, p);
        return 0;
    }

    printf("\n=== RAMYXEN Native C++20 Engine Checks (ABI v102) ===\n");

    ASSERT_TRUE(Ramyxen_GetAbiVersion() == 102, "1. ABI version returns 102");
    ASSERT_TRUE(sizeof(RamyxenSystemMemorySnapshot) == 136, "2. RamyxenSystemMemorySnapshot ABI size is 136 bytes");
    ASSERT_TRUE(sizeof(RamyxenVirtualRegionInfo) == 48, "3. RamyxenVirtualRegionInfo ABI size is 48 bytes");
    ASSERT_TRUE(sizeof(RamyxenProcessMemoryMetrics) == 632, "4. RamyxenProcessMemoryMetrics ABI size is 632 bytes");
    ASSERT_TRUE(sizeof(RamyxenHeapSummary) == 40, "5. RamyxenHeapSummary ABI size is 40 bytes");
    ASSERT_TRUE(sizeof(RamyxenKernelPageListMetrics) == 128, "6. RamyxenKernelPageListMetrics ABI size is 128 bytes");
    ASSERT_TRUE(sizeof(RamyxenModuleInfo) == 792 && sizeof(RamyxenNativeErrorInfo) == 88,
        "7. RamyxenModuleInfo (792B) & RamyxenNativeErrorInfo (88B) ABI sizes verified");

    ASSERT_TRUE(Ramyxen_GetSystemMemorySnapshot(nullptr) == RAMYXEN_ERR_INVALID_ARG, "8. Null pointer rejected safely");

    RamyxenNativeErrorInfo lastErr{};
    ASSERT_TRUE(Ramyxen_GetLastNativeError(&lastErr) == RAMYXEN_OK &&
        lastErr.StatusCode == RAMYXEN_ERR_INVALID_ARG &&
        strcmp(lastErr.FunctionName, "Ramyxen_GetSystemMemorySnapshot") == 0,
        "9. Ramyxen_GetLastNativeError records exact function name and status code");

    RamyxenSystemMemorySnapshot snap{};
    int32_t rc = Ramyxen_GetSystemMemorySnapshot(&snap);
    ASSERT_TRUE(rc == RAMYXEN_OK && snap.StatusCode == RAMYXEN_OK, "10. Real GlobalMemoryStatusEx + GetPerformanceInfo succeeds");
    ASSERT_TRUE(snap.TotalPhysicalBytes > (512ULL * 1024 * 1024), "11. TotalPhysicalBytes > 512 MB on live host");
    ASSERT_TRUE(snap.InUsePhysicalBytes > 0 && snap.AvailablePhysicalBytes > 0, "12. InUse and Available physical bytes are positive");
    ASSERT_TRUE(snap.CommitTotalBytes > 0 && snap.CommitLimitBytes >= snap.CommitTotalBytes, "13. CommitTotalBytes <= CommitLimitBytes");
    ASSERT_TRUE(snap.PageSizeBytes == 4096, "14. PageSizeBytes is 4096 on x64 Windows");

    RamyxenKernelPageListMetrics kernelLists{};
    rc = Ramyxen_GetKernelPageListMetrics(&kernelLists);
    ASSERT_TRUE(rc == RAMYXEN_OK && kernelLists.HasExactFaultCounters == 1U && kernelLists.PageFaultCountCumulative > 0,
        "15. NtQuerySystemInformation(SystemPerformanceInformation) returns real kernel page fault & hard read counters");

    const uint32_t selfPid = GetCurrentProcessId();
    RamyxenProcessMemoryMetrics proc{};
    rc = Ramyxen_GetProcessMemoryMetrics(selfPid, &proc);
    ASSERT_TRUE(rc == RAMYXEN_OK && proc.WorkingSetBytes > 0 && proc.PrivateCommitBytes > 0,
        "16. Self process WorkingSet and PrivateCommit > 0");
    ASSERT_TRUE(proc.IsProtectedOrRestricted == 0U && proc.VirtualSizeBytes >= proc.WorkingSetBytes,
        "17. Self process opened with least privilege is NOT falsely marked IsProtectedOrRestricted");
    ASSERT_TRUE(wcslen(proc.ExecutablePath) > 0, "18. Self process ExecutablePath resolved safely");

    RamyxenVirtualRegionInfo regions[128]{};
    int32_t totalRegions = 0;
    rc = Ramyxen_EnumerateVirtualRegions(selfPid, regions, 128, &totalRegions);
    ASSERT_TRUE(rc == RAMYXEN_OK && totalRegions > 5, "19. VirtualQueryEx enumerates >5 virtual regions in current process");

    // Verify true total region count exceeds small buffer when truncated (F-035)
    RamyxenVirtualRegionInfo tinyRegions[2]{};
    int32_t totalRegionsSmall = 0;
    rc = Ramyxen_EnumerateVirtualRegions(selfPid, tinyRegions, 2, &totalRegionsSmall);
    ASSERT_TRUE(rc == RAMYXEN_OK && totalRegionsSmall > 2,
        "20. Ramyxen_EnumerateVirtualRegions reports true total region count even when buffer is small");

    RamyxenModuleInfo mods[64]{};
    int32_t totalMods = 0;
    rc = Ramyxen_EnumerateProcessModulesFast(selfPid, mods, 64, &totalMods);
    ASSERT_TRUE(rc == RAMYXEN_OK && totalMods >= 3 && wcslen(mods[0].ModuleName) > 0,
        "21. Ramyxen_EnumerateProcessModulesFast enumerates loaded PE modules via Toolhelp32");

    // Verify true total module count exceeds small buffer when truncated (F-036)
    RamyxenModuleInfo tinyMods[2]{};
    int32_t totalModsSmall = 0;
    rc = Ramyxen_EnumerateProcessModulesFast(selfPid, tinyMods, 2, &totalModsSmall);
    ASSERT_TRUE(rc == RAMYXEN_OK && totalModsSmall >= totalMods && totalModsSmall > 2,
        "22. Ramyxen_EnumerateProcessModulesFast reports true total module count when truncated");

    void* testHeapAlloc = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 65536);
    RamyxenHeapSummary heaps[16]{};
    int32_t totalHeaps = 0;
    rc = Ramyxen_EnumerateProcessHeaps(selfPid, heaps, 16, &totalHeaps);
    if (testHeapAlloc) HeapFree(GetProcessHeap(), 0, testHeapAlloc);
    ASSERT_TRUE(rc == RAMYXEN_OK && totalHeaps >= 1 && heaps[0].CommittedBytes >= 65536 && heaps[0].AllocatedBytes > 0 && heaps[0].BlockCount > 0,
        "23. Self EnumerateProcessHeaps walks default process heap with real counts");

    // Spawn a real external child process and test external heap walk (F-030)
    wchar_t selfPath[MAX_PATH]{};
    GetModuleFileNameW(nullptr, selfPath, MAX_PATH);
    wchar_t cmdLine[MAX_PATH + 64]{};
    swprintf_s(cmdLine, L"\"%s\" --sleep-child", selfPath);
    STARTUPINFOW si{};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};
    bool extHeapOk = false;
    if (CreateProcessW(nullptr, cmdLine, nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi)) {
        Sleep(150);
        RamyxenHeapSummary extHeaps[16]{};
        int32_t extTotal = 0;
        int32_t extRc = Ramyxen_EnumerateProcessHeaps(pi.dwProcessId, extHeaps, 16, &extTotal);
        extHeapOk = (extRc == RAMYXEN_OK && extTotal >= 1 && extHeaps[0].CommittedBytes > 0);
        TerminateProcess(pi.hProcess, 0);
        WaitForSingleObject(pi.hProcess, 2000);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
    }
    ASSERT_TRUE(extHeapOk, "24. External-process Toolhelp32 heap walk succeeds on real spawned child process");

    const char testBuf[] = "RAMYXEN_NATIVE_READ_VERIFY_TOKEN";
    uint8_t readOut[64]{};
    int32_t bytesRead = 0;
    rc = Ramyxen_ReadProcessMemorySafe(selfPid, reinterpret_cast<uint64_t>(testBuf), readOut, sizeof(testBuf), &bytesRead);
    ASSERT_TRUE(rc == RAMYXEN_OK && bytesRead == sizeof(testBuf) && memcmp(testBuf, readOut, sizeof(testBuf)) == 0,
        "25. ReadProcessMemorySafe accurately reads live process buffer");

    // Official icon extraction + provenance + strict zero GDI/USER handle delta over 40 checked calls (F-023, F-030)
    uint8_t iconBgra[32 * 32 * 4]{};
    int32_t iW = 0, iH = 0, prov = 0;
    rc = Ramyxen_ExtractExecutableIconBgra(L"C:\\Windows\\explorer.exe", 32, iconBgra, sizeof(iconBgra), &iW, &iH, &prov);
    bool nonZeroPixel = false;
    for (int i = 0; i < (int)sizeof(iconBgra); i += 4) {
        if (iconBgra[i + 3] != 0) { nonZeroPixel = true; break; }
    }
    // Warm up one call so lazy OS GDI/COM caches settle, then assert STRICT delta == 0 across 40 checked iterations
    int all40Ok = 1;
    const DWORD gdiBefore = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS);
    const DWORD usrBefore = GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS);
    for (int k = 0; k < 40; ++k) {
        int32_t pLoop = 0;
        if (Ramyxen_ExtractExecutableIconBgra(L"C:\\Windows\\explorer.exe", 32, iconBgra, sizeof(iconBgra), &iW, &iH, &pLoop) != RAMYXEN_OK) {
            all40Ok = 0;
            break;
        }
    }
    const DWORD gdiAfter = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS);
    const DWORD usrAfter = GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS);
    ASSERT_TRUE(rc == RAMYXEN_OK && iW == 32 && iH == 32 && prov == RAMYXEN_ICON_PROV_PE_RESOURCE &&
        nonZeroPixel && all40Ok && gdiAfter == gdiBefore && usrAfter == usrBefore,
        "26. ExtractExecutableIconBgra returns PE_RESOURCE provenance with STRICT 0 GDI/USER handle delta across 40 checked calls");

    // Verify non-existent executable does NOT return a spoofed generic file-type icon (F-023)
    int32_t missingProv = 0;
    rc = Ramyxen_ExtractExecutableIconBgra(L"C:\\Windows\\nonexistent_ramyxen_missing_999.exe", 32, iconBgra, sizeof(iconBgra), &iW, &iH, &missingProv);
    ASSERT_TRUE(rc != RAMYXEN_OK && missingProv == RAMYXEN_ICON_PROV_NONE,
        "27. ExtractExecutableIconBgra rejects non-existent executable (no generic SHGFI_USEFILEATTRIBUTES spoofing)");

    RamyxenProcessMemoryMetrics invalidProc{};
    rc = Ramyxen_GetProcessMemoryMetrics(0xFFFFFFFEU, &invalidProc);
    ASSERT_TRUE(rc == RAMYXEN_ERR_PROCESS_NOT_FOUND, "28. Non-existent PID returns RAMYXEN_ERR_PROCESS_NOT_FOUND");

    // R3-29: Reject pid == 0 in heap and module enumeration (R2-NEW-006)
    RamyxenHeapSummary zeroHeaps[4]{};
    RamyxenModuleInfo zeroMods[4]{};
    int32_t zeroTotal = 0;
    int32_t rcZeroHeap = Ramyxen_EnumerateProcessHeaps(0, zeroHeaps, 4, &zeroTotal);
    int32_t rcZeroMod = Ramyxen_EnumerateProcessModulesFast(0, zeroMods, 4, &zeroTotal);
    ASSERT_TRUE(rcZeroHeap == RAMYXEN_ERR_INVALID_ARG && rcZeroMod == RAMYXEN_ERR_INVALID_ARG,
        "29. EnumerateProcessHeaps and EnumerateProcessModulesFast reject pid == 0 with RAMYXEN_ERR_INVALID_ARG");

    // R3-30: Directory path rejected & iconless .exe classified honestly as GENERIC_FILETYPE (never SHELL_FILE) (R2-NEW-012)
    int32_t dirProv = 0, selfProv = 0;
    int32_t rcDir = Ramyxen_ExtractExecutableIconBgra(L"C:\\Windows", 32, iconBgra, sizeof(iconBgra), &iW, &iH, &dirProv);
    int32_t rcSelfIcon = Ramyxen_ExtractExecutableIconBgra(selfPath, 32, iconBgra, sizeof(iconBgra), &iW, &iH, &selfProv);
    ASSERT_TRUE(rcDir == RAMYXEN_ERR_API_FAILED && dirProv == RAMYXEN_ICON_PROV_NONE &&
        rcSelfIcon == RAMYXEN_ERR_API_FAILED && selfProv == RAMYXEN_ICON_PROV_GENERIC_FILETYPE,
        "30. ExtractExecutableIconBgra rejects directories and classifies iconless .exe honestly as GENERIC_FILETYPE");

    // R3-31: Virtual region enumeration uses uintptr_t without address wraparound (R2-NEW-022)
    RamyxenVirtualRegionInfo vrCheck[32]{};
    int32_t vrTotal = 0;
    int32_t rcVr = Ramyxen_EnumerateVirtualRegions(selfPid, vrCheck, 32, &vrTotal);
    bool monotonic = (rcVr == RAMYXEN_OK && vrTotal >= 1);
    for (int i = 1; monotonic && i < 32 && i < vrTotal; ++i) {
        if (vrCheck[i].BaseAddress <= vrCheck[i - 1].BaseAddress) monotonic = false;
    }
    ASSERT_TRUE(monotonic, "31. EnumerateVirtualRegions returns strictly monotonic addresses without wraparound");

    printf("\nSummary: %d passed, %d failed.\n", g_passed, g_failed);
    return g_failed == 0 ? 0 : 1;
}
