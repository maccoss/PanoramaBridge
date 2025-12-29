# PanoramaBridge Testing Guide

Comprehensive testing documentation for PanoramaBridge development and quality assurance.

## Quick Start

### Running Tests

```bash
# Run all tests
python -m pytest tests/ -v

# Run with coverage report
python -m pytest tests/ --cov=panoramabridge --cov-report=html --cov-report=term-missing -v

# Run specific test file
python -m pytest tests/test_file_monitoring_robustness.py -v

# Run by keyword
python -m pytest -k "thread" -v
```

### VS Code Test Explorer

1. Click the beaker icon in the Activity Bar
2. Tests are organized by file and class
3. Click the play button next to any test to run it
4. Right-click for Debug, Run, and more options

## Test Architecture

### Testing Strategy

PanoramaBridge uses a **logic-based testing approach** that:

- **Focuses on business logic** without requiring full GUI instantiation
- **Uses mock objects** for reliable WebDAV client testing
- **Covers performance scenarios** and edge conditions
- **Validates component interactions** through integration tests

### Test Directory Structure

```
tests/
├── conftest.py                              # Shared fixtures and utilities
├── run_tests.py                             # Main test runner script
├── __init__.py                              # Package marker
│
├── # Core Functionality Tests
├── test_file_processing.py                  # File processing logic
├── test_progress_tracking.py                # Progress callback validation
├── test_upload_history_simple.py            # Upload history tracking
│
├── # UI and Integration Tests
├── test_qt_ui.py                            # Qt UI component tests
├── test_ui_integration.py                   # UI integration scenarios
├── test_queue_table_integration.py          # Queue table operations
├── test_table_ordering_logic.py             # Table ordering algorithms
├── test_table_ordering_and_progress_messages.py
│
├── # Thread Safety and Robustness
├── test_thread_safe_ui_updates.py           # Cross-thread UI safety
├── test_file_monitoring_robustness.py       # Crash prevention tests
├── test_infinite_loop_fix.py                # Infinite loop prevention
├── test_infinite_loop_simple.py             # Simple loop tests
│
├── # Verification and Integrity
├── test_multilevel_verification.py          # Multi-level verification
├── test_remote_integrity_check.py           # Remote integrity checks
│
├── # Caching and Queue Features
├── test_queue_and_cache_features.py         # Queue/cache functionality
├── test_queue_cache_logic.py                # Cache logic validation
├── test_complete_queue_cache_features.py    # Comprehensive cache tests
│
├── # Performance Tests
├── test_performance.py                      # Performance benchmarks
├── test_large_file_progress_fixes.py        # Large file handling
│
└── # Integration and Real Method Tests
    ├── test_app_integration.py              # Application integration
    ├── test_real_methods_integration.py     # Real method testing
    └── test_new_features.py                 # New feature validation
```

## Test Categories

### 1. Core Functionality Tests

**File Processing** (`test_file_processing.py`)
- File discovery and filtering
- Extension matching (case-insensitive)
- Checksum calculation
- Duplicate prevention

**Progress Tracking** (`test_progress_tracking.py`)
- Progress callback logic validation
- File iteration and WebDAV client progress
- Mock-based WebDAV operations testing
- Error handling and edge cases

**Upload History** (`test_upload_history_simple.py`)
- Persistent upload tracking
- History file management
- Skip-already-uploaded detection

### 2. UI and Integration Tests

**Qt UI Tests** (`test_qt_ui.py`)
- Widget instantiation and configuration
- Signal/slot connections
- UI state management

**Table Ordering** (`test_table_ordering_logic.py`)
- Files fill top-to-bottom (row 0, 1, 2...)
- Bottom-append insertion logic
- FIFO processing order validation
- Unique key format: `filename|hash(filepath)`

**Queue Table Integration** (`test_queue_table_integration.py`)
- File tracking and duplicate prevention
- Relative path display functionality
- Status visibility and updates
- Queue management operations

### 3. Thread Safety Tests

**Thread-Safe UI Updates** (`test_thread_safe_ui_updates.py`)
- `QMetaObject.invokeMethod` usage verification
- `QueuedConnection` for thread safety
- Error handling in UI updates
- Robustness when app_instance is None

**File Monitoring Robustness** (`test_file_monitoring_robustness.py`)
- File copy simulation without crashes
- Nonexistent file handling
- Permission error recovery
- I/O error handling
- Concurrent file operations
- Thread safety validation

### 4. Verification and Integrity Tests

**Multi-level Verification** (`test_multilevel_verification.py`)
- ETag verification (SHA256 and MD5 formats)
- Size comparison
- Accessibility checks
- Conflict detection

**Remote Integrity** (`test_remote_integrity_check.py`)
- Remote file existence verification
- Integrity verification process
- Missing file detection and re-upload

### 5. Caching and Performance Tests

**Checksum Caching** (`test_queue_cache_logic.py`)
- Cache key generation
- Cache invalidation
- Performance optimization validation

**Performance Tests** (`test_performance.py`)
- Large file handling
- Chunked upload performance
- Cache lookup speed

## Test Fixtures

The `conftest.py` file provides shared fixtures:

```python
@pytest.fixture
def temp_dir():
    """Create a temporary directory for testing."""

@pytest.fixture
def sample_file(temp_dir):
    """Create a sample test file with known content."""

@pytest.fixture
def large_sample_file(temp_dir):
    """Create a 10MB file for performance testing."""

@pytest.fixture
def mock_webdav_client():
    """Create a mock WebDAV client for testing."""

@pytest.fixture
def mock_app_instance():
    """Create a mock application instance for testing."""

@pytest.fixture
def file_queue():
    """Create a file queue for testing."""

@pytest.fixture
def sample_extensions():
    """Standard file extensions: ['raw', 'wiff', 'mzML', 'mzXML']"""
```

## Critical Issues Covered by Tests

### Application Crash Prevention

**Problem**: App crashed when copying files to monitored subfolders
**Root Cause**: Direct Qt UI calls from worker threads
**Solution**: `QMetaObject.invokeMethod` with `QueuedConnection`
**Tests**: `test_file_monitoring_robustness.py`

### Thread Safety

**Problem**: UI updates from file monitoring threads were unsafe
**Root Cause**: FileMonitorHandler calling UI methods directly
**Solution**: All UI updates use Qt's thread-safe mechanisms
**Tests**: `test_thread_safe_ui_updates.py`

### Infinite Loop Prevention

**Problem**: Certain file operations could cause infinite loops
**Solution**: Proper state tracking and termination conditions
**Tests**: `test_infinite_loop_fix.py`, `test_infinite_loop_simple.py`

## Running Specific Test Categories

```bash
# Critical stability tests
pytest tests/test_file_monitoring_robustness.py tests/test_thread_safe_ui_updates.py -v

# All robustness-related tests
pytest -k "robustness" -v

# All thread safety tests  
pytest -k "thread" -v

# All verification tests
pytest -k "verification or integrity" -v

# All caching tests
pytest -k "cache" -v
```

## Coverage Reports

```bash
# Generate HTML coverage report
python -m pytest tests/ --cov=panoramabridge --cov-report=html

# View report
open htmlcov/index.html  # macOS
xdg-open htmlcov/index.html  # Linux
start htmlcov/index.html  # Windows
```

## VS Code Configuration

### Required Extensions

- **Python** (`ms-python.python`) - Core Python support
- **Python Debugger** (`ms-python.debugpy`) - Debug support
- **Ruff** (`charliermarsh.ruff`) - Fast Python linter
- **Python Test Adapter** - Enhanced test integration

### Debug Configuration

The project includes debug configurations in `.vscode/launch.json`:

1. **Python: Debug Tests** - Debug all tests
2. **Python: Debug Current Test File** - Debug active test file

### Tasks

Available test tasks in `.vscode/tasks.json`:

- **Run All Tests** - Execute full test suite
- **Run WebDAV Tests** - WebDAV-specific tests
- **Run Tests with Coverage** - Generate coverage report

## Qt Testing Considerations

### Avoiding Qt Crashes in Tests

Qt widgets require careful handling in tests:

1. **Use mock objects** instead of real Qt widgets when possible
2. **Initialize QApplication** only once per test session
3. **Use `pytest-qt`** for proper Qt event loop handling
4. **Clean up widgets** after each test

### pytest-qt Usage

```python
def test_qt_widget(qtbot):
    """Example Qt test using pytest-qt."""
    widget = MyWidget()
    qtbot.addWidget(widget)
    
    # Interact with widget
    qtbot.mouseClick(widget.button, Qt.MouseButton.LeftButton)
    
    # Assert expected state
    assert widget.label.text() == "Clicked"
```

## Continuous Integration

Tests are automatically run on:

- Every push to `main` branch
- Every pull request to `main` branch
- Release builds

See [GitHub Actions documentation](../build_scripts/GITHUB_ACTIONS.md) for CI/CD details.

## Adding New Tests

1. Create test file in `tests/` with `test_` prefix
2. Import necessary fixtures from `conftest.py`
3. Use descriptive test names: `test_feature_scenario_expected_result`
4. Add appropriate pytest markers if needed
5. Run locally before pushing

```python
# Example test structure
import pytest
from unittest.mock import Mock

class TestNewFeature:
    """Tests for the new feature."""
    
    def test_feature_basic_usage(self, temp_dir, mock_webdav_client):
        """Test basic feature functionality."""
        # Arrange
        # ...
        
        # Act
        result = do_something()
        
        # Assert
        assert result == expected
```

## Troubleshooting

### Common Issues

**Tests not discovered**
- Ensure test files start with `test_`
- Check pytest.ini configuration
- Verify `__init__.py` exists in tests/

**Qt-related crashes**
- Use mock objects instead of real widgets
- Ensure single QApplication instance
- Check thread safety in UI tests

**Import errors**
- Activate virtual environment
- Install all requirements: `pip install -r requirements.txt -r requirements-dev.txt`

**Coverage gaps**
- Add tests for uncovered code paths
- Use `--cov-report=term-missing` to identify gaps

