#pragma once
#include <stdint.h>

#ifdef RAMYXEN_NATIVE_EXPORTS
#define RAMYXEN_API __declspec(dllexport)
#else
#define RAMYXEN_API __declspec(dllimport)
#endif

extern "C" {

#define RAMYXEN_ABI_VERSION 102

enum RamyxenNativeStatus : int32_t {
    RAMYXEN_OK = 0,
    RAMYXEN_ERR_INVALID_ARG = -1,
    RAMYXEN_ERR_ACCESS_DENIED = -2,
    RAMYXEN_ERR_PROTECTED_PROCESS = -3,
    RAMYXEN_ERR_PROCESS_NOT_FOUND = -4,
    RAMYXEN_ERR_PARTIAL_READ = -5,
    RAMYXEN_ERR_API_FAILED = -6,
    RAMYXEN_ERR_TIMEOUT = -7,
    RAMYXEN_ERR_UNSUPPORTED = -8
};

enum RamyxenIconProvenance : int32_t {
    RAMYXEN_ICON_PROV_NONE = 0,
    RAMYXEN_ICON_PROV_PE_RESOURCE = 1,       // Extracted directly from executable PE icon group
    RAMYXEN_ICON_PROV_SHELL_FILE = 2,        // Resolved from Windows Shell for actual existing file
    RAMYXEN_ICON_PROV_GENERIC_FILETYPE = 3   // Generic .exe file-type fallback (NOT official app icon)
};

#pragma pack(push, 8)

struct RamyxenSystemMemorySnapshot {
    uint64_t InstalledPhysicalBytes;
    uint64_t TotalPhysicalBytes;
    uint64_t AvailablePhysicalBytes;
    uint64_t InUsePhysicalBytes;
    uint64_t HardwareReservedBytes;
    uint64_t CommitTotalBytes;
    uint64_t CommitLimitBytes;
    uint64_t CommitPeakBytes;
    uint64_t SystemCacheBytes;
    uint64_t KernelTotalBytes;
    uint64_t KernelPagedBytes;
    uint64_t KernelNonpagedBytes;
    uint64_t PageSizeBytes;
    uint32_t MemoryLoadPercent;
    uint32_t HandleCount;
    uint32_t ProcessCount;
    uint32_t ThreadCount;
    uint64_t TimestampUtcMs;
    int32_t  StatusCode;
    uint32_t ReservedPadding;
};

struct RamyxenKernelPageListMetrics {
    uint64_t ZeroPageBytes;
    uint64_t FreePageBytes;
    uint64_t ModifiedPageBytes;
    uint64_t ModifiedNoWritePageBytes;
    uint64_t BadPageBytes;
    uint64_t StandbyPriorityBytes[8];
    uint64_t TotalStandbyBytes;
    uint32_t PageFaultCountCumulative;
    uint32_t PageReadHardFaultCountCumulative;
    uint32_t HasExactMemoryLists;
    uint32_t HasExactFaultCounters;
};

struct RamyxenProcessMemoryMetrics {
    uint32_t Pid;
    uint32_t ParentPid;
    uint64_t WorkingSetBytes;
    uint64_t PrivateWorkingSetBytes;
    uint64_t SharedWorkingSetBytes;
    uint64_t PrivateCommitBytes;
    uint64_t PeakWorkingSetBytes;
    uint64_t PagefileUsageBytes;
    uint64_t VirtualSizeBytes;
    uint64_t PagedPoolBytes;
    uint64_t NonpagedPoolBytes;
    uint32_t PageFaultCount;
    uint32_t HandleCount;
    uint32_t ThreadCount;
    uint32_t IsProtectedOrRestricted;
    uint64_t StartTimeFileTimeUtc;
    int32_t  StatusCode;
    uint32_t ArchitectureCode; // 1 = x64, 2 = x86, 3 = ARM64, 0 = Unknown
    wchar_t  ExecutablePath[260];
};

struct RamyxenVirtualRegionInfo {
    uint64_t BaseAddress;
    uint64_t AllocationBase;
    uint64_t RegionSizeBytes;
    uint32_t State;             // MEM_COMMIT=0x1000, MEM_RESERVE=0x2000, MEM_FREE=0x10000
    uint32_t Protect;           // PAGE_* flags
    uint32_t AllocationProtect;
    uint32_t Type;              // MEM_PRIVATE=0x20000, MEM_MAPPED=0x40000, MEM_IMAGE=0x1000000
    uint32_t IsSuspiciousRwx;   // 1 if Private/Mapped + Executable Write (RWX / WCX)
    uint32_t Reserved;
};

struct RamyxenHeapSummary {
    uint64_t HeapId;
    uint32_t Flags;             // Bit 31 (0x80000000) set when walk was time/block-bounded
    uint32_t BlockCount;
    uint64_t CommittedBytes;
    uint64_t AllocatedBytes;
    uint64_t FreeBytes;
};

struct RamyxenModuleInfo {
    uint64_t BaseAddress;
    uint64_t SizeBytes;
    wchar_t  ModuleName[128];
    wchar_t  FilePath[260];
};

struct RamyxenNativeErrorInfo {
    int32_t  StatusCode;
    uint32_t Win32ErrorCode;
    int32_t  HResult;
    uint32_t TargetPid;
    uint64_t TimestampUtcMs;
    char     FunctionName[64];
};

#pragma pack(pop)

#ifdef __cplusplus
static_assert(sizeof(RamyxenSystemMemorySnapshot) == 136, "RamyxenSystemMemorySnapshot ABI size mismatch");
static_assert(sizeof(RamyxenKernelPageListMetrics) == 128, "RamyxenKernelPageListMetrics ABI size mismatch");
static_assert(sizeof(RamyxenProcessMemoryMetrics) == 632, "RamyxenProcessMemoryMetrics ABI size mismatch");
static_assert(sizeof(RamyxenVirtualRegionInfo) == 48, "RamyxenVirtualRegionInfo ABI size mismatch");
static_assert(sizeof(RamyxenHeapSummary) == 40, "RamyxenHeapSummary ABI size mismatch");
static_assert(sizeof(RamyxenModuleInfo) == 792, "RamyxenModuleInfo ABI size mismatch");
static_assert(sizeof(RamyxenNativeErrorInfo) == 88, "RamyxenNativeErrorInfo ABI size mismatch");
#endif

RAMYXEN_API int32_t Ramyxen_GetAbiVersion(void);

RAMYXEN_API int32_t Ramyxen_GetLastNativeError(
    RamyxenNativeErrorInfo* outError);

RAMYXEN_API int32_t Ramyxen_GetSystemMemorySnapshot(
    RamyxenSystemMemorySnapshot* outSnapshot);

RAMYXEN_API int32_t Ramyxen_GetKernelPageListMetrics(
    RamyxenKernelPageListMetrics* outMetrics);

RAMYXEN_API int32_t Ramyxen_GetProcessMemoryMetrics(
    uint32_t pid,
    RamyxenProcessMemoryMetrics* outMetrics);

RAMYXEN_API int32_t Ramyxen_EnumerateVirtualRegions(
    uint32_t pid,
    RamyxenVirtualRegionInfo* outRegions,
    int32_t maxRegions,
    int32_t* outTotalRegions);

RAMYXEN_API int32_t Ramyxen_EnumerateProcessHeaps(
    uint32_t pid,
    RamyxenHeapSummary* outHeaps,
    int32_t maxHeaps,
    int32_t* outTotalHeaps);

RAMYXEN_API int32_t Ramyxen_EnumerateProcessModulesFast(
    uint32_t pid,
    RamyxenModuleInfo* outModules,
    int32_t maxModules,
    int32_t* outTotalModules);

RAMYXEN_API int32_t Ramyxen_ReadProcessMemorySafe(
    uint32_t pid,
    uint64_t baseAddress,
    uint8_t* outBuffer,
    int32_t bufferSize,
    int32_t* outBytesRead);

RAMYXEN_API int32_t Ramyxen_ExtractExecutableIconBgra(
    const wchar_t* filePath,
    int32_t desiredSize,
    uint8_t* outBgraBuffer,
    int32_t bufferBytes,
    int32_t* outWidth,
    int32_t* outHeight,
    int32_t* outProvenance);

}
