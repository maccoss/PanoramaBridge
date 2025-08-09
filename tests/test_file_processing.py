"""
Tests for file monitoring and processing functionality.
"""

import os

# Import the module under test
import sys
import tempfile
import threading
import time
from pathlib import Path
from unittest.mock import MagicMock, Mock, patch

import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from panoramabridge import FileMonitorHandler, FileProcessor


class TestFileProcessor:
    """Test file processing functionality."""

    def test_init(self, mock_app_instance, file_queue):
        """Test FileProcessor initialization."""
        processor = FileProcessor(file_queue, mock_app_instance)

        assert processor.file_queue == file_queue
        assert processor.app_instance == mock_app_instance
        assert processor.webdav_client is None  # Set later via set_webdav_client
        assert processor.running is True

    def test_calculate_checksum(self, sample_file, mock_app_instance, file_queue):
        """Test checksum calculation."""
        file_path, expected_size = sample_file

        processor = FileProcessor(file_queue, mock_app_instance)
        checksum = processor.calculate_checksum(file_path)

        # Verify checksum is calculated correctly
        assert len(checksum) == 64  # SHA-256 hex digest
        assert isinstance(checksum, str)

    def test_calculate_checksum_large_file(self, large_sample_file, mock_app_instance, file_queue):
        """Test checksum calculation for large files."""
        file_path, expected_size = large_sample_file

        processor = FileProcessor(file_queue, mock_app_instance)
        checksum = processor.calculate_checksum(file_path)

        assert len(checksum) == 64
        assert isinstance(checksum, str)

    def test_checksum_caching(self, sample_file, mock_app_instance, file_queue):
        """Test checksum caching functionality."""
        file_path, expected_size = sample_file

        processor = FileProcessor(file_queue, mock_app_instance)

        # First call should calculate checksum and cache it
        checksum1 = processor.calculate_checksum(file_path)

        # Check that it was cached in app_instance
        assert hasattr(mock_app_instance, "local_checksum_cache")
        assert len(mock_app_instance.local_checksum_cache) > 0

        # Second call should use cached value
        checksum2 = processor.calculate_checksum(file_path)

        assert checksum1 == checksum2

    def test_set_webdav_client(self, mock_webdav_client, mock_app_instance, file_queue):
        """Test setting WebDAV client."""
        processor = FileProcessor(file_queue, mock_app_instance)

        processor.set_webdav_client(mock_webdav_client, "/remote/path")

        assert processor.webdav_client == mock_webdav_client
        assert processor.remote_base_path == "/remote/path"

    def test_set_local_base(self, mock_app_instance, file_queue):
        """Test setting local base path."""
        processor = FileProcessor(file_queue, mock_app_instance)

        processor.set_local_base("/local/path")

        assert processor.local_base_path == "/local/path"


class TestFileMonitorHandler:
    """Test file monitoring functionality."""

    def test_init(self, file_queue, mock_app_instance):
        """Test FileMonitorHandler initialization."""
        extensions = [".raw", ".txt"]
        monitor = FileMonitorHandler(
            extensions, file_queue, monitor_subdirs=True, app_instance=mock_app_instance
        )

        assert monitor.extensions == extensions
        assert monitor.file_queue == file_queue
        assert monitor.monitor_subdirs is True
        assert monitor.app_instance == mock_app_instance

    def test_file_event_handling(self, temp_dir, file_queue, mock_app_instance):
        """Test file event handling."""
        extensions = [".raw"]
        monitor = FileMonitorHandler(extensions, file_queue, app_instance=mock_app_instance)

        # Create a test file
        test_file = os.path.join(temp_dir, "test_file.raw")
        with open(test_file, "w", encoding="utf-8") as f:
            f.write("Test file content")

        # Mock the _should_queue_file method to return True
        monitor._should_queue_file = Mock(return_value=True)

        # Test file creation event
        event = Mock()
        event.is_directory = False
        event.src_path = test_file

        monitor.on_created(event)

        # In test environment, files are processed immediately, so check if file was queued
        # rather than checking pending_files
        queued_files = []
        while not file_queue.empty():
            try:
                queued_files.append(file_queue.get_nowait())
            except Exception:
                break

        assert test_file in queued_files, f"File {test_file} should have been queued"

    def test_extension_filtering_in_handle_file(self, temp_dir, file_queue, mock_app_instance):
        """Test that only files with specified extensions are handled."""
        extensions = [".raw"]
        monitor = FileMonitorHandler(extensions, file_queue, app_instance=mock_app_instance)

        # Create files with different extensions
        raw_file = os.path.join(temp_dir, "test.raw")
        txt_file = os.path.join(temp_dir, "test.txt")

        with open(raw_file, "w", encoding="utf-8") as f:
            f.write("Raw file")
        with open(txt_file, "w", encoding="utf-8") as f:
            f.write("Text file")

        # Handle the files directly
        monitor._handle_file(raw_file)
        monitor._handle_file(txt_file)

        # In test environment, files are processed immediately, so check if file was queued
        queued_files = []
        while not file_queue.empty():
            try:
                queued_files.append(file_queue.get_nowait())
            except Exception:
                break

        # Only the .raw file should be queued (txt file should be filtered out)
        assert raw_file in queued_files, f"Raw file {raw_file} should have been queued"
        assert txt_file not in queued_files, f"Text file {txt_file} should not have been queued"

    def test_duplicate_prevention(self, temp_dir, file_queue, mock_app_instance):
        """Test that duplicate files are not queued."""
        extensions = [".raw"]
        monitor = FileMonitorHandler(extensions, file_queue, app_instance=mock_app_instance)

        test_file = os.path.join(temp_dir, "test.raw")
        with open(test_file, "w", encoding="utf-8") as f:
            f.write("Test file")

        # Mock app_instance to track queued files
        mock_app_instance.queued_files = set()
        mock_app_instance.processing_files = set()

        # First call should return True (file not queued)
        result1 = monitor._should_queue_file(test_file)
        assert result1 is True
        assert test_file in mock_app_instance.queued_files

        # Second call should return False (file already queued)
        result2 = monitor._should_queue_file(test_file)
        assert result2 is False


class TestFileProcessingIntegration:
    """Integration tests for file processing workflow."""

    def test_file_workflow_basic(self, temp_dir, mock_webdav_client, mock_app_instance):
        """Test basic file processing workflow."""
        from queue import Queue

        file_queue = Queue()

        # Create test file
        test_file = os.path.join(temp_dir, "test.raw")
        with open(test_file, "w", encoding="utf-8") as f:
            f.write("Test content for workflow")

        # Create processor
        processor = FileProcessor(file_queue, mock_app_instance)
        processor.set_webdav_client(mock_webdav_client, "/remote")
        processor.set_local_base(temp_dir)

        # Add file to queue
        file_queue.put(test_file)

        # Verify processor is configured
        assert processor.webdav_client == mock_webdav_client
        assert processor.remote_base_path == "/remote"
        assert processor.local_base_path == temp_dir
        assert not file_queue.empty()

    def test_checksum_consistency(self, sample_file, mock_app_instance, file_queue):
        """Test that checksum calculation is consistent."""
        file_path, _ = sample_file

        processor = FileProcessor(file_queue, mock_app_instance)

        # Calculate checksum multiple times
        checksums = [processor.calculate_checksum(file_path) for _ in range(3)]

        # All checksums should be identical
        assert all(checksum == checksums[0] for checksum in checksums)
        assert len(checksums[0]) == 64
