// ============================================================================
// RAMYXEN Out-of-Process Killable Heap Inspection Worker (F-003, R2-NEW-006..009)
//
// Executes CreateToolhelp32Snapshot(TH32CS_SNAPHEAPLIST) + Heap32First/Next
// inside a dedicated short-lived child process so the main RAMYXEN process
// can enforce a hard external timeout via TerminateProcess without leaking
// threads or stalling the thread pool on suspended/huge target processes.
// ============================================================================

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <windows.h>
#include "../include/ramyxen_native.h"

static bool VerifyTargetProcessStartTime(uint32_t pid, uint64_t expectedFileTimeUtc) {
    if (expectedFileTimeUtc == 0) return true;
    HANDLE hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!hProc) return true; // If access denied to query time, let native heap API report exact access error
    FILETIME createFt{}, exitFt{}, kernelFt{}, userFt{};
    bool match = true;
    if (GetProcessTimes(hProc, &createFt, &exitFt, &kernelFt, &userFt)) {
        ULARGE_INTEGER uli{};
        uli.LowPart = createFt.dwLowDateTime;
        uli.HighPart = createFt.dwHighDateTime;
        // Allow up to 10ms clock quantization tolerance
        uint64_t actual = uli.QuadPart;
        uint64_t diff = actual > expectedFileTimeUtc ? (actual - expectedFileTimeUtc) : (expectedFileTimeUtc - actual);
        match = diff <= 100000ULL;
    }
    CloseHandle(hProc);
    return match;
}

int main(int argc, char** argv) {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    SetDllDirectoryW(L"");

    const int32_t nativeAbi = Ramyxen_GetAbiVersion();
    if (nativeAbi != RAMYXEN_ABI_VERSION) {
        printf("META|%d|%d|0\n", nativeAbi, RAMYXEN_ERR_UNSUPPORTED);
        printf("ERR|%d|0|ABI_MISMATCH\n", RAMYXEN_ERR_UNSUPPORTED);
        fflush(stdout);
        return 3;
    }

    if (argc < 2) {
        fprintf(stderr, "Usage: RAMYXEN.HeapWorker.exe <pid> [maxHeaps] [expectedStartFileTimeUtc]\n");
        return 2;
    }

    if (argv[1][0] == '-' || argv[1][0] == '\0') {
        fprintf(stderr, "Invalid PID argument.\n");
        return 2;
    }

    errno = 0;
    char* endPtr = nullptr;
    unsigned long parsedPid = strtoul(argv[1], &endPtr, 10);
    if (errno == ERANGE || endPtr == argv[1] || *endPtr != '\0' || parsedPid == 0) {
        fprintf(stderr, "Invalid PID argument.\n");
        return 2;
    }

    int32_t maxHeaps = 64;
    if (argc >= 3) {
        errno = 0;
        char* maxEnd = nullptr;
        long parsedMax = strtol(argv[2], &maxEnd, 10);
        if (errno == ERANGE || maxEnd == argv[2] || *maxEnd != '\0' || parsedMax <= 0 || parsedMax > 256) {
            fprintf(stderr, "Invalid maxHeaps argument (1..256).\n");
            return 2;
        }
        maxHeaps = static_cast<int32_t>(parsedMax);
    }

    uint64_t expectedStartFt = 0;
    if (argc >= 4) {
        errno = 0;
        char* ftEnd = nullptr;
        unsigned long long parsedFt = strtoull(argv[3], &ftEnd, 10);
        if (errno == ERANGE || ftEnd == argv[3] || *ftEnd != '\0') {
            fprintf(stderr, "Invalid expectedStartFileTimeUtc argument.\n");
            return 2;
        }
        expectedStartFt = static_cast<uint64_t>(parsedFt);
    }

    const uint32_t targetPid = static_cast<uint32_t>(parsedPid);
    if (!VerifyTargetProcessStartTime(targetPid, expectedStartFt)) {
        printf("META|%d|%d|0\n", nativeAbi, RAMYXEN_ERR_PROCESS_NOT_FOUND);
        printf("ERR|%d|0|PidReused\n", RAMYXEN_ERR_PROCESS_NOT_FOUND);
        fflush(stdout);
        return 4;
    }

    RamyxenHeapSummary heaps[256]{};
    int32_t totalHeaps = 0;
    const int32_t rc = Ramyxen_EnumerateProcessHeaps(
        targetPid,
        heaps,
        maxHeaps,
        &totalHeaps);

    printf("META|%d|%d|%d\n", nativeAbi, rc, totalHeaps);
    if (rc == RAMYXEN_OK && totalHeaps > 0) {
        const int32_t emitCount = totalHeaps < maxHeaps ? totalHeaps : maxHeaps;
        for (int32_t i = 0; i < emitCount; ++i) {
            printf("HEAP|%llu|%u|%u|%llu|%llu|%llu\n",
                static_cast<unsigned long long>(heaps[i].HeapId),
                heaps[i].Flags,
                heaps[i].BlockCount,
                static_cast<unsigned long long>(heaps[i].CommittedBytes),
                static_cast<unsigned long long>(heaps[i].AllocatedBytes),
                static_cast<unsigned long long>(heaps[i].FreeBytes));
        }
    } else if (rc != RAMYXEN_OK) {
        RamyxenNativeErrorInfo err{};
        if (Ramyxen_GetLastNativeError(&err) == RAMYXEN_OK) {
            printf("ERR|%d|%u|%s\n", err.StatusCode, err.Win32ErrorCode, err.FunctionName);
        }
    }
    fflush(stdout);
    return rc == RAMYXEN_OK ? 0 : 1;
}
