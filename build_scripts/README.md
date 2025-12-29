# Build Scripts

Scripts and configuration files for building PanoramaBridge executables.

## Quick Build

### Windows x64 (Intel/AMD)

```powershell
# PowerShell
./build_windows.ps1

# Command Prompt
build_windows.bat
```

### Windows ARM64 (Snapdragon)

```powershell
# Must run on ARM64 hardware
./build_windows_arm64.ps1
```

### Linux

```bash
# Make executable (first time only)
chmod +x build_scripts/build_linux.sh

# Run build
./build_scripts/build_linux.sh
```

## Files

| File | Purpose |
|------|---------|
| `PanoramaBridge.spec` | PyInstaller spec for Windows x64 builds |
| `PanoramaBridge-arm64.spec` | PyInstaller spec for Windows ARM64 builds |
| `PanoramaBridge-linux.spec` | PyInstaller spec for Linux builds |
| `build_windows.ps1` | PowerShell build script (x64) |
| `build_windows_arm64.ps1` | PowerShell build script (ARM64) |
| `build_windows.bat` | Batch build script (x64) |
| `build_linux.sh` | Bash build script (Linux) |
| `BUILD_WINDOWS.md` | Detailed build instructions |
| `GITHUB_ACTIONS.md` | CI/CD workflow documentation |

## Requirements

### Windows
- Python 3.12+ (matching target architecture)
- PyInstaller: `pip install pyinstaller`
- All dependencies from `requirements.txt`

### Linux
- Python 3.12+
- PyInstaller: `pip install pyinstaller`
- Qt6 libraries: `sudo apt install python3-pyqt6` (Ubuntu/Debian)
- All dependencies from `requirements.txt`

## Build Output

| Platform | Output |
|----------|--------|
| Windows x64 | `../dist/PanoramaBridge.exe` |
| Windows ARM64 | `../dist/PanoramaBridge-arm64.exe` |
| Linux | `../dist/PanoramaBridge` |

Build artifacts are in `../build/` directory.

## Detailed Documentation

- [BUILD_WINDOWS.md](BUILD_WINDOWS.md) - Complete Windows build guide
- [GITHUB_ACTIONS.md](GITHUB_ACTIONS.md) - CI/CD and automated releases
- [Multi-Architecture Build](../docs/MULTI_ARCHITECTURE_BUILD.md) - Cross-architecture builds

## CI/CD

GitHub Actions automatically builds executables:

- **On push to main**: Creates development builds (artifacts)
- **On release tags**: Creates production releases with downloads

See [GITHUB_ACTIONS.md](GITHUB_ACTIONS.md) for details.


