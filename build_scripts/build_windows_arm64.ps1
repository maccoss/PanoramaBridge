# PowerShell script to build PanoramaBridge executable for ARM64/Snapdragon
# This script should be run on an ARM64 Windows system (Snapdragon laptop/PC)
# Run this from the build_scripts directory with: .\build_windows_arm64.ps1
# Or from the root directory with: .\build_scripts\build_windows_arm64.ps1

Write-Host "Building PanoramaBridge ARM64 Windows Executable..." -ForegroundColor Green
Write-Host ""

# Check architecture
$arch = $env:PROCESSOR_ARCHITECTURE
Write-Host "Detected processor architecture: $arch" -ForegroundColor Yellow
if ($arch -ne "ARM64") {
    Write-Host "WARNING: You are not on an ARM64 system!" -ForegroundColor Red
    Write-Host "This build may not work properly on Snapdragon processors." -ForegroundColor Red
    Write-Host "For best results, run this on an ARM64 Windows device." -ForegroundColor Red
    Write-Host ""
}

# Change to the root directory if we're in build_scripts
$currentDir = Get-Location
if ($currentDir.Path.EndsWith("build_scripts")) {
    Set-Location ".."
    Write-Host "Changed to root directory: $(Get-Location)" -ForegroundColor Yellow
}

# Check if Python is available and get architecture
try {
    $pythonVersion = python --version 2>&1
    $pythonPlatform = python -c "import platform; print(f'{platform.machine()} - {platform.architecture()[0]}')" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Python not found"
    }
    Write-Host "Python found: $pythonVersion" -ForegroundColor Yellow
    Write-Host "Python platform: $pythonPlatform" -ForegroundColor Yellow
    
    # Check if Python is ARM64
    if ($pythonPlatform -notlike "*ARM64*" -and $pythonPlatform -notlike "*aarch64*") {
        Write-Host "WARNING: Python may not be ARM64 native!" -ForegroundColor Red
        Write-Host "Consider installing ARM64 Python from https://python.org" -ForegroundColor Red
        Write-Host ""
    }
} catch {
    Write-Host "ERROR: Python not found in PATH" -ForegroundColor Red
    Write-Host "Please install ARM64 Python from https://python.org" -ForegroundColor Red
    Write-Host "Make sure to check 'Add Python to PATH' during installation" -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}

# Check if we're in the right directory
if (-not (Test-Path "panoramabridge.py")) {
    Write-Host "ERROR: panoramabridge.py not found" -ForegroundColor Red
    Write-Host "Please run this script from the PanoramaBridge directory" -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}

# Create virtual environment for ARM64 if it doesn't exist
if (-not (Test-Path ".venv-arm64\Scripts")) {
    Write-Host "Creating/Recreating virtual environment for ARM64..." -ForegroundColor Yellow
    if (Test-Path ".venv-arm64") { Remove-Item -Recurse -Force .venv-arm64 }
    python -m venv .venv-arm64
}

# Activate virtual environment
Write-Host "Activating ARM64 virtual environment..." -ForegroundColor Yellow
try {
    & ".\.venv-arm64\Scripts\Activate.ps1"
} catch {
    Write-Host "Warning: Could not activate virtual environment via Activate.ps1" -ForegroundColor Yellow
    Write-Host "Continuing with virtual environment using direct python executable..." -ForegroundColor Yellow
}

# Upgrade pip in virtual environment
Write-Host "Updating pip..." -ForegroundColor Yellow
& ".\.venv-arm64\Scripts\python.exe" -m pip install --upgrade pip

# Install requirements with ARM64 specific handling
Write-Host "Installing requirements for ARM64..." -ForegroundColor Yellow
Write-Host "Note: This may take longer as some packages might compile from source" -ForegroundColor Yellow
& ".\.venv-arm64\Scripts\python.exe" -m pip install -r requirements.txt

# Check PyQt6 installation
Write-Host "Verifying PyQt6 installation..." -ForegroundColor Yellow
$pyqt6Check = & ".\.venv-arm64\Scripts\python.exe" -c "import PyQt6.QtCore; print('PyQt6 OK - Version:', PyQt6.QtCore.PYQT_VERSION_STR)" 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "PyQt6 verification: $pyqt6Check" -ForegroundColor Green
} else {
    Write-Host "ERROR: PyQt6 installation failed!" -ForegroundColor Red
    Write-Host "This is required for the GUI. Try installing manually:" -ForegroundColor Red
    Write-Host "  .\.venv-arm64\Scripts\python.exe -m pip install PyQt6" -ForegroundColor Red
    Read-Host "Press Enter to continue anyway"
}

# Install PyInstaller in virtual environment
Write-Host "Installing PyInstaller..." -ForegroundColor Yellow
& ".\.venv-arm64\Scripts\python.exe" -m pip install pyinstaller

# Clean previous builds
Write-Host "Cleaning previous builds..." -ForegroundColor Yellow
if (Test-Path "dist") { Remove-Item -Recurse -Force dist }
if (Test-Path "build") { Remove-Item -Recurse -Force build }

# Build executable using ARM64 spec file
Write-Host "Building ARM64 executable using ARM64 spec file..." -ForegroundColor Yellow
& ".\.venv-arm64\Scripts\pyinstaller.exe" "build_scripts\PanoramaBridge-arm64.spec"

# Check if build was successful
if (Test-Path "dist\PanoramaBridge-arm64.exe") {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "ARM64 BUILD SUCCESSFUL!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "ARM64 Executable created at: dist\PanoramaBridge-arm64.exe" -ForegroundColor Yellow
    
    $fileInfo = Get-Item "dist\PanoramaBridge-arm64.exe"
    $sizeInMB = [math]::Round($fileInfo.Length / 1MB, 2)
    Write-Host "File size: $($fileInfo.Length) bytes ($sizeInMB MB)" -ForegroundColor Yellow
    
    # Test the executable architecture
    Write-Host ""
    Write-Host "Verifying executable architecture..." -ForegroundColor Yellow
    try {
        $exeArch = & dumpbin /headers "dist\PanoramaBridge-arm64.exe" 2>$null | Select-String "machine"
        if ($exeArch) {
            Write-Host "Executable architecture: $exeArch" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "Could not verify architecture (dumpbin not available)" -ForegroundColor Yellow
    }
    
    Write-Host ""
    Write-Host "You can now run: dist\PanoramaBridge-arm64.exe" -ForegroundColor Green
    Write-Host "This executable is optimized for ARM64/Snapdragon processors" -ForegroundColor Green
    Write-Host ""
} else {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Red
    Write-Host "ARM64 BUILD FAILED!" -ForegroundColor Red
    Write-Host "========================================" -ForegroundColor Red
    Write-Host ""
    Write-Host "Check the output above for errors." -ForegroundColor Red
    Write-Host "Common issues on ARM64:" -ForegroundColor Red
    Write-Host "- Some Python packages may not have ARM64 wheels" -ForegroundColor Red
    Write-Host "- PyQt6 ARM64 installation issues" -ForegroundColor Red
    Write-Host "- Missing ARM64 development tools" -ForegroundColor Red
}

Write-Host ""
Read-Host "Press Enter to exit"
