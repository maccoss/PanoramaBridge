"""
Pytest configuration and test runner script for PanoramaBridge.
"""

# pytest.ini equivalent configuration
pytest_plugins = []

# Test configuration
def pytest_configure(config):
    """Configure pytest."""
    config.addinivalue_line(
        "markers",
        "slow: marks tests as slow (deselect with '-m \"not slow\"')"
    )

# Coverage configuration (if pytest-cov is installed)
COVERAGE_CONFIG = {
    '--cov': 'panoramabridge',
    '--cov-report': 'html:htmlcov',
    '--cov-report': 'term-missing',
    '--cov-fail-under': '80'
}

if __name__ == "__main__":
    import sys
    import os
    import subprocess
    
    # Ensure the main module is in the Python path
    sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    
    # Basic pytest run
    cmd = [sys.executable, "-m", "pytest", "-v"]
    
    # Add coverage if available
    try:
        import pytest_cov
        cmd.extend(["--cov=panoramabridge", "--cov-report=term-missing"])
    except ImportError:
        print("pytest-cov not available, running without coverage")
    
    # Add test discovery
    cmd.append("tests/")
    
    print(f"Running: {' '.join(cmd)}")
    result = subprocess.run(cmd)
    sys.exit(result.returncode)
