#!/usr/bin/env python3
"""Test script to verify upload progress tracking works with actual files."""

import os
import tempfile
import time
from panoramabridge import WebDAVClient


def test_progress_callback(bytes_sent, total_bytes):
    """Test progress callback that prints progress."""
    percentage = (bytes_sent / total_bytes * 100) if total_bytes > 0 else 0
    print(f"Progress: {bytes_sent:,} / {total_bytes:,} bytes ({percentage:.1f}%)")


def create_test_file(size_mb=1):
    """Create a test file of specified size in MB."""
    temp_file = tempfile.NamedTemporaryFile(delete=False, suffix='.txt')
    
    # Write data in chunks to create a file of the specified size
    chunk_size = 64 * 1024  # 64KB chunks
    bytes_to_write = size_mb * 1024 * 1024
    chunk_data = b'A' * min(chunk_size, bytes_to_write)
    
    bytes_written = 0
    while bytes_written < bytes_to_write:
        remaining = bytes_to_write - bytes_written
        if remaining < len(chunk_data):
            temp_file.write(chunk_data[:remaining])
        else:
            temp_file.write(chunk_data)
        bytes_written += len(chunk_data)
    
    temp_file.close()
    return temp_file.name


def test_upload_progress():
    """Test the upload progress tracking."""
    print("Creating test file...")
    test_file = create_test_file(5)  # 5MB test file
    
    print(f"Created test file: {test_file}")
    print(f"File size: {os.path.getsize(test_file):,} bytes")
    
    # Test with a mock WebDAV client (we'll just test the ProgressFileWrapper)
    print("\nTesting ProgressFileWrapper directly...")
    
    # Import the ProgressFileWrapper class from the WebDAV client
    # We'll need to create a mock version since the class is defined inside the method
    class TestProgressFileWrapper:
        def __init__(self, file_path, callback, total_size):
            self.file_path = file_path
            self.callback = callback
            self.total_size = total_size
            self.bytes_sent = 0
            self._file = None
            
        def __enter__(self):
            self._file = open(self.file_path, 'rb')
            return self
            
        def __exit__(self, exc_type, exc_val, exc_tb):
            if self._file:
                self._file.close()
                
        def read(self, size=-1):
            """Read method that tracks progress"""
            if not self._file:
                return b''
            
            # Use a reasonable chunk size if size is -1 or very large
            if size == -1 or size > 64 * 1024:
                size = 64 * 1024  # 64KB chunks
            
            data = self._file.read(size)
            if data:
                self.bytes_sent += len(data)
                
                # Report progress
                if self.callback:
                    self.callback(self.bytes_sent, self.total_size)
            
            return data
        
        def __len__(self):
            return self.total_size
    
    # Test the wrapper
    file_size = os.path.getsize(test_file)
    with TestProgressFileWrapper(test_file, test_progress_callback, file_size) as wrapper:
        print("Reading file through ProgressFileWrapper...")
        total_read = 0
        while True:
            chunk = wrapper.read(32 * 1024)  # 32KB chunks
            if not chunk:
                break
            total_read += len(chunk)
            time.sleep(0.01)  # Simulate network delay
    
    print(f"\nTotal bytes read: {total_read:,}")
    
    # Clean up
    os.unlink(test_file)
    print("Test file cleaned up.")


if __name__ == "__main__":
    test_upload_progress()
