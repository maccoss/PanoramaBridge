"""
Pytest configuration and fixtures for PanoramaBridge tests.
"""
import pytest
import tempfile
import shutil
import os
import hashlib
import queue
from unittest.mock import Mock


@pytest.fixture
def temp_dir():
    """Create a temporary directory for testing."""
    temp_path = tempfile.mkdtemp()
    yield temp_path
    shutil.rmtree(temp_path, ignore_errors=True)


@pytest.fixture
def sample_file(temp_dir):
    """Create a sample test file with known content."""
    file_path = os.path.join(temp_dir, "test_file.raw")
    content = b"Sample mass spectrometry data file content for testing"
    with open(file_path, 'wb') as f:
        f.write(content)
    return file_path, content


@pytest.fixture
def large_sample_file(temp_dir):
    """Create a large sample file for performance testing."""
    file_path = os.path.join(temp_dir, "large_test_file.raw")
    # Create a 10MB file
    content = b"0" * (10 * 1024 * 1024)
    with open(file_path, 'wb') as f:
        f.write(content)
    return file_path, content


@pytest.fixture
def mock_webdav_client():
    """Create a mock WebDAV client for testing."""
    client = Mock()
    client.test_connection.return_value = True
    client.get_file_info.return_value = {'exists': False}
    client.upload_file_chunked.return_value = (True, "")
    client.create_directory.return_value = True
    client.store_checksum.return_value = True
    client.get_stored_checksum.return_value = None
    return client


@pytest.fixture
def mock_app_instance():
    """Create a mock application instance for testing."""
    app = Mock()
    app.local_checksum_cache = {}
    app.queued_files = set()
    app.processing_files = set()
    app.created_directories = set()
    app.file_remote_paths = {}
    
    # Mock UI controls for locked file handling
    app.enable_locked_retry_check = Mock()
    app.enable_locked_retry_check.isChecked.return_value = False
    app.initial_wait_spin = Mock()
    app.initial_wait_spin.value.return_value = 1  # 1 minute for testing
    app.retry_interval_spin = Mock()
    app.retry_interval_spin.value.return_value = 5  # 5 seconds for testing
    app.max_retries_spin = Mock()
    app.max_retries_spin.value.return_value = 3
    
    return app


@pytest.fixture
def file_queue():
    """Create a file queue for testing."""
    return queue.Queue()


@pytest.fixture
def sample_extensions():
    """Standard file extensions for testing."""
    return ['raw', 'wiff', 'mzML', 'mzXML']


def calculate_test_checksum(filepath: str) -> str:
    """Calculate SHA256 checksum for test files."""
    hash_obj = hashlib.sha256()
    with open(filepath, 'rb') as f:
        while chunk := f.read(8192):
            hash_obj.update(chunk)
    return hash_obj.hexdigest()


@pytest.fixture
def webdav_test_config():
    """Test configuration for WebDAV connection."""
    return {
        'url': 'https://test.example.com',
        'username': 'test_user',
        'password': 'test_password',
        'auth_type': 'basic'
    }
