"""
Tests for file monitoring and processing functionality.
"""
import os
import tempfile
import time
import threading
from pathlib import Path
from unittest.mock import Mock, patch, MagicMock
import pytest

# Import the module under test
import sys
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from panoramabridge import FileProcessor, FileMonitorHandler


class TestFileProcessor:
    """Test file processing functionality."""
    
    def test_init(self, mock_webdav_client):
        """Test FileProcessor initialization."""
        processor = FileProcessor(mock_webdav_client)
        
        assert processor.webdav_client == mock_webdav_client
        assert processor.checksum_cache == {}
        assert processor.stats['files_processed'] == 0
        assert processor.stats['bytes_transferred'] == 0
    
    def test_calculate_checksum(self, sample_file, calculate_test_checksum):
        """Test checksum calculation."""
        file_path, expected_size = sample_file
        
        processor = FileProcessor(Mock())
        checksum = processor.calculate_checksum(file_path)
        
        # Verify checksum is calculated correctly
        expected_checksum = calculate_test_checksum(file_path)
        assert checksum == expected_checksum
        assert len(checksum) == 64  # SHA-256 hex digest
    
    def test_calculate_checksum_large_file(self, large_sample_file, calculate_test_checksum):
        """Test checksum calculation for large files."""
        file_path, expected_size = large_sample_file
        
        processor = FileProcessor(Mock())
        checksum = processor.calculate_checksum(file_path)
        
        expected_checksum = calculate_test_checksum(file_path)
        assert checksum == expected_checksum
        assert len(checksum) == 64
    
    def test_checksum_caching(self, sample_file):
        """Test checksum caching functionality."""
        file_path, expected_size = sample_file
        
        processor = FileProcessor(Mock())
        
        # First call should calculate checksum
        with patch.object(processor, 'calculate_checksum', wraps=processor.calculate_checksum) as mock_calc:
            checksum1 = processor.get_cached_checksum(file_path)
            assert mock_calc.call_count == 1
        
        # Modify file timestamp to simulate unchanged file
        original_mtime = os.path.getmtime(file_path)
        
        # Second call should use cached value
        with patch.object(processor, 'calculate_checksum', wraps=processor.calculate_checksum) as mock_calc:
            checksum2 = processor.get_cached_checksum(file_path)
            assert mock_calc.call_count == 0
        
        assert checksum1 == checksum2
        assert file_path in processor.checksum_cache
    
    def test_checksum_cache_invalidation(self, sample_file):
        """Test checksum cache invalidation when file changes."""
        file_path, expected_size = sample_file
        
        processor = FileProcessor(Mock())
        
        # First calculation
        checksum1 = processor.get_cached_checksum(file_path)
        
        # Wait briefly then modify file
        time.sleep(0.1)
        with open(file_path, 'a') as f:
            f.write("modified content")
        
        # Second calculation should recalculate due to changed mtime
        with patch.object(processor, 'calculate_checksum', wraps=processor.calculate_checksum) as mock_calc:
            checksum2 = processor.get_cached_checksum(file_path)
            assert mock_calc.call_count == 1
        
        assert checksum1 != checksum2
    
    def test_is_file_locked_not_locked(self, sample_file):
        """Test checking if file is not locked."""
        file_path, _ = sample_file
        
        processor = FileProcessor(Mock())
        assert processor.is_file_locked(file_path) is False
    
    def test_is_file_locked_permission_error(self, temp_dir):
        """Test file lock detection with permission error."""
        file_path = os.path.join(temp_dir, 'locked_file.raw')
        with open(file_path, 'w') as f:
            f.write("test content")
        
        processor = FileProcessor(Mock())
        
        # Mock permission error
        with patch('builtins.open', side_effect=PermissionError("File is locked")):
            assert processor.is_file_locked(file_path) is True
    
    @patch('panoramabridge.threading.Event')
    def test_wait_for_file_available(self, mock_event, sample_file):
        """Test waiting for file to become available."""
        file_path, _ = sample_file
        
        processor = FileProcessor(Mock())
        processor.shutdown_event = Mock()
        processor.shutdown_event.is_set.return_value = False
        
        # Mock file is initially locked, then becomes available
        processor.is_file_locked = Mock(side_effect=[True, True, False])
        
        result = processor.wait_for_file_available(file_path, timeout=5)
        
        assert result is True
        assert processor.is_file_locked.call_count == 3
    
    def test_wait_for_file_available_timeout(self, sample_file):
        """Test timeout when waiting for file."""
        file_path, _ = sample_file
        
        processor = FileProcessor(Mock())
        processor.shutdown_event = Mock()
        processor.shutdown_event.is_set.return_value = False
        
        # Mock file is always locked
        processor.is_file_locked = Mock(return_value=True)
        
        result = processor.wait_for_file_available(file_path, timeout=0.1)
        
        assert result is False
    
    def test_process_file_success(self, sample_file, mock_webdav_client):
        """Test successful file processing."""
        file_path, expected_size = sample_file
        
        # Mock successful upload
        mock_webdav_client.upload_file_chunked.return_value = (True, "")
        mock_webdav_client.store_checksum.return_value = True
        mock_webdav_client.get_stored_checksum.return_value = None
        
        processor = FileProcessor(mock_webdav_client)
        
        # Mock progress callback
        progress_callback = Mock()
        
        success = processor.process_file(file_path, "/remote/test_file.raw", progress_callback)
        
        assert success is True
        assert processor.stats['files_processed'] == 1
        assert processor.stats['bytes_transferred'] == expected_size
        
        # Verify WebDAV calls
        mock_webdav_client.upload_file_chunked.assert_called_once()
        mock_webdav_client.store_checksum.assert_called_once()
    
    def test_process_file_checksum_match(self, sample_file, mock_webdav_client):
        """Test file processing when checksum matches (skip upload)."""
        file_path, expected_size = sample_file
        
        processor = FileProcessor(mock_webdav_client)
        local_checksum = processor.calculate_checksum(file_path)
        
        # Mock stored checksum matches local
        mock_webdav_client.get_stored_checksum.return_value = local_checksum
        
        progress_callback = Mock()
        success = processor.process_file(file_path, "/remote/test_file.raw", progress_callback)
        
        assert success is True
        assert processor.stats['files_skipped'] == 1
        
        # Upload should not be called
        mock_webdav_client.upload_file_chunked.assert_not_called()
    
    def test_process_file_locked(self, sample_file, mock_webdav_client):
        """Test processing locked file."""
        file_path, _ = sample_file
        
        processor = FileProcessor(mock_webdav_client)
        processor.is_file_locked = Mock(return_value=True)
        processor.wait_for_file_available = Mock(return_value=False)
        
        progress_callback = Mock()
        success = processor.process_file(file_path, "/remote/test_file.raw", progress_callback)
        
        assert success is False
        processor.wait_for_file_available.assert_called_once_with(file_path, timeout=300)


class TestFileMonitorHandler:
    """Test file monitoring functionality."""
    
    def test_init(self, file_queue, sample_extensions):
        """Test FileMonitorHandler initialization."""
        handler = FileMonitorHandler(file_queue, sample_extensions)
        
        assert handler.file_queue == file_queue
        assert handler.file_extensions == sample_extensions
        assert handler.debounce_time == 2.0
    
    def test_should_process_file_valid_extension(self, file_queue, sample_extensions):
        """Test file filtering by extension."""
        handler = FileMonitorHandler(file_queue, sample_extensions)
        
        assert handler.should_process_file("test.raw") is True
        assert handler.should_process_file("data.mzML") is True
        assert handler.should_process_file("experiment.d") is True
    
    def test_should_process_file_invalid_extension(self, file_queue, sample_extensions):
        """Test filtering out invalid extensions."""
        handler = FileMonitorHandler(file_queue, sample_extensions)
        
        assert handler.should_process_file("test.txt") is False
        assert handler.should_process_file("data.log") is False
        assert handler.should_process_file("temp") is False
    
    def test_should_process_file_system_files(self, file_queue, sample_extensions):
        """Test filtering out system files."""
        handler = FileMonitorHandler(file_queue, sample_extensions)
        
        assert handler.should_process_file(".hidden.raw") is False
        assert handler.should_process_file("desktop.ini") is False
        assert handler.should_process_file("Thumbs.db") is False
        assert handler.should_process_file(".DS_Store") is False
    
    @patch('time.time')
    def test_file_debouncing(self, mock_time, file_queue, sample_extensions, temp_dir):
        """Test file modification debouncing."""
        handler = FileMonitorHandler(file_queue, sample_extensions)
        
        test_file = os.path.join(temp_dir, "test.raw")
        with open(test_file, 'w') as f:
            f.write("test content")
        
        # First modification
        mock_time.return_value = 1000.0
        handler.on_modified(type('Event', (), {'src_path': test_file}))
        
        # Second modification within debounce time
        mock_time.return_value = 1001.0
        handler.on_modified(type('Event', (), {'src_path': test_file}))
        
        # Third modification after debounce time
        mock_time.return_value = 1003.0
        handler.on_modified(type('Event', (), {'src_path': test_file}))
        
        # Only the last modification should be queued
        assert file_queue.qsize() == 1
        
        queued_path = file_queue.get()
        assert queued_path == test_file
    
    def test_on_created_valid_file(self, file_queue, sample_extensions, temp_dir):
        """Test file creation event handling."""
        handler = FileMonitorHandler(file_queue, sample_extensions)
        
        test_file = os.path.join(temp_dir, "new_file.raw")
        with open(test_file, 'w') as f:
            f.write("test content")
        
        # Simulate file creation event
        event = type('Event', (), {'src_path': test_file, 'is_directory': False})
        handler.on_created(event)
        
        assert file_queue.qsize() == 1
        queued_path = file_queue.get()
        assert queued_path == test_file
    
    def test_on_created_directory(self, file_queue, sample_extensions, temp_dir):
        """Test directory creation event handling (should be ignored)."""
        handler = FileMonitorHandler(file_queue, sample_extensions)
        
        test_dir = os.path.join(temp_dir, "new_directory")
        os.makedirs(test_dir)
        
        # Simulate directory creation event
        event = type('Event', (), {'src_path': test_dir, 'is_directory': True})
        handler.on_created(event)
        
        assert file_queue.qsize() == 0
    
    def test_on_moved_to_valid_extension(self, file_queue, sample_extensions, temp_dir):
        """Test file move/rename event handling."""
        handler = FileMonitorHandler(file_queue, sample_extensions)
        
        old_file = os.path.join(temp_dir, "old_name.tmp")
        new_file = os.path.join(temp_dir, "new_name.raw")
        
        with open(old_file, 'w') as f:
            f.write("test content")
        os.rename(old_file, new_file)
        
        # Simulate file move event
        event = type('Event', (), {
            'src_path': old_file,
            'dest_path': new_file,
            'is_directory': False
        })
        handler.on_moved(event)
        
        assert file_queue.qsize() == 1
        queued_path = file_queue.get()
        assert queued_path == new_file
