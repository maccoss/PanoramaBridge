# Building PanoramaBridge Windows Executable

This guide will help you create a Windows executable (`.exe`) file for PanoramaBridge.

## Why You Need This

This guide will help you create the correct Windows executable.

## Prerequisites

1. **Windows Python Installation**
   - Download and install Python from [python.org](https://python.org)
   - **IMPORTANT**: During installation, check "Add Python to PATH"
   - Recommended version: Python 3.8 or newer

2. **Visual Studio Build Tools** (Optional but recommended)
   - Some packages may require compilation
   - Download from Microsoft Visual Studio website

## Build Methods

### Method 1: Automated Build (Recommended)

1. **Open Windows Command Prompt or PowerShell** (not WSL!)
   - Press `Win + R`, type `cmd` or `powershell`, press Enter

2. **Navigate to the project directory:**
   ```cmd
   cd C:\Users\macco\Documents\GitHub\maccoss\PanoramaBridge
   ```

3. **Run the build script:**
   
   **For Command Prompt:**
   ```cmd
   build_windows.bat
   ```
   
   **For PowerShell:**
   ```powershell
   .\build_windows.ps1
   ```

### Method 2: Manual Build

1. **Open Windows Command Prompt/PowerShell** (not WSL!)

2. **Navigate to project directory:**
   ```cmd
   cd C:\Users\macco\Documents\GitHub\maccoss\PanoramaBridge
   ```

3. **Create virtual environment:**
   ```cmd
   python -m venv .venv
   .venv\Scripts\activate
   ```

4. **Install dependencies:**
   ```cmd
   python -m pip install --upgrade pip
   pip install -r requirements.txt
   pip install pyinstaller
   ```

5. **Test imports (optional):**
   ```cmd
   python test_imports.py
   ```

6. **Build executable:**
   ```cmd
   pyinstaller PanoramaBridge.spec
   ```

### Method 3: Simple One-File Build

If the spec file doesn't work, try this simple approach:

```cmd
pyinstaller --onefile --windowed --name PanoramaBridge panoramabridge.py
```

## Output

After a successful build, you'll find:
- **Executable**: `dist\PanoramaBridge.exe`
- **Size**: Approximately 60-80 MB
- **Dependencies**: All included (no need to install Python on target machines)

## Running the Executable

1. **From Windows:**
   ```cmd
   dist\PanoramaBridge.exe
   ```

2. **Double-click** the file in Windows Explorer

## Troubleshooting

### "Python not found"
- Reinstall Python and ensure "Add to PATH" is checked
- Or use full path: `C:\Users\%USERNAME%\AppData\Local\Programs\Python\Python311\python.exe`

### "Module not found" errors
- Make sure you're using Windows Python, not WSL Python
- Reinstall requirements: `pip install -r requirements.txt`

### Large file size
- This is normal for PyQt6 applications (60-80 MB)
- PyInstaller includes the entire Python runtime

### Antivirus warnings
- Some antivirus software flags PyInstaller executables
- Add exception for the `dist` folder
- This is a known false positive

### Console window appears
- Change `console=False` to `console=True` in the spec file for debugging
- Change back to `console=False` for release version

## Distribution

The executable (`dist\PanoramaBridge.exe`) is self-contained and can be:
- Copied to other Windows machines
- Shared without requiring Python installation
- Run directly from USB drives or network shares

## Notes

- Build on the same Windows version you plan to run on
- The executable is about 50x larger than the script but includes everything needed
- First run may be slower as Windows scans the new executable
