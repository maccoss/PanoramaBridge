"""
Comprehensive diagnostic test for infinite loop scenarios.

This test simulates various real-world conditions that might trigger infinite loops.
"""

import hashlib
import os
import queue
import sys
import tempfile
import threading
import time
from unittest.mock import Mock, patch

# Add parent directory to path for imports
sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

def test_comprehensive_infinite_loop_scenarios():
    """Test various scenarios that could cause infinite loops."""

    print("🔍 Testing comprehensive infinite loop scenarios...")

    from panoramabridge import FileMonitorHandler, MainWindow

    # Create test file
    with tempfile.NamedTemporaryFile(suffix='.raw', delete=False) as tmp:
        content = b"Test file for comprehensive infinite loop testing"
        tmp.write(content)
        filepath = tmp.name

    try:
        # Setup mocks
        mock_app = Mock()
        mock_app.upload_history = {}
        mock_app.queued_files = set()
        mock_app.processing_files = set()

        file_processor = Mock()
        checksum = hashlib.sha256(content).hexdigest()
        file_processor.calculate_checksum.return_value = checksum
        mock_app.file_processor = file_processor

        # Create handler
        file_queue = queue.Queue()
        handler = FileMonitorHandler(
            file_queue=file_queue,
            app_instance=mock_app,
            extensions=['.raw']
        )

        print(f"✓ Test file created: {filepath}")
        print(f"✓ File checksum: {checksum}")

        # Scenario 1: Multiple rapid file system events (simulating system touches)
        print("\n📋 Scenario 1: Multiple rapid file system events")

        # First event - should queue
        result1 = handler._should_queue_file(filepath)
        print(f"  Event 1: Queue={result1} (expected: True)")
        assert result1 is True

        # Simulate successful upload
        mock_app.upload_history[filepath] = {
            'checksum': checksum,
            'timestamp': time.time(),
            'remote_path': '/remote/test_file.raw'
        }
        mock_app.queued_files.clear()
        print("  ✓ Simulated successful upload")

        # Multiple subsequent events - should all be skipped
        for i in range(10):
            result = handler._should_queue_file(filepath)
            print(f"  Event {i+2}: Queue={result} (expected: False)")
            assert result is False, f"Event {i+2} should not queue file"

        print("  ✅ Scenario 1 passed - no infinite loop!")

        # Scenario 2: Polling with unchanged files
        print("\n📋 Scenario 2: Backup polling with unchanged files")

        # Mock main window for polling test
        with patch('panoramabridge.QApplication'):
            main_window = MainWindow()
            main_window.upload_history = {filepath: {'checksum': checksum, 'timestamp': time.time(), 'remote_path': '/remote/test_file.raw'}}
            main_window.queued_files = set()
            main_window.processing_files = set()
            main_window.file_processor = file_processor

            # Multiple polling calls - should all be skipped
            for i in range(5):
                result = main_window._should_queue_file_poll(filepath)
                print(f"  Poll {i+1}: Queue={result} (expected: False)")
                assert result is False, f"Poll {i+1} should not queue file"

        print("  ✅ Scenario 2 passed - polling doesn't cause infinite loop!")

        # Scenario 3: Mixed file events and polling
        print("\n📋 Scenario 3: Mixed file events and polling")

        mock_app.queued_files.clear()

        # File event - should be skipped
        result_event = handler._should_queue_file(filepath)
        print(f"  File event: Queue={result_event} (expected: False)")
        assert result_event is False

        # Polling - should be skipped
        result_poll = main_window._should_queue_file_poll(filepath)
        print(f"  Polling: Queue={result_poll} (expected: False)")
        assert result_poll is False

        print("  ✅ Scenario 3 passed - mixed events handled correctly!")

        # Scenario 4: File modification detection
        print("\n📋 Scenario 4: Actual file modification detection")

        # Modify file content
        modified_content = content + b" - ACTUALLY MODIFIED"
        with open(filepath, 'wb') as f:
            f.write(modified_content)

        # Update mock to return new checksum
        new_checksum = hashlib.sha256(modified_content).hexdigest()
        file_processor.calculate_checksum.return_value = new_checksum
        mock_app.queued_files.clear()

        # Should queue modified file
        result_modified = handler._should_queue_file(filepath)
        print(f"  Modified file: Queue={result_modified} (expected: True)")
        assert result_modified is True, "Modified file should be queued"

        print("  ✅ Scenario 4 passed - modified files are correctly detected!")

        # Scenario 5: Concurrent access simulation
        print("\n📋 Scenario 5: Concurrent access simulation")

        # Reset for concurrent test
        mock_app.upload_history[filepath] = {'checksum': new_checksum, 'timestamp': time.time(), 'remote_path': '/remote/test_file.raw'}
        file_processor.calculate_checksum.return_value = new_checksum
        results = []

        def concurrent_test():
            mock_app.queued_files.discard(filepath)  # Clear for each thread
            result = handler._should_queue_file(filepath)
            results.append(result)

        # Run multiple threads concurrently
        threads = []
        for i in range(5):
            thread = threading.Thread(target=concurrent_test)
            threads.append(thread)
            thread.start()

        for thread in threads:
            thread.join()

        print(f"  Concurrent results: {results}")
        # Most results should be False (file unchanged)
        false_count = results.count(False)
        print(f"  False results: {false_count}/5 (expected: most should be False)")
        assert false_count >= 3, "Most concurrent calls should return False"

        print("  ✅ Scenario 5 passed - concurrent access handled!")

        print("\n🎉 ALL SCENARIOS PASSED! The infinite loop fix is working correctly!")

    except Exception as e:
        print(f"\n❌ Test failed with error: {e}")
        import traceback
        traceback.print_exc()
        return False

    finally:
        # Cleanup
        try:
            os.unlink(filepath)
        except (OSError, FileNotFoundError):
            pass

    return True


def test_edge_cases():
    """Test edge cases that might cause issues."""

    print("\n🔍 Testing edge cases...")

    from panoramabridge import FileMonitorHandler

    # Test with missing checksum in upload history
    mock_app = Mock()
    mock_app.upload_history = {}
    mock_app.queued_files = set()
    mock_app.processing_files = set()

    file_processor = Mock()
    mock_app.file_processor = file_processor

    file_queue = queue.Queue()
    handler = FileMonitorHandler(
        file_queue=file_queue,
        app_instance=mock_app,
        extensions=['.raw']
    )

    # Test with malformed upload history entry
    with tempfile.NamedTemporaryFile(suffix='.raw', delete=False) as tmp:
        tmp.write(b"test content")
        filepath = tmp.name

    try:
        # Malformed history entry (missing checksum)
        mock_app.upload_history[filepath] = {
            'timestamp': time.time(),
            'remote_path': '/remote/test_file.raw'
            # Missing 'checksum' key
        }

        result = handler._should_queue_file(filepath)
        print(f"  Malformed history entry: Queue={result} (expected: True - fail safe)")
        assert result is True, "Should queue when checksum is missing (fail-safe behavior)"

        print("  ✅ Edge case test passed - malformed entries handled safely!")

    finally:
        os.unlink(filepath)


if __name__ == "__main__":
    print("Running comprehensive infinite loop diagnostic tests...\n")

    success = test_comprehensive_infinite_loop_scenarios()
    if success:
        test_edge_cases()
        print("\n🏆 ALL TESTS PASSED! The infinite loop fix is comprehensive and working.")
    else:
        print("\n💥 Some tests failed - there may still be infinite loop issues.")
