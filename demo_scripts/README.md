# Demo Scripts and Diagnostics

This directory contains demonstration scripts, examples, and diagnostic tools for PanoramaBridge development, testing, and troubleshooting.

## Scripts Overview

### Verification and Integrity

| Script | Purpose |
|--------|---------|
| `demo_accessibility_assessment.py` | Demonstrates accessibility verification checks |
| `demo_integrity_verification_fix.py` | Shows integrity verification system in action |
| `demo_verification_messages.py` | Examples of upload verification messages |

### Diagnostics and Testing

| Script | Purpose |
|--------|---------|
| `diagnostic_infinite_loop_test.py` | Tests infinite loop prevention logic |
| `verify_test_setup.py` | Verifies test environment is configured correctly |

## Usage

These scripts are for development and testing purposes. Run directly with Python:

```bash
# Activate virtual environment first
source .venv/bin/activate  # Linux/macOS
.venv\Scripts\activate     # Windows

# Run a demo script
python demo_scripts/demo_verification_messages.py
```

## When to Use These Scripts

- **Development**: Testing new features before integration
- **Debugging**: Diagnosing issues with verification
- **Learning**: Understanding how specific features work
- **Testing**: Validating behavior in specific scenarios

## Related Documentation

- [Main README](../README.md) - Installation and usage
- [Testing Guide](../docs/TESTING.md) - Running the test suite
- [Verification System](../docs/VERIFICATION_SYSTEM.md) - How verification works