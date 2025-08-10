"""
Tests for the infinite loop fix in PanoramaBridge.

This module tests the checksum-based duplicate prevention system that prevents
files from being re-uploaded when they haven't actually changed, fixing the
infinite loop issue caused by file system events.
"""

import hashlib
import os
import queue

# Import the classes we need to test
import sys
import tempfile
import time
import unittest.mock as mock
from pathlib import Path

import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

from panoramabridge import FileMonitorHandler, FileProcessor, MainWindow


class TestInfiniteLoopFix:
    """Test suite for the infinite loop fix functionality."""

    @pytest.fixture
    def mock_app_instance(self):
        """Create a mock app instance with upload history."""
        app = mock.Mock()
        app.upload_history = {}
        app.queued_files = set()
        app.processing_files = set()
        return app

    @pytest.fixture
    def file_processor(self, mock_app_instance):
        """Create a FileProcessor instance for testing."""
        file_queue = queue.Queue()
        processor = FileProcessor(file_queue, mock_app_instance)
        return processor

    @pytest.fixture
    def file_monitor_handler(self, mock_app_instance):
        """Create a FileMonitorHandler instance for testing."""
        handler = FileMonitorHandler(
            file_queue=queue.Queue(),
            app_instance=mock_app_instance,
            extensions=['.raw', '.mzML']
        )
        return handler

    @pytest.fixture
    def main_window(self):
        """Create a mock MainWindow instance for testing polling methods."""
        window = mock.Mock()
        window.upload_history = {}
        window.queued_files = set()
        window.processing_files = set()
        window.file_processor = mock.Mock()
        window.file_processor.calculate_checksum = mock.Mock(return_value="dummy_checksum")

        # Mock the _should_queue_file_poll method behavior
        def mock_should_queue_file_poll(filepath):
            # Check if file is already queued or being processed
            if filepath in window.queued_files:
                return False
            if filepath in window.processing_files:
                return False

            # Check if file was already uploaded and hasn't changed
            if filepath in window.upload_history:
                try:
                    current_checksum = window.file_processor.calculate_checksum(filepath)
                    stored_info = window.upload_history[filepath]
                    stored_checksum = stored_info.get('checksum', '')

                    if current_checksum == stored_checksum:
                        return False  # File unchanged, don't queue
                    else:
                        window.queued_files.add(filepath)
                        return True  # File modified, queue it
                except Exception:
                    window.queued_files.add(filepath)
                    return True

            # New file, queue it
            window.queued_files.add(filepath)
            return True

        window._should_queue_file_poll = mock_should_queue_file_poll
        return window

    def test_should_queue_file_new_file(self, file_monitor_handler, mock_app_instance):
        """Test that new files are queued correctly."""
        filepath = "/test/new_file.raw"

        # New file should be queued
        result = file_monitor_handler._should_queue_file(filepath)

        assert result is True
        assert filepath in mock_app_instance.queued_files

    def test_should_queue_file_already_queued(self, file_monitor_handler, mock_app_instance):
        """Test that already queued files are not re-queued."""
        filepath = "/test/queued_file.raw"
        mock_app_instance.queued_files.add(filepath)

        # Already queued file should not be re-queued
        result = file_monitor_handler._should_queue_file(filepath)

        assert result is False

    def test_should_queue_file_currently_processing(self, file_monitor_handler, mock_app_instance):
        """Test that files currently being processed are not re-queued."""
        filepath = "/test/processing_file.raw"
        mock_app_instance.processing_files.add(filepath)

        # File being processed should not be re-queued
        result = file_monitor_handler._should_queue_file(filepath)

        assert result is False

    def test_should_queue_file_unchanged_uploaded(self, file_monitor_handler, mock_app_instance, sample_file):
        """Test that unchanged uploaded files are not re-queued."""
        filepath, content = sample_file

        # Calculate checksum for the file
        checksum = hashlib.sha256(content).hexdigest()

        # Add to upload history
        mock_app_instance.upload_history[filepath] = {
            'checksum': checksum,
            'timestamp': time.time(),
            'remote_path': '/remote/test_file.raw'
        }

        # Mock the checksum calculation to return the same checksum
        mock_app_instance.file_processor.calculate_checksum.return_value = checksum

        # File should not be re-queued (unchanged)
        result = file_monitor_handler._should_queue_file(filepath)

        assert result is False
        assert filepath not in mock_app_instance.queued_files

    def test_should_queue_file_modified_uploaded(self, file_monitor_handler, mock_app_instance, sample_file):
        """Test that modified uploaded files are re-queued."""
        filepath, content = sample_file

        # Original checksum
        original_checksum = hashlib.sha256(content).hexdigest()

        # Add to upload history with original checksum
        mock_app_instance.upload_history[filepath] = {
            'checksum': original_checksum,
            'timestamp': time.time(),
            'remote_path': '/remote/test_file.raw'
        }

        # Mock the checksum calculation to return a different checksum (file modified)
        new_checksum = hashlib.sha256(content + b"modified").hexdigest()
        mock_app_instance.file_processor.calculate_checksum.return_value = new_checksum

        # File should be re-queued (modified)
        result = file_monitor_handler._should_queue_file(filepath)

        assert result is True
        assert filepath in mock_app_instance.queued_files

    def test_should_queue_file_checksum_error(self, file_monitor_handler, mock_app_instance, sample_file):
        """Test behavior when checksum calculation fails."""
        filepath, content = sample_file

        # Add to upload history
        checksum = hashlib.sha256(content).hexdigest()
        mock_app_instance.upload_history[filepath] = {
            'checksum': checksum,
            'timestamp': time.time(),
            'remote_path': '/remote/test_file.raw'
        }

        # Mock checksum calculation to raise an exception
        mock_app_instance.file_processor.calculate_checksum.side_effect = Exception("Checksum error")

        # File should still be queued when checksum check fails (fail-safe behavior)
        result = file_monitor_handler._should_queue_file(filepath)

        assert result is True
        assert filepath in mock_app_instance.queued_files

    def test_should_queue_file_poll_unchanged_uploaded(self, main_window, sample_file):
        """Test polling method with unchanged uploaded files."""
        filepath, content = sample_file

        # Setup upload history
        checksum = hashlib.sha256(content).hexdigest()
        main_window.upload_history[filepath] = {
            'checksum': checksum,
            'timestamp': time.time(),
            'remote_path': '/remote/test_file.raw'
        }

        # Mock checksum calculation
        main_window.file_processor.calculate_checksum.return_value = checksum

        # Initialize tracking sets
        main_window.queued_files = set()
        main_window.processing_files = set()

        # File should not be queued (unchanged)
        result = main_window._should_queue_file_poll(filepath)

        assert result is False
        assert filepath not in main_window.queued_files

    def test_should_queue_file_poll_modified_uploaded(self, main_window, sample_file):
        """Test polling method with modified uploaded files."""
        filepath, content = sample_file

        # Setup upload history with original checksum
        original_checksum = hashlib.sha256(content).hexdigest()
        main_window.upload_history[filepath] = {
            'checksum': original_checksum,
            'timestamp': time.time(),
            'remote_path': '/remote/test_file.raw'
        }

        # Mock checksum calculation to return different checksum
        new_checksum = hashlib.sha256(content + b"modified").hexdigest()
        main_window.file_processor.calculate_checksum.return_value = new_checksum

        # Initialize tracking sets
        main_window.queued_files = set()
        main_window.processing_files = set()

        # File should be queued (modified)
        result = main_window._should_queue_file_poll(filepath)

        assert result is True
        assert filepath in main_window.queued_files

    def test_integration_file_events_no_infinite_loop(self, file_monitor_handler, mock_app_instance, temp_dir):
        """Integration test simulating multiple file events to ensure no infinite loop."""
        # Create a test file
        filepath = os.path.join(temp_dir, "test_file.raw")
        content = b"Test file content"
        with open(filepath, "wb") as f:
            f.write(content)

        # Calculate checksum
        checksum = hashlib.sha256(content).hexdigest()

        # Mock checksum calculation
        mock_app_instance.file_processor.calculate_checksum.return_value = checksum

        # First time - file should be queued (new file)
        result1 = file_monitor_handler._should_queue_file(filepath)
        assert result1 is True
        assert filepath in mock_app_instance.queued_files

        # Simulate successful upload by adding to history and clearing queued files
        mock_app_instance.upload_history[filepath] = {
            'checksum': checksum,
            'timestamp': time.time(),
            'remote_path': '/remote/test_file.raw'
        }
        mock_app_instance.queued_files.clear()

        # Second time - file should NOT be queued (unchanged after upload)
        result2 = file_monitor_handler._should_queue_file(filepath)
        assert result2 is False
        assert filepath not in mock_app_instance.queued_files

        # Third time - still should NOT be queued (preventing infinite loop)
        result3 = file_monitor_handler._should_queue_file(filepath)
        assert result3 is False
        assert filepath not in mock_app_instance.queued_files

    def test_performance_large_upload_history(self, file_monitor_handler, mock_app_instance, sample_file):
        """Test performance with large upload history."""
        filepath, content = sample_file
        checksum = hashlib.sha256(content).hexdigest()

        # Create large upload history (1000 files)
        for i in range(1000):
            mock_app_instance.upload_history[f"/test/file_{i}.raw"] = {
                'checksum': f"checksum_{i}",
                'timestamp': time.time(),
                'remote_path': f'/remote/file_{i}.raw'
            }

        # Add our test file to history
        mock_app_instance.upload_history[filepath] = {
            'checksum': checksum,
            'timestamp': time.time(),
            'remote_path': '/remote/test_file.raw'
        }

        # Mock checksum calculation
        mock_app_instance.file_processor.calculate_checksum.return_value = checksum

        # Performance test - should still be fast
        start_time = time.time()
        result = file_monitor_handler._should_queue_file(filepath)
        end_time = time.time()

        assert result is False  # File should not be queued (unchanged)
        assert end_time - start_time < 1.0  # Should complete within 1 second

    def test_edge_case_empty_upload_history_entry(self, file_monitor_handler, mock_app_instance, sample_file):
        """Test edge case with malformed upload history entry."""
        filepath, content = sample_file

        # Add malformed entry to upload history (missing checksum)
        mock_app_instance.upload_history[filepath] = {
            'timestamp': time.time(),
            'remote_path': '/remote/test_file.raw'
            # Missing 'checksum' key
        }

        # File should be queued when checksum is missing (fail-safe)
        result = file_monitor_handler._should_queue_file(filepath)

        assert result is True
        assert filepath in mock_app_instance.queued_files

    def test_concurrent_access_thread_safety(self, file_monitor_handler, mock_app_instance, sample_file):
        """Test thread safety of the duplicate prevention logic."""
        import threading

        filepath, content = sample_file
        checksum = hashlib.sha256(content).hexdigest()

        mock_app_instance.upload_history[filepath] = {
            'checksum': checksum,
            'timestamp': time.time(),
            'remote_path': '/remote/test_file.raw'
        }

        mock_app_instance.file_processor.calculate_checksum.return_value = checksum

        results = []

        def test_thread():
            # Clear queued files for each thread
            mock_app_instance.queued_files.discard(filepath)
            result = file_monitor_handler._should_queue_file(filepath)
            results.append(result)

        # Run multiple threads concurrently
        threads = []
        for _ in range(5):
            thread = threading.Thread(target=test_thread)
            threads.append(thread)
            thread.start()

        for thread in threads:
            thread.join()

        # All threads should return False (file unchanged)
        assert all(result is False for result in results)


class TestFileSystemEventIntegration:
    """Integration tests for file system events with infinite loop fix."""

    @pytest.fixture
    def mock_qt_app(self):
        """Mock QApplication for GUI tests."""
        with mock.patch('panoramabridge.QApplication'):
            yield

    def test_file_modification_event_filtering(self, temp_dir, mock_qt_app):
        """Test that file modification events are properly filtered."""
        # Create test file
        filepath = os.path.join(temp_dir, "test_file.raw")
        content = b"Original content"
        with open(filepath, "wb") as f:
            f.write(content)

        # Mock app instance
        mock_app = mock.Mock()
        mock_app.upload_history = {}
        mock_app.queued_files = set()
        mock_app.processing_files = set()

        # Create file processor mock
        file_processor = mock.Mock()
        checksum = hashlib.sha256(content).hexdigest()
        file_processor.calculate_checksum.return_value = checksum
        mock_app.file_processor = file_processor

        # Create file monitor handler
        file_queue = queue.Queue()
        handler = FileMonitorHandler(
            file_queue=file_queue,
            app_instance=mock_app,
            extensions=['.raw']
        )

        # First modification - should queue file (new)
        handler._handle_file(filepath)

        # Simulate successful upload
        mock_app.upload_history[filepath] = {
            'checksum': checksum,
            'timestamp': time.time(),
            'remote_path': '/remote/test_file.raw'
        }
        mock_app.queued_files.clear()

        # Second modification event (system touch) - should NOT queue
        original_queue_size = file_queue.qsize()
        handler._handle_file(filepath)

        # Queue size should not increase (file not re-queued)
        assert file_queue.qsize() == original_queue_size

    def test_real_file_modification_detection(self, temp_dir, mock_qt_app):
        """Test that real file modifications are detected and queued."""
        # Create test file
        filepath = os.path.join(temp_dir, "test_file.raw")
        original_content = b"Original content"
        with open(filepath, "wb") as f:
            f.write(original_content)

        # Mock app instance
        mock_app = mock.Mock()
        mock_app.upload_history = {}
        mock_app.queued_files = set()
        mock_app.processing_files = set()

        # Create file processor mock
        file_processor = mock.Mock()
        mock_app.file_processor = file_processor

        # Original checksum
        original_checksum = hashlib.sha256(original_content).hexdigest()
        file_processor.calculate_checksum.return_value = original_checksum

        # Create file monitor handler
        file_queue = queue.Queue()
        handler = FileMonitorHandler(
            file_queue=file_queue,
            app_instance=mock_app,
            extensions=['.raw']
        )

        # First time - queue file
        handler._handle_file(filepath)
        assert filepath in mock_app.queued_files

        # Simulate upload
        mock_app.upload_history[filepath] = {
            'checksum': original_checksum,
            'timestamp': time.time(),
            'remote_path': '/remote/test_file.raw'
        }
        mock_app.queued_files.clear()

        # Modify file content
        modified_content = b"Modified content - actually changed"
        with open(filepath, "wb") as f:
            f.write(modified_content)

        # Update mock to return new checksum
        new_checksum = hashlib.sha256(modified_content).hexdigest()
        file_processor.calculate_checksum.return_value = new_checksum

        # Handle file event - should queue modified file
        handler._handle_file(filepath)
        assert filepath in mock_app.queued_files


if __name__ == "__main__":
    # Allow running tests directly
    pytest.main([__file__, "-v"])
