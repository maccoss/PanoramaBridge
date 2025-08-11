# Build Scripts

This directory contains files for building PanoramaBridge executables and distributions.

## Windows Build

### Files
- `PanoramaBridge.spec` - PyInstaller specification file for x86_64 Windows executable
- `PanoramaBridge-arm64.spec` - PyInstaller specification file for ARM64 Windows executable
- `build_windows.ps1` - PowerShell script for building x86_64 Windows executable
- `build_windows_arm64.ps1` - PowerShell script for building ARM64 Windows executable
- `build_windows.bat` - Batch script for building x86_64 Windows executable
- [`BUILD_WINDOWS.md`](BUILD_WINDOWS.md) - Detailed instructions for Windows build process
- [`MULTI_ARCHITECTURE_BUILD.md`](../docs/MULTI_ARCHITECTURE_BUILD.md) - Multi-architecture build guide

### Quick Build

#### x86_64 (Intel/AMD)
```bash
# Using PowerShell
./build_windows.ps1

# Using Command Prompt
build_windows.bat
```

#### ARM64 (Snapdragon)
```bash
# Must run on ARM64 hardware
./build_windows_arm64.ps1
```

### Requirements
- Python 3.12+ (matching target architecture)
- PyInstaller (`pip install pyinstaller`)
- All dependencies from `requirements.txt`

### Architecture Support
- **x86_64**: Standard Intel/AMD processors - `PanoramaBridge.exe`
- **ARM64**: Snapdragon and other ARM64 processors - `PanoramaBridge-arm64.exe`

## Build Output
- **x86_64**: Executable will be created in `../dist/PanoramaBridge.exe`
- **ARM64**: Executable will be created in `../dist/PanoramaBridge-arm64.exe`
- Build artifacts will be in `../build/` directory

## CI/CD Support
GitHub Actions automatically builds both architectures:
- x86_64 build on `windows-latest` runner
- ARM64 build on `windows-latest-arm64` runner

For detailed build instructions, see [BUILD_WINDOWS.md](BUILD_WINDOWS.md) and [MULTI_ARCHITECTURE_BUILD.md](../docs/MULTI_ARCHITECTURE_BUILD.md).
