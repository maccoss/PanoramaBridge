#!/bin/bash
# Test script for PanoramaBridge development
# Usage: ./run_tests.sh [options]

set -e  # Exit on any error

echo "🧪 PanoramaBridge Test Runner"
echo "=============================="

# Check if we're in a virtual environment
if [[ -z "${VIRTUAL_ENV}" ]]; then
    echo "⚠️  Warning: Not in a virtual environment"
    echo "   Consider running: source .venv/bin/activate"
    echo ""
fi

# Parse command line arguments
RUN_ALL=false
RUN_STABLE_ONLY=true
VERBOSE=false
COVERAGE=false

for arg in "$@"; do
    case $arg in
        --all|-a)
            RUN_ALL=true
            RUN_STABLE_ONLY=false
            ;;
        --stable|-s)
            RUN_STABLE_ONLY=true
            RUN_ALL=false
            ;;
        --verbose|-v)
            VERBOSE=true
            ;;
        --coverage|-c)
            COVERAGE=true
            ;;
        --help|-h)
            echo "Options:"
            echo "  --all, -a        Run all tests (including potentially unstable ones)"
            echo "  --stable, -s     Run only stable tests (default)"
            echo "  --verbose, -v    Verbose output"
            echo "  --coverage, -c   Generate coverage report"
            echo "  --help, -h       Show this help"
            exit 0
            ;;
    esac
done

# Build pytest command
PYTEST_CMD="python -m pytest"

if [[ "$VERBOSE" == true ]]; then
    PYTEST_CMD="$PYTEST_CMD -v"
fi

if [[ "$COVERAGE" == true ]]; then
    PYTEST_CMD="$PYTEST_CMD --cov=panoramabridge --cov-report=term-missing --cov-report=html"
fi

# Run tests based on selection
if [[ "$RUN_STABLE_ONLY" == true ]]; then
    echo "🔧 Running stable tests only..."
    echo "   Tests: test_upload_history_simple.py, test_qt_ui.py"
    echo ""
    $PYTEST_CMD tests/test_upload_history_simple.py tests/test_qt_ui.py --tb=short
    
elif [[ "$RUN_ALL" == true ]]; then
    echo "🚀 Running all tests (may include experimental/unstable ones)..."
    echo "   Note: Some tests may be skipped or fail due to Qt initialization issues"
    echo ""
    $PYTEST_CMD tests/ --tb=short
fi

echo ""
echo "✅ Test run completed!"

if [[ "$COVERAGE" == true ]]; then
    echo "📊 Coverage report generated in htmlcov/index.html"
fi

echo ""
echo "💡 Tip: Use './run_tests.sh --help' to see all options"
