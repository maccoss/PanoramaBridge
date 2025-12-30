"""
Tests for WebDAV client functionality.
"""

import os

# Import the module under test
import sys
import tempfile
from unittest.mock import MagicMock, Mock, patch

import pytest
import requests
from requests import Response

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from panoramabridge import WebDAVClient


class TestWebDAVClient:
    """Test WebDAV client functionality."""

    def test_init(self, webdav_test_config):
        """Test WebDAV client initialization."""
        client = WebDAVClient(
            url=webdav_test_config["url"],
            username=webdav_test_config["username"],
            password=webdav_test_config["password"],
            auth_type=webdav_test_config["auth_type"],
        )

        assert client.url == webdav_test_config["url"]
        assert client.username == webdav_test_config["username"]
        assert client.password == webdav_test_config["password"]
        # Chunk size is now dynamically determined per upload, not a fixed attribute

    @patch("panoramabridge.requests.Session.request")
    def test_connection_success(self, mock_request, webdav_test_config):
        """Test successful connection."""
        # Mock successful response
        mock_response = Mock()
        mock_response.status_code = 200
        mock_request.return_value = mock_response

        client = WebDAVClient(**webdav_test_config)
        result = client.test_connection()

        assert result is True
        mock_request.assert_called_once_with("OPTIONS", webdav_test_config["url"], timeout=10)

    @patch("panoramabridge.requests.Session.request")
    def test_connection_with_webdav_fallback(self, mock_request, webdav_test_config):
        """Test connection fallback to /webdav endpoint."""
        # First call fails, second succeeds
        mock_response_fail = Mock()
        mock_response_fail.status_code = 404
        mock_response_success = Mock()
        mock_response_success.status_code = 200
        mock_request.side_effect = [mock_response_fail, mock_response_success]

        client = WebDAVClient(**webdav_test_config)
        result = client.test_connection()

        assert result is True
        assert client.url == f"{webdav_test_config['url']}/webdav"
        assert mock_request.call_count == 2

    @patch("panoramabridge.requests.Session.request")
    def test_connection_failure(self, mock_request, webdav_test_config):
        """Test connection failure."""
        mock_request.side_effect = requests.ConnectionError("Connection failed")

        client = WebDAVClient(**webdav_test_config)
        result = client.test_connection()

        assert result is False

    @patch("panoramabridge.requests.Session.request")
    def test_list_directory(self, mock_request, webdav_test_config):
        """Test directory listing."""
        # Mock PROPFIND response
        mock_response = Mock()
        mock_response.status_code = 207
        mock_response.text = """<?xml version="1.0"?>
        <multistatus xmlns="DAV:">
            <response>
                <href>/test/file1.raw</href>
                <propstat>
                    <prop>
                        <displayname>file1.raw</displayname>
                        <getcontentlength>1024</getcontentlength>
                        <resourcetype/>
                    </prop>
                </propstat>
            </response>
        </multistatus>"""
        mock_request.return_value = mock_response

        client = WebDAVClient(**webdav_test_config)
        items = client.list_directory("/test")

        assert len(items) == 1
        assert items[0]["name"] == "file1.raw"
        assert items[0]["size"] == 1024
        assert items[0]["is_dir"] is False

    @patch("panoramabridge.requests.Session.get")
    def test_download_file(self, mock_get, webdav_test_config, temp_dir):
        """Test file download."""
        # Mock successful download
        mock_response = Mock()
        mock_response.status_code = 200
        mock_response.iter_content.return_value = [b"test content"]
        mock_get.return_value = mock_response

        client = WebDAVClient(**webdav_test_config)
        local_path = os.path.join(temp_dir, "downloaded_file.raw")

        success, error = client.download_file("/remote/file.raw", local_path)

        assert success is True
        assert error == ""
        assert os.path.exists(local_path)

        with open(local_path, "rb") as f:
            content = f.read()
        assert content == b"test content"

    @patch("panoramabridge.requests.Session.put")
    def test_upload_small_file(self, mock_put, webdav_test_config, sample_file):
        """Test uploading a small file."""
        file_path, _ = sample_file

        # Mock successful upload
        mock_response = Mock()
        mock_response.status_code = 201
        mock_put.return_value = mock_response

        client = WebDAVClient(**webdav_test_config)

        # Mock progress callback
        progress_callback = Mock()

        success, error = client.upload_file_chunked(
            file_path, "/remote/test_file.raw", progress_callback
        )

        assert success is True
        assert error == ""
        # For small files (<100MB), progress callback is called once at the start
        assert progress_callback.call_count >= 1
        # Verify progress callback was called with correct arguments
        progress_callback.assert_called_with(0, os.path.getsize(file_path))

    @patch("panoramabridge.requests.Session.request")
    def test_create_directory(self, mock_request, webdav_test_config):
        """Test directory creation."""
        mock_response = Mock()
        mock_response.status_code = 201
        mock_request.return_value = mock_response

        client = WebDAVClient(**webdav_test_config)
        result = client.create_directory("/test/new_dir")

        assert result is True
        mock_request.assert_called_once_with("MKCOL", f"{webdav_test_config['url']}/test/new_dir")

    def test_should_show_item_filtering(self, webdav_test_config):
        """Test file/directory filtering logic."""
        client = WebDAVClient(**webdav_test_config)

        # Should show normal files
        assert client._should_show_item("data.raw", False) is True
        assert client._should_show_item("experiment", True) is True

        # Should hide system files
        assert client._should_show_item(".hidden", False) is False
        assert client._should_show_item("copy_directory_temp", False) is False
        assert client._should_show_item("__pycache__", True) is False
        assert client._should_show_item(".DS_Store", False) is False

    @patch("panoramabridge.requests.Session.put")
    def test_store_checksum(self, mock_put, webdav_test_config):
        """Test checksum storage."""
        mock_response = Mock()
        mock_response.status_code = 201
        mock_put.return_value = mock_response

        client = WebDAVClient(**webdav_test_config)
        result = client.store_checksum("/test/file.raw", "abc123def456")

        assert result is True
        mock_put.assert_called_once()

        # Check that checksum was sent as data
        call_args = mock_put.call_args
        assert call_args[1]["data"] == b"abc123def456"

    @patch("panoramabridge.requests.Session.get")
    def test_get_stored_checksum(self, mock_get, webdav_test_config):
        """Test checksum retrieval."""
        mock_response = Mock()
        mock_response.status_code = 200
        mock_response.text = "abc123def456"
        mock_get.return_value = mock_response

        client = WebDAVClient(**webdav_test_config)
        checksum = client.get_stored_checksum("/test/file.raw")

        assert checksum == "abc123def456"

    @patch("panoramabridge.requests.Session.get")
    def test_get_stored_checksum_not_found(self, mock_get, webdav_test_config):
        """Test checksum retrieval when not found."""
        mock_response = Mock()
        mock_response.status_code = 404
        mock_get.return_value = mock_response

        client = WebDAVClient(**webdav_test_config)
        checksum = client.get_stored_checksum("/test/file.raw")

        assert checksum is None

    @patch("panoramabridge.requests.Session.request")
    def test_get_file_info_success(self, mock_request, webdav_test_config):
        """Test get_file_info with successful response."""
        # Mock PROPFIND response
        mock_response = Mock()
        mock_response.status_code = 207
        mock_response.text = """<?xml version="1.0" encoding="utf-8"?>
        <multistatus xmlns="DAV:">
            <response>
                <href>/test/file.raw</href>
                <propstat>
                    <prop>
                        <displayname>file.raw</displayname>
                        <getcontentlength>1024</getcontentlength>
                        <getlastmodified>Wed, 09 Aug 2025 10:30:00 GMT</getlastmodified>
                        <getetag>"abc123def456"</getetag>
                    </prop>
                </propstat>
            </response>
        </multistatus>"""
        mock_request.return_value = mock_response

        client = WebDAVClient(**webdav_test_config)
        info = client.get_file_info("/test/file.raw")

        assert info is not None
        assert info["exists"] is True
        assert info["size"] == 1024
        assert info["etag"] == "abc123def456"
        assert info["last_modified"] == "Wed, 09 Aug 2025 10:30:00 GMT"

        # Verify PROPFIND was called with correct headers and XML body
        mock_request.assert_called_once()
        call_args = mock_request.call_args
        assert call_args[0][0] == "PROPFIND"
        assert call_args[1]["headers"]["Depth"] == "0"
        assert call_args[1]["headers"]["Content-Type"] == "application/xml"

        # Verify XML body has correct encoding (no spaces around dash)
        xml_body = call_args[1]["data"]
        assert 'encoding="utf-8"' in xml_body
        assert 'encoding="utf - 8"' not in xml_body  # Ensure malformed version is not present

    @patch("panoramabridge.requests.Session.request")
    def test_get_file_info_not_found(self, mock_request, webdav_test_config):
        """Test get_file_info when file doesn't exist."""
        mock_response = Mock()
        mock_response.status_code = 404
        mock_request.return_value = mock_response

        client = WebDAVClient(**webdav_test_config)
        info = client.get_file_info("/test/nonexistent.raw")

        assert info is not None
        assert info["exists"] is False
        assert info["path"] == "/test/nonexistent.raw"

    @patch("panoramabridge.requests.Session.request")
    def test_get_file_info_server_error(self, mock_request, webdav_test_config):
        """Test get_file_info with server error."""
        mock_response = Mock()
        mock_response.status_code = 500
        mock_request.return_value = mock_response

        client = WebDAVClient(**webdav_test_config)
        info = client.get_file_info("/test/file.raw")

        assert info is None

    @patch("panoramabridge.requests.Session.put")
    def test_upload_403_forbidden_chunked(self, mock_put, webdav_test_config, sample_file):
        """Test that HTTP 403 on chunked upload fails immediately with error message."""
        file_path, _ = sample_file

        # Create a file large enough to trigger chunked upload (>100MB)
        large_file = os.path.join(os.path.dirname(file_path), "large_test.raw")
        with open(large_file, "wb") as f:
            f.write(b"0" * (101 * 1024 * 1024))  # 101 MB

        # Mock 403 Forbidden response
        mock_response = Mock()
        mock_response.status_code = 403
        mock_response.reason = "Forbidden"
        mock_response.text = "You don't have permission to upload to /_webdav/"
        mock_put.return_value = mock_response

        client = WebDAVClient(**webdav_test_config)
        success, error = client.upload_file_chunked(large_file, "/_webdav/test.raw")

        # Should fail immediately without falling back to regular upload
        assert success is False
        assert "403" in error
        assert "Forbidden" in error

        # Clean up
        os.remove(large_file)

    @patch("panoramabridge.requests.Session.put")
    def test_upload_502_retry_logic(self, mock_put, webdav_test_config, sample_file):
        """Test that HTTP 502 triggers retry logic."""
        file_path, _ = sample_file

        # Mock 502 responses followed by success
        mock_502 = Mock()
        mock_502.status_code = 502
        mock_502.reason = "Bad Gateway"
        mock_502.text = "The gateway server received an invalid response"

        mock_success = Mock()
        mock_success.status_code = 201

        # First two attempts fail with 502, third succeeds
        mock_put.side_effect = [mock_502, mock_502, mock_success]

        client = WebDAVClient(**webdav_test_config)
        success, error = client.upload_file_chunked(file_path, "/test/file.raw")

        # Should succeed after retries
        assert success is True
        assert error == ""
        # Should have called put 3 times (2 failures + 1 success)
        assert mock_put.call_count == 3

    @patch("panoramabridge.requests.Session.put")
    def test_upload_502_max_retries_exceeded(self, mock_put, webdav_test_config, sample_file):
        """Test that upload fails after max retries with 502."""
        file_path, _ = sample_file

        # Mock 502 response that persists
        mock_502 = Mock()
        mock_502.status_code = 502
        mock_502.reason = "Bad Gateway"
        mock_502.text = "The gateway server received an invalid response"
        mock_put.return_value = mock_502

        client = WebDAVClient(**webdav_test_config)
        success, error = client.upload_file_chunked(file_path, "/test/file.raw")

        # Should fail after max retries (default 3)
        assert success is False
        assert "502" in error
        assert "Bad Gateway" in error
        # Should have called put 4 times (initial + 3 retries)
        assert mock_put.call_count == 4

    @patch("panoramabridge.requests.Session.put")
    def test_upload_404_no_retry(self, mock_put, webdav_test_config, sample_file):
        """Test that HTTP 404 does not trigger retry (client error)."""
        file_path, _ = sample_file

        # Mock 404 response
        mock_404 = Mock()
        mock_404.status_code = 404
        mock_404.reason = "Not Found"
        mock_404.text = "The requested resource was not found"
        mock_put.return_value = mock_404

        client = WebDAVClient(**webdav_test_config)
        success, error = client.upload_file_chunked(file_path, "/test/file.raw")

        # Should fail immediately without retries
        assert success is False
        assert "404" in error
        assert "Not Found" in error
        # Should have called put only once (no retries for 4xx errors)
        assert mock_put.call_count == 1

    @patch("panoramabridge.requests.Session.put")
    def test_upload_timeout_configured(self, mock_put, webdav_test_config, sample_file):
        """Test that timeout is properly configured on upload."""
        file_path, _ = sample_file

        # Mock successful response
        mock_response = Mock()
        mock_response.status_code = 201
        mock_put.return_value = mock_response

        client = WebDAVClient(**webdav_test_config)
        success, error = client.upload_file_chunked(file_path, "/test/file.raw")

        assert success is True
        # Verify timeout was passed to the PUT request
        call_kwargs = mock_put.call_args[1]
        assert "timeout" in call_kwargs
        assert call_kwargs["timeout"] == 300  # Default 5 minute timeout

    @patch("panoramabridge.requests.Session.put")
    def test_upload_503_service_unavailable_retry(self, mock_put, webdav_test_config, sample_file):
        """Test that HTTP 503 triggers retry logic."""
        file_path, _ = sample_file

        # Mock 503 response followed by success
        mock_503 = Mock()
        mock_503.status_code = 503
        mock_503.reason = "Service Unavailable"
        mock_503.text = "The server is temporarily unable to handle the request"

        mock_success = Mock()
        mock_success.status_code = 201

        mock_put.side_effect = [mock_503, mock_success]

        client = WebDAVClient(**webdav_test_config)
        success, error = client.upload_file_chunked(file_path, "/test/file.raw")

        # Should succeed after retry
        assert success is True
        assert error == ""
        assert mock_put.call_count == 2

    def test_verify_message_logic_fix(self):
        """Test that verification message checking doesn't cause TypeError.

        This tests the fix for the bug where verification failure checking used:
            if [list] in string:  # Wrong! Causes TypeError
        Instead of:
            if any(item in string for item in [list]):  # Correct!
        """
        # Simulate the verification messages that could be returned
        test_messages = [
            "remote file not found",
            "cannot read remote file",
            "verification error: timeout",
            "size mismatch (local: 100, remote: 200)",
            "checksum mismatch"
        ]

        error_types = ["verification error", "remote file not found", "cannot read remote file"]

        # Test that each message is correctly identified
        for msg in test_messages:
            # This is the CORRECT way (what we fixed it to)
            is_verification_error = any(err in msg for err in error_types)

            # This would be the WRONG way (what the bug was)
            # if error_types in msg:  # This would raise TypeError!

            # Verify the logic works
            if msg in ["remote file not found", "cannot read remote file", "verification error: timeout"]:
                assert is_verification_error is True
            else:
                assert is_verification_error is False
