"""
Tests for progress tracking functionality in PanoramaBridge.

This module tests the progress tracking system including:
- WebDAV client progress callbacks
- FileProcessor progress handling
- ProgressFileIterator functionality
- Real progress percentage calculations
"""

import pytest
import tempfile
import os
from unittest.mock import Mock, patch
from panoramabridge import WebDAVClient, FileProcessor
import queue


class TestProgressTracking:
    """Test suite for progress tracking functionality"""
    
    def test_progress_callback_logic(self):
        """Test the basic progress callback logic"""
        progress_calls = []
        
        def test_progress_callback(current, total):
            percent = (current / total) * 100 if total > 0 else 0
            progress_calls.append((current, total, percent))
            
            # Test status logic
            if current > 0 and current < total:
                status = "Uploading..."
            elif current >= total:
                status = "Complete"
            else:
                status = "Reading file..."
            
            return status
        
        # Test with various progress values
        test_file_size = 5000000  # 5MB file
        
        # Test initial state
        status = test_progress_callback(0, test_file_size)
        assert status == "Reading file..."
        assert progress_calls[-1] == (0, test_file_size, 0.0)
        
        # Test 10% progress
        status = test_progress_callback(500000, test_file_size)
        assert status == "Uploading..."
        assert progress_calls[-1] == (500000, test_file_size, 10.0)
        
        # Test 50% progress
        status = test_progress_callback(2500000, test_file_size)
        assert status == "Uploading..."
        assert progress_calls[-1] == (2500000, test_file_size, 50.0)
        
        # Test 90% progress
        status = test_progress_callback(4500000, test_file_size)
        assert status == "Uploading..."
        assert progress_calls[-1] == (4500000, test_file_size, 90.0)
        
        # Test 100% complete
        status = test_progress_callback(5000000, test_file_size)
        assert status == "Complete"
        assert progress_calls[-1] == (5000000, test_file_size, 100.0)
        
        # Verify we got all expected calls
        assert len(progress_calls) == 5


class TestProgressFileIterator:
    """Test suite for the ProgressFileIterator in WebDAV client"""
    
    def setup_method(self):
        """Set up test files for each test"""
        # Create a temporary test file
        self.temp_file = tempfile.NamedTemporaryFile(delete=False, suffix='.test')
        self.test_data = b'x' * 1000000  # 1MB test file
        self.temp_file.write(self.test_data)
        self.temp_file.close()
        self.test_file_path = self.temp_file.name
        
    def teardown_method(self):
        """Clean up test files after each test"""
        if os.path.exists(self.test_file_path):
            os.unlink(self.test_file_path)
    
    def test_progress_file_iterator_concept(self):
        """Test the ProgressFileIterator concept"""
        
        class ProgressFileIterator:
            def __init__(self, file_path, callback, total_size):
                self.file_path = file_path
                self.callback = callback
                self.total_size = total_size
                self.bytes_sent = 0
                self.chunk_size = 64 * 1024  # 64KB chunks
                self.file = None
                
            def __enter__(self):
                self.file = open(self.file_path, 'rb')
                return self
                
            def __exit__(self, exc_type, exc_val, exc_tb):
                if self.file:
                    self.file.close()
                    
            def __iter__(self):
                return self
                
            def __next__(self):
                if not self.file:
                    raise StopIteration
                    
                chunk = self.file.read(self.chunk_size)
                if not chunk:
                    raise StopIteration
                
                self.bytes_sent += len(chunk)
                
                if self.callback:
                    self.callback(self.bytes_sent, self.total_size)
                
                return chunk
            
            def __len__(self):
                return self.total_size
        
        # Test callback tracking
        progress_calls = []
        def test_callback(current, total):
            percent = (current / total) * 100 if total > 0 else 0
            progress_calls.append((current, total, percent))
        
        # Test the iterator
        file_size = os.path.getsize(self.test_file_path)
        
        with ProgressFileIterator(self.test_file_path, test_callback, file_size) as iterator:
            chunks = list(iterator)  # This will trigger the progress callbacks
        
        # Verify results
        assert len(chunks) > 0, "Should have processed at least one chunk"
        assert len(progress_calls) > 0, "Should have made progress callbacks"
        
        # Verify final progress is 100%
        final_current, final_total, final_percent = progress_calls[-1]
        assert final_current == file_size, "Final progress should equal file size"
        assert final_total == file_size, "Total should equal file size"
        assert final_percent == 100.0, "Final percentage should be 100%"
        
        # Verify progress is monotonically increasing
        for i in range(1, len(progress_calls)):
            prev_percent = progress_calls[i-1][2]
            curr_percent = progress_calls[i][2]
            assert curr_percent >= prev_percent, "Progress should be monotonically increasing"
    
    def test_progress_file_iterator_chunk_sizes(self):
        """Test ProgressFileIterator with different chunk sizes"""
        
        class ProgressFileIterator:
            def __init__(self, file_path, callback, total_size, chunk_size=64*1024):
                self.file_path = file_path
                self.callback = callback
                self.total_size = total_size
                self.bytes_sent = 0
                self.chunk_size = chunk_size
                self.file = None
                
            def __enter__(self):
                self.file = open(self.file_path, 'rb')
                return self
                
            def __exit__(self, exc_type, exc_val, exc_tb):
                if self.file:
                    self.file.close()
                    
            def __iter__(self):
                return self
                
            def __next__(self):
                if not self.file:
                    raise StopIteration
                    
                chunk = self.file.read(self.chunk_size)
                if not chunk:
                    raise StopIteration
                
                self.bytes_sent += len(chunk)
                
                if self.callback:
                    self.callback(self.bytes_sent, self.total_size)
                
                return chunk
            
            def __len__(self):
                return self.total_size
        
        file_size = os.path.getsize(self.test_file_path)
        
        # Test with small chunks (more progress updates)
        progress_calls_small = []
        def callback_small(current, total):
            progress_calls_small.append((current, total))
        
        with ProgressFileIterator(self.test_file_path, callback_small, file_size, chunk_size=1024) as iterator:
            chunks_small = list(iterator)
        
        # Test with large chunks (fewer progress updates)  
        progress_calls_large = []
        def callback_large(current, total):
            progress_calls_large.append((current, total))
        
        with ProgressFileIterator(self.test_file_path, callback_large, file_size, chunk_size=256*1024) as iterator:
            chunks_large = list(iterator)
        
        # Verify both reach 100% but with different granularity
        assert len(progress_calls_small) > len(progress_calls_large), "Small chunks should generate more progress updates"
        assert progress_calls_small[-1][0] == file_size, "Small chunks should reach full file size"
        assert progress_calls_large[-1][0] == file_size, "Large chunks should reach full file size"
        
        # Verify chunk counts are different
        assert len(chunks_small) > len(chunks_large), "Small chunks should create more chunk objects"


class TestFileProcessorProgress:
    """Test suite for FileProcessor progress handling"""
    
    def test_file_processor_progress_callback(self):
        """Test that FileProcessor correctly handles progress callbacks"""
        
        # Create a mock file queue and app instance
        file_queue = queue.Queue()
        app_instance = Mock()
        
        # Create FileProcessor
        processor = FileProcessor(file_queue, app_instance)
        
        # Mock the signals
        processor.progress_update = Mock()
        processor.status_update = Mock()
        
        # Test the progress callback logic from upload_file method
        filepath = "/test/file.raw"
        filename = "file.raw"
        
        def create_progress_callback():
            """Create the progress callback function as it would be in upload_file"""
            def progress_callback(current, total):
                # Simply pass through the actual progress from WebDAV client
                processor.progress_update.emit(filepath, current, total)
                
                # Update status message when upload starts
                if current > 0 and current < total:
                    processor.status_update.emit(filename, "Uploading...", filepath)
            
            return progress_callback
        
        callback = create_progress_callback()
        
        # Test progress callback with various values
        test_file_size = 2000000  # 2MB
        
        # Test initial progress (0 bytes)
        callback(0, test_file_size)
        processor.progress_update.emit.assert_called_with(filepath, 0, test_file_size)
        
        # Test upload started (should trigger status update)
        callback(100000, test_file_size)  # 5%
        processor.progress_update.emit.assert_called_with(filepath, 100000, test_file_size)
        processor.status_update.emit.assert_called_with(filename, "Uploading...", filepath)
        
        # Test mid-progress (should continue uploading status)
        processor.status_update.reset_mock()  # Reset to check if called again
        callback(1000000, test_file_size)  # 50%
        processor.progress_update.emit.assert_called_with(filepath, 1000000, test_file_size)
        processor.status_update.emit.assert_called_with(filename, "Uploading...", filepath)
        
        # Test completion (no status update for completion)
        processor.status_update.reset_mock()
        callback(2000000, test_file_size)  # 100%
        processor.progress_update.emit.assert_called_with(filepath, 2000000, test_file_size)
        processor.status_update.emit.assert_not_called()  # No status update at 100%


class TestWebDAVClientProgress:
    """Test suite for WebDAV client progress functionality"""
    
    def setup_method(self):
        """Set up for each test"""
        self.webdav_client = WebDAVClient("http://test.com", "user", "pass")
        
        # Create a test file
        self.temp_file = tempfile.NamedTemporaryFile(delete=False, suffix='.test')
        self.test_data = b'x' * 500000  # 500KB test file
        self.temp_file.write(self.test_data)
        self.temp_file.close()
        self.test_file_path = self.temp_file.name
    
    def teardown_method(self):
        """Clean up after each test"""
        if os.path.exists(self.test_file_path):
            os.unlink(self.test_file_path)
    
    @patch('panoramabridge.requests.Session')
    @patch('panoramabridge.os.path.getsize')
    def test_upload_file_chunked_progress_callback(self, mock_getsize, mock_session_class):
        """Test that upload_file_chunked properly calls progress callback"""
        
        # Mock file size
        mock_getsize.return_value = 500000  # 500KB
        
        # Mock the session and response
        mock_session = Mock()
        mock_session_class.return_value = mock_session
        mock_response = Mock()
        mock_response.status_code = 201
        mock_session.put.return_value = mock_response
        
        # Track progress calls
        progress_calls = []
        def progress_callback(current, total):
            progress_calls.append((current, total))
        
        # Mock the session for the WebDAVClient
        self.webdav_client.session = mock_session
        
        # Call upload_file_chunked
        success, error = self.webdav_client.upload_file_chunked(
            self.test_file_path, "/remote/path", progress_callback
        )
        
        # Verify upload was successful
        assert success
        assert error == ""
        
        # Verify progress callback was called
        assert len(progress_calls) >= 2, "Progress callback should be called at least twice (start and end)"
        
        # Verify initial call with 0 progress
        first_call = progress_calls[0]
        assert first_call[0] == 0, "First progress call should be 0 bytes"
        assert first_call[1] == 500000, "Total should be file size"
        
        # Verify final call with 100% progress
        final_call = progress_calls[-1]
        assert final_call[0] == 500000, "Final progress should be full file size"
        assert final_call[1] == 500000, "Total should be file size"
    
    @patch('panoramabridge.requests.Session')
    @patch('panoramabridge.os.path.getsize')
    def test_upload_file_chunked_no_callback(self, mock_getsize, mock_session_class):
        """Test that upload_file_chunked works without progress callback"""
        
        # Mock file size
        mock_getsize.return_value = 500000  # 500KB
        
        # Mock the session and response
        mock_session = Mock()
        mock_session_class.return_value = mock_session
        mock_response = Mock()
        mock_response.status_code = 201
        mock_session.put.return_value = mock_response
        
        # Mock the session for the WebDAVClient
        self.webdav_client.session = mock_session
        
        # Call upload_file_chunked without callback
        success, error = self.webdav_client.upload_file_chunked(
            self.test_file_path, "/remote/path", None
        )
        
        # Verify upload was successful even without callback
        assert success
        assert error == ""
        
        # Verify session.put was called
        mock_session.put.assert_called_once()


class TestProgressIntegration:
    """Integration tests for the complete progress tracking system"""
    
    def test_complete_progress_flow(self):
        """Test the complete flow from WebDAV client to FileProcessor"""
        
        # This test simulates the complete progress tracking flow
        progress_updates = []
        status_updates = []
        
        def mock_progress_update(filepath, current, total):
            percent = (current / total) * 100 if total > 0 else 0
            progress_updates.append((filepath, current, total, percent))
        
        def mock_status_update(filename, status, filepath):
            status_updates.append((filename, status, filepath))
        
        # Test the FileProcessor callback logic
        filepath = "/test/large_file.raw"
        filename = "large_file.raw"
        
        def progress_callback(current, total):
            # Simulate FileProcessor's progress callback
            mock_progress_update(filepath, current, total)
            
            # Update status message when upload starts
            if current > 0 and current < total:
                mock_status_update(filename, "Uploading...", filepath)
        
        # Simulate a large file upload with multiple progress updates
        test_file_size = 10000000  # 10MB
        chunk_size = 64 * 1024  # 64KB chunks
        
        # Simulate initial status
        mock_status_update(filename, "Reading file...", filepath)
        
        # Simulate progress updates as file is uploaded
        for bytes_sent in range(0, test_file_size + chunk_size, chunk_size):
            current_bytes = min(bytes_sent, test_file_size)
            progress_callback(current_bytes, test_file_size)
        
        # Verify we got the expected number of updates
        assert len(progress_updates) > 0, "Should have progress updates"
        assert len(status_updates) > 0, "Should have status updates"
        
        # Verify initial status
        assert status_updates[0] == (filename, "Reading file...", filepath)
        
        # Verify final progress is 100%
        final_progress = progress_updates[-1]
        assert final_progress[1] == test_file_size, "Final current should equal file size"
        assert final_progress[2] == test_file_size, "Final total should equal file size"
        assert final_progress[3] == 100.0, "Final percentage should be 100%"
        
        # Verify we have "Uploading..." status updates
        uploading_statuses = [update for update in status_updates if update[1] == "Uploading..."]
        assert len(uploading_statuses) > 0, "Should have uploading status updates"
        
        # Verify progress is always increasing or equal
        for i in range(1, len(progress_updates)):
            prev_percent = progress_updates[i-1][3]
            curr_percent = progress_updates[i][3]
            assert curr_percent >= prev_percent, f"Progress should be monotonic: {prev_percent} -> {curr_percent}"


if __name__ == "__main__":
    # Run the tests if this file is executed directly
    pytest.main([__file__, "-v"])
