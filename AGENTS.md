# AI Agents Guide for PanoramaBridge

This document provides guidance for AI agents (GitHub Copilot, Claude, etc.) working on the PanoramaBridge project.

## Project Overview

**PanoramaBridge** is a Python Qt6 desktop application for monitoring local directories and automatically transferring files to Panorama WebDAV servers. It's designed for mass spectrometry laboratory workflows.

### Key Technologies

| Technology | Purpose |
|------------|---------|
| Python 3.12+ | Core language |
| PyQt6 | GUI framework |
| watchdog | File system monitoring |
| requests | WebDAV HTTP communication |
| keyring | Secure credential storage |
| pytest | Testing framework |
| pytest-qt | Qt widget testing |
| PyInstaller | Windows executable creation |
| Ruff | Linting and formatting |

### Architecture

```
panoramabridge.py          # Main application (5000+ lines, single file)
├── WebDAVClient           # WebDAV server communication
├── FileProcessor          # File processing and checksums
├── FileMonitorHandler     # File system event handling
├── TransferThread         # Background upload worker
└── PanoramaBridgeApp      # Main Qt application window
```

## Development Environment Setup

### 1. Clone and Create Virtual Environment

```bash
git clone https://github.com/maccoss/PanoramaBridge.git
cd PanoramaBridge

# Linux/macOS
python -m venv .venv
source .venv/bin/activate

# Windows (for native performance)
python -m venv .venv-win
.venv-win\Scripts\activate
```

### 2. Install Dependencies

```bash
# Core dependencies
pip install -r requirements.txt

# Development dependencies (testing, linting)
pip install -r requirements-dev.txt
```

### 3. Verify Installation

```bash
# Run the application
python panoramabridge.py

# Run tests
python -m pytest tests/ -v
```

## Testing

### Running Tests

```bash
# Run all tests
python -m pytest tests/ -v

# Run with coverage
python -m pytest tests/ --cov=panoramabridge --cov-report=html --cov-report=term-missing -v

# Run specific test file
python -m pytest tests/test_file_monitoring_robustness.py -v

# Run by keyword
python -m pytest -k "thread" -v

# Run critical stability tests
python -m pytest tests/test_file_monitoring_robustness.py tests/test_thread_safe_ui_updates.py -v
```

### VS Code Tasks

```bash
# Using tasks.json
# Run All Tests - default test task
# Run WebDAV Tests - WebDAV-specific
# Run Tests with Coverage - generates HTML report
```

### Test Categories

| Category | Test Files | Purpose |
|----------|------------|---------|
| Core functionality | `test_file_processing.py`, `test_progress_tracking.py` | Basic operations |
| Thread safety | `test_thread_safe_ui_updates.py`, `test_file_monitoring_robustness.py` | Crash prevention |
| UI integration | `test_qt_ui.py`, `test_queue_table_integration.py` | GUI components |
| Verification | `test_multilevel_verification.py`, `test_remote_integrity_check.py` | Upload integrity |
| Caching | `test_queue_cache_logic.py`, `test_complete_queue_cache_features.py` | Performance |

### Writing Tests

```python
# Test file structure
import pytest
from unittest.mock import Mock

class TestNewFeature:
    """Tests for new feature."""
    
    def test_basic_usage(self, temp_dir, mock_webdav_client):
        """Test basic functionality."""
        # Arrange
        # ...
        
        # Act
        result = do_something()
        
        # Assert
        assert result == expected
```

### Qt Testing Notes

- Use `pytest-qt` for Qt widget tests
- Use mock objects to avoid full GUI instantiation when possible
- Be careful with thread safety - use `QMetaObject.invokeMethod` for cross-thread updates

## Building

### Local Development Build

```bash
# Just run directly
python panoramabridge.py
```

### Windows Executable

```bash
# Install PyInstaller
pip install pyinstaller

# Build using spec file
pyinstaller build_scripts/PanoramaBridge.spec

# Or build scripts
# PowerShell:
./build_scripts/build_windows.ps1

# Command Prompt:
build_scripts/build_windows.bat
```

Output: `dist/PanoramaBridge.exe`

### ARM64 Build

```bash
# Must run on ARM64 hardware
./build_scripts/build_windows_arm64.ps1
```

Output: `dist/PanoramaBridge-arm64.exe`

## GitHub Actions CI/CD

### Workflows

| Workflow | File | Triggers | Purpose |
|----------|------|----------|---------|
| Build Windows | `.github/workflows/build-windows.yml` | Push to main, PRs | CI builds |
| Release Builder | `.github/workflows/release.yml` | Manual, tags | Production releases |
| Publish to PyPI | `.github/workflows/publish-pypi.yml` | Release published, manual | PyPI distribution |

### Creating a Release

#### Method 1: Git Tags (Recommended)

```bash
git tag v1.0.0
git push origin v1.0.0
```

#### Method 2: Manual Release

1. Go to GitHub → Actions → Release Builder
2. Click "Run workflow"
3. Fill in:
   - Create release: `true`
   - Release tag: `v1.0.0`
   - Release name: `PanoramaBridge v1.0.0`
4. Click "Run workflow"

### Downloading Artifacts

- **Development builds**: Actions → workflow run → Artifacts section
- **Releases**: Releases page → download assets

### PyPI Publishing

The package is automatically published to PyPI when a GitHub release is created.

**Automatic Publishing (on release):**

1. Create a new release on GitHub (manually or via Release Builder workflow)
2. The `publish-pypi.yml` workflow triggers automatically
3. Package is built and uploaded to PyPI

**Manual Publishing to TestPyPI:**

1. Go to GitHub → Actions → Publish to PyPI
2. Click "Run workflow"
3. Set "Publish to TestPyPI" to `true`
4. Click "Run workflow"

**Required GitHub Secrets/Environments:**

- Create environments named `pypi` and `testpypi` in repository settings
- Configure trusted publishing on PyPI (recommended) or use API tokens

## Code Quality

### No Emojis Policy

**Do not use emojis in any code, documentation, or output.** This includes:

- Source code (log messages, error messages, status updates)
- Documentation files (README, AGENTS.md, all docs/)
- Test output and print statements
- Build scripts and shell scripts
- Comments and docstrings

Use plain text alternatives:
- Instead of checkmark emojis, use `[OK]`, `PASS`, or `Verified`
- Instead of X/cross emojis, use `[FAIL]`, `Error:`, or `Failed`
- Instead of warning emojis, use `Warning:` or `[WARN]`
- Instead of icon emojis, use descriptive text

### Linting

```bash
# Check with Ruff
ruff check .

# Auto-fix issues
ruff check --fix .

# Format code
ruff format .
```

### Configuration

Linting is configured in `pyproject.toml`:

- Line length: 100 characters
- Python version: 3.12
- Qt naming conventions allowed (camelCase)
- Test-specific relaxations

## Key Files Reference

### Core Application

| File | Purpose |
|------|---------|
| `panoramabridge.py` | Main application code |
| `requirements.txt` | Core dependencies |
| `requirements-dev.txt` | Development dependencies |
| `pyproject.toml` | Project config, linting rules |
| `pytest.ini` | Pytest configuration |

### Build System

| File | Purpose |
|------|---------|
| `build_scripts/PanoramaBridge.spec` | PyInstaller x64 spec |
| `build_scripts/PanoramaBridge-arm64.spec` | PyInstaller ARM64 spec |
| `build_scripts/build_windows.ps1` | PowerShell build script |
| `build_scripts/build_windows.bat` | Batch build script |

### GitHub Actions

| File | Purpose |
|------|---------|
| `.github/workflows/build-windows.yml` | CI build workflow |
| `.github/workflows/release.yml` | Release workflow |

### Documentation

| File | Purpose |
|------|---------|
| `README.md` | Main user documentation |
| `docs/README.md` | Technical docs index |
| `docs/TESTING.md` | Test suite guide |
| `docs/FILE_MONITORING.md` | File monitoring system |
| `docs/VERIFICATION_SYSTEM.md` | Upload verification |
| `docs/CACHING_SYSTEM.md` | Performance caching |

## Common Tasks

### Adding a New Feature

1. Understand existing code in `panoramabridge.py`
2. Implement feature with appropriate error handling
3. Add tests in `tests/` directory
4. Run full test suite
5. Update documentation if needed
6. Check linting passes

### Fixing a Bug

1. Write a failing test that demonstrates the bug
2. Fix the bug in `panoramabridge.py`
3. Verify test passes
4. Run full test suite to check for regressions
5. Update docs if behavior changed

### Updating Dependencies

1. Update version in `requirements.txt` or `requirements-dev.txt`
2. Test locally
3. Verify CI builds still work
4. Update documentation if needed

## Important Patterns

### Thread Safety

Always use Qt's thread-safe mechanisms for UI updates from background threads:

```python
# Correct - safe cross-thread update
QMetaObject.invokeMethod(
    self.app_instance,
    "update_status",
    Qt.ConnectionType.QueuedConnection,
    Q_ARG(str, "Status message")
)

# Incorrect - can cause crashes
self.app_instance.update_status("Status message")  # Don't do this from worker thread
```

### File Path Handling

Use Path objects and handle cross-platform paths:

```python
from pathlib import Path

config_dir = Path.home() / '.panoramabridge'
config_file = config_dir / 'config.json'
```

### Error Handling

Log errors and provide user-friendly messages:

```python
try:
    result = risky_operation()
except PermissionError:
    logger.error(f"Permission denied: {filepath}")
    self.show_error("Cannot access file - it may be locked by another program")
except Exception as e:
    logger.exception(f"Unexpected error: {e}")
    self.show_error(f"An error occurred: {str(e)}")
```

### Checksum Caching

Always use the caching system for checksums:

```python
# Correct - uses cache
checksum = self.file_processor.calculate_checksum(filepath)

# The method internally handles caching:
# - Returns cached value if file unchanged
# - Calculates and caches if file new/modified
```

## Troubleshooting Agent Issues

### Tests Failing

1. Ensure virtual environment is activated
2. Install all dependencies: `pip install -r requirements.txt -r requirements-dev.txt`
3. Check for Qt-related issues (may need display on Linux)
4. Run specific test with `-v` for details

### Build Failing

1. Check PyInstaller is installed
2. Verify all dependencies are available
3. Check spec file matches current code
4. Review build output for missing modules

### Import Errors

1. Verify virtual environment is activated
2. Check all requirements installed
3. For PyQt6 issues, may need system Qt libraries

## PyPI Distribution

See the [README.md](README.md#installation) for pip installation instructions once the package is published to PyPI.

The package will be installable via:

```bash
pip install panoramabridge
```

## Contact

- **Author**: Michael MacCoss - MacCoss Lab, University of Washington
- **Repository**: https://github.com/maccoss/PanoramaBridge
- **License**: Apache License 2.0

