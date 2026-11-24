#define RAMYXEN_NATIVE_EXPORTS
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <psapi.h>
#include <tlhelp32.h>
#include <shellapi.h>
#include <objbase.h>
#include <mutex>
#include <vector>
#include <stdlib.h>
#include <string.h>
#include "../include/ramyxen_native.h"

#pragma comment(lib, "psapi.lib")
#pragma comment(lib, "kernel32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "ole32.lib")

namespace {

struct ScopedHandle {
    HANDLE h;
    explicit ScopedHandle(HANDLE handle = nullptr) noexcept : h(handle) {}
    ScopedHandle(const ScopedHandle&) = delete;
    ScopedHandle& operator=(const ScopedHandle&) = delete;
    ~ScopedHandle() noexcept { Reset(); }
    void Reset(HANDLE newHandle = nullptr) noexcept {
        if (h && h != INVALID_HANDLE_VALUE) CloseHandle(h);
        h = newHandle;
    }
    bool IsValid() const noexcept { return h != nullptr && h != INVALID_HANDLE_VALUE; }
};

struct ScopedIcon {
    HICON h;
    explicit ScopedIcon(HICON icon = nullptr) noexcept : h(icon) {}
    ScopedIcon(const ScopedIcon&) = delete;
    ScopedIcon& operator=(const ScopedIcon&) = delete;
    ~ScopedIcon() noexcept { if (h) DestroyIcon(h); }
    bool IsValid() const noexcept { return h != nullptr; }
};

struct ScopedGdiObject {
    HGDIOBJ h;
    explicit ScopedGdiObject(HGDIOBJ obj = nullptr) noexcept : h(obj) {}
    ScopedGdiObject(const ScopedGdiObject&) = delete;
    ScopedGdiObject& operator=(const ScopedGdiObject&) = delete;
    ~ScopedGdiObject() noexcept { if (h) DeleteObject(h); }
};

struct ScopedMemoryDC {
    HDC hdc;
    explicit ScopedMemoryDC(HDC dc = nullptr) noexcept : hdc(dc) {}
    ScopedMemoryDC(const ScopedMemoryDC&) = delete;
    ScopedMemoryDC& operator=(const ScopedMemoryDC&) = delete;
    ~ScopedMemoryDC() noexcept { if (hdc) DeleteDC(hdc); }
};

struct ScopedComInit {
    HRESULT hr;
    bool shouldUninit;
    ScopedComInit() noexcept : hr(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE)), shouldUninit(false) {
        if (hr == S_OK || hr == S_FALSE) {
            shouldUninit = true;
        }
        // RPC_E_CHANGED_MODE means thread already initialized in MTA/other apartment; do NOT call CoUninitialize.
    }
    ScopedComInit(const ScopedComInit&) = delete;
    ScopedComInit& operator=(const ScopedComInit&) = delete;
    ~ScopedComInit() noexcept {
        if (shouldUninit) CoUninitialize();
    }
    bool IsUsable() const noexcept { return hr == S_OK || hr == S_FALSE || hr == RPC_E_CHANGED_MODE; }
};

thread_local RamyxenNativeErrorInfo g_tlsLastError{};

uint64_t GetCurrentUtcMs() {
    FILETIME ft{};
    GetSystemTimeAsFileTime(&ft);
    ULARGE_INTEGER uli{};
    uli.LowPart = ft.dwLowDateTime;
    uli.HighPart = ft.dwHighDateTime;
    const uint64_t EPOCH_DIFF = 116444736000000000ULL;
    return (uli.QuadPart > EPOCH_DIFF) ? ((uli.QuadPart - EPOCH_DIFF) / 10000ULL) : 0ULL;
}

void RecordNativeError(const char* funcName, int32_t statusCode, DWORD win32Err, uint32_t targetPid) noexcept {
    ZeroMemory(&g_tlsLastError, sizeof(g_tlsLastError));
    g_tlsLastError.StatusCode = statusCode;
    g_tlsLastError.Win32ErrorCode = win32Err;
    g_tlsLastError.HResult = static_cast<int32_t>(HRESULT_FROM_WIN32(win32Err));
    g_tlsLastError.TargetPid = targetPid;
    g_tlsLastError.TimestampUtcMs = GetCurrentUtcMs();
    if (funcName) {
        strncpy_s(g_tlsLastError.FunctionName, sizeof(g_tlsLastError.FunctionName), funcName, _TRUNCATE);
    }
}

using NtQuerySystemInformationFn = LONG(WINAPI*)(ULONG SystemInformationClass, PVOID SystemInformation, ULONG SystemInformationLength, PULONG ReturnLength);

NtQuerySystemInformationFn GetNtQuerySystemInformation() {
    static NtQuerySystemInformationFn fn = reinterpret_cast<NtQuerySystemInformationFn>(
        GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQuerySystemInformation"));
    return fn;
}

struct NT_SYSTEM_MEMORY_LIST_INFORMATION {
    ULONG_PTR ZeroPageCount;
    ULONG_PTR FreePageCount;
    ULONG_PTR ModifiedPageCount;
    ULONG_PTR ModifiedNoWritePageCount;
    ULONG_PTR BadPageCount;
    ULONG_PTR PageCountByPriority[8];
    ULONG_PTR RepurposedPagesByPriority[8];
    ULONG_PTR ModifiedPageCountPageFile;
};

struct NT_SYSTEM_PERFORMANCE_INFO_PREFIX {
    uint64_t IdleProcessTime;
    uint64_t IoReadTransferCount;
    uint64_t IoWriteTransferCount;
    uint64_t IoOtherTransferCount;
    uint32_t IoReadOperationCount;
    uint32_t IoWriteOperationCount;
    uint32_t IoOtherOperationCount;
    uint32_t AvailablePages;
    uint32_t CommittedPages;
    uint32_t CommitLimit;
    uint32_t PeakCommitment;
    uint32_t PageFaultCount;
    uint32_t CopyOnWriteCount;
    uint32_t TransitionCount;
    uint32_t CacheTransitionCount;
    uint32_t DemandZeroCount;
    uint32_t PageReadCount;
    uint32_t PageReadIoCount;
    uint8_t  RemainingPadding[512];
};

bool TryEnableSingleProcessProfilePrivilege() {
    HANDLE hToken = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &hToken)) {
        return false;
    }
    ScopedHandle tokenGuard(hToken);
    TOKEN_PRIVILEGES tp{};
    if (!LookupPrivilegeValueW(nullptr, L"SeProfileSingleProcessPrivilege", &tp.Privileges[0].Luid)) {
        return false;
    }
    tp.PrivilegeCount = 1;
    tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
    AdjustTokenPrivileges(hToken, FALSE, &tp, sizeof(tp), nullptr, nullptr);
    return GetLastError() == ERROR_SUCCESS;
}

bool IsExecutableWriteProtect(DWORD protect) {
    const DWORD baseProt = protect & 0xFF;
    return baseProt == PAGE_EXECUTE_READWRITE || baseProt == PAGE_EXECUTE_WRITECOPY;
}

} // namespace

extern "C" {

RAMYXEN_API int32_t Ramyxen_GetAbiVersion(void) {
    return 102; // v1.0.2 ABI (adds outProvenance to ExtractExecutableIconBgra + strict ABI enforcement)
}

RAMYXEN_API int32_t Ramyxen_GetLastNativeError(
    RamyxenNativeErrorInfo* outError) {
    if (!outError) {
        RecordNativeError("Ramyxen_GetLastNativeError", RAMYXEN_ERR_INVALID_ARG, ERROR_INVALID_PARAMETER, 0);
        return RAMYXEN_ERR_INVALID_ARG;
    }
    *outError = g_tlsLastError;
    return RAMYXEN_OK;
}

RAMYXEN_API int32_t Ramyxen_GetSystemMemorySnapshot(
    RamyxenSystemMemorySnapshot* outSnapshot) {
    if (!outSnapshot) {
        RecordNativeError("Ramyxen_GetSystemMemorySnapshot", RAMYXEN_ERR_INVALID_ARG, ERROR_INVALID_PARAMETER, 0);
        return RAMYXEN_ERR_INVALID_ARG;
    }
    ZeroMemory(outSnapshot, sizeof(RamyxenSystemMemorySnapshot));

    MEMORYSTATUSEX memStatus{};
    memStatus.dwLength = sizeof(memStatus);
    if (!GlobalMemoryStatusEx(&memStatus)) {
        RecordNativeError("Ramyxen_GetSystemMemorySnapshot", RAMYXEN_ERR_API_FAILED, GetLastError(), 0);
        outSnapshot->StatusCode = RAMYXEN_ERR_API_FAILED;
        return RAMYXEN_ERR_API_FAILED;
    }

    PERFORMANCE_INFORMATION perfInfo{};
    perfInfo.cb = sizeof(perfInfo);
    const BOOL hasPerf = GetPerformanceInfo(&perfInfo, sizeof(perfInfo));

    ULONGLONG installedKb = 0;
    if (!GetPhysicallyInstalledSystemMemory(&installedKb) || installedKb == 0) {
        installedKb = memStatus.ullTotalPhys / 1024ULL;
    }

    const uint64_t installedBytes = installedKb * 1024ULL;
    const uint64_t totalPhys = memStatus.ullTotalPhys;
    const uint64_t availPhys = memStatus.ullAvailPhys;
    const uint64_t inUsePhys = (totalPhys > availPhys) ? (totalPhys - availPhys) : 0ULL;
    const uint64_t hwReserved = (installedBytes > totalPhys) ? (installedBytes - totalPhys) : 0ULL;

    outSnapshot->InstalledPhysicalBytes = installedBytes;
    outSnapshot->TotalPhysicalBytes = totalPhys;
    outSnapshot->AvailablePhysicalBytes = availPhys;
    outSnapshot->InUsePhysicalBytes = inUsePhys;
    outSnapshot->HardwareReservedBytes = hwReserved;
    outSnapshot->MemoryLoadPercent = memStatus.dwMemoryLoad;

    if (hasPerf) {
        const uint64_t pg = perfInfo.PageSize;
        outSnapshot->PageSizeBytes = pg;
        outSnapshot->CommitTotalBytes = static_cast<uint64_t>(perfInfo.CommitTotal) * pg;
        outSnapshot->CommitLimitBytes = static_cast<uint64_t>(perfInfo.CommitLimit) * pg;
        outSnapshot->CommitPeakBytes = static_cast<uint64_t>(perfInfo.CommitPeak) * pg;
        outSnapshot->SystemCacheBytes = static_cast<uint64_t>(perfInfo.SystemCache) * pg;
        outSnapshot->KernelTotalBytes = static_cast<uint64_t>(perfInfo.KernelTotal) * pg;
        outSnapshot->KernelPagedBytes = static_cast<uint64_t>(perfInfo.KernelPaged) * pg;
        outSnapshot->KernelNonpagedBytes = static_cast<uint64_t>(perfInfo.KernelNonpaged) * pg;
        outSnapshot->HandleCount = perfInfo.HandleCount;
        outSnapshot->ProcessCount = perfInfo.ProcessCount;
        outSnapshot->ThreadCount = perfInfo.ThreadCount;
    } else {
        outSnapshot->PageSizeBytes = 4096;
        outSnapshot->CommitLimitBytes = memStatus.ullTotalPageFile;
        outSnapshot->CommitTotalBytes = (memStatus.ullTotalPageFile > memStatus.ullAvailPageFile)
            ? (memStatus.ullTotalPageFile - memStatus.ullAvailPageFile) : inUsePhys;
    }

    outSnapshot->TimestampUtcMs = GetCurrentUtcMs();
    outSnapshot->StatusCode = RAMYXEN_OK;
    return RAMYXEN_OK;
}

RAMYXEN_API int32_t Ramyxen_GetKernelPageListMetrics(
    RamyxenKernelPageListMetrics* outMetrics) {
    if (!outMetrics) {
        RecordNativeError("Ramyxen_GetKernelPageListMetrics", RAMYXEN_ERR_INVALID_ARG, ERROR_INVALID_PARAMETER, 0);
        return RAMYXEN_ERR_INVALID_ARG;
    }
    ZeroMemory(outMetrics, sizeof(RamyxenKernelPageListMetrics));

    const uint64_t pageSize = 4096ULL;
    auto ntQuery = GetNtQuerySystemInformation();
    if (!ntQuery) {
        RecordNativeError("Ramyxen_GetKernelPageListMetrics", RAMYXEN_ERR_API_FAILED, ERROR_PROC_NOT_FOUND, 0);
        return RAMYXEN_ERR_API_FAILED;
    }

    NT_SYSTEM_PERFORMANCE_INFO_PREFIX perfBuf{};
    ULONG retLen = 0;
    LONG statusPerf = ntQuery(2 /* SystemPerformanceInformation */, &perfBuf, sizeof(perfBuf), &retLen);
    if (statusPerf >= 0 && retLen >= 0x58) {
        outMetrics->PageFaultCountCumulative = perfBuf.PageFaultCount;
        outMetrics->PageReadHardFaultCountCumulative = perfBuf.PageReadCount;
        outMetrics->HasExactFaultCounters = 1U;
    }

    static std::once_flag s_privOnce;
    std::call_once(s_privOnce, []() { TryEnableSingleProcessProfilePrivilege(); });

    NT_SYSTEM_MEMORY_LIST_INFORMATION memList{};
    ULONG listRetLen = 0;
    LONG statusList = ntQuery(80 /* SystemMemoryListInformation */, &memList, sizeof(memList), &listRetLen);
    if (statusList == 0) {
        outMetrics->ZeroPageBytes = static_cast<uint64_t>(memList.ZeroPageCount) * pageSize;
        outMetrics->FreePageBytes = static_cast<uint64_t>(memList.FreePageCount) * pageSize;
        outMetrics->ModifiedPageBytes = static_cast<uint64_t>(memList.ModifiedPageCount) * pageSize;
        outMetrics->ModifiedNoWritePageBytes = static_cast<uint64_t>(memList.ModifiedNoWritePageCount) * pageSize;
        outMetrics->BadPageBytes = static_cast<uint64_t>(memList.BadPageCount) * pageSize;
        uint64_t totalStandby = 0;
        for (int i = 0; i < 8; ++i) {
            const uint64_t prioBytes = static_cast<uint64_t>(memList.PageCountByPriority[i]) * pageSize;
            outMetrics->StandbyPriorityBytes[i] = prioBytes;
            totalStandby += prioBytes;
        }
        outMetrics->TotalStandbyBytes = totalStandby;
        outMetrics->HasExactMemoryLists = 1U;
    }

    return RAMYXEN_OK;
}

RAMYXEN_API int32_t Ramyxen_GetProcessMemoryMetrics(
    uint32_t pid,
    RamyxenProcessMemoryMetrics* outMetrics) {
    if (!outMetrics) {
        RecordNativeError("Ramyxen_GetProcessMemoryMetrics", RAMYXEN_ERR_INVALID_ARG, ERROR_INVALID_PARAMETER, pid);
        return RAMYXEN_ERR_INVALID_ARG;
    }
    ZeroMemory(outMetrics, sizeof(RamyxenProcessMemoryMetrics));
    outMetrics->Pid = pid;

    if (pid == 0) {
        outMetrics->StatusCode = RAMYXEN_ERR_PROTECTED_PROCESS;
        outMetrics->IsProtectedOrRestricted = 1;
        wcscpy_s(outMetrics->ExecutablePath, L"System Idle Process");
        return RAMYXEN_OK;
    }

    ScopedHandle hProc(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
    if (!hProc.IsValid()) {
        const DWORD err = GetLastError();
        if (err == ERROR_ACCESS_DENIED) {
            RecordNativeError("Ramyxen_GetProcessMemoryMetrics", RAMYXEN_ERR_ACCESS_DENIED, err, pid);
            outMetrics->IsProtectedOrRestricted = 1;
            outMetrics->StatusCode = RAMYXEN_ERR_ACCESS_DENIED;
            return RAMYXEN_ERR_ACCESS_DENIED;
        }
        RecordNativeError("Ramyxen_GetProcessMemoryMetrics", RAMYXEN_ERR_PROCESS_NOT_FOUND, err, pid);
        outMetrics->StatusCode = RAMYXEN_ERR_PROCESS_NOT_FOUND;
        return RAMYXEN_ERR_PROCESS_NOT_FOUND;
    }

    wchar_t fullPathBuf[2048]{};
    DWORD pathLen = 2047;
    if (QueryFullProcessImageNameW(hProc.h, 0, fullPathBuf, &pathLen) && pathLen > 0) {
        wcsncpy_s(outMetrics->ExecutablePath, _countof(outMetrics->ExecutablePath), fullPathBuf, _TRUNCATE);
    } else if (pid == 4) {
        wcscpy_s(outMetrics->ExecutablePath, L"System (ntoskrnl.exe)");
    }

    PROCESS_MEMORY_COUNTERS_EX2 pmc2{};
    pmc2.cb = sizeof(pmc2);
    if (GetProcessMemoryInfo(hProc.h, reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&pmc2), sizeof(pmc2))) {
        outMetrics->WorkingSetBytes = pmc2.WorkingSetSize;
        outMetrics->PrivateWorkingSetBytes = pmc2.PrivateWorkingSetSize;
        outMetrics->SharedWorkingSetBytes = (pmc2.WorkingSetSize > pmc2.PrivateWorkingSetSize)
            ? (pmc2.WorkingSetSize - pmc2.PrivateWorkingSetSize) : 0ULL;
        outMetrics->PrivateCommitBytes = pmc2.PrivateUsage;
        outMetrics->PeakWorkingSetBytes = pmc2.PeakWorkingSetSize;
        outMetrics->PagefileUsageBytes = pmc2.PagefileUsage;
        outMetrics->VirtualSizeBytes = pmc2.WorkingSetSize + pmc2.PrivateUsage;
        outMetrics->PagedPoolBytes = pmc2.QuotaPagedPoolUsage;
        outMetrics->NonpagedPoolBytes = pmc2.QuotaNonPagedPoolUsage;
        outMetrics->PageFaultCount = pmc2.PageFaultCount;
    } else {
        PROCESS_MEMORY_COUNTERS_EX pmc{};
        pmc.cb = sizeof(pmc);
        if (GetProcessMemoryInfo(hProc.h, reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&pmc), sizeof(pmc))) {
            outMetrics->WorkingSetBytes = pmc.WorkingSetSize;
            outMetrics->PrivateWorkingSetBytes = pmc.WorkingSetSize;
            outMetrics->PrivateCommitBytes = pmc.PrivateUsage;
            outMetrics->PeakWorkingSetBytes = pmc.PeakWorkingSetSize;
            outMetrics->PagefileUsageBytes = pmc.PagefileUsage;
            outMetrics->VirtualSizeBytes = pmc.WorkingSetSize + pmc.PrivateUsage;
            outMetrics->PagedPoolBytes = pmc.QuotaPagedPoolUsage;
            outMetrics->NonpagedPoolBytes = pmc.QuotaNonPagedPoolUsage;
            outMetrics->PageFaultCount = pmc.PageFaultCount;
        }
    }

    DWORD handles = 0;
    if (GetProcessHandleCount(hProc.h, &handles)) {
        outMetrics->HandleCount = handles;
    }

    FILETIME ftCreate{}, ftExit{}, ftKernel{}, ftUser{};
    if (GetProcessTimes(hProc.h, &ftCreate, &ftExit, &ftKernel, &ftUser)) {
        ULARGE_INTEGER uli{};
        uli.LowPart = ftCreate.dwLowDateTime;
        uli.HighPart = ftCreate.dwHighDateTime;
        outMetrics->StartTimeFileTimeUtc = uli.QuadPart;
    }

    USHORT procMachine = 0, nativeMachine = 0;
    if (IsWow64Process2(hProc.h, &procMachine, &nativeMachine)) {
        if (procMachine == IMAGE_FILE_MACHINE_UNKNOWN) {
            outMetrics->ArchitectureCode = (nativeMachine == IMAGE_FILE_MACHINE_ARM64) ? 3 : 1;
        } else {
            outMetrics->ArchitectureCode = 2; // x86 WOW64
        }
    } else {
        outMetrics->ArchitectureCode = 1;
    }

    outMetrics->StatusCode = RAMYXEN_OK;
    return RAMYXEN_OK;
}

RAMYXEN_API int32_t Ramyxen_EnumerateVirtualRegions(
    uint32_t pid,
    RamyxenVirtualRegionInfo* outRegions,
    int32_t maxRegions,
    int32_t* outTotalRegions) {
    if (!outTotalRegions || maxRegions < 0) {
        RecordNativeError("Ramyxen_EnumerateVirtualRegions", RAMYXEN_ERR_INVALID_ARG, ERROR_INVALID_PARAMETER, pid);
        return RAMYXEN_ERR_INVALID_ARG;
    }
    *outTotalRegions = 0;

    // VirtualQueryEx strictly requires PROCESS_QUERY_INFORMATION (do not fall back to LIMITED which silently returns 0)
    ScopedHandle hProc(OpenProcess(PROCESS_QUERY_INFORMATION, FALSE, pid));
    if (!hProc.IsValid()) {
        const DWORD err = GetLastError();
        const int32_t code = (err == ERROR_ACCESS_DENIED) ? RAMYXEN_ERR_ACCESS_DENIED : RAMYXEN_ERR_PROCESS_NOT_FOUND;
        RecordNativeError("Ramyxen_EnumerateVirtualRegions", code, err, pid);
        return code;
    }

    SYSTEM_INFO sysInfo{};
    GetNativeSystemInfo(&sysInfo);
    const uint8_t* addr = static_cast<const uint8_t*>(sysInfo.lpMinimumApplicationAddress);
    const uint8_t* maxAddr = static_cast<const uint8_t*>(sysInfo.lpMaximumApplicationAddress);

    int32_t count = 0;
    const int32_t hardCountCap = 16384;
    MEMORY_BASIC_INFORMATION mbi{};
    while (addr < maxAddr && VirtualQueryEx(hProc.h, addr, &mbi, sizeof(mbi)) == sizeof(mbi)) {
        if (mbi.State != MEM_FREE) {
            if (outRegions && count < maxRegions) {
                RamyxenVirtualRegionInfo& r = outRegions[count];
                r.BaseAddress = reinterpret_cast<uint64_t>(mbi.BaseAddress);
                r.AllocationBase = reinterpret_cast<uint64_t>(mbi.AllocationBase);
                r.RegionSizeBytes = static_cast<uint64_t>(mbi.RegionSize);
                r.State = mbi.State;
                r.Protect = mbi.Protect;
                r.AllocationProtect = mbi.AllocationProtect;
                r.Type = mbi.Type;
                r.IsSuspiciousRwx = (mbi.State == MEM_COMMIT && (mbi.Type == MEM_PRIVATE || mbi.Type == MEM_MAPPED) && IsExecutableWriteProtect(mbi.Protect)) ? 1U : 0U;
                r.Reserved = 0;
            }
            count++;
            if (count >= hardCountCap) break;
        }
        const uintptr_t curAddr = reinterpret_cast<uintptr_t>(addr);
        const uintptr_t baseAddr = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
        const uintptr_t nextAddr = baseAddr + mbi.RegionSize;
        if (nextAddr <= curAddr || nextAddr < baseAddr) break;
        addr = reinterpret_cast<const uint8_t*>(nextAddr);
    }

    *outTotalRegions = count;
    return RAMYXEN_OK;
}

RAMYXEN_API int32_t Ramyxen_EnumerateProcessHeaps(
    uint32_t pid,
    RamyxenHeapSummary* outHeaps,
    int32_t maxHeaps,
    int32_t* outTotalHeaps) {
    if (!outTotalHeaps || maxHeaps < 0 || pid == 0) {
        RecordNativeError("Ramyxen_EnumerateProcessHeaps", RAMYXEN_ERR_INVALID_ARG, ERROR_INVALID_PARAMETER, pid);
        return RAMYXEN_ERR_INVALID_ARG;
    }
    *outTotalHeaps = 0;

    // Cooperative loop budget (true hard termination for external processes is enforced by RAMYXEN.HeapWorker.exe)
    const ULONGLONG deadlineTick = GetTickCount64() + 1200ULL;

    if (pid == GetCurrentProcessId()) {
        // Short 80ms / 1024-block HeapLock cap so inspecting self never stalls UI/XAML allocations
        const ULONGLONG selfLockDeadline = GetTickCount64() + 80ULL;
        HANDLE stackHeaps[128]{};
        std::vector<HANDLE> dynamicHeaps;
        HANDLE* heapPtr = stackHeaps;
        DWORD numHeaps = GetProcessHeaps(128, stackHeaps);
        if (numHeaps > 128) {
            dynamicHeaps.resize(numHeaps, nullptr);
            DWORD retryCount = GetProcessHeaps(numHeaps, dynamicHeaps.data());
            numHeaps = (retryCount <= dynamicHeaps.size()) ? retryCount : static_cast<DWORD>(dynamicHeaps.size());
            heapPtr = dynamicHeaps.data();
        }
        const int32_t total = static_cast<int32_t>(numHeaps);
        const HANDLE defaultHeap = GetProcessHeap();

        for (int32_t i = 0; i < total; ++i) {
            if (outHeaps && i < maxHeaps) {
                HANDLE hHeap = heapPtr[i];
                uint64_t allocBytes = 0;
                uint64_t freeBytes = 0;
                uint64_t regionCommitBytes = 0;
                uint32_t blocks = 0;
                bool bounded = false;

                if (hHeap != nullptr && GetTickCount64() < selfLockDeadline && HeapLock(hHeap)) {
                    PROCESS_HEAP_ENTRY entry{};
                    while (HeapWalk(hHeap, &entry)) {
                        if (entry.wFlags & PROCESS_HEAP_REGION) {
                            regionCommitBytes += entry.Region.dwCommittedSize;
                        } else if (entry.wFlags & PROCESS_HEAP_ENTRY_BUSY) {
                            blocks++;
                            allocBytes += entry.cbData;
                        } else if (!(entry.wFlags & PROCESS_HEAP_UNCOMMITTED_RANGE)) {
                            blocks++;
                            freeBytes += entry.cbData;
                        }
                        if (blocks >= 1024 || ((blocks & 0x3F) == 0 && GetTickCount64() >= selfLockDeadline)) {
                            bounded = true;
                            break;
                        }
                    }
                    HeapUnlock(hHeap);
                } else {
                    bounded = true;
                }

                outHeaps[i].HeapId = reinterpret_cast<uint64_t>(hHeap);
                outHeaps[i].Flags = ((hHeap == defaultHeap || i == 0) ? 1U : 0U) | (bounded ? 0x80000000U : 0U);
                outHeaps[i].BlockCount = blocks;
                outHeaps[i].AllocatedBytes = allocBytes;
                outHeaps[i].FreeBytes = freeBytes;
                outHeaps[i].CommittedBytes = (regionCommitBytes > (allocBytes + freeBytes))
                    ? regionCommitBytes : (allocBytes + freeBytes);
            }
        }
        *outTotalHeaps = total;
        return RAMYXEN_OK;
    }

    ScopedHandle hSnap(CreateToolhelp32Snapshot(TH32CS_SNAPHEAPLIST, pid));
    if (!hSnap.IsValid()) {
        const DWORD err = GetLastError();
        const int32_t code = (err == ERROR_ACCESS_DENIED) ? RAMYXEN_ERR_ACCESS_DENIED : RAMYXEN_ERR_API_FAILED;
        RecordNativeError("Ramyxen_EnumerateProcessHeaps", code, err, pid);
        return code;
    }

    HEAPLIST32 hl{};
    hl.dwSize = sizeof(hl);
    if (!Heap32ListFirst(hSnap.h, &hl)) {
        *outTotalHeaps = 0;
        return RAMYXEN_OK;
    }

    int32_t count = 0;
    bool listTruncated = false;
    do {
        if (outHeaps && count < maxHeaps) {
            uint64_t allocBytes = 0;
            uint64_t freeBytes = 0;
            uint32_t blocks = 0;
            bool bounded = false;

            if (GetTickCount64() < deadlineTick) {
                HEAPENTRY32 he{};
                he.dwSize = sizeof(he);
                if (Heap32First(&he, pid, hl.th32HeapID)) {
                    do {
                        blocks++;
                        if (he.dwFlags & LF32_FREE) freeBytes += he.dwBlockSize;
                        else allocBytes += he.dwBlockSize;
                        if (blocks >= 1024 || ((blocks & 0x3F) == 0 && GetTickCount64() >= deadlineTick)) {
                            bounded = true;
                            break;
                        }
                    } while (Heap32Next(&he));
                } else if (GetLastError() != ERROR_NO_MORE_FILES) {
                    bounded = true;
                }
            } else {
                bounded = true;
            }

            outHeaps[count].HeapId = static_cast<uint64_t>(hl.th32HeapID);
            outHeaps[count].Flags = hl.dwFlags | (bounded ? 0x80000000U : 0U);
            outHeaps[count].BlockCount = blocks;
            outHeaps[count].AllocatedBytes = allocBytes;
            outHeaps[count].FreeBytes = freeBytes;
            outHeaps[count].CommittedBytes = allocBytes + freeBytes;
        }
        count++;
        if (count >= 64 || GetTickCount64() >= deadlineTick) {
            listTruncated = true;
            break;
        }
    } while (Heap32ListNext(hSnap.h, &hl));

    if (listTruncated && outHeaps && count > 0 && (count - 1) < maxHeaps) {
        outHeaps[count - 1].Flags |= 0x80000000U;
    }

    *outTotalHeaps = count;
    return RAMYXEN_OK;
}

RAMYXEN_API int32_t Ramyxen_EnumerateProcessModulesFast(
    uint32_t pid,
    RamyxenModuleInfo* outModules,
    int32_t maxModules,
    int32_t* outTotalModules) {
    if (!outTotalModules || maxModules < 0 || pid == 0) {
        RecordNativeError("Ramyxen_EnumerateProcessModulesFast", RAMYXEN_ERR_INVALID_ARG, ERROR_INVALID_PARAMETER, pid);
        return RAMYXEN_ERR_INVALID_ARG;
    }
    *outTotalModules = 0;

    // MSDN-mandated retry loop for ERROR_BAD_LENGTH (24) while target loader list mutates
    ScopedHandle hSnap;
    DWORD lastErr = 0;
    for (int attempt = 0; attempt < 5; ++attempt) {
        hSnap.Reset(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid));
        if (hSnap.IsValid()) break;
        lastErr = GetLastError();
        if (lastErr != ERROR_BAD_LENGTH) break;
        Sleep(5);
    }

    if (!hSnap.IsValid()) {
        const int32_t code = (lastErr == ERROR_ACCESS_DENIED) ? RAMYXEN_ERR_ACCESS_DENIED : RAMYXEN_ERR_API_FAILED;
        RecordNativeError("Ramyxen_EnumerateProcessModulesFast", code, lastErr, pid);
        return code;
    }

    MODULEENTRY32W me{};
    me.dwSize = sizeof(me);
    if (!Module32FirstW(hSnap.h, &me)) {
        return RAMYXEN_OK;
    }

    // Count ALL modules so *outTotalModules reports exact total even if truncated at maxModules
    int32_t count = 0;
    do {
        if (outModules && count < maxModules) {
            RamyxenModuleInfo& m = outModules[count];
            ZeroMemory(&m, sizeof(m));
            m.BaseAddress = reinterpret_cast<uint64_t>(me.modBaseAddr);
            m.SizeBytes = static_cast<uint64_t>(me.modBaseSize);
            wcsncpy_s(m.ModuleName, _countof(m.ModuleName), me.szModule, _TRUNCATE);
            wcsncpy_s(m.FilePath, _countof(m.FilePath), me.szExePath, _TRUNCATE);
        }
        count++;
        if (count >= 4096) break;
    } while (Module32NextW(hSnap.h, &me));

    *outTotalModules = count;
    return RAMYXEN_OK;
}

RAMYXEN_API int32_t Ramyxen_ReadProcessMemorySafe(
    uint32_t pid,
    uint64_t baseAddress,
    uint8_t* outBuffer,
    int32_t bufferSize,
    int32_t* outBytesRead) {
    if (!outBuffer || !outBytesRead || bufferSize <= 0 || bufferSize > (16 * 1024 * 1024)) {
        RecordNativeError("Ramyxen_ReadProcessMemorySafe", RAMYXEN_ERR_INVALID_ARG, ERROR_INVALID_PARAMETER, pid);
        return RAMYXEN_ERR_INVALID_ARG;
    }
    *outBytesRead = 0;

    ScopedHandle hProc(OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
    if (!hProc.IsValid()) {
        const DWORD err = GetLastError();
        const int32_t code = (err == ERROR_ACCESS_DENIED) ? RAMYXEN_ERR_ACCESS_DENIED : RAMYXEN_ERR_PROCESS_NOT_FOUND;
        RecordNativeError("Ramyxen_ReadProcessMemorySafe", code, err, pid);
        return code;
    }

    SIZE_T bytesRead = 0;
    const BOOL ok = ReadProcessMemory(
        hProc.h,
        reinterpret_cast<LPCVOID>(baseAddress),
        outBuffer,
        static_cast<SIZE_T>(bufferSize),
        &bytesRead);

    *outBytesRead = static_cast<int32_t>(bytesRead);
    if (!ok) {
        const DWORD readErr = GetLastError();
        if (bytesRead > 0) {
            RecordNativeError("Ramyxen_ReadProcessMemorySafe", RAMYXEN_ERR_PARTIAL_READ, readErr, pid);
            return RAMYXEN_ERR_PARTIAL_READ;
        }
        RecordNativeError("Ramyxen_ReadProcessMemorySafe", RAMYXEN_ERR_API_FAILED, readErr, pid);
        return RAMYXEN_ERR_API_FAILED;
    }
    return RAMYXEN_OK;
}

RAMYXEN_API int32_t Ramyxen_ExtractExecutableIconBgra(
    const wchar_t* filePath,
    int32_t desiredSize,
    uint8_t* outBgraBuffer,
    int32_t bufferBytes,
    int32_t* outWidth,
    int32_t* outHeight,
    int32_t* outProvenance) {
    if (!filePath || !filePath[0] || !outBgraBuffer || !outWidth || !outHeight || !outProvenance || desiredSize <= 0 || desiredSize > 256) {
        RecordNativeError("Ramyxen_ExtractExecutableIconBgra", RAMYXEN_ERR_INVALID_ARG, ERROR_INVALID_PARAMETER, 0);
        return RAMYXEN_ERR_INVALID_ARG;
    }
    *outWidth = 0;
    *outHeight = 0;
    *outProvenance = RAMYXEN_ICON_PROV_NONE;

    const int32_t requiredBytes = desiredSize * desiredSize * 4;
    if (bufferBytes < requiredBytes) {
        RecordNativeError("Ramyxen_ExtractExecutableIconBgra", RAMYXEN_ERR_INVALID_ARG, ERROR_INSUFFICIENT_BUFFER, 0);
        return RAMYXEN_ERR_INVALID_ARG;
    }

    // 1. Thread-safe, COM-free DPI-exact PE resource extraction via PrivateExtractIconsW (primary) + ExtractIconExW (fallback)
    HICON hExtracted = nullptr;
    int32_t provenance = RAMYXEN_ICON_PROV_NONE;
    UINT iconId = 0;
    UINT privateExtracted = PrivateExtractIconsW(filePath, 0, desiredSize, desiredSize, &hExtracted, &iconId, 1, 0);
    if (privateExtracted > 0 && privateExtracted != UINT_MAX && hExtracted != nullptr) {
        provenance = RAMYXEN_ICON_PROV_PE_RESOURCE;
    } else {
        if (hExtracted) { DestroyIcon(hExtracted); hExtracted = nullptr; }
        HICON hLarge = nullptr;
        HICON hSmall = nullptr;
        UINT numExtracted = ExtractIconExW(filePath, 0, &hLarge, &hSmall, 1);
        if (numExtracted != 0 && numExtracted != UINT_MAX) {
            if (desiredSize <= 20 && hSmall) {
                hExtracted = hSmall;
                if (hLarge) DestroyIcon(hLarge);
                provenance = RAMYXEN_ICON_PROV_PE_RESOURCE;
            } else if (hLarge) {
                hExtracted = hLarge;
                if (hSmall) DestroyIcon(hSmall);
                provenance = RAMYXEN_ICON_PROV_PE_RESOURCE;
            } else if (hSmall) {
                hExtracted = hSmall;
                provenance = RAMYXEN_ICON_PROV_PE_RESOURCE;
            }
        }
    }

    // 2. Fallback to Windows Shell SHGetFileInfoW ONLY for existing non-directory files on strict STA apartment.
    //    Existing .exe files with NO PE icon resource receive Windows' generic .exe icon -> classify honestly as GENERIC_FILETYPE (R2-NEW-012)!
    DWORD fallbackErr = ERROR_FILE_NOT_FOUND;
    if (!hExtracted) {
        DWORD attrs = GetFileAttributesW(filePath);
        if (attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY) == 0) {
            fallbackErr = ERROR_RESOURCE_DATA_NOT_FOUND;
            size_t len = wcslen(filePath);
            bool isExe = (len >= 4 && _wcsicmp(filePath + len - 4, L".exe") == 0);
            if (isExe) {
                // Existing .exe has zero PE icon resources; do NOT mislabel Windows' generic .exe icon as official!
                *outProvenance = RAMYXEN_ICON_PROV_GENERIC_FILETYPE;
                RecordNativeError("Ramyxen_ExtractExecutableIconBgra", RAMYXEN_ERR_API_FAILED, ERROR_RESOURCE_DATA_NOT_FOUND, 0);
                return RAMYXEN_ERR_API_FAILED;
            }
            ScopedComInit comInit;
            if (comInit.hr == S_OK || comInit.hr == S_FALSE) {
                SHFILEINFOW sfi{};
                UINT flags = SHGFI_ICON | ((desiredSize <= 20) ? SHGFI_SMALLICON : SHGFI_LARGEICON);
                DWORD_PTR res = SHGetFileInfoW(filePath, 0, &sfi, sizeof(sfi), flags);
                if (res != 0 && sfi.hIcon) {
                    hExtracted = sfi.hIcon;
                    provenance = RAMYXEN_ICON_PROV_SHELL_FILE;
                } else {
                    fallbackErr = GetLastError();
                }
            } else {
                fallbackErr = static_cast<DWORD>(comInit.hr);
            }
        } else if (attrs != INVALID_FILE_ATTRIBUTES) {
            fallbackErr = ERROR_ACCESS_DENIED;
        } else {
            fallbackErr = GetLastError();
        }
    }

    if (!hExtracted) {
        RecordNativeError("Ramyxen_ExtractExecutableIconBgra", RAMYXEN_ERR_API_FAILED, fallbackErr, 0);
        return RAMYXEN_ERR_API_FAILED;
    }

    ScopedIcon iconGuard(hExtracted);

    BITMAPINFO bmi{};
    bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bmi.bmiHeader.biWidth = desiredSize;
    bmi.bmiHeader.biHeight = -desiredSize; // Top-down DIB
    bmi.bmiHeader.biPlanes = 1;
    bmi.bmiHeader.biBitCount = 32;
    bmi.bmiHeader.biCompression = BI_RGB;

    void* dibBits = nullptr;
    ScopedMemoryDC memDc(CreateCompatibleDC(nullptr));
    if (!memDc.hdc) {
        RecordNativeError("Ramyxen_ExtractExecutableIconBgra", RAMYXEN_ERR_API_FAILED, GetLastError(), 0);
        return RAMYXEN_ERR_API_FAILED;
    }

    HBITMAP hDib = CreateDIBSection(memDc.hdc, &bmi, DIB_RGB_COLORS, &dibBits, nullptr, 0);
    if (!hDib || !dibBits) {
        RecordNativeError("Ramyxen_ExtractExecutableIconBgra", RAMYXEN_ERR_API_FAILED, GetLastError(), 0);
        return RAMYXEN_ERR_API_FAILED;
    }
    ScopedGdiObject dibGuard(hDib);

    HGDIOBJ oldObj = SelectObject(memDc.hdc, hDib);
    ZeroMemory(dibBits, static_cast<size_t>(requiredBytes));

    BOOL drawn = DrawIconEx(memDc.hdc, 0, 0, iconGuard.h, desiredSize, desiredSize, 0, nullptr, DI_NORMAL);
    const DWORD drawErr = drawn ? ERROR_SUCCESS : GetLastError();
    SelectObject(memDc.hdc, oldObj);

    if (!drawn) {
        RecordNativeError("Ramyxen_ExtractExecutableIconBgra", RAMYXEN_ERR_API_FAILED, drawErr, 0);
        return RAMYXEN_ERR_API_FAILED;
    }

    // Check alpha channel; if legacy 1-bit masked icon left alpha=0, render DI_MASK so opaque pure-black pixels keep alpha=255
    uint8_t* px = static_cast<uint8_t*>(dibBits);
    bool anyAlpha = false;
    for (int32_t i = 3; i < requiredBytes; i += 4) {
        if (px[i] != 0) { anyAlpha = true; break; }
    }
    if (!anyAlpha) {
        void* maskBits = nullptr;
        HBITMAP hMaskDib = CreateDIBSection(memDc.hdc, &bmi, DIB_RGB_COLORS, &maskBits, nullptr, 0);
        if (hMaskDib && maskBits) {
            ScopedGdiObject maskGuard(hMaskDib);
            HGDIOBJ oldM = SelectObject(memDc.hdc, hMaskDib);
            memset(maskBits, 0xFF, static_cast<size_t>(requiredBytes));
            BOOL maskDrawn = DrawIconEx(memDc.hdc, 0, 0, iconGuard.h, desiredSize, desiredSize, 0, nullptr, DI_MASK);
            SelectObject(memDc.hdc, oldM);
            if (maskDrawn) {
                const uint8_t* mpx = static_cast<const uint8_t*>(maskBits);
                for (int32_t i = 0; i < requiredBytes; i += 4) {
                    // Mask pixel == 0 (black) means opaque icon pixel (even if color is pure RGB(0,0,0)!)
                    bool opaque = (mpx[i] == 0 && mpx[i + 1] == 0 && mpx[i + 2] == 0);
                    px[i + 3] = opaque ? 255 : 0;
                    if (!opaque) { px[i] = 0; px[i + 1] = 0; px[i + 2] = 0; }
                }
            }
        }
    }

    GdiFlush();
    memcpy(outBgraBuffer, dibBits, static_cast<size_t>(requiredBytes));
    *outWidth = desiredSize;
    *outHeight = desiredSize;
    *outProvenance = provenance;
    return RAMYXEN_OK;
}

} // extern "C"
