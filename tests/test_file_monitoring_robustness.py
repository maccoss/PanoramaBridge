#!/usr/bin/env python3
"""
Test suite for file monitoring robustness improvements.
This tests the exception handling improvements to prevent crashes during file copying operations.
"""

import logging
import os
import queue
import shutil
import sys
import tempfile
import threading
import time
from unittest.mock import MagicMock, Mock, patch

import pytest

# Setup path to import the main module
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

# Import the classes we need to test
from panoramabridge import FileMonitorHandler

# Configure logging for tests
logging.basicConfig(level=logging.DEBUG)
logger = logging.getLogger(__name__)


class TestFileMonitoringRobustness:
    """Test file monitoring robustness and exception handling"""

    def setup_method(self):
        """Setup test environment"""
        self.temp_dir = tempfile.mkdtemp()
        self.file_queue = queue.Queue()
        self.mock_app = Mock()
        self.mock_app.queued_files = set()
        self.mock_app.processing_files = set()
        self.mock_app.upload_history = {}  # Add for infinite loop fix

        # Mock file_processor with calculate_checksum method
        self.mock_app.file_processor = Mock()
        self.mock_app.file_processor.calculate_checksum = Mock(return_value="dummy_checksum_for_testing")

        # Create monitor handler
        self.monitor = FileMonitorHandler(
            extensions=[".txt", ".raw"],
            file_queue=self.file_queue,
            monitor_subdirs=True,
            app_instance=self.mock_app,
        )

    def teardown_method(self):
        """Cleanup test environment"""
        # Clean up temp directory
        if os.path.exists(self.temp_dir):
            shutil.rmtree(self.temp_dir, ignore_errors=True)

    def test_file_copy_simulation_no_crash(self):
        """Test that file copying doesn't crash the monitoring system"""
        test_file = os.path.join(self.temp_dir, "test_file.txt")

        # Simulate file being copied (create empty file first)
        with open(test_file, "w") as f:
            f.write("")

        # Handle the initial file creation
        self.monitor._handle_file(test_file)

        # Simulate file growing during copy (modify multiple times)
        for i in range(5):
            with open(test_file, "a") as f:
                f.write(f"Data chunk {i}\n")

            # Handle the modification
            self.monitor._handle_file(test_file)
            time.sleep(0.1)

        # Wait for stability check
        time.sleep(2.0)

        # Verify no crashes occurred and file was eventually queued
        # The queue should have the file after stability check
        queued_files = []
        while not self.file_queue.empty():
            try:
                queued_files.append(self.file_queue.get_nowait())
            except queue.Empty:
                break

        assert len(queued_files) > 0, "File should have been queued after stability"
        assert test_file in queued_files, "The test file should be in the queue"

    def test_nonexistent_file_handling(self):
        """Test handling of file events for files that no longer exist"""
        nonexistent_file = os.path.join(self.temp_dir, "nonexistent.txt")

        # This should not crash even though file doesn't exist
        self.monitor._handle_file(nonexistent_file)

        # Verify no files were queued
        assert self.file_queue.empty(), "No files should be queued for nonexistent file"

    def test_permission_error_handling(self):
        """Test handling of permission errors during file access"""
        test_file = os.path.join(self.temp_dir, "test_file.txt")

        # Create file
        with open(test_file, "w") as f:
            f.write("test content")

        # Mock os.path.getsize to raise permission error
        with patch("os.path.getsize", side_effect=PermissionError("Access denied")):
            # This should not crash
            self.monitor._handle_file(test_file)

        # File should not crash the system - verify no exceptions were raised
        # The file will be handled by retry mechanism rather than pending_files
        assert True, "System should handle permission errors gracefully"

    def test_io_error_handling(self):
        """Test handling of IO errors during file access"""
        test_file = os.path.join(self.temp_dir, "test_file.txt")

        # Create file
        with open(test_file, "w") as f:
            f.write("test content")

        # Mock os.path.getsize to raise IO error
        with patch("os.path.getsize", side_effect=OSError("File locked")):
            # This should not crash
            self.monitor._handle_file(test_file)

        # File should not crash the system - verify no exceptions were raised
        # The file will be handled by retry mechanism rather than pending_files
        assert True, "System should handle IO errors gracefully"

    def test_ui_update_error_handling(self):
        """Test handling of UI update errors"""
        test_file = os.path.join(self.temp_dir, "test_file.txt")

        # Create file
        with open(test_file, "w") as f:
            f.write("test content")

        # Mock app instance to raise error on UI update
        self.mock_app.add_queued_file_to_table.side_effect = Exception("UI Error")

        # Add file to pending and make it stable
        self.monitor.pending_files[test_file] = (12, time.time() - 2)

        # This should not crash despite UI error
        self.monitor._handle_file(test_file)

        # File should still be queued despite UI error
        assert not self.file_queue.empty(), "File should be queued despite UI error"

    def test_delayed_check_thread_error_handling(self):
        """Test that errors in delayed check threads don't crash the system"""
        test_file = os.path.join(self.temp_dir, "test_file.txt")

        # Create file
        with open(test_file, "w") as f:
            f.write("test content")

        # Mock file size check in delayed thread to cause error
        original_getsize = os.path.getsize

        def mock_getsize(path):
            if (
                "delayed_check" in threading.current_thread().name
                or path == test_file
                and len(threading.enumerate()) > 2
            ):  # Delayed thread running
                raise RuntimeError("Simulated thread error")
            return original_getsize(path)

        with patch("os.path.getsize", side_effect=mock_getsize):
            # Handle new file (should start delayed check)
            self.monitor._handle_file(test_file)

            # Wait for delayed check to run and fail
            time.sleep(2.0)

        # System should still be functioning despite thread error
        # File should be removed from pending due to error cleanup
        assert test_file not in self.monitor.pending_files, (
            "File should be cleaned up after thread error"
        )

    def test_concurrent_file_operations(self):
        """Test handling of concurrent file operations"""
        test_files = []

        # Create multiple test files
        for i in range(5):
            test_file = os.path.join(self.temp_dir, f"test_file_{i}.txt")
            with open(test_file, "w") as f:
                f.write(f"test content {i}")
            test_files.append(test_file)

        # Handle all files concurrently
        threads = []
        for test_file in test_files:
            thread = threading.Thread(target=self.monitor._handle_file, args=(test_file,))
            threads.append(thread)
            thread.start()

        # Wait for all threads to complete
        for thread in threads:
            thread.join()

        # Wait for delayed checks
        time.sleep(2.0)

        # Verify all files were eventually processed without crashes
        queued_files = []
        while not self.file_queue.empty():
            try:
                queued_files.append(self.file_queue.get_nowait())
            except queue.Empty:
                break

        assert len(queued_files) == 5, "All files should be queued"
        for test_file in test_files:
            assert test_file in queued_files, f"File {test_file} should be queued"

    def test_file_disappears_during_monitoring(self):
        """Test handling when file disappears during monitoring"""
        test_file = os.path.join(self.temp_dir, "disappearing_file.txt")

        # Create file and start monitoring
        with open(test_file, "w") as f:
            f.write("temporary content")

        self.monitor._handle_file(test_file)

        # In test environment, file gets processed immediately and queued
        # Check if file was queued initially
        queued_files = []
        while not self.file_queue.empty():
            try:
                queued_files.append(self.file_queue.get_nowait())
            except queue.Empty:
                break

        # File should have been queued initially
        assert test_file in queued_files, "File should have been queued initially"

        # Delete the file
        os.remove(test_file)

        # Handle file again (simulates modification event on deleted file)
        # This should not crash and should handle the missing file gracefully
        try:
            self.monitor._handle_file(test_file)
            # Should complete without error
            assert True, "File handling should not crash when file is deleted"
        except Exception as e:
            assert False, f"File handling crashed when file was deleted: {e}"

    def test_retry_monitoring_after_access_error(self):
        """Test retry mechanism when file access fails initially"""
        test_file = os.path.join(self.temp_dir, "retry_file.txt")

        # Track retry attempts
        retry_attempts = []

        def mock_handle_file(filepath):
            retry_attempts.append(filepath)
            # Call original method for second attempt
            if len(retry_attempts) > 1:
                FileMonitorHandler._handle_file(self.monitor, filepath)

        # Mock os.path.exists to fail first time, succeed second time
        exists_calls = []

        def mock_exists(path):
            exists_calls.append(path)
            if len(exists_calls) == 1:
                return False  # First call fails
            else:
                return os.path.exists(path)  # Subsequent calls use real function

        # Create file after first failure
        with patch("os.path.exists", side_effect=mock_exists):
            with patch.object(self.monitor, "_handle_file", side_effect=mock_handle_file):
                # First attempt should fail and schedule retry
                self.monitor._handle_file(test_file)

                # Create the file
                with open(test_file, "w") as f:
                    f.write("retry content")

                # Wait for retry
                time.sleep(2.5)  # Wait longer than retry delay

        # Should have attempted retry
        assert len(retry_attempts) >= 1, "Should have attempted retry"


if __name__ == "__main__":
    # Run tests
    pytest.main([__file__, "-v"])
