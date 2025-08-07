# Build Scripts

This directory contains files for building PanoramaBridge executables and distributions.

## Windows Build

### Files
- `PanoramaBridge.spec` - PyInstaller specification file for creating Windows executable
- `build_windows.ps1` - PowerShell script for building Windows executable
- `build_windows.bat` - Batch script for building Windows executable  
- [`BUILD_WINDOWS.md`](BUILD_WINDOWS.md) - Detailed instructions for Windows build process

### Quick Build
```bash
# Using PowerShell
./build_windows.ps1

# Using Command Prompt
build_windows.bat
```

### Requirements
- Python 3.8+
- PyInstaller (`pip install pyinstaller`)
- All dependencies from `requirements.txt`

## Build Output
- Executable will be created in `../dist/PanoramaBridge.exe`
- Build artifacts will be in `../build/` directory

For detailed build instructions, see [BUILD_WINDOWS.md](BUILD_WINDOWS.md).
