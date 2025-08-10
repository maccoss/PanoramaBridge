@echo off
REM Windows batch script to build PanoramaBridge executable
REM Run this from the build_scripts directory with: build_windows.bat
REM Or from the root directory with: build_scripts\build_windows.bat

echo Building PanoramaBridge Windows Executable...
echo.

REM Change to the root directory if we're in build_scripts
if "%CD:~-13%"=="build_scripts" (
    cd ..
    echo Changed to root directory: %CD%
)

REM Check if Python is available
python --version >nul 2>&1
if errorlevel 1 (
    echo ERROR: Python not found in PATH
    echo Please install Python from https://python.org
    echo Make sure to check "Add Python to PATH" during installation
    pause
    exit /b 1
)

echo Python found: 
python --version

REM Check if we're in the right directory
if not exist "panoramabridge.py" (
    echo ERROR: panoramabridge.py not found
    echo Please run this script from the PanoramaBridge directory
    pause
    exit /b 1
)

REM Create virtual environment if it doesn't exist or has wrong structure
if not exist ".venv-win\Scripts" (
    echo Creating/Recreating virtual environment for Windows...
    if exist ".venv-win" rmdir /s /q .venv-win
    python -m venv .venv-win
)

REM Activate virtual environment
echo Activating virtual environment...
REM Try to activate, but continue even if it fails
call .venv-win\Scripts\activate.bat 2>nul || echo Virtual environment activation may have failed, continuing...

REM Upgrade pip in virtual environment
echo Updating pip...
.venv-win\Scripts\python.exe -m pip install --upgrade pip

REM Install requirements in virtual environment
echo Installing requirements...
.venv-win\Scripts\python.exe -m pip install -r requirements.txt

REM Install PyInstaller in virtual environment
echo Installing PyInstaller...
.venv-win\Scripts\python.exe -m pip install pyinstaller

REM Clean previous builds
echo Cleaning previous builds...
if exist "dist" rmdir /s /q dist
if exist "build" rmdir /s /q build

REM Build executable using spec file
echo Building executable using spec file...
.venv-win\Scripts\pyinstaller.exe "build_scripts\PanoramaBridge.spec"

REM Check if build was successful
if exist "dist\PanoramaBridge.exe" (
    echo.
    echo ========================================
    echo BUILD SUCCESSFUL!
    echo ========================================
    echo.
    echo Executable created at: dist\PanoramaBridge.exe
    echo File size: 
    dir dist\PanoramaBridge.exe | findstr PanoramaBridge.exe
    echo.
    echo You can now run: dist\PanoramaBridge.exe
    echo.
) else (
    echo.
    echo ========================================
    echo BUILD FAILED!
    echo ========================================
    echo.
    echo Check the output above for errors.
)

echo.
echo Press any key to exit...
pause >nul
