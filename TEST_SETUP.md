# VS Code Python Test Explorer Setup

This document explains how to use the pytest framework with VS Code's Python Test Explorer.

## ✅ Setup Complete!

The project is already configured for VS Code Test Explorer integration. All configuration files have been created:

- `.vscode/settings.json` - Test configuration
- `.vscode/launch.json` - Debug configurations  
- `.vscode/tasks.json` - Test running tasks
- `.vscode/extensions.json` - Recommended extensions
- `pytest.ini` - Pytest configuration
- `tests/conftest.py` - Test fixtures and utilities

## 🚀 Getting Started

### 1. Install Recommended Extensions

When you open this project in VS Code, you'll be prompted to install recommended extensions:

- **Python** - Core Python support
- **Python Debugger** - Debug support
- **Ruff** - Fast Python linter
- **Python Test Adapter** - Enhanced test integration

### 2. Open Test Explorer

- Press `Ctrl+Shift+P` (or `Cmd+Shift+P` on Mac)
- Type "Test: Focus on Test Explorer View"
- Or click the beaker icon in the Activity Bar

### 3. Running Tests

**From Test Explorer:**
- Click the ▶️ button next to any test to run it
- Right-click for more options (Debug, Run, etc.)
- Tests are organized by file and class

**From Command Palette:**
- `Ctrl+Shift+P` → "Test: Run All Tests"
- `Ctrl+Shift+P` → "Test: Debug All Tests"

**From Tasks:**
- `Ctrl+Shift+P` → "Tasks: Run Task"
- Select "Run All Tests", "Run WebDAV Tests", or "Run Tests with Coverage"

### 4. Debugging Tests

**Option 1: Test Explorer**
- Right-click on any test → "Debug Test"

**Option 2: Debug Panel**  
- Go to Run & Debug panel (Ctrl+Shift+D)
- Select "Debug Current Test" or "Debug All Tests" configuration
- Press F5 to start debugging

**Option 3: Inline Debugging**
- Set breakpoints in test files
- Right-click in editor → "Debug Test"

## 📊 Test Categories

### ✅ Working Tests (Ready to Use)
- **WebDAV Client Tests** (12 tests) - All passing ✓
  - Connection testing
  - File operations  
  - Directory operations
  - Checksum storage/retrieval
  - Error handling

### 🔧 Framework Tests (Need API Alignment)
- **File Processing Tests** (23 tests)
  - File monitoring
  - Checksum caching
  - Locked file handling
- **Performance Tests** (11 tests)  
  - Cache performance
  - Memory management
  - Concurrency testing

### 🚫 Skipped Tests (Safely Handled)
- **App Integration Tests** (11 tests) - Skipped to avoid PyQt6 crashes

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
- Terminal shows coverage summary
- Open `htmlcov/index.html` in browser for detailed view

## ⚙️ Configuration Details

### Python Interpreter
- Configured to use `.venv/bin/python` automatically
- Virtual environment activated in terminal

### Test Discovery  
- Automatically scans `tests/` directory
- Discovers tests on save
- Uses pytest for execution

### File Associations
- `pytest.ini` recognized as INI file
- `conftest.py` recognized as Python file

## 🎯 Quick Actions

| Action | Shortcut/Method |
|--------|----------------|
| Run all tests | Tasks → "Run All Tests" |
| Run current test | Right-click test in explorer |
| Debug test | Right-click → "Debug Test" |  
| View coverage | Tasks → "Run Tests with Coverage" |
| Refresh tests | Test Explorer refresh button |

The test framework is ready to use with comprehensive WebDAV testing and a structure for extending to other components! 🎉
