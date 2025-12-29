# Remote Integrity Check System (Updated 2025)

## Overview

PanoramaBridge includes a comprehensive verification system to check that all local files are properly uploaded and intact on the remote server. The system has been recently enhanced with improved conflict resolution logic that no longer assumes corruption when files differ.

**Key Improvements (2025 Update):**
- **No Corruption Assumptions**: File differences are treated as conflicts requiring user resolution, not assumed corruption
- **Comprehensive File Coverage**: Checks ALL local files, not just those in upload history  
- **Intelligent Conflict Resolution**: Uses your configured conflict resolution settings for all file differences
- **Better User Feedback**: Clear, actionable error messages with specific guidance

**Verification Messages You'll See:**

- "Remote file verified by ETag (SHA256 format)" - Best case: server ETag matches local SHA256
- "Remote file verified by ETag (MD5 format)" - Server uses MD5-based ETags (Apache default)
- "Remote file verified by Size + accessibility (ETag unavailable)" - Server doesn't provide ETags
- "Remote file verified by Size + accessibility (server uses unknown ETag format)" - Unrecognized ETag format
- "File conflict detected - applying conflict resolution" - File differs, using your settings to resolve

## How It Works

### Remote Integrity Check Button

Located next to "Start Monitoring" in the Transfer Status tab. Click to verify all files currently shown in the
transfer table.

### Multi-Level Verification Process

Files are verified using the most efficient method available:

1. **File Exists & Size Match** - Checks filename and file size between local and remote
2. **ETag Verification** - Primary method supporting both SHA256 and MD5 formats
3. **Size + Accessibility Check** - Downloads first 8KB to verify file can be read

**Important**: Checksums are generated during upload for metadata purposes but are not used for
verification due to the performance cost of downloading and recalculating checksums.

### ETag Format Support

The system now supports multiple ETag formats:

- **SHA256 ETags**: Direct comparison with local file SHA256 checksum
- **MD5 ETags** (Apache servers): Calculates MD5 of local file for comparison
- **Unknown formats**: Falls back to size + accessibility verification

### Accessibility Assessment

When ETag verification is unavailable or fails, the system performs an **accessibility check** to verify
that the remote file exists and can be read properly:

**How Accessibility is Tested:**

1. **HTTP HEAD Request**: Sends a partial content request to download only the first 8KB of the file
2. **Read Verification**: Confirms the server can successfully serve file content (not just metadata)
3. **Response Validation**: Ensures the server returns actual file data, not error pages or redirects

**Technical Implementation:**

```python
# Downloads first 8192 bytes to verify file accessibility
head_data = self.webdav_client.download_file_head(remote_path, 8192)
if head_data is None:
    return False, "cannot read remote file"
```

**What Accessibility Confirms:**

- ✅ **File Exists**: Remote file is present on the server
- ✅ **File Readable**: Server can successfully serve file content
- ✅ **Not Corrupted Metadata**: File isn't a broken link or placeholder
- ✅ **Permission Access**: User has read permissions for the file
- ✅ **Server Functional**: WebDAV server is responding correctly

**What Accessibility Cannot Confirm:**

- ❌ **Content Integrity**: Cannot verify the entire file content is correct
- ❌ **Checksum Match**: No cryptographic verification of file contents
- ❌ **Complete File**: Only verifies first 8KB, not the entire file

**When Accessibility is Used:**

- Server doesn't provide ETags
- Server uses unknown ETag formats
- ETag verification fails but file might still be valid
- As final verification step when other methods unavailable

### What Happens During Check

- Button shows "Checking... (X/Y)" progress
- File monitoring pauses temporarily
- **All local files are verified**, not just previously uploaded ones
- Missing files are automatically queued for re-upload
- File differences trigger conflict resolution based on your settings
- Results dialog shows verification status with specific details

### Results

**If all files verified:** "All X files verified successfully!"

**If issues found:** Shows counts and specific guidance for:

- **Files missing from remote**: Automatically queued for upload
- **Files with conflicts**: Handled according to your conflict resolution settings
- **Files with verification errors**: Network or server issues requiring attention

### Conflict Resolution Integration

When the integrity check detects that a file differs between local and remote, it no longer assumes corruption. Instead:

1. **Applies Your Settings**: Uses your configured conflict resolution setting ("Ask me each time", "Always upload", etc.)
2. **Respects User Choice**: For "Ask me each time", shows conflict dialog for user decision
3. **Clear Communication**: Explains that files differ without assuming which version is "correct"
4. **Preserves Data**: Never automatically overwrites without user consent when set to "Ask me each time"

This approach ensures that legitimate changes (whether made locally or on the server) are handled appropriately.

## When to Use

- After large batch uploads to confirm integrity
- When troubleshooting upload issues
- Before important data analysis to ensure files are complete
- When concerned about network interruptions during uploads

## Technical Details

- Runs in background thread to keep UI responsive
- Uses existing upload history for reference checksums
- Integrates with conflict resolution system for changed files
- Handles network errors gracefully with detailed logging
