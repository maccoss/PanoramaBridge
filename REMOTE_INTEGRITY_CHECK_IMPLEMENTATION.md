# Remote Integrity Check System

## Overview

PanoramaBridge includes an on-demand verification system to check that uploaded files are intact on the
remote server. **Note**: Checksums are generated for upload metadata but are **not used for verification**
due to performance cost.

**Verification Messages You'll See:**

- "Remote file verified by ETag (SHA256 format)" - Best case: server ETag matches local SHA256
- "Remote file verified by ETag (MD5 format)" - Server uses MD5-based ETags (Apache default)
- "Remote file verified by Size + accessibility (ETag unavailable)" - Server doesn't provide ETags
- "Remote file verified by Size + accessibility (server uses unknown ETag format)" - Unrecognized ETag format

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

### What Happens During Check

- Button shows "Checking... (X/Y)" progress
- File monitoring pauses temporarily
- Results dialog shows verification status
- Missing/corrupted files are automatically re-queued for upload

### Results

**If all files verified:** "All X files verified successfully!"

**If issues found:** Shows counts of:

- Files missing from remote (queued for upload)
- Files corrupted on remote (queued for re-upload)
- Files with verification errors

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
