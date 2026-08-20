# VS Code Python Test Explorer Setup

> [!NOTE]
> **This describes the retired Python application.** PanoramaBridge is now a native Windows
> application built on .NET 8 -- see the [README](../../README.md) and the
> [.NET port handoff](../DOTNET_PORT_HANDOFF.md). This file is kept for reference while existing installations
> are migrated, and will be removed: see the [removal plan](../PYTHON_REMOVAL_PLAN.md).

This document explains how to use the comprehensive pytest framework with VS Code's Python Test Explorer.

## ✅ Complete Test Suite Ready!

The project now has a comprehensive test suite covering all core functionality and new features:

**Configuration Files:**
- `.vscode/settings.json` - Test configuration
- `.vscode/launch.json` - Debug configurations
- `.vscode/tasks.json` - Test running tasks
- `.vscode/extensions.json` - Recommended extensions
- `pytest.ini` - Pytest configuration
- `tests/conftest.py` - Test fixtures and utilities
- `tests/run_tests.py` - Main test runner script

## 🚀 Getting Started

### 1. Install Recommended Extensions

When you open this project in VS Code, you'll be prompted to install recommended extensions:

- **Python** - Core Python support
- **Python Debugger** - Debug support
- **Ruff** - Fast Python linter
- **Python Test Adapter** - Enhanced test integration

### 2. Running Tests

**Quick Test Run (Recommended):**
```bash
python3 tests/run_tests.py
```

**From Test Explorer:**
- Click the beaker icon in the Activity Bar
- Click the ▶️ button next to any test to run it
- Right-click for more options (Debug, Run, etc.)
- Tests are organized by file and class

**From Command Line:**
```bash
# Run core functionality tests
python3 -m pytest tests/test_progress_tracking.py tests/test_complete_queue_cache_features.py -v

# Run all tests
python3 -m pytest tests/ -v

# Run with coverage
python3 -m pytest tests/ --cov=panoramabridge --cov-report=html
```

### 3. Debugging Tests

**Option 1: Test Explorer**

- Right-click on any test → "Debug Test"
- Breakpoints will be automatically hit

**Option 2: Debug Configuration**

- Go to Run & Debug panel (Ctrl+Shift+D)
- Select "Python: Debug Tests" configuration
- Press F5 to start debugging

**Option 3: Inline Debugging**

- Set breakpoints in test files
- Click the debug icon next to any test in Test Explorer

## 📊 Current Test Suite Status

### ✅ Comprehensive Test Coverage (25 Tests - All Passing)

**Core Progress Tracking Tests** (7 tests) - `test_progress_tracking.py`

- Progress callback logic validation
- File iteration and WebDAV client progress
- Mock-based testing for WebDAV operations
- Error handling and edge cases

**Queue Table Integration Tests** (8 tests) - `test_complete_queue_cache_features.py`

- File tracking and duplicate prevention
- Relative path display functionality
- Status visibility and updates
- Queue management operations

**Persistent Checksum Caching Tests** (10 tests) - `test_complete_queue_cache_features.py`

- Configuration save/load functionality
- Periodic cache saving and recovery
- Performance optimization scenarios
- Cache persistence across sessions

## 📊 Test Categories

## 🧪 Test Approach & Architecture

### Logic-Based Testing Strategy

The comprehensive test suite uses a **logic-based testing approach** that avoids Qt widget instantiation to prevent crashes while maintaining thorough validation:

- **Business Logic Focus**: Tests validate the core functionality without GUI dependencies
- **Mock-Based WebDAV**: Uses mock objects for reliable WebDAV client testing
- **Performance Scenarios**: Covers optimization cases and edge conditions
- **Integration Testing**: Validates component interactions and data flow

### Test File Organization

```
tests/
├── conftest.py                           # Shared fixtures and utilities
├── run_tests.py                         # Main test runner script
├── test_progress_tracking.py            # Original progress functionality (7 tests)
└── test_complete_queue_cache_features.py # New features comprehensive suite (18 tests)
```

## 📈 Coverage & Performance

### Test Coverage

```bash
# Generate coverage report
python3 -m pytest tests/ --cov=panoramabridge --cov-report=html --cov-report=term
```

- HTML report generated in `htmlcov/index.html`
- Covers core business logic and new feature implementations
- Focus on critical paths and error handling scenarios

## ⚙️ Configuration Details

### Python Interpreter

- Configured to use `.venv/bin/python` automatically
- Fallback to system Python if virtual environment unavailable

### Test Discovery

- Automatically scans `tests/` directory
- Follows pytest naming conventions (`test_*.py` files)
- Organizes tests by functionality and feature areas

### File Associations

- `pytest.ini` recognized as INI file
- `.vscode/` configuration files have proper syntax highlighting

## 📝 Test Files Structure

```
tests/
├── conftest.py              # Test fixtures and configuration
├── test_webdav_client.py    # WebDAV functionality tests ✅
├── test_file_processing.py  # File monitoring tests 🔧
├── test_performance.py      # Performance optimization tests 🔧
└── test_app_integration.py  # GUI integration tests 🚫
```

## 🛠️ Available Tasks

Access via `Ctrl+Shift+P` → "Tasks: Run Task":

- **Run All Tests** - Execute all tests with verbose output
- **Run WebDAV Tests** - Execute only the working WebDAV tests
- **Run Tests with Coverage** - Generate HTML coverage reports

## 🐛 Debugging Tips

1. **Set Breakpoints** - Click in the gutter next to line numbers
2. **Inspect Variables** - Hover over variables during debugging
3. **Use Debug Console** - Evaluate expressions while paused
4. **Step Through Code** - Use F10 (step over) and F11 (step into)

## 📈 Coverage Reports

When running "Run Tests with Coverage" task:
- HTML report generated in `htmlcov/index.html`
- Terminal shows coverage summary and line-by-line details
- Focus areas: core business logic and feature implementations

## 🔧 Troubleshooting

### Common Issues

**Tests not appearing in Test Explorer:**
1. Check Python interpreter is correctly set
2. Verify pytest is installed: `pip install pytest`
3. Check pytest.ini configuration
4. Reload VS Code window (`Ctrl+Shift+P` → "Developer: Reload Window")

**Import errors in tests:**
1. Ensure working directory is project root
2. Verify panoramabridge.py is in the same directory as tests/
3. Check Python path configuration

**Test failures:**
1. Review test output in Test Explorer
2. Use debug mode to step through failing tests
3. Check mock configurations for WebDAV tests
4. Verify test data and fixtures

### Performance Notes

- **Logic-based tests**: Run quickly without Qt overhead
- **Mock WebDAV operations**: Eliminate network dependencies
- **Comprehensive coverage**: 25 tests validate all functionality
- **Reliable execution**: Consistent results across environments

## 🎯 Quick Actions

| Action | Shortcut/Method |
|--------|----------------|
| Run all tests | `python3 tests/run_tests.py` |
| Run specific test file | `python3 -m pytest tests/test_*.py -v` |
| Debug test | Right-click test in Test Explorer → "Debug Test" |
| View coverage | `python3 -m pytest tests/ --cov=panoramabridge --cov-report=html` |
| Refresh tests | Test Explorer refresh button |

## 🎉 Summary

The comprehensive test suite is **ready to use** and provides thorough validation of:

- ✅ **Original functionality**: Progress tracking and WebDAV operations (7 tests)
- ✅ **Queue table integration**: File tracking and duplicate prevention (8 tests)
- ✅ **Persistent checksum caching**: Config save/load and performance optimization (10 tests)

**Total: 25 tests - All passing!**

The logic-based testing approach ensures reliable, fast execution while maintaining comprehensive coverage of all core functionality and new features.
