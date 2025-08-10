#!/usr/bin/env python3
"""
Test runner for PanoramaBridge test suite.

This script runs the core test suites:
1. Progress tracking tests (original functionality)
2. Queue table integration and persistent checksum caching tests (new features)

Usage:
    python3 tests/run_tests.py

    Or from project root:
    python3 -m pytest tests/test_progress_tracking.py tests/test_complete_queue_cache_features.py -v
"""

import os
import subprocess
import sys
from pathlib import Path

# pytest.ini equivalent configuration
pytest_plugins = []


# Test configuration
def pytest_configure(config):
    """Configure pytest."""
    config.addinivalue_line(
        "markers", "slow: marks tests as slow (deselect with '-m \"not slow\"')"
    )


# Coverage configuration (if pytest-cov is installed)
COVERAGE_CONFIG = [
    "--cov=panoramabridge",
    "--cov-report=html:htmlcov",
    "--cov-report=term-missing",
    "--cov-fail-under=80",
]


def run_tests():
    """Run the main test suites"""
    project_root = Path(__file__).parent.parent
    os.chdir(project_root)

    # Core test files to run
    test_files = [
        "tests/test_progress_tracking.py",  # Original progress tracking functionality
        "tests/test_complete_queue_cache_features.py",  # New queue table and cache features
    ]

    print("=" * 80)
    print("PanoramaBridge Test Suite")
    print("=" * 80)
    print("Running core functionality tests...")
    print()

    # Run the tests with verbose output
    cmd = [sys.executable, "-m", "pytest"] + test_files + ["-v", "--tb=short"]

    try:
        subprocess.run(cmd, check=True, capture_output=False)
        print("\n" + "=" * 80)
        print("✅ All tests passed successfully!")
        print("Core functionality and new features are working correctly.")
        print("=" * 80)
        return 0

    except subprocess.CalledProcessError as e:
        print("\n" + "=" * 80)
        print("❌ Some tests failed!")
        print("=" * 80)
        return e.returncode


if __name__ == "__main__":
    # Ensure the main module is in the Python path
    sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    sys.exit(run_tests())

    # Basic pytest run
    cmd = [sys.executable, "-m", "pytest", "-v"]

    # Add coverage if available
    try:
        import pytest_cov  # noqa: F401

        cmd.extend(["--cov=panoramabridge", "--cov-report=term-missing"])
    except ImportError:
        print("pytest-cov not available, running without coverage")

    # Add test discovery
    cmd.append("tests/")

    print(f"Running: {' '.join(cmd)}")
    result = subprocess.run(cmd)
    sys.exit(result.returncode)
