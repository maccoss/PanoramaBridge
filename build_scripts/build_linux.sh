#!/bin/bash
# Build script for PanoramaBridge Linux executable
# Run this from the build_scripts directory with: ./build_linux.sh
# Or from the root directory with: ./build_scripts/build_linux.sh

set -e  # Exit on any error

echo "========================================"
echo "Building PanoramaBridge Linux Executable"
echo "========================================"
echo ""

# Change to the root directory if we're in build_scripts
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ "$SCRIPT_DIR" == *"build_scripts" ]]; then
    cd "$SCRIPT_DIR/.."
    echo "Changed to root directory: $(pwd)"
fi

# Check if we're in the right directory
if [[ ! -f "panoramabridge.py" ]]; then
    echo "ERROR: panoramabridge.py not found"
    echo "Please run this script from the PanoramaBridge directory"
    exit 1
fi

# Check Python version
echo "Checking Python..."
PYTHON_VERSION=$(python3 --version 2>&1)
echo "Python found: $PYTHON_VERSION"

# Create virtual environment if it doesn't exist
if [[ ! -d ".venv" ]]; then
    echo "Creating virtual environment..."
    python3 -m venv .venv
fi

# Activate virtual environment
echo "Activating virtual environment..."
source .venv/bin/activate

# Upgrade pip
echo "Updating pip..."
pip install --upgrade pip

# Install requirements
echo "Installing requirements..."
pip install -r requirements.txt

# Install PyInstaller
echo "Installing PyInstaller..."
pip install pyinstaller

# Clean previous builds
echo "Cleaning previous builds..."
rm -rf dist/PanoramaBridge 2>/dev/null || true
rm -rf build/PanoramaBridge 2>/dev/null || true

# Build executable using Linux spec file
echo "Building Linux executable..."
pyinstaller build_scripts/PanoramaBridge-linux.spec

# Check if build was successful
if [[ -f "dist/PanoramaBridge" ]]; then
    echo ""
    echo "========================================"
    echo "BUILD SUCCESSFUL!"
    echo "========================================"
    echo ""
    echo "Executable created at: dist/PanoramaBridge"
    
    FILE_SIZE=$(stat --printf="%s" dist/PanoramaBridge 2>/dev/null || stat -f%z dist/PanoramaBridge 2>/dev/null)
    SIZE_MB=$(echo "scale=2; $FILE_SIZE / 1048576" | bc)
    echo "File size: $FILE_SIZE bytes ($SIZE_MB MB)"
    
    echo ""
    echo "You can now run: ./dist/PanoramaBridge"
    echo ""
else
    echo ""
    echo "========================================"
    echo "BUILD FAILED!"
    echo "========================================"
    echo ""
    echo "Check the output above for errors."
    echo "Common issues:"
    echo "- Missing system Qt libraries (install python3-pyqt6)"
    echo "- Missing development tools"
    exit 1
fi
