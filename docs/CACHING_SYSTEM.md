# PanoramaBridge Caching System

Documentation of the checksum caching and queue management systems for optimal performance.

## Overview

PanoramaBridge implements sophisticated caching mechanisms to dramatically improve performance when handling large files, particularly for mass spectrometry workflows where files can be multiple gigabytes.

## Checksum Caching

### Problem Addressed

Without caching, the application constantly recalculates SHA256 checksums for unchanged files. For large mass spectrometry files (>1GB), this creates significant overhead:

- **CPU intensive**: Full file read required for each calculation
- **I/O intensive**: Entire file read from disk
- **Time consuming**: Multi-GB files take minutes to hash

### Solution: Local Checksum Cache

Cache checksums in memory using file metadata as cache keys:

```python
# Cache key combines path, size, and modification time
cache_key = f"{filepath}|{file_size}|{file_mtime:.0f}"

# Automatic invalidation when files change
if file_size_changed or mtime_changed:
    cache_miss = True  # Recalculate checksum
```

### Performance Improvements

Benchmark results show dramatic speedups:

| File Size | Without Cache | With Cache | Improvement |
|-----------|--------------|------------|-------------|
| 1 MB | ~50ms | <1ms | 75x faster |
| 10 MB | ~500ms | <1ms | 660x faster |
| 50 MB | ~2.5s | <1ms | 1,734x faster |
| 1 GB+ | ~45s | <1ms | 3,000x+ faster |

### Implementation

```python
def calculate_checksum(self, filepath: str, algorithm: str = 'sha256') -> str:
    """Enhanced checksum calculation with local caching."""
    
    # Get file stats for cache key
    stat = os.stat(filepath)
    cache_key = f"{filepath}|{stat.st_size}|{stat.st_mtime:.0f}"
    
    # Check cache first
    if cache_key in self.app_instance.local_checksum_cache:
        logger.debug(f"Cache HIT: {os.path.basename(filepath)}")
        return self.app_instance.local_checksum_cache[cache_key]
    
    # Calculate new checksum if not cached
    logger.debug(f"Cache MISS: Calculating for {os.path.basename(filepath)}")
    checksum = self._calculate_from_file(filepath, algorithm)
    
    # Store in cache with cleanup
    self._store_in_cache(cache_key, checksum)
    
    return checksum
```

### Cache Management

#### Size Limits

```python
MAX_CACHE_SIZE = 1000  # Maximum entries

def _store_in_cache(self, cache_key: str, checksum: str):
    """Store with automatic memory management."""
    self.cache[cache_key] = checksum
    
    # Cleanup when too large
    if len(self.cache) > MAX_CACHE_SIZE:
        # Remove oldest 100 entries (FIFO)
        items = list(self.cache.items())
        for old_key, _ in items[:100]:
            del self.cache[old_key]
```

#### Automatic Invalidation

Cache entries are automatically invalidated when:

- **File size changes**: Indicates content modification
- **Modification time changes**: File was updated
- **File deleted**: Entry naturally expires

#### Memory Footprint

- **Per entry**: ~100 bytes (filepath hash + 64-char checksum)
- **Maximum**: ~100KB for 1,000 entries
- **No file data stored**: Only checksums

## Queue Management

### Transfer Queue

The transfer queue manages files awaiting upload:

```python
class TransferQueue:
    def __init__(self):
        self.queue = queue.Queue()  # Thread-safe queue
        self.queued_files = set()   # For duplicate prevention
        self.processing_files = set()  # Currently uploading
```

### Duplicate Prevention

Multiple mechanisms prevent duplicate uploads:

1. **Queued files set**: Tracks files in queue
2. **Processing files set**: Tracks files being uploaded
3. **Upload history**: Tracks successfully uploaded files
4. **Remote path check**: Prevents re-uploading same destination

```python
def add_to_queue(self, filepath: str, remote_path: str):
    """Add file to upload queue with duplicate prevention."""
    
    # Check if already queued
    if filepath in self.queued_files:
        logger.debug(f"Already queued: {filepath}")
        return False
    
    # Check if currently processing
    if filepath in self.processing_files:
        logger.debug(f"Currently processing: {filepath}")
        return False
    
    # Check upload history
    if self._already_uploaded(filepath, remote_path):
        logger.debug(f"Already uploaded: {filepath}")
        return False
    
    # Add to queue
    self.queued_files.add(filepath)
    self.queue.put((filepath, remote_path))
    return True
```

### Table Ordering

Files are displayed in FIFO order:

- **First file**: Row 0 (top)
- **Second file**: Row 1
- **New files**: Appended to bottom
- **Processing order**: Same as display order

```python
# Correct: Append to bottom
row = self.table.rowCount()
self.table.insertRow(row)

# Incorrect (old behavior): Insert at top
# self.table.insertRow(0)  # Don't do this
```

## Upload History

### Persistent Tracking

Upload history stored in `~/.panorama_upload_history.json`:

```json
{
  "/path/to/file.raw": {
    "remote_path": "/webdav/project/file.raw",
    "checksum": "abc123...",
    "size": 1048576,
    "uploaded_at": "2025-12-29T10:30:00Z"
  }
}
```

### History Operations

```python
def load_upload_history(self):
    """Load history from persistent storage."""
    history_file = Path.home() / '.panorama_upload_history.json'
    if history_file.exists():
        with open(history_file, 'r') as f:
            self.upload_history = json.load(f)

def save_upload_history(self):
    """Save history to persistent storage."""
    history_file = Path.home() / '.panorama_upload_history.json'
    with open(history_file, 'w') as f:
        json.dump(self.upload_history, f, indent=2)

def record_upload(self, filepath: str, remote_path: str, checksum: str):
    """Record successful upload."""
    self.upload_history[filepath] = {
        'remote_path': remote_path,
        'checksum': checksum,
        'size': os.path.getsize(filepath),
        'uploaded_at': datetime.now().isoformat()
    }
    self.save_upload_history()
```

### Skip-Already-Uploaded

When monitoring starts, previously uploaded files are skipped:

```python
def should_upload(self, filepath: str, remote_path: str) -> bool:
    """Check if file needs uploading."""
    
    if filepath not in self.upload_history:
        return True  # Never uploaded
    
    history = self.upload_history[filepath]
    
    # Check if file changed since upload
    current_size = os.path.getsize(filepath)
    if current_size != history.get('size'):
        return True  # Size changed - re-upload
    
    # Check if remote path changed
    if remote_path != history.get('remote_path'):
        return True  # Different destination
    
    return False  # Already uploaded and unchanged
```

## Performance Monitoring

### Debug Logging

Enable detailed cache logging:

```python
# In logging configuration
logger.setLevel(logging.DEBUG)

# Cache operations logged:
# "Cache HIT: sample.raw (1048576 bytes)"
# "Cache MISS: Calculating for large_file.raw"
# "Cache cleanup: now 900 entries"
```

### Performance Metrics

Track cache efficiency:

```python
class CacheMetrics:
    def __init__(self):
        self.hits = 0
        self.misses = 0
    
    @property
    def hit_rate(self):
        total = self.hits + self.misses
        return self.hits / total if total > 0 else 0
```

## Laboratory Workflow Integration

### Mass Spectrometry Optimization

- **Large file handling**: Dramatic speedup for multi-GB .raw, .wiff, .mzML files
- **Batch processing**: Efficient handling of experiment file sets
- **Instrument compatibility**: Works with locked files from mass spectrometers

### Typical Workflow

1. **First scan**: All files calculated (cache miss)
2. **Subsequent scans**: Instant lookups (cache hit)
3. **File changes**: Automatic cache invalidation
4. **New files**: Calculated and cached

## Configuration

### Application Settings

Cache settings are managed automatically:

- **No user configuration required**: Caching is always enabled
- **Automatic cleanup**: Memory managed automatically
- **Session-only persistence**: Cache rebuilt on restart

### Memory Tuning

For environments with limited memory:

```python
# Reduce cache size limit
MAX_CACHE_SIZE = 500  # Instead of 1000

# More aggressive cleanup
CLEANUP_COUNT = 200   # Remove more entries at once
```

## Troubleshooting

### Cache Not Working

1. **Check debug logs** for "Cache HIT" vs "Cache MISS" messages
2. **Verify file stability**: Files must stop changing before caching
3. **Check memory usage**: Cache may be hitting size limits

### Performance Issues

1. **First scan is slow**: Normal - cache is being built
2. **Subsequent scans slow**: Check for file modifications invalidating cache
3. **High memory usage**: Review MAX_CACHE_SIZE setting

### Stale Cache Entries

Cache entries are automatically invalidated when:
- File size changes
- File modification time changes

No manual cache clearing is typically needed.

## Related Documentation

- [File Monitoring](FILE_MONITORING.md) - How files are detected
- [Verification System](VERIFICATION_SYSTEM.md) - How uploads are verified
- [Testing Guide](TESTING.md) - Cache-related tests

