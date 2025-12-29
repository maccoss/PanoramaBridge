# Release Process Guide

This document explains how to create releases for PanoramaBridge with multi-architecture support.

## Release Workflows

### 1. Main Release Workflow (`release.yml`)
**Recommended for production releases**

- **Trigger**: Git tags (e.g., `v1.2.0`) or manual workflow dispatch
- **Builds**: Both x86_64 and ARM64 using native runners
- **Output**: Two executables with full build info

#### Creating a Tagged Release
```bash
# Create and push a version tag
git tag v1.2.0
git push origin v1.2.0
```

#### Manual Release Creation
1. Go to GitHub Actions -> Release Builder
2. Click "Run workflow"
3. Fill in:
   - Create a new release: `true`
   - Release tag: `v1.2.0`
   - Release name: `PanoramaBridge v1.2.0`

### 2. Fallback Release Workflow (`release-fallback.yml`)
**Use only if ARM64 runners are unavailable**

- **Trigger**: Manual only
- **Builds**: x86_64 native, ARM64 cross-compiled
- **Output**: Draft release (requires manual review)

## Release Assets

Each release includes:

### Executables
- `PanoramaBridge.exe` - x86_64 (Intel/AMD)
- `PanoramaBridge-arm64.exe` - ARM64 (Snapdragon)

### Build Information
- `BUILD_INFO_x64.txt` - x86_64 build details
- `BUILD_INFO_arm64.txt` - ARM64 build details

## Release Notes Template

The workflows automatically generate comprehensive release notes including:

- **Multi-architecture download instructions**
- **System compatibility information** 
- **Installation steps**
- **What's new in this release**
- **Build verification checksums**

## Quality Assurance

### Automated Checks
- Python architecture verification
- PyQt6 installation verification
- Executable architecture verification (when possible)
- SHA256 checksum generation
- File size reporting

### Manual Testing Checklist
Before publishing a release:

- [ ] Test x86_64 executable on Intel/AMD system
- [ ] Test ARM64 executable on Snapdragon/ARM64 system
- [ ] Verify both executables start without errors
- [ ] Test basic WebDAV connectivity
- [ ] Test file monitoring and upload
- [ ] Verify UI functionality

## Troubleshooting

### ARM64 Runner Issues
If you see errors like "No runner matching the labels":
1. Use the fallback workflow instead
2. Or temporarily disable ARM64 builds by commenting out the matrix entry

### Cross-compilation Warnings
The fallback workflow will show warnings for ARM64 cross-compilation:
- This is expected behavior
- The executable should still work but test thoroughly
- Native ARM64 builds are always preferred

### Release Creation Failures
Common issues:
- **Missing tag**: Ensure git tag exists and is pushed
- **Permissions**: Check repository has Actions write permissions
- **Artifact download**: Verify all build jobs completed successfully

## Best Practices

1. **Use semantic versioning**: `v1.2.3` format
2. **Test before releasing**: Use workflow_dispatch first
3. **Native builds preferred**: Use main workflow when possible  
4. **Document breaking changes**: Update release notes accordingly
5. **Verify checksums**: Include SHA256 hashes in release notes

## Architecture Decision Tree

```
Creating a release?
│
├── Have access to ARM64 runners?
│   ├── Yes → Use main release workflow (release.yml)
│   └── No → Use fallback workflow (release-fallback.yml)
│
└── Testing only?
    └── Use workflow_dispatch on build-windows.yml
```
