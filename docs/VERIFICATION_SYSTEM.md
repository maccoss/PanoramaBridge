# PanoramaBridge Verification System

Complete documentation of the file verification and integrity checking system in PanoramaBridge.

## Overview

PanoramaBridge implements a comprehensive verification system to ensure file integrity during transfers to Panorama WebDAV servers (LabKey Server). The system is designed specifically for Panorama, which does not support ETag-based verification.

## Verification Hierarchy

The verification system uses a 3-level hierarchy, checking each level in order:

```
Level 1: File Size Comparison (instant)
    ↓ (if sizes match)
Level 2: Checksum File Verification (if .checksum file exists)
    ↓ (if checksum file unavailable)
Level 3: Accessibility Check (fallback)
```

### Level 1: File Size Comparison

- **Method**: Compare local file size with remote file size
- **Performance**: Instant (metadata only)
- **Action on mismatch**: Return failure immediately

### Level 2: Checksum File Verification

**Primary verification method** when available:

- PanoramaBridge uploads a `.checksum` file alongside each data file
- Contains the SHA256 hash of the uploaded file
- Downloads the small checksum file (<100 bytes) and compares with local

```python
# Download and compare checksum file
checksum_path = remote_path + ".checksum"
checksum_data = self.webdav_client.download_file_head(checksum_path, 1024)
remote_checksum = checksum_data.decode('utf-8').strip()

if expected_checksum == remote_checksum:
    return True, "Size + checksum verified"
```

### Level 3: Accessibility Check

**Fallback verification** when checksum files are unavailable:

- Downloads first 8KB to verify file readability
- Confirms file exists, is readable, and user has permissions
- **Note**: Limited verification - cannot confirm complete file integrity

## Verification Messages

Users see clear, specific messages indicating verification method:

| Message | Meaning |
|---------|---------|
| `"Size + checksum verified"` | Checksum file matched local hash |
| `"Size + accessibility"` | File readable but no checksum file |
| `"size mismatch"` | Local and remote sizes differ |
| `"remote file not found"` | File doesn't exist on server |
| `"cannot read remote file"` | File exists but can't be read |

## Remote Integrity Check

### On-Demand Verification

The "Remote Integrity Check" button verifies all local files exist on the remote server:

1. **Scans local directory** for files matching configured extensions
2. **Checks remote existence** for each file
3. **Verifies integrity** using the 3-level hierarchy
4. **Reports results** with clear status indicators

### Verification Results

| Status | Meaning | Action |
|--------|---------|--------|
| Verified | File exists and integrity confirmed | None |
| Missing | File not found on remote | Auto-queued for upload |
| Changed/Conflict | Local and remote differ | Uses conflict resolution |
| Error | Network/verification error | Logged for troubleshooting |

### Startup Verification

When monitoring starts, PanoramaBridge automatically:

1. Loads upload history from persistent storage
2. Checks if previously uploaded files still exist on remote
3. Re-queues any missing files for upload

## Conflict Resolution

### Philosophy

PanoramaBridge treats file differences as **potential conflicts**, not corruption. This allows handling legitimate changes whether local or server-side.

### Conflict Detection

File conflict detected when:
- Same-length hash values don't match (same algorithm, different content)
- Local and remote files have different sizes

### Resolution Options

Configured in Transfer Settings:

| Setting | Behavior |
|---------|----------|
| **"Ask me each time"** | Shows dialog for user choice |
| **"Always upload"** | Automatically uploads local version |
| **"Always skip"** | Keeps remote version unchanged |
| **"Always use newer"** | Compares modification times |

### User Dialog Options

When "Ask me each time" is selected:

- **Upload Local**: Replace remote with local version
- **Skip**: Keep remote version, don't upload
- **Compare**: View details of both versions
- **Cancel**: Stop verification process

## Checksum System

### Purpose

Checksums serve multiple purposes:

- **Primary verification**: SHA256 stored as `.checksum` files on server for integrity checking
- **Upload history**: Track which files have been uploaded
- **Conflict detection**: Compare local vs stored checksums

**Note**: Panorama/LabKey Server does not support ETag-based verification, so PanoramaBridge uses checksum files instead.

### Checksum Calculation

```python
hash_obj = hashlib.sha256()
with open(filepath, 'rb') as f:
    while chunk := f.read(256 * 1024):  # 256KB chunks
        hash_obj.update(chunk)
checksum = hash_obj.hexdigest()
```

### Checksum Caching

Local caching dramatically improves performance:

- **Cache key**: `filepath + file_size + modification_time`
- **Cache limit**: 1,000 entries with automatic cleanup
- **Invalidation**: Automatic when file size or mtime changes
- **Performance**: Up to 1700x faster for unchanged files

## Upload History

### Persistent Tracking

Upload history stored in `~/.panorama_upload_history.json`:

```json
{
  "/path/to/file.raw": {
    "remote_path": "/webdav/project/file.raw",
    "checksum": "sha256...",
    "size": 1048576,
    "uploaded_at": "2025-12-29T10:30:00Z"
  }
}
```

### Benefits

- Skip already-uploaded files across restarts
- Track upload timestamps for auditing
- Enable integrity re-verification
- Support resume after interruption

## Performance Optimizations

### Design Principles

- **Size check first**: Instant metadata comparison catches most changes
- **Small checksum files**: Only download tiny `.checksum` files, not full data files
- **Local caching**: Avoid recalculating checksums for unchanged files
- **Fallback gracefully**: Accessibility check when checksums unavailable

### Performance Features

- Size comparison (instant, no network for mismatch)
- Checksum file verification (tiny download ~100 bytes)
- Local checksum caching (avoid recalculation)
- Minimal 8KB accessibility checks (fallback only)

### Performance Impact

| Operation | Without Caching | With Caching |
|-----------|-----------------|---------------|
| Checksum lookup | ~50ms (1MB) to ~45s (1GB) | <1ms |
| Verify 100MB file | ~500ms (checksum file) | ~500ms |
| Large file verification | Seconds | Seconds |

## Implementation Details

### Core Method

```python
def verify_remote_file_integrity(
    self, 
    local_filepath: str, 
    remote_path: str, 
    expected_checksum: str
) -> tuple[bool, str]:
    """
    Verify remote file exists and matches local file.
    
    Returns:
        (success: bool, verification_method: str)
    """
    # Step 1: File existence and size comparison
    remote_info = self.webdav_client.get_file_info(remote_path)
    if not remote_info.get("exists"):
        return False, "file not found on remote"
    
    local_size = os.path.getsize(local_filepath)
    remote_size = remote_info.get("size", 0)
    
    if local_size != remote_size:
        return False, f"size mismatch (local: {local_size}, remote: {remote_size})"
    
    # Step 2: Checksum file verification
    checksum_path = remote_path + ".checksum"
    checksum_info = self.webdav_client.get_file_info(checksum_path)
    
    if checksum_info and checksum_info.get("exists"):
        # Download small checksum file (<100 bytes)
        checksum_data = self.webdav_client.download_file_head(checksum_path, 1024)
        if checksum_data:
            remote_checksum = checksum_data.decode('utf-8').strip()
            if expected_checksum == remote_checksum:
                return True, "Size + checksum verified"
            else:
                return False, "checksum mismatch"
    
    # Step 3: Accessibility check (fallback)
    head_data = self.webdav_client.download_file_head(remote_path, 8192)
    if head_data is None:
        return False, "cannot read remote file"
    
    return True, "Size + accessibility"
```

## Troubleshooting

### Verification Failures

**"Size mismatch"**

- File was modified after upload
- Upload was incomplete
- **Action**: Re-upload the file

**"Checksum mismatch"**

- File content differs between local and remote
- **Action**: Use conflict resolution to choose version

**"Cannot read remote file"**

- Permission denied on server
- Network issue
- **Action**: Check permissions, retry later

**"File not found on remote"**

- File was deleted from server
- Wrong remote path
- **Action**: Re-upload or verify path

### Performance Issues

**Slow verification**

- Check network connection
- Verify checksum caching is enabled locally

**High bandwidth usage**

- Should not happen - only small checksum files are downloaded
- If accessibility fallback triggers frequently, check if checksum files exist

## Related Documentation

- [Testing Guide](TESTING.md) - Verification tests
- [File Monitoring](FILE_MONITORING.md) - How files are detected
- [Caching System](CACHING_SYSTEM.md) - Checksum caching details
