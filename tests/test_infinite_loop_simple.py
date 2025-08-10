"""
Simple integration test for the infinite loop fix.

This test can be run standalone or as part of the pytest suite.
"""

import hashlib
import os
import queue
import tempfile
import time
from unittest.mock import Mock
import pytest
import sys

# Add parent directory to path for imports
sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

def test_infinite_loop_fix_simple():
    """Simple test to verify the infinite loop fix works."""
    
    # Import after adding to path
    from panoramabridge import FileMonitorHandler
    
    # Create temporary file
    with tempfile.NamedTemporaryFile(suffix='.raw', delete=False) as tmp:
        tmp.write(b"Test file content for infinite loop fix")
        filepath = tmp.name
    
    try:
        # Mock app instance
        mock_app = Mock()
        mock_app.upload_history = {}
        mock_app.queued_files = set()
        mock_app.processing_files = set()
        
        # Mock file processor
        file_processor = Mock()
        checksum = hashlib.sha256(b"Test file content for infinite loop fix").hexdigest()
        file_processor.calculate_checksum.return_value = checksum
        mock_app.file_processor = file_processor
        
        # Create handler
        file_queue = queue.Queue()
        handler = FileMonitorHandler(
            file_queue=file_queue,
            app_instance=mock_app,
            extensions=['.raw']
        )
        
        # Test 1: New file should be queued
        result1 = handler._should_queue_file(filepath)
        assert result1 is True, "New file should be queued"
        assert filepath in mock_app.queued_files, "File should be in queued_files set"
        
        # Simulate successful upload
        mock_app.upload_history[filepath] = {
            'checksum': checksum,
            'timestamp': time.time(),
            'remote_path': '/remote/test_file.raw'
        }
        mock_app.queued_files.clear()
        
        # Test 2: Same file (unchanged) should NOT be queued again
        result2 = handler._should_queue_file(filepath)
        assert result2 is False, "Unchanged file should NOT be re-queued"
        assert filepath not in mock_app.queued_files, "File should NOT be in queued_files set"
        
        # Test 3: Multiple calls should still not queue (infinite loop prevention)
        for i in range(5):
            result = handler._should_queue_file(filepath)
            assert result is False, f"Call {i+1}: Unchanged file should NOT be re-queued"
        
        print("✅ Infinite loop fix test passed!")
        
    finally:
        # Clean up
        os.unlink(filepath)


def test_file_modification_detection():
    """Test that actual file modifications are detected."""
    
    from panoramabridge import FileMonitorHandler
    
    # Create temporary file
    with tempfile.NamedTemporaryFile(suffix='.raw', delete=False) as tmp:
        original_content = b"Original content"
        tmp.write(original_content)
        filepath = tmp.name
    
    try:
        # Mock app instance
        mock_app = Mock()
        mock_app.upload_history = {}
        mock_app.queued_files = set()
        mock_app.processing_files = set()
        
        # Mock file processor
        file_processor = Mock()
        mock_app.file_processor = file_processor
        
        # Create handler
        file_queue = queue.Queue()
        handler = FileMonitorHandler(
            file_queue=file_queue,
            app_instance=mock_app,
            extensions=['.raw']
        )
        
        # Original checksum
        original_checksum = hashlib.sha256(original_content).hexdigest()
        file_processor.calculate_checksum.return_value = original_checksum
        
        # First upload
        handler._should_queue_file(filepath)
        mock_app.upload_history[filepath] = {
            'checksum': original_checksum,
            'timestamp': time.time(),
            'remote_path': '/remote/test_file.raw'
        }
        mock_app.queued_files.clear()
        
        # Modify file
        modified_content = original_content + b" - MODIFIED"
        with open(filepath, 'wb') as f:
            f.write(modified_content)
        
        # Update mock to return new checksum
        new_checksum = hashlib.sha256(modified_content).hexdigest()
        file_processor.calculate_checksum.return_value = new_checksum
        
        # Modified file should be queued again
        result = handler._should_queue_file(filepath)
        assert result is True, "Modified file should be re-queued"
        assert filepath in mock_app.queued_files, "Modified file should be in queued_files"
        
        print("✅ File modification detection test passed!")
        
    finally:
        os.unlink(filepath)


if __name__ == "__main__":
    print("Running infinite loop fix tests...")
    test_infinite_loop_fix_simple()
    test_file_modification_detection()
    print("All tests passed! ✅")
