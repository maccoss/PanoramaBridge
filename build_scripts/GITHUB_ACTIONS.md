# GitHub Actions CI/CD Setup

This document explains the automated build system for PanoramaBridge using GitHub Actions.

## Overview

Two workflows automate building Windows executables:
- **`build-windows.yml`** - Continuous Integration (builds on every push)
- **`release.yml`** - Release Builder (creates tagged releases)

## Workflows

### Continuous Integration (`build-windows.yml`)
**Triggers:**
- Every push to `main` branch
- Every pull request to `main` branch
- Manual trigger from GitHub UI
- New releases

**Actions:**
- Builds Windows executable on every code change
- Runs on Windows runner with Python 3.12
- Uploads executable as artifact for 30 days
- Automatically attaches executable to GitHub releases

### Release Builder (`release.yml`)
**Triggers:**
- Git tags starting with `v` (e.g., `v1.0.0`)
- Manual trigger with release options

**Actions:**
- Builds production-ready Windows executable
- Runs tests before building
- Creates detailed build information
- Uploads artifacts for 90 days
- Creates GitHub releases with download files
- Includes SHA256 checksums for security

## Creating Releases

### Method 1: Git Tags (Recommended)
```bash
# Create and push a version tag
git tag v1.0.0
git push origin v1.0.0
```
This automatically creates a GitHub release with the executable.

### Method 2: Manual Release
1. Go to your GitHub repository
2. Click "Actions" tab
3. Select "Release Builder" workflow
4. Click "Run workflow"
5. Fill in release details:
   - ✅ Create a new release: `true`
   - Release tag: `v1.0.0`
   - Release name: `PanoramaBridge v1.0.0`
6. Click "Run workflow"

## Downloading Built Executables

### From Artifacts (Development Builds)
1. Go to "Actions" tab in GitHub
2. Click on any completed workflow run
3. Scroll down to "Artifacts" section
4. Download `PanoramaBridge-Windows-Build-XXX.zip`

### From Releases (Production Builds)
1. Go to "Releases" section on main repository page
2. Download `PanoramaBridge.exe` from latest release
3. Verify SHA256 hash if needed for security

## Build Environment

**Windows Runner Specs:**
- OS: Windows Server 2022
- Python: 3.12
- PyInstaller: Latest version
- All dependencies from `requirements.txt`

**Executable Details:**
- Single-file executable (no installation needed)
- Includes all Python dependencies
- Size: ~50-80MB (typical for PyQt6 apps)
- Architecture: x64 (compatible with modern Windows systems)

## Security Notes

- Executables are **unsigned** (would need code signing certificate for production)
- Windows SmartScreen may show warnings
- SHA256 checksums provided for verification
- All builds happen on GitHub's secure runners

## Troubleshooting

### Build Failures
Check the Actions tab for error logs:
- Python import errors → Check `requirements.txt`
- PyInstaller errors → Check `build_scripts/PanoramaBridge.spec`
- Missing files → Ensure all assets are committed

### Download Issues
- Large file sizes may take time to upload/download
- Artifacts expire (30 days for CI, 90 days for releases)
- Use releases for long-term downloads
