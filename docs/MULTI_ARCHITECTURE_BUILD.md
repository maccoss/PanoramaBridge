# Multi-Architecture Build Guide

This document explains how to build PanoramaBridge for different processor architectures.

## Supported Architectures

- **x86_64 (Intel/AMD)**: Standard desktop/laptop processors
- **ARM64 (Snapdragon)**: ARM-based processors including Snapdragon laptops

## Local Building

### x86_64 Build
```powershell
# Run from project root
.\build_scripts\build_windows.ps1
```
- Creates: `dist\PanoramaBridge.exe`
- Compatible with Intel/AMD processors

### ARM64 Build
```powershell
# Must run on ARM64 hardware (Snapdragon laptop/PC)
.\build_scripts\build_windows_arm64.ps1
```
- Creates: `dist\PanoramaBridge-arm64.exe`
- Compatible with ARM64/Snapdragon processors
- **Important**: Must be built on ARM64 hardware for best results

## GitHub Actions CI/CD

### Automatic Builds
The main workflow (`build-windows.yml`) automatically builds both architectures:

- **x86_64**: Uses `windows-latest` runner
- **ARM64**: Uses `windows-latest-arm64` runner (if available)

### Manual Fallback
If ARM64 runners are unavailable, use the fallback workflow:

1. Go to Actions tab in GitHub
2. Select "Build Windows Executable (Fallback)"
3. Click "Run workflow"

This attempts cross-compilation but may have limitations.

## Distribution Strategy

### Release Artifacts
When creating releases, both executables are automatically uploaded:
- `PanoramaBridge-Windows-x64.exe` - For Intel/AMD systems
- `PanoramaBridge-Windows-ARM64.exe` - For Snapdragon/ARM64 systems

### User Download Guide
**For end users:**
- **Intel/AMD laptop/desktop**: Download `PanoramaBridge-Windows-x64.exe`
- **Snapdragon laptop/ARM device**: Download `PanoramaBridge-Windows-ARM64.exe`
- **Not sure?**: Try x64 first - it works on most systems

## Technical Details

### Architecture Detection
The build scripts automatically detect and verify:
- System processor architecture
- Python installation architecture
- Executable output architecture

### Performance Considerations
- **ARM64 native**: Best performance on Snapdragon devices
- **x64 on ARM64**: Works via emulation but with performance penalty
- **ARM64 cross-compiled**: May have compatibility issues

### Dependencies
All Python dependencies (PyQt6, watchdog, requests, keyring) support both architectures with proper wheels available.

## Troubleshooting

### ARM64 Build Issues
1. **PyQt6 not installing**: Ensure you're using ARM64 Python
2. **Cross-compilation fails**: Use native ARM64 hardware instead
3. **Executable won't run**: Verify you're on ARM64 hardware

### GitHub Actions Issues
1. **ARM64 runner unavailable**: Use fallback workflow
2. **Build failures**: Check Python/PyQt6 compatibility
3. **Large executable size**: Normal for bundled Qt applications

## Future Enhancements
- macOS ARM64 (Apple Silicon) support
- Linux ARM64 support
- Automated architecture detection in installer
