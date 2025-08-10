import os
import queue
import shutil
import sys
import tempfile
import time
import unittest
from unittest.mock import MagicMock, Mock, patch

from PyQt6.QtCore import Q_ARG, QMetaObject, Qt, QTimer
from PyQt6.QtWidgets import QApplication

# Add the parent directory to the path to import panoramabridge
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from panoramabridge import FileMonitorHandler


class TestThreadSafeUIUpdates(unittest.TestCase):
    """Test thread-safe UI updates using QMetaObject.invokeMethod"""

    def setUp(self):
        """Set up test environment"""
        # Ensure QApplication exists
        self.app = QApplication.instance()
        if not self.app:
            self.app = QApplication([])

        # Create temporary directory for testing
        self.temp_dir = tempfile.mkdtemp()
        self.test_extensions = [".raw", ".txt"]
        self.file_queue = queue.Queue()

        # Create mock app instance that mimics the real UI behavior
        self.mock_app = Mock()
        self.mock_app.add_queued_file_to_table = Mock()
        self.mock_app.queued_files = set()  # Add this for _should_queue_file check
        self.mock_app.processing_files = set()  # Add this for _should_queue_file check
        self.mock_app.upload_history = {}  # Add this for _should_queue_file check
        self.mock_app.file_processor = Mock()  # Add this for _should_queue_file check

        # Track QMetaObject.invokeMethod calls
        self.invoke_method_calls = []

    def tearDown(self):
        """Clean up test environment"""
        if os.path.exists(self.temp_dir):
            shutil.rmtree(self.temp_dir)

    def test_qmetaobject_invoke_method_called(self):
        """Test that QMetaObject.invokeMethod is called for UI updates"""
        handler = FileMonitorHandler(
            extensions=self.test_extensions,
            file_queue=self.file_queue,
            monitor_subdirs=True,
            app_instance=self.mock_app,
        )

        # Create a test file
        test_file = os.path.join(self.temp_dir, "test.raw")
        with open(test_file, "w") as f:
            f.write("test content")

        # Mock QMetaObject.invokeMethod to track calls
        invoke_calls = []

        def mock_invoke_method(obj, method_name, connection_type, *args):
            invoke_calls.append(
                {
                    "object": obj,
                    "method": method_name,
                    "connection_type": connection_type,
                    "args": args,
                }
            )
            # Don't actually invoke since we're testing the call mechanism
            return True

        with patch.object(QMetaObject, "invokeMethod", side_effect=mock_invoke_method):
            # Simulate file handling
            handler._handle_file(test_file)

            # Allow some time for processing
            time.sleep(0.1)

            # Process any pending Qt events
            self.app.processEvents()

        # Verify that QMetaObject.invokeMethod was called
        self.assertGreater(len(invoke_calls), 0, "QMetaObject.invokeMethod should have been called")

        # Check the call details
        ui_update_calls = [
            call for call in invoke_calls if call["method"] == "add_queued_file_to_table"
        ]
        self.assertGreater(len(ui_update_calls), 0, "Should have called add_queued_file_to_table")

        for call in ui_update_calls:
            self.assertEqual(call["object"], self.mock_app, "Should call method on app instance")
            self.assertEqual(
                call["connection_type"],
                Qt.ConnectionType.QueuedConnection,
                "Should use QueuedConnection",
            )
            self.assertTrue(
                any(isinstance(arg, type(Q_ARG(str, test_file))) for arg in call["args"]),
                "Should pass file path as Q_ARG",
            )

    def test_thread_safety_mechanism(self):
        """Test that the thread safety mechanism works correctly"""
        handler = FileMonitorHandler(
            extensions=self.test_extensions,
            file_queue=self.file_queue,
            monitor_subdirs=True,
            app_instance=self.mock_app,
        )

        # Create multiple test files
        test_files = []
        for i in range(3):
            test_file = os.path.join(self.temp_dir, f"test_{i}.raw")
            with open(test_file, "w") as f:
                f.write(f"test content {i}")
            test_files.append(test_file)

        invoke_calls = []

        def track_invoke_method(obj, method_name, connection_type, *args):
            invoke_calls.append(
                {
                    "method": method_name,
                    "connection_type": connection_type,
                    "thread_safe": connection_type == Qt.ConnectionType.QueuedConnection,
                }
            )
            return True

        with patch.object(QMetaObject, "invokeMethod", side_effect=track_invoke_method):
            # Process all test files
            for test_file in test_files:
                handler._handle_file(test_file)

            # Allow processing time
            time.sleep(0.2)
            self.app.processEvents()

        # Verify all UI updates used thread-safe mechanism
        ui_calls = [call for call in invoke_calls if call["method"] == "add_queued_file_to_table"]
        self.assertGreater(len(ui_calls), 0, "Should have made UI update calls")

        for call in ui_calls:
            self.assertTrue(
                call["thread_safe"], "All UI updates should use QueuedConnection for thread safety"
            )

    def test_error_handling_in_ui_updates(self):
        """Test that errors in UI update scheduling are handled gracefully"""
        handler = FileMonitorHandler(
            extensions=self.test_extensions,
            file_queue=self.file_queue,
            monitor_subdirs=True,
            app_instance=self.mock_app,
        )

        test_file = os.path.join(self.temp_dir, "test.raw")
        with open(test_file, "w") as f:
            f.write("test content")

        # Mock QMetaObject.invokeMethod to raise an exception
        def failing_invoke_method(*args, **kwargs):
            raise RuntimeError("Simulated UI update failure")

        with patch.object(QMetaObject, "invokeMethod", side_effect=failing_invoke_method):
            with patch("panoramabridge.logger") as mock_logger:
                # This should not crash, just log an error
                handler._handle_file(test_file)

                # Allow processing time
                time.sleep(0.1)
                self.app.processEvents()

                # Verify error was logged
                error_calls = [
                    call
                    for call in mock_logger.error.call_args_list
                    if "Error scheduling UI table update" in str(call)
                ]
                self.assertGreater(len(error_calls), 0, "Should log UI update errors")

    def test_no_crash_with_none_app_instance(self):
        """Test that handler doesn't crash when app_instance is None"""
        handler = FileMonitorHandler(
            extensions=self.test_extensions,
            file_queue=self.file_queue,
            monitor_subdirs=True,
            app_instance=None,  # No app instance
        )

        test_file = os.path.join(self.temp_dir, "test.raw")
        with open(test_file, "w") as f:
            f.write("test content")

        # This should not crash
        try:
            handler._handle_file(test_file)
            time.sleep(0.1)
            self.app.processEvents()
        except Exception as e:
            self.fail(f"Handler should not crash with None app_instance: {e}")

    def test_file_queuing_with_ui_updates(self):
        """Test that files are both queued and UI is updated"""
        handler = FileMonitorHandler(
            extensions=self.test_extensions,
            file_queue=self.file_queue,
            monitor_subdirs=True,
            app_instance=self.mock_app,
        )

        test_file = os.path.join(self.temp_dir, "test.raw")
        with open(test_file, "w") as f:
            f.write("test content")

        invoke_calls = []

        def track_invoke_method(*args, **kwargs):
            invoke_calls.append(args)
            return True

        with patch.object(QMetaObject, "invokeMethod", side_effect=track_invoke_method):
            initial_queue_size = self.file_queue.qsize()

            # Process the file
            handler._handle_file(test_file)
            time.sleep(0.1)  # Allow for file stability check

            # Simulate second check (file is stable)
            handler._handle_file(test_file)
            time.sleep(0.1)
            self.app.processEvents()

            # Verify file was queued
            final_queue_size = self.file_queue.qsize()
            self.assertGreater(final_queue_size, initial_queue_size, "File should have been queued")

            # Verify UI update was attempted
            self.assertGreater(len(invoke_calls), 0, "UI update should have been attempted")


if __name__ == "__main__":
    unittest.main()
