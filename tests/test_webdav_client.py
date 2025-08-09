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
