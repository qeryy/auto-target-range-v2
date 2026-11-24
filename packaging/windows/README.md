# Windows Packaging

This directory owns every Windows release build. The source tree and release
artifacts use separate names so a file path always identifies its layer.

| Layer | Entry point | Output |
| --- | --- | --- |
| Native desktop | `build-desktop.ps1` | `dist/windows/desktop/SpiritScout-<version>-windows-x64.zip` |
| Backend runtime | `build-backend-runtime.ps1` | `build/backend-runtime/` |
| Backend console | `build-backend-console.ps1` | `dist/windows/backend-console/SpiritScout-<version>-windows-x64-backend.exe` |

`build-desktop.ps1` is the only end-user Windows release entry point. The other
two scripts build reusable backend layers for packaging, diagnostics, and
compatibility.

Python embeddable archives prefer Huawei Cloud, npmmirror, and TUNA before
falling back to python.org. Dependency installation prefers the TUNA PyPI
mirror and retries official PyPI when needed.

The old WebView portable archive is not built by the current scripts and is not
the complete desktop release.

The desktop source lives in `desktop/`. The generated app-image is
staged under `build/windows-desktop/`, and the distributable archive
is written under the workspace `dist/` directory. Build outputs are not tracked.
