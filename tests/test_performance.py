"""
Performance and optimization tests for PanoramaBridge.
Tests only functionality that actually exists in the implementation.
"""

import os

# Import the module under test
import sys
import tempfile
import threading
import time
from unittest.mock import MagicMock, Mock, patch

import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from panoramabridge import FileMonitorHandler, FileProcessor, MainWindow


class TestChecksumCaching:
    """Test checksum caching performance and correctness."""

    def test_checksum_calculation_performance(self, temp_dir, mock_app_instance, file_queue):
        """Test that checksum calculation works and is performant."""
        processor = FileProcessor(file_queue, mock_app_instance)

        # Create test file
        test_file = os.path.join(temp_dir, "performance_test.raw")
        with open(test_file, "w", encoding="utf-8") as f:
            f.write("test content for performance testing" * 1000)

        # Test checksum calculation
        start_time = time.time()
        checksum1 = processor.calculate_checksum(test_file)
        first_duration = time.time() - start_time

        # Second calculation
        start_time = time.time()
        checksum2 = processor.calculate_checksum(test_file)
        second_duration = time.time() - start_time

        # Verify same checksum
        assert checksum1 == checksum2
        assert len(checksum1) == 64  # SHA256 hash

        # Both calculations should be reasonably fast
        assert first_duration < 5.0
        assert second_duration < 5.0


class TestFileMonitoringPerformance:
    """Test file monitoring performance and resource usage."""

    def test_file_handler_efficiency(self, temp_dir, file_queue, mock_app_instance):
        """Test that file handling is efficient."""
        extensions = [".raw", ".mzML", ".txt"]
        monitor = FileMonitorHandler(extensions, file_queue, app_instance=mock_app_instance)

        # Create multiple test files
        test_files = []
        for i in range(10):
            test_file = os.path.join(temp_dir, f"test_{i}.raw")
            with open(test_file, "w", encoding="utf-8") as f:
                f.write(f"Test content {i}")
            test_files.append(test_file)

        # Measure handling time
        start_time = time.time()
        for test_file in test_files:
            monitor._handle_file(test_file)
        handling_duration = time.time() - start_time

        # Should handle files quickly
        assert handling_duration < 1.0  # Less than 1 second for 10 files

        # In test environment, files are processed immediately and queued
        # Check that all files were queued
        queued_files = []
        while not file_queue.empty():
            try:
                queued_files.append(file_queue.get_nowait())
            except Exception:
                break

        assert len(queued_files) == 10, f"Expected 10 files to be queued, got {len(queued_files)}"
        for test_file in test_files:
            assert test_file in queued_files, f"File {test_file} should have been queued"


class TestFileConflictResolution:
    """Test file conflict detection and resolution mechanisms."""

    def test_file_comparison_identical_files(self, temp_dir, file_queue, mock_app_instance):
        """Test that identical files are detected correctly using verify_remote_file_integrity."""
        # Create test file
        test_file = os.path.join(temp_dir, "identical_test.raw")
        with open(test_file, "w", encoding="utf-8") as f:
            f.write("identical content for testing")

        # Calculate checksum using FileProcessor
        processor = FileProcessor(file_queue, mock_app_instance)
        local_checksum = processor.calculate_checksum(test_file)
        remote_path = "/remote/path/identical_test.raw"

        # Create a mock MainWindow and bind the actual method
        mock_window = Mock()
        mock_window.verify_remote_file_integrity = MainWindow.verify_remote_file_integrity.__get__(mock_window, MainWindow)

        # Mock WebDAV client methods
        mock_webdav = Mock()
        mock_window.webdav_client = mock_webdav

        # Mock get_file_info to return file exists with matching size
        mock_webdav.get_file_info.side_effect = lambda path: (
            {
                "exists": True,
                "size": os.path.getsize(test_file),
                "modified": os.path.getmtime(test_file),
            } if path == remote_path else
            {
                "exists": True,
                "size": len(local_checksum.encode('utf-8')),
            }  # checksum file exists
        )

        # Mock download_file_head to return matching checksum
        mock_webdav.download_file_head.return_value = local_checksum.encode('utf-8')

        # Test verification
        is_intact, reason = mock_window.verify_remote_file_integrity(test_file, remote_path, local_checksum)

        # Should detect as identical with checksum verification
        assert is_intact is True
        assert reason == "Size + checksum verified"

    def test_file_comparison_different_files(self, temp_dir, file_queue, mock_app_instance):
        """Test that different files are detected correctly using verify_remote_file_integrity."""
        # Create test file
        test_file = os.path.join(temp_dir, "different_test.raw")
        with open(test_file, "w", encoding="utf-8") as f:
            f.write("local content for testing")

        # Calculate checksum using FileProcessor
        processor = FileProcessor(file_queue, mock_app_instance)
        local_checksum = processor.calculate_checksum(test_file)
        remote_path = "/remote/path/different_test.raw"

        # Create a mock MainWindow and bind the actual method
        mock_window = Mock()
        mock_window.verify_remote_file_integrity = MainWindow.verify_remote_file_integrity.__get__(mock_window, MainWindow)

        # Mock WebDAV client methods
        mock_webdav = Mock()
        mock_window.webdav_client = mock_webdav

        # Create different checksum with same length
        different_checksum = "different" + local_checksum[9:]

        # Mock get_file_info to return file exists with matching size
        mock_webdav.get_file_info.side_effect = lambda path: (
            {
                "exists": True,
                "size": os.path.getsize(test_file),
                "modified": os.path.getmtime(test_file) - 100,
            } if path == remote_path else
            {
                "exists": True,
                "size": len(different_checksum.encode('utf-8')),
            }  # checksum file exists
        )

        # Mock download_file_head to return different checksum
        mock_webdav.download_file_head.return_value = different_checksum.encode('utf-8')

        # Test verification
        is_intact, reason = mock_window.verify_remote_file_integrity(test_file, remote_path, local_checksum)

        # Should detect as different due to checksum mismatch
        assert is_intact is False
        assert "checksum mismatch" in reason

    def test_file_comparison_new_file(self, temp_dir, file_queue, mock_app_instance):
        """Test that new files (not existing remotely) are handled correctly using verify_remote_file_integrity."""
        # Create test file
        test_file = os.path.join(temp_dir, "new_test.raw")
        with open(test_file, "w", encoding="utf-8") as f:
            f.write("new file content")

        # Calculate checksum using FileProcessor
        processor = FileProcessor(file_queue, mock_app_instance)
        local_checksum = processor.calculate_checksum(test_file)
        remote_path = "/remote/path/new_test.raw"

        # Create a mock MainWindow and bind the actual method
        mock_window = Mock()
        mock_window.verify_remote_file_integrity = MainWindow.verify_remote_file_integrity.__get__(mock_window, MainWindow)

        # Mock WebDAV client methods
        mock_webdav = Mock()
        mock_window.webdav_client = mock_webdav

        # Mock get_file_info to return file doesn't exist
        mock_webdav.get_file_info.return_value = {"exists": False}

        # Test verification
        is_intact, reason = mock_window.verify_remote_file_integrity(test_file, remote_path, local_checksum)

        # Should detect as not intact because remote file doesn't exist
        assert is_intact is False
        assert reason == "remote file not found"


class TestErrorHandling:
    """Test error handling and recovery mechanisms."""

    def test_checksum_calculation_missing_file(self, mock_app_instance, file_queue):
        """Test behavior when calculating checksum of missing file."""
        processor = FileProcessor(file_queue, mock_app_instance)

        nonexistent_file = "/path/to/nonexistent/file.raw"

        # Should raise an exception for missing file
        with pytest.raises((FileNotFoundError, OSError)):
            processor.calculate_checksum(nonexistent_file)

    def test_checksum_calculation_with_permission_error(
        self, temp_dir, mock_app_instance, file_queue
    ):
        """Test recovery from checksum calculation permission errors."""
        processor = FileProcessor(file_queue, mock_app_instance)

        test_file = os.path.join(temp_dir, "permission_test.raw")
        with open(test_file, "w", encoding="utf-8") as f:
            f.write("test content")

        # Mock file read permission error
        with patch("builtins.open", side_effect=PermissionError("Permission denied")):
            with pytest.raises(PermissionError):
                processor.calculate_checksum(test_file)
