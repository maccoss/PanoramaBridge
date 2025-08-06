# PowerShell script to build PanoramaBridge executable
# Run this from PowerShell with: .\build_windows.ps1

Write-Host "Building PanoramaBridge Windows Executable..." -ForegroundColor Green
Write-Host ""

# Check if Python is available
try {
    $pythonVersion = python --version 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Python not found"
    }
    Write-Host "Python found: $pythonVersion" -ForegroundColor Yellow
} catch {
    Write-Host "ERROR: Python not found in PATH" -ForegroundColor Red
    Write-Host "Please install Python from https://python.org" -ForegroundColor Red
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

# Create virtual environment if it doesn't exist or has wrong structure
if (-not (Test-Path ".venv\Scripts")) {
    Write-Host "Creating/Recreating virtual environment for Windows..." -ForegroundColor Yellow
    if (Test-Path ".venv") { Remove-Item -Recurse -Force .venv }
    python -m venv .venv
}

# Activate virtual environment
Write-Host "Activating virtual environment..." -ForegroundColor Yellow
try {
    & ".\.venv\Scripts\Activate.ps1"
} catch {
    Write-Host "Warning: Could not activate virtual environment via Activate.ps1" -ForegroundColor Yellow
    Write-Host "Continuing with virtual environment using direct python executable..." -ForegroundColor Yellow
}

# Upgrade pip in virtual environment
Write-Host "Updating pip..." -ForegroundColor Yellow
& ".\.venv\Scripts\python.exe" -m pip install --upgrade pip

# Install requirements in virtual environment
Write-Host "Installing requirements..." -ForegroundColor Yellow
& ".\.venv\Scripts\python.exe" -m pip install -r requirements.txt

# Install PyInstaller in virtual environment
Write-Host "Installing PyInstaller..." -ForegroundColor Yellow
& ".\.venv\Scripts\python.exe" -m pip install pyinstaller

# Clean previous builds
Write-Host "Cleaning previous builds..." -ForegroundColor Yellow
if (Test-Path "dist") { Remove-Item -Recurse -Force dist }
if (Test-Path "build") { Remove-Item -Recurse -Force build }

# Build executable
Write-Host "Building executable..." -ForegroundColor Yellow
& ".\.venv\Scripts\pyinstaller.exe" --onefile --windowed --name "PanoramaBridge" panoramabridge.py

# Check if build was successful
if (Test-Path "dist\PanoramaBridge.exe") {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "BUILD SUCCESSFUL!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "Executable created at: dist\PanoramaBridge.exe" -ForegroundColor Yellow
    
    $fileInfo = Get-Item "dist\PanoramaBridge.exe"
    $sizeInMB = [math]::Round($fileInfo.Length / 1MB, 2)
    Write-Host "File size: $($fileInfo.Length) bytes ($sizeInMB MB)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "You can now run: dist\PanoramaBridge.exe" -ForegroundColor Green
    Write-Host ""
} else {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Red
    Write-Host "BUILD FAILED!" -ForegroundColor Red
    Write-Host "========================================" -ForegroundColor Red
    Write-Host ""
    Write-Host "Check the output above for errors." -ForegroundColor Red
}

Write-Host ""
Read-Host "Press Enter to exit"
