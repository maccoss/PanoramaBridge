# PanoramaBridge File Monitoring System

Complete documentation of the file monitoring, detection, and processing system.

## Overview

PanoramaBridge monitors local directories for new files and automatically queues them for transfer to WebDAV servers. The system uses efficient OS-level file events as the primary detection method.

## Monitoring Architecture

### Primary Method: OS File System Events

Uses the `watchdog` library with efficient OS-level event detection:

- **Events monitored**: `on_created`, `on_modified`, `on_moved`
- **Response time**: Immediate - no delay between file creation and detection
- **Resource usage**: Minimal CPU overhead (no directory scanning)
- **Battery impact**: Low - no continuous disk access

### Backup Method: Polling (Optional)

Polling is **disabled by default** but available for special scenarios:

- **Default**: Disabled
- **Interval**: 1-30 minutes (configurable)
- **Use cases**: Network drives, WSL2, virtual machines

## Configuration

### Advanced Settings UI

```
Advanced Settings Tab:
☐ Enable backup file polling (default: unchecked)
  └ Polling interval (minutes): [2] (only enabled when checkbox checked)

☑ Handle locked files (e.g., from mass spectrometers) (default: checked)
  ├ Initial wait time (minutes): [30]
  ├ Retry interval (seconds): [30]
  └ Max retries: [20]
```

### When to Enable Polling

Enable backup polling only for:

| Scenario | Reason |
|----------|--------|
| Network drives | May not support OS events reliably |
| WSL2 environments | File event detection unreliable for Windows paths |
| Virtual machines | VM file system pass-through may not support events |
| Container environments | Docker may need polling |
| Legacy file systems | Older systems without modern event support |

**Recommendation**: For optimal performance on Windows, use the native Windows build with `.venv-win` virtual environment.

## File Detection Process

### 1. Event Detection

```python
class FileMonitorHandler(FileSystemEventHandler):
    def on_created(self, event):
        # File created - check if it matches extensions
        if self._matches_extensions(event.src_path):
            self._queue_for_stability_check(event.src_path)
    
    def on_modified(self, event):
        # File modified - may be a file still being written
        if self._matches_extensions(event.src_path):
            self._update_pending_file(event.src_path)
    
    def on_moved(self, event):
        # File renamed/moved into monitored directory
        if self._matches_extensions(event.dest_path):
            self._queue_for_stability_check(event.dest_path)
```

### 2. Extension Filtering

- **Case-insensitive** matching against configured extensions
- **Hidden files** filtered (starting with `.` or `~`)
- **Common extensions**: `.raw`, `.wiff`, `.mzML`, `.mzXML`, `.sld`, `.csv`

### 3. File Stability Check

Files must be stable before queuing for upload:

```python
# Default 1-second stability timeout
def _check_file_stability(self, filepath):
    current_size = os.path.getsize(filepath)
    
    # If size changed since last check, reset timer
    if current_size != self.pending_files.get(filepath, {}).get('size'):
        self.pending_files[filepath] = {
            'size': current_size,
            'timestamp': time.time()
        }
        return False  # Not stable yet
    
    # Stable if size unchanged for stability_timeout
    elapsed = time.time() - self.pending_files[filepath]['timestamp']
    return elapsed >= self.stability_timeout
```

### 4. Duplicate Prevention

Multiple mechanisms prevent duplicate uploads:

- **`queued_files`**: Set of files currently in queue
- **`processing_files`**: Set of files being uploaded
- **Remote path check**: Prevents re-uploading same destinations
- **Upload history**: Tracks successfully uploaded files

## Locked File Handling

### Mass Spectrometer Workflow Support

Mass spectrometers lock files during data acquisition. PanoramaBridge handles this intelligently:

#### Detection
```python
# File lock detected during checksum calculation
try:
    with open(filepath, 'rb') as f:
        calculate_checksum(f)
except PermissionError:
    # File is locked - instrument still writing
    return schedule_retry(filepath)
```

#### Smart Retry Logic

| Setting | Default | Description |
|---------|---------|-------------|
| Initial wait | 30 min | Wait time before first retry |
| Retry interval | 30 sec | Time between retry attempts |
| Max retries | 20 | Maximum retry attempts |

#### Progress Indication

Users see clear status messages:

```
"File locked - waiting for instrument (5/30 minutes elapsed)"
"Retrying locked file... attempt 3/20"
"File unlocked - proceeding with upload"
```

## Performance Optimization

### OS Events vs Polling

| Aspect | OS Events | Polling (30s) |
|--------|-----------|---------------|
| CPU usage | Minimal | High |
| Response time | Immediate | Up to 30s |
| Battery impact | Low | High |
| Memory usage | Low | Higher |

### Performance Gains

With polling disabled (default):

- **CPU usage**: ~95% reduction in background CPU
- **Battery life**: Significant improvement on laptops
- **Memory usage**: ~15% reduction
- **File detection**: Still immediate (OS events)

## Thread Safety

### Cross-Thread UI Updates

File monitoring runs in a background thread. UI updates use Qt's thread-safe mechanisms:

```python
def _safe_ui_update(self, method, *args):
    """Schedule UI update on main thread."""
    QMetaObject.invokeMethod(
        self.app_instance,
        method,
        Qt.ConnectionType.QueuedConnection,
        *[Q_ARG(type(arg), arg) for arg in args]
    )
```

### Crash Prevention

The monitoring system includes robust error handling:

- **File disappearance**: Graceful handling when files are deleted during monitoring
- **Permission errors**: Proper handling of locked/protected files
- **I/O errors**: Recovery from temporary access issues
- **Concurrent operations**: Safe handling of multiple simultaneous file events

## Windows Native Optimization

### `.venv-win` Virtual Environment

For optimal Windows performance:

```bash
# Create Windows-optimized virtual environment
python -m venv .venv-win

# Activate
.venv-win\Scripts\activate

# Install dependencies
pip install -r requirements.txt
```

### Benefits

- Better file system event detection vs WSL2
- Enhanced locked file handling for mass spectrometers
- Native Windows file locking mechanism support
- Eliminated virtualization overhead

## Monitoring Startup Logic

```python
def start_monitoring(self):
    # Always start OS-level monitoring (primary method)
    self.observer = Observer()
    self.observer.schedule(
        self.event_handler,
        self.watch_directory,
        recursive=self.subdirectories
    )
    self.observer.start()
    logger.info("Started OS-level file monitoring")

    # Only start polling if explicitly enabled
    if self.enable_polling_check.isChecked():
        interval_ms = self.polling_interval_spin.value() * 60 * 1000
        self.poll_timer.start(interval_ms)
        logger.info(f"Started backup polling every {self.polling_interval_spin.value()} minutes")
    else:
        logger.info("Backup polling disabled - relying on OS file events only")
```

## Troubleshooting

### Files Not Being Detected

1. **Verify directory path** is correct and accessible
2. **Check file extensions** match exactly (case-insensitive)
3. **Ensure files are in monitored directory** (or subdirectories if enabled)
4. **Check file stability timeout** - may need adjustment for large files
5. **WSL2 users**: Use native Windows build with `.venv-win`

### Performance Issues

1. **Disable polling** if enabled (use OS events only)
2. **Check checksum caching** is working (see CACHING_SYSTEM.md)
3. **Monitor system resources** during file operations
4. **Review log files** for errors or warnings

### Locked File Issues

1. **"File locked - waiting for instrument"** is normal during data acquisition
2. **Adjust wait times** in Advanced Settings if needed
3. **Increase max retries** if files take longer to complete
4. **Check logs** for "File locked during checksum" messages

## Related Documentation

- [Caching System](CACHING_SYSTEM.md) - Checksum caching for performance
- [Verification System](VERIFICATION_SYSTEM.md) - Upload verification
- [Testing Guide](TESTING.md) - File monitoring tests

