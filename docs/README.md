# PanoramaBridge Documentation

Technical documentation and implementation guides for PanoramaBridge.

## Core Documentation

### System Architecture

- **[File Monitoring](FILE_MONITORING.md)** - File detection, OS events, locked file handling, and Windows optimization
- **[Verification System](VERIFICATION_SYSTEM.md)** - Upload verification, ETag support, integrity checks, and conflict resolution
- **[Caching System](CACHING_SYSTEM.md)** - Checksum caching, queue management, and performance optimization

### Development

- **[Testing Guide](TESTING.md)** - Comprehensive test suite documentation, running tests, and Qt testing approaches
- **[Release Process](RELEASE_PROCESS.md)** - Version management and release procedures

### Build & Deployment

- **[Multi-Architecture Build](MULTI_ARCHITECTURE_BUILD.md)** - Building for x64 and ARM64 architectures

## Quick Links

| Topic | Document |
|-------|----------|
| Running tests | [TESTING.md](TESTING.md#quick-start) |
| File monitoring setup | [FILE_MONITORING.md](FILE_MONITORING.md#configuration) |
| Upload verification | [VERIFICATION_SYSTEM.md](VERIFICATION_SYSTEM.md#verification-hierarchy) |
| Performance tuning | [CACHING_SYSTEM.md](CACHING_SYSTEM.md#performance-improvements) |
| Windows builds | [../build_scripts/BUILD_WINDOWS.md](../build_scripts/BUILD_WINDOWS.md) |
| GitHub Actions | [../build_scripts/GITHUB_ACTIONS.md](../build_scripts/GITHUB_ACTIONS.md) |

## Archived Documentation

The following files contain historical implementation details and have been consolidated into the core documentation above:

<details>
<summary>Click to expand archived files</summary>

### Verification & Integrity (consolidated into VERIFICATION_SYSTEM.md)

- `ENHANCED_ETAG_VERIFICATION_SUMMARY.md` - ETag verification implementation history
- `REMOTE_INTEGRITY_CHECK_IMPLEMENTATION.md` - Remote integrity check implementation details
- `INTEGRITY_CHECK_IMPROVEMENTS_2025.md` - 2025 integrity improvements
- `UPLOAD_VERIFICATION_IMPROVEMENTS.md` - Upload verification enhancements

### File Monitoring (consolidated into FILE_MONITORING.md)

- `FILE_MONITORING_OPTIMIZATION.md` - Monitoring optimization details
- `FILE_MONITORING_ROBUSTNESS_IMPROVEMENTS.md` - Robustness improvements

### Caching (consolidated into CACHING_SYSTEM.md)

- `CHECKSUM_CACHING_SUMMARY.md` - Checksum caching implementation
- `QUEUE_CACHE_IMPLEMENTATION_SUMMARY.md` - Queue and cache features

### Testing (consolidated into TESTING.md)

- `TEST_SETUP.md` - Original test setup guide
- `TEST_SUITE_SUMMARY.md` - Test suite overview
- `QT_TESTING_GUIDE.md` - Qt-specific testing
- `UPLOAD_HISTORY_TESTS_FIXED.md` - Upload history test fixes

### Maintenance

- `LINTING_FIXES_SUMMARY.md` - Code quality improvements (August 2025)

</details>

## Navigation

- **[Main README](../README.md)** - Installation and usage instructions
- **[Build Scripts](../build_scripts/README.md)** - Windows executable builds
- **[Demo Scripts](../demo_scripts/README.md)** - Example scripts and diagnostics
- **[AI Agents Guide](../AGENTS.md)** - Guide for AI-assisted development

