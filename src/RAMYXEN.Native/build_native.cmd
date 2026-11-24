@echo off
setlocal
set "VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
if not exist "%VCVARS%" (
    for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do (
        set "VCVARS=%%i\VC\Auxiliary\Build\vcvars64.bat"
    )
)
call "%VCVARS%" >nul
if errorlevel 1 exit /b 1

if not exist "%~dp0bin\Release" mkdir "%~dp0bin\Release"
pushd "%~dp0bin\Release"

cl /nologo /O2 /MT /EHsc /std:c++20 /W4 /guard:cf /Brepro /LD "%~dp0src\ramyxen_native.cpp" /Fe:RAMYXEN.Native.dll /link /Brepro /guard:cf /DEPENDENTLOADFLAG:0x800 psapi.lib kernel32.lib advapi32.lib shell32.lib user32.lib gdi32.lib ole32.lib
if errorlevel 1 (
    popd
    exit /b 1
)

cl /nologo /O2 /MT /EHsc /std:c++20 /W4 /guard:cf /Brepro "%~dp0src\ramyxen_heap_worker.cpp" RAMYXEN.Native.lib /Fe:RAMYXEN.HeapWorker.exe /link /Brepro /guard:cf /DEPENDENTLOADFLAG:0xA00 kernel32.lib
if errorlevel 1 (
    popd
    exit /b 1
)

cl /nologo /O2 /MT /EHsc /std:c++20 /W4 /guard:cf /Brepro "%~dp0tests\ramyxen_native_tests.cpp" RAMYXEN.Native.lib /Fe:RAMYXEN.Native.Tests.exe /link /Brepro /guard:cf user32.lib kernel32.lib
if errorlevel 1 (
    popd
    exit /b 1
)

popd
exit /b 0
