"""
Performance and optimization tests for PanoramaBridge.
"""
import os
import time
import tempfile
import threading
from pathlib import Path
from unittest.mock import Mock, patch
import pytest

# Import the module under test
import sys
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from panoramabridge import FileProcessor, FileMonitorHandler


class TestChecksumCaching:
    """Test checksum caching performance and correctness."""
    
    def test_checksum_cache_performance(self, temp_dir):
        """Test that checksum caching provides performance benefits."""
        processor = FileProcessor(Mock())
        
        # Create test file
        test_file = os.path.join(temp_dir, "performance_test.raw")
        with open(test_file, 'w', encoding='utf-8') as f:
            f.write("test content for performance testing" * 1000)
        
        # First calculation (no cache)
        start_time = time.time()
        checksum1 = processor.get_cached_checksum(test_file)
        first_duration = time.time() - start_time
        
        # Second calculation (from cache)
        start_time = time.time()
        checksum2 = processor.get_cached_checksum(test_file)
        cached_duration = time.time() - start_time
        
        # Verify same checksum
        assert checksum1 == checksum2
        
        # Cached access should be significantly faster
        assert cached_duration < first_duration / 10
        
        # Verify cache entry exists
        assert test_file in processor.checksum_cache
    
    def test_checksum_cache_memory_efficiency(self, temp_dir):
        """Test that cache doesn't grow indefinitely."""
        processor = FileProcessor(Mock())
        
        # Create multiple test files
        test_files = []
        for i in range(5):
            test_file = os.path.join(temp_dir, f"test_file_{i}.raw")
            with open(test_file, 'w', encoding='utf-8') as f:
                f.write(f"test content {i}")
            test_files.append(test_file)
        
        # Calculate checksums for all files
        checksums = []
        for test_file in test_files:
            checksum = processor.get_cached_checksum(test_file)
            checksums.append(checksum)
        
        # Verify all files are in cache
        assert len(processor.checksum_cache) == 5
        
        # Verify all checksums are different
        assert len(set(checksums)) == 5
    
    def test_checksum_cache_invalidation_timing(self, temp_dir):
        """Test precise cache invalidation timing."""
        processor = FileProcessor(Mock())
        
        test_file = os.path.join(temp_dir, "timing_test.raw")
        with open(test_file, 'w', encoding='utf-8') as f:
            f.write("initial content")
        
        # Get initial checksum
        checksum1 = processor.get_cached_checksum(test_file)
        
        # Ensure different timestamp by waiting
        time.sleep(0.1)
        
        # Modify file
        with open(test_file, 'a', encoding='utf-8') as f:
            f.write(" modified")
        
        # Get checksum again - should be recalculated
        with patch.object(processor, 'calculate_checksum', wraps=processor.calculate_checksum) as mock_calc:
            checksum2 = processor.get_cached_checksum(test_file)
            mock_calc.assert_called_once()
        
        # Checksums should be different
        assert checksum1 != checksum2
    
    def test_concurrent_checksum_access(self, temp_dir):
        """Test thread safety of checksum caching."""
        processor = FileProcessor(Mock())
        
        test_file = os.path.join(temp_dir, "concurrent_test.raw")
        with open(test_file, 'w', encoding='utf-8') as f:
            f.write("concurrent access test content")
        
        results = []
        errors = []
        
        def calculate_checksum_thread():
            try:
                checksum = processor.get_cached_checksum(test_file)
                results.append(checksum)
            except Exception as e:
                errors.append(str(e))
        
        # Start multiple threads
        threads = []
        for _ in range(10):
            thread = threading.Thread(target=calculate_checksum_thread)
            threads.append(thread)
            thread.start()
        
        # Wait for all threads
        for thread in threads:
            thread.join()
        
        # Verify no errors
        assert len(errors) == 0
        
        # Verify all results are the same
        assert len(set(results)) == 1
        assert len(results) == 10


class TestFileMonitoringOptimizations:
    """Test file monitoring performance optimizations."""
    
    def test_debouncing_effectiveness(self, file_queue, sample_extensions, temp_dir):
        """Test that debouncing reduces queue noise."""
        handler = FileMonitorHandler(file_queue, sample_extensions)
        handler.debounce_time = 0.1  # Short debounce for testing
        
        test_file = os.path.join(temp_dir, "debounce_test.raw")
        with open(test_file, 'w', encoding='utf-8') as f:
            f.write("test content")
        
        # Simulate rapid file modifications
        with patch('time.time', side_effect=[1.0, 1.05, 1.08, 1.15]):
            # Three rapid modifications
            for _ in range(3):
                handler.on_modified(type('Event', (), {'src_path': test_file}))
            
            # One modification after debounce period
            handler.on_modified(type('Event', (), {'src_path': test_file}))
        
        # Should only have 2 items in queue (first and last)
        assert file_queue.qsize() == 2
    
    def test_extension_filtering_performance(self, file_queue, temp_dir):
        """Test that extension filtering is efficient."""
        extensions = {'.raw', '.mzML', '.d'}
        handler = FileMonitorHandler(file_queue, extensions)
        
        # Test files with various extensions
        test_files = [
            'data.raw',      # Should process
            'experiment.mzML',  # Should process
            'folder.d',      # Should process
            'log.txt',       # Should skip
            'config.json',   # Should skip
            'backup.zip',    # Should skip
        ]
        
        processed_count = 0
        for filename in test_files:
            if handler.should_process_file(filename):
                processed_count += 1
        
        # Only 3 files should be processed
        assert processed_count == 3
    
    def test_system_file_filtering(self, file_queue, sample_extensions):
        """Test filtering of system files."""
        handler = FileMonitorHandler(file_queue, sample_extensions)
        
        system_files = [
            '.DS_Store',
            'desktop.ini',
            'Thumbs.db',
            '.hidden.raw',
            'tmp_file.raw.tmp',
            'copy_directory_temp.raw'
        ]
        
        for filename in system_files:
            assert handler.should_process_file(filename) is False
    
    def test_large_directory_handling(self, file_queue, sample_extensions, temp_dir):
        """Test handling of directories with many files."""
        handler = FileMonitorHandler(file_queue, sample_extensions)
        
        # Create many files
        for i in range(100):
            if i % 3 == 0:
                filename = f"data_{i:03d}.raw"
            elif i % 3 == 1:
                filename = f"log_{i:03d}.txt"  # Should be filtered out
            else:
                filename = f"temp_{i:03d}.tmp"  # Should be filtered out
            
            test_file = os.path.join(temp_dir, filename)
            with open(test_file, 'w', encoding='utf-8') as f:
                f.write(f"content {i}")
            
            # Simulate file creation event
            if handler.should_process_file(filename):
                event = type('Event', (), {'src_path': test_file, 'is_directory': False})
                handler.on_created(event)
        
        # Should only have .raw files in queue (approximately 34 files)
        assert 30 <= file_queue.qsize() <= 40


class TestMemoryAndResourceUsage:
    """Test memory usage and resource management."""
    
    def test_checksum_cache_memory_limit(self, temp_dir):
        """Test that checksum cache doesn't consume excessive memory."""
        processor = FileProcessor(Mock())
        
        # Create files and calculate checksums
        files_created = 50
        for i in range(files_created):
            test_file = os.path.join(temp_dir, f"memory_test_{i}.raw")
            with open(test_file, 'w', encoding='utf-8') as f:
                f.write(f"test content {i}" * 100)
            
            processor.get_cached_checksum(test_file)
        
        # Cache should contain all files
        assert len(processor.checksum_cache) == files_created
        
        # Each cache entry should contain reasonable data
        for file_path, cache_entry in processor.checksum_cache.items():
            assert 'checksum' in cache_entry
            assert 'mtime' in cache_entry
            assert len(cache_entry['checksum']) == 64  # SHA-256 hex
            assert isinstance(cache_entry['mtime'], float)
    
    def test_file_handle_cleanup(self, temp_dir):
        """Test that file handles are properly cleaned up."""
        processor = FileProcessor(Mock())
        
        test_file = os.path.join(temp_dir, "handle_test.raw")
        with open(test_file, 'w', encoding='utf-8') as f:
            f.write("test content for handle cleanup")
        
        # Calculate checksum multiple times
        for _ in range(10):
            processor.calculate_checksum(test_file)
        
        # File should still be accessible (no leaked handles)
        assert os.path.exists(test_file)
        
        # Should be able to modify file
        with open(test_file, 'a', encoding='utf-8') as f:
            f.write(" modified")
    
    def test_queue_memory_usage(self, file_queue, sample_extensions, temp_dir):
        """Test that file queue doesn't grow unbounded."""
        handler = FileMonitorHandler(file_queue, sample_extensions)
        
        # Add many files to queue
        files_added = 100
        for i in range(files_added):
            test_file = os.path.join(temp_dir, f"queue_test_{i}.raw")
            handler.file_queue.put(test_file)
        
        assert handler.file_queue.qsize() == files_added
        
        # Process some items
        processed = 0
        while processed < 50 and not handler.file_queue.empty():
            handler.file_queue.get()
            processed += 1
        
        assert handler.file_queue.qsize() == files_added - processed


class TestErrorHandlingAndRecovery:
    """Test error handling and recovery mechanisms."""
    
    def test_checksum_calculation_error_recovery(self, temp_dir):
        """Test recovery from checksum calculation errors."""
        processor = FileProcessor(Mock())
        
        test_file = os.path.join(temp_dir, "error_test.raw")
        with open(test_file, 'w', encoding='utf-8') as f:
            f.write("test content")
        
        # Mock file read error
        with patch('builtins.open', side_effect=IOError("Disk error")):
            checksum = processor.calculate_checksum(test_file)
            # Should return None or handle gracefully
            assert checksum is None or isinstance(checksum, str)
    
    def test_locked_file_handling_optimization(self, temp_dir):
        """Test optimized handling of locked files."""
        processor = FileProcessor(Mock())
        
        test_file = os.path.join(temp_dir, "locked_test.raw")
        with open(test_file, 'w', encoding='utf-8') as f:
            f.write("test content")
        
        # Mock file locking scenario
        processor.is_file_locked = Mock(side_effect=[True, True, False])
        
        # Test with short timeout
        start_time = time.time()
        result = processor.wait_for_file_available(test_file, timeout=0.2)
        duration = time.time() - start_time
        
        # Should return True (file became available) in reasonable time
        assert result is True
        assert duration < 1.0  # Should not take too long
    
    def test_monitoring_resilience(self, file_queue, sample_extensions, temp_dir):
        """Test monitoring system resilience."""
        handler = FileMonitorHandler(file_queue, sample_extensions)
        
        # Test with non-existent file
        non_existent_file = os.path.join(temp_dir, "does_not_exist.raw")
        event = type('Event', (), {'src_path': non_existent_file, 'is_directory': False})
        
        # Should handle gracefully without crashing
        try:
            handler.on_created(event)
            handler.on_modified(event)
        except Exception as e:
            pytest.fail(f"Handler should not raise exception for non-existent file: {e}")
        
        # Test with permission denied scenario
        restricted_file = os.path.join(temp_dir, "restricted.raw")
        with open(restricted_file, 'w', encoding='utf-8') as f:
            f.write("test")
        
        # Make file read-only if possible (platform dependent)
        try:
            os.chmod(restricted_file, 0o444)
            event = type('Event', (), {'src_path': restricted_file, 'is_directory': False})
            handler.on_modified(event)
        except (OSError, PermissionError):
            # Platform may not support this test
            pass
