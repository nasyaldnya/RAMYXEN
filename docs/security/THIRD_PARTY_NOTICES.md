# RAMYXEN — Third-Party Software Notices and Information

> [!IMPORTANT]
> **Project License Status:** RAMYXEN's original source code and documentation are licensed under the **Apache License, Version 2.0** (see root [`LICENSE`](../../LICENSE)). This document records the third-party runtime, SDK, and icon components utilized by RAMYXEN, which remain subject to their respective licenses.

---

## 1. .NET 10 Runtime and Libraries (`Microsoft.NETCore.App`)

- **Publisher:** .NET Foundation and Contributors
- **License:** MIT License
- **Repository:** https://github.com/dotnet/runtime

```text
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## 2. Microsoft Windows App SDK & WinUI 3 (`Microsoft.WindowsAppSDK 2.7.0`)

- **Publisher:** Microsoft Corporation
- **License:** MIT License (WinUI 3 & WindowsAppSDK open-source components) / Microsoft Windows App SDK License Terms (redistributable runtime binaries)
- **Repository:** https://github.com/microsoft/WindowsAppSDK

---

## 3. Microsoft Windows SDK Libraries (Static CRT `/MT`)

- **Publisher:** Microsoft Corporation
- **Components:** `kernel32.dll`, `psapi.dll`, `dbghelp.dll`, `advapi32.dll`, `user32.dll`, `shell32.dll`, `gdi32.dll`, `ole32.dll`, `dwmapi.dll`, `powrprof.dll` (native C++20 engine links CRT statically via `/MT`)
- **License:** Microsoft Windows Software Development Kit License Terms

---

## 4. Iconography & Microsoft Fluent Design Reference

- **Publisher (Reference Design Language):** Microsoft Corporation (`@fluentui/svg-icons` `1.1.x` MIT reference)
- **Usage in RAMYXEN:** `src/RAMYXEN.App/Icons/FluentIconCatalog.cs` contains compact, hand-authored `24×24` offline vector path geometries inspired by Microsoft Fluent System Icons for navigation, toolbar, Command Palette, diagnostics, and process category fallback iconography (zero runtime network calls).
- **RAMYXEN Custom First-Party Geometry:** `FluentGlyph.BrandMark` (the hexagonal/diamond RAMYXEN core emblem) and the multi-resolution `Assets/RAMYXEN.ico` / `Assets/RAMYXEN-256.png` application icon are original RAMYXEN brand artwork and are not derived from Fluent System Icons.

```text
MIT License

Copyright (c) 2020 Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
