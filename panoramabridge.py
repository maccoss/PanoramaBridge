#!/usr/bin/env python3
"""
PanoramaBridge - A Python Qt6 Application for Directory Monitoring and WebDAV File Transfer

This application monitors local directories for new files and automatically transfers them to WebDAV servers (like Panorama) with comprehensive features:

- Real-time file monitoring using watchdog
- Chunked upload support for large files
- SHA256 checksum generation for upload metadata (not used for verification due to performance cost)
- Conflict resolution with user interaction
- Secure credential storage using system keyring
- Remote directory browsing and management
- Configurable file extensions and directory structure preservation
- Progress tracking and comprehensive logging

Author: Michael MacCoss - MacCoss Lab, University of Washington
License: Apache License 2.0
"""

__version__ = "0.1.9rc4"

import hashlib  # For calculating SHA256 checksums
import json  # For configuration file storage
import logging
import os
import pickle  # For persistent upload tracking
import queue  # For thread-safe file processing queue

# Standard library imports
import sys
import tempfile  # For temporary file operations during verification
import threading  # For background operations
import time  # For file stability checks and timestamps
import xml.etree.ElementTree as ET
from datetime import datetime
from pathlib import Path
from typing import Optional
from urllib.parse import quote, unquote, urljoin

# WebDAV client using requests library
import requests
from PyQt6.QtCore import (
    Q_ARG,
    QMetaObject,
    Qt,
    QThread,
    QTimer,
    pyqtSignal,
    pyqtSlot,
)
from PyQt6.QtGui import QColor, QFont, QIcon

# Third-party imports (must be installed via pip)
try:
    from PyQt6.QtWidgets import (
        QApplication,
        QCheckBox,
        QComboBox,
        QDialog,
        QFileDialog,
        QGridLayout,
        QGroupBox,
        QHBoxLayout,
        QInputDialog,
        QLabel,
        QLineEdit,
        QMainWindow,
        QMenu,
        QMessageBox,
        QProgressBar,
        QPushButton,
        QRadioButton,
        QSpinBox,
        QTableWidget,
        QTableWidgetItem,
        QTabWidget,
        QTextEdit,
        QTreeWidget,
        QTreeWidgetItem,
        QVBoxLayout,
        QWidget,
    )
except ImportError:
    print("PyQt6 is not installed. Please install it with: pip install PyQt6")
    sys.exit(1)
from requests.auth import HTTPBasicAuth, HTTPDigestAuth
from watchdog.events import FileSystemEventHandler

# File monitoring using watchdog library
from watchdog.observers import Observer

# Configure comprehensive logging to both console and file
logging.basicConfig(
    level=logging.DEBUG,
    format="%(asctime)s - %(name)s - %(levelname)s - %(message)s",
    handlers=[
        logging.StreamHandler(),  # Console output for real-time monitoring
        logging.FileHandler("panoramabridge.log", mode="a"),  # Persistent log file
    ],
)
logger = logging.getLogger(__name__)

# Secure credential storage setup (optional dependency)
# If keyring is not available, credentials won't be saved but app will still work
KEYRING_AVAILABLE = False
keyring = None
try:
    import keyring

    KEYRING_AVAILABLE = True
    logger.info("Keyring available for secure credential storage")
except ImportError:
    KEYRING_AVAILABLE = False
    logger.warning("Keyring not available - credential saving will be disabled")


class WebDAVClient:
    """
    WebDAV client with chunked upload support and comprehensive file operations.

    This class handles all WebDAV server interactions including:
    - Connection testing with automatic endpoint detection
    - Directory listing and browsing
    - File upload with chunked transfer for large files
    - File download and verification
    - Checksum generation for upload metadata (not used for verification)
    - Directory creation with proper error handling

    Supports both Basic and Digest authentication methods.
    """

    def __init__(self, url: str, username: str, password: str, auth_type: str = "basic"):
        """
        Initialize WebDAV client with connection parameters.

        Args:
            url: WebDAV server URL (e.g., "https://panoramaweb.org")
            username: Username for authentication
            password: Password for authentication
            auth_type: Authentication type ("basic" or "digest")
        """
        self.url = url.rstrip("/")  # Remove trailing slash for consistency
        self.username = username
        self.password = password

        # Configure authentication based on type
        if auth_type == "digest":
            self.auth = HTTPDigestAuth(username, password)
        else:
            self.auth = HTTPBasicAuth(username, password)

        # Create persistent session for connection reuse
        self.session = requests.Session()
        self.session.auth = self.auth

    def test_connection(self) -> bool:
        """
        Test WebDAV server connectivity with automatic endpoint detection.

        First tries the provided URL, then attempts common WebDAV endpoints
        like /webdav if the initial connection fails. Updates self.url if
        a working endpoint is found.

        Returns:
            bool: True if connection successful, False otherwise
        """
        try:
            logger.info(f"Testing connection to: {self.url}")

            # First try with the exact URL provided by user
            response = self.session.request("OPTIONS", self.url, timeout=10)
            logger.info(f"OPTIONS request to {self.url} returned: {response.status_code}")
            if response.status_code in [200, 204, 207]:
                logger.info("Connection successful with provided URL")
                return True

            # If that fails, try with /webdav appended (common WebDAV endpoint)
            if not self.url.endswith("/webdav"):
                webdav_url = f"{self.url.rstrip('/')}/webdav"
                logger.info(f"Trying with /webdav suffix: {webdav_url}")
                response = self.session.request("OPTIONS", webdav_url, timeout=10)
                logger.info(f"OPTIONS request to {webdav_url} returned: {response.status_code}")
                if response.status_code in [200, 204, 207]:
                    # Update the URL to use the working endpoint
                    logger.info(f"Connection successful, updating URL to: {webdav_url}")
                    self.url = webdav_url
                    return True

            logger.warning("Connection failed - no valid WebDAV endpoint found")
            return False
        except Exception as e:
            logger.error(f"Connection test failed: {e}")
            return False

    def list_directory(self, path: str = "/") -> list[dict]:
        """List contents of a WebDAV directory"""
        logger.info(f"list_directory called with path: {path}")
        url = urljoin(self.url, quote(path))
        logger.info(f"Requesting directory listing for URL: {url}")

        headers = {"Depth": "1", "Content-Type": "application/xml"}

        # PROPFIND request body
        body = """<?xml version="1.0" encoding="utf-8"?>
        <propfind xmlns="DAV:">
            <prop>
                <displayname/>
                <resourcetype/>
                <getcontentlength/>
                <getlastmodified/>
            </prop>
        </propfind>"""

        try:
            logger.info(f"Sending PROPFIND request to: {url}")
            response = self.session.request("PROPFIND", url, headers=headers, data=body)
            logger.info(f"PROPFIND response status: {response.status_code}")
            if response.status_code == 207:  # Multi-Status
                logger.info(f"PROPFIND successful for {path}, parsing response...")
                items = self._parse_propfind_response(response.text, path)
                logger.info(f"Directory listing for {path} returned {len(items)} items")
                return items
            else:
                logger.error(f"Failed to list directory {path}: HTTP {response.status_code}")
                logger.error(f"Response body: {response.text[:500]}")  # First 500 chars
                return []
        except Exception as e:
            logger.error(f"Error listing directory {path}: {e}")
            return []

    def _should_show_item(self, item_name: str, is_dir: bool) -> bool:
        """Determine if an item should be shown in the directory listing"""
        # Hide system files and directories that start with a dot
        if item_name.startswith("."):
            logger.debug(f"Filtering out hidden item: {item_name}")
            return False

        # Hide common system/backup files - be more specific about patterns
        system_patterns = [
            "copy_directory_fileroot_change_",
            "copy_directory_",
            "copy_direct",
            ".norcrawl",
            ".htaccess",
            ".DS_Store",
            "Thumbs.db",
            "__pycache__",
        ]

        for pattern in system_patterns:
            if item_name.startswith(pattern):
                logger.debug(f"Filtering out system item: {item_name}")
                return False

        # Hide common system directories - be more restrictive for directories
        if is_dir:
            system_dirs = [
                "nextflow",
                "output",
                "proteome",
                ".git",
                ".svn",
                "__pycache__",
                ".tmp",
                "temp",
                "cache",
                ".trash",
                ".recycle",
            ]
            # Case-insensitive comparison for system directories
            if item_name.lower() in [d.lower() for d in system_dirs]:
                logger.debug(f"Filtering out system directory: {item_name}")
                return False

        logger.debug(f"Including item: {item_name} (is_dir: {is_dir})")
        return True

    def _parse_propfind_response(self, xml_response: str, base_path: str) -> list[dict]:
        """Parse PROPFIND XML response"""
        logger.info(f"Parsing PROPFIND response for base_path: {base_path}")
        items = []
        try:
            root = ET.fromstring(xml_response)

            # Define namespace
            ns = {"d": "DAV:"}

            responses = root.findall(".//d:response", ns)
            logger.info(f"Found {len(responses)} response elements in XML")

            for i, response in enumerate(responses):
                href = response.find("d:href", ns)
                if href is None:
                    logger.debug(f"Response {i}: No href element found, skipping")
                    continue

                href_text = href.text
                if href_text is None:
                    logger.debug(f"Response {i}: href text is None, skipping")
                    continue

                logger.debug(f"Response {i}: Processing href: {href_text}")

                # Skip the base path itself (compare unquoted paths)
                unquoted_href = unquote(href_text.rstrip("/"))
                unquoted_base = base_path.rstrip("/")
                if unquoted_href == unquoted_base:
                    logger.debug(f"Response {i}: Skipping base path itself: {unquoted_href}")
                    continue

                props = response.find(".//d:prop", ns)
                if props is None:
                    logger.debug(f"Response {i}: No properties found, skipping")
                    continue

                item_name = os.path.basename(unquoted_href)
                logger.debug(f"Response {i}: Item name extracted: '{item_name}'")

                item = {"name": item_name, "path": unquote(href_text), "is_dir": False, "size": 0}

                # Check if it's a directory
                resourcetype = props.find("d:resourcetype", ns)
                if resourcetype is not None:
                    collection = resourcetype.find("d:collection", ns)
                    item["is_dir"] = collection is not None

                # Get size
                size = props.find("d:getcontentlength", ns)
                if size is not None and size.text:
                    item["size"] = int(size.text)

                logger.debug(
                    f"Response {i}: Item details: name='{item['name']}', is_dir={item['is_dir']}, size={item['size']}"
                )

                # Filter out system files and directories
                if not self._should_show_item(item["name"], item["is_dir"]):
                    logger.info(f"Filtering out: {item['name']} (is_dir: {item['is_dir']})")
                    continue

                logger.info(
                    f"Including item: {item['name']} (is_dir: {item['is_dir']}, size: {item['size']})"
                )
                items.append(item)

        except Exception as e:
            logger.error(f"Error parsing PROPFIND response: {e}")
            logger.error(f"XML response (first 1000 chars): {xml_response[:1000]}")

        logger.info(f"Total items returned for {base_path}: {len(items)}")
        return items

    def get_file_info(self, path: str) -> dict | None:
        """Get information about a remote file"""
        url = urljoin(self.url, quote(path))

        headers = {"Depth": "0", "Content-Type": "application/xml"}

        # PROPFIND request body
        body = """<?xml version="1.0" encoding="utf-8"?>
        <propfind xmlns="DAV:">
            <prop>
                <displayname/>
                <getcontentlength/>
                <getlastmodified/>
                <getetag/>
            </prop>
        </propfind>"""

        try:
            response = self.session.request("PROPFIND", url, headers=headers, data=body)
            if response.status_code == 207:  # Multi-Status
                # Parse the response to get file info
                root = ET.fromstring(response.text)
                ns = {"d": "DAV:"}

                for response_elem in root.findall(".//d:response", ns):
                    href = response_elem.find("d:href", ns)
                    if href is None:
                        continue

                    props = response_elem.find(".//d:prop", ns)
                    if props is None:
                        continue

                    info = {
                        "path": unquote(href.text) if href.text else path,
                        "exists": True,
                        "size": 0,
                        "etag": None,
                        "last_modified": None,
                    }

                    # Get size
                    size_elem = props.find("d:getcontentlength", ns)
                    if size_elem is not None and size_elem.text:
                        info["size"] = int(size_elem.text)

                    # Get ETag (often contains checksum info)
                    etag_elem = props.find("d:getetag", ns)
                    if etag_elem is not None and etag_elem.text:
                        info["etag"] = etag_elem.text.strip('"')

                    # Get last modified
                    modified_elem = props.find("d:getlastmodified", ns)
                    if modified_elem is not None and modified_elem.text:
                        info["last_modified"] = modified_elem.text

                    return info

            elif response.status_code == 404:
                return {"exists": False, "path": path}
            else:
                logger.warning(f"Failed to get file info for {path}: {response.status_code}")
                return None

        except Exception as e:
            logger.error(f"Error getting file info for {path}: {e}")
            return None

    def download_file_head(self, path: str, size: int = 8192) -> bytes | None:
        """Download the first few bytes of a remote file for checksum comparison"""
        url = urljoin(self.url, quote(path))

        headers = {"Range": f"bytes=0-{size - 1}"}

        try:
            response = self.session.get(url, headers=headers)
            if response.status_code in [200, 206]:  # OK or Partial Content
                return response.content
            else:
                logger.warning(f"Failed to download file head for {path}: {response.status_code}")
                return None
        except Exception as e:
            logger.error(f"Error downloading file head for {path}: {e}")
            return None

    def download_file(self, remote_path: str, local_path: str) -> tuple[bool, str]:
        """Download a complete file from the WebDAV server
        Returns: (success, error_message)
        """
        url = urljoin(self.url, quote(remote_path))

        try:
            response = self.session.get(url, stream=True)
            if response.status_code == 200:
                with open(local_path, "wb") as f:
                    for chunk in response.iter_content(chunk_size=8192):
                        if chunk:
                            f.write(chunk)
                return True, ""
            else:
                error_msg = f"HTTP {response.status_code}: {response.reason}"
                return False, error_msg
        except Exception as e:
            return False, str(e)

    def create_directory(self, path: str) -> bool:
        """Create a directory on the WebDAV server"""
        url = urljoin(self.url, quote(path))
        try:
            logger.info(f"Creating directory at: {url}")
            response = self.session.request("MKCOL", url)
            logger.info(f"MKCOL response: {response.status_code} - {response.reason}")

            if response.status_code in [201, 204]:
                logger.info(f"Directory created successfully: {path}")
                return True
            elif response.status_code == 405:
                logger.info(f"Directory already exists: {path}")
                return True
            elif response.status_code == 403:
                logger.error(f"Permission denied creating directory: {path}")
                return False
            elif response.status_code == 409:
                logger.error(f"Conflict creating directory (parent may not exist): {path}")
                return False
            else:
                logger.error(
                    f"Failed to create directory {path}: {response.status_code} - {response.reason}"
                )
                if response.text:
                    logger.error(f"Response body: {response.text}")
                return False

        except Exception as e:
            logger.error(f"Error creating directory {path}: {e}")
            return False

    def upload_file_chunked(
        self, local_path: str, remote_path: str, progress_callback=None
    ) -> tuple[bool, str]:
        """Upload a file in chunks with progress callback using manual HTTP chunking"""
        try:
            file_size = os.path.getsize(local_path)
            url = urljoin(self.url, quote(remote_path))

            # Determine optimal chunk size based on file size
            def get_optimal_chunk_size(total_size):
                if total_size > 10 * 1024 * 1024 * 1024:  # > 10GB
                    return 4 * 1024 * 1024  # 4MB chunks for massive files
                elif total_size > 5 * 1024 * 1024 * 1024:  # > 5GB
                    return 2 * 1024 * 1024  # 2MB chunks for huge files
                elif total_size > 1024 * 1024 * 1024:  # > 1GB
                    return 1024 * 1024  # 1MB chunks for very large files
                elif total_size > 100 * 1024 * 1024:  # > 100MB
                    return 256 * 1024  # 256KB chunks for large files
                else:
                    return 64 * 1024  # 64KB chunks for smaller files

            chunk_size = get_optimal_chunk_size(file_size)
            chunk_size_mb = chunk_size / (1024 * 1024)
            total_size_mb = file_size / (1024 * 1024)
            logger.info(
                f"Upload chunking: {total_size_mb:.1f}MB file using {chunk_size_mb:.1f}MB chunks ({chunk_size:,} bytes)"
            )

            # Initialize progress
            if progress_callback:
                progress_callback(0, file_size)

            # Use manual chunked upload with multiple HTTP requests for true progress tracking
            # This approach sends the file in multiple smaller HTTP PUT requests
            bytes_uploaded = 0

            # For files larger than 100MB, use Range uploads if server supports it
            # Otherwise fall back to single upload
            if file_size > 100 * 1024 * 1024:
                # Try chunked upload approach - send file in multiple requests
                logger.info(f"Attempting chunked upload for large file ({total_size_mb:.1f}MB)")

                # Test if server supports Range requests by trying a small upload first
                try:
                    with open(local_path, "rb") as file:
                        # Read first chunk
                        first_chunk = file.read(chunk_size)

                        # Send first chunk with Range header
                        headers = {
                            "Content-Range": f"bytes 0-{len(first_chunk) - 1}/{file_size}",
                            "Content-Length": str(len(first_chunk)),
                        }

                        response = self.session.put(url, data=first_chunk, headers=headers)

                        if response.status_code in [200, 201, 204, 206, 308]:
                            # Server accepts Range uploads, continue with chunks
                            bytes_uploaded = len(first_chunk)
                            if progress_callback:
                                # Don't report 100% prematurely, even with small files
                                report_bytes = (
                                    min(bytes_uploaded, file_size - 1)
                                    if bytes_uploaded >= file_size
                                    else bytes_uploaded
                                )
                                progress_callback(report_bytes, file_size)

                            # Upload remaining chunks
                            while bytes_uploaded < file_size:
                                chunk = file.read(chunk_size)
                                if not chunk:
                                    break

                                start_byte = bytes_uploaded
                                end_byte = start_byte + len(chunk) - 1

                                headers = {
                                    "Content-Range": f"bytes {start_byte}-{end_byte}/{file_size}",
                                    "Content-Length": str(len(chunk)),
                                }

                                response = self.session.put(url, data=chunk, headers=headers)

                                if response.status_code not in [200, 201, 204, 206, 308]:
                                    # Chunk failed, fall back to regular upload
                                    logger.warning(
                                        f"Chunk upload failed at byte {start_byte}, falling back to regular upload"
                                    )
                                    break

                                bytes_uploaded += len(chunk)
                                if progress_callback:
                                    # Don't report 100% during chunked upload - let FileProcessor handle completion
                                    report_bytes = (
                                        min(bytes_uploaded, file_size - 1)
                                        if bytes_uploaded >= file_size
                                        else bytes_uploaded
                                    )
                                    progress_callback(report_bytes, file_size)

                            # Check if we completed the chunked upload
                            if bytes_uploaded >= file_size:
                                logger.info("Chunked upload completed successfully")
                                return True, ""

                        # If we get here, chunked upload failed, fall back to regular upload
                        logger.info(
                            "Server doesn't support chunked upload, falling back to regular upload"
                        )

                except Exception as e:
                    logger.warning(f"Chunked upload failed: {e}, falling back to regular upload")

            # Fall back to regular single-request upload
            # Use a simpler approach that at least shows some progress
            logger.info("Using regular upload with estimated progress")

            # Create a file-like object that gives periodic progress updates
            # This reads from disk in chunks and reports progress as data is SENT over network
            class TimedProgressFile:
                def __init__(self, filepath, progress_callback, total_size):
                    self.filepath = filepath
                    self.progress_callback = progress_callback
                    self.total_size = total_size
                    self.bytes_read = 0
                    self._file = None
                    self.last_report_time = time.time()
                    self.report_interval = 0.25  # Report every 0.25 seconds for smoother progress
                    self.last_reported_bytes = 0
                    # Use larger chunk size for better network performance
                    self.chunk_size = 1 * 1024 * 1024  # 1MB chunks for streaming

                def __enter__(self):
                    self._file = open(self.filepath, "rb")
                    return self

                def __exit__(self, exc_type, exc_val, exc_tb):
                    if self._file:
                        self._file.close()

                def read(self, size=-1):
                    """
                    Read method called by requests library to get data to send.
                    This is called repeatedly as data is sent over the network.
                    """
                    if not self._file:
                        return b""

                    # Use larger chunks for better performance
                    # requests will call this repeatedly until we return empty bytes
                    chunk_to_read = self.chunk_size if size == -1 else min(size, self.chunk_size)
                    data = self._file.read(chunk_to_read)

                    if data:
                        self.bytes_read += len(data)
                        current_time = time.time()
                        bytes_changed = self.bytes_read - self.last_reported_bytes
                        time_elapsed = current_time - self.last_report_time

                        # Report progress frequently for smooth updates
                        # Report if: enough time passed OR significant data sent
                        if time_elapsed >= self.report_interval or bytes_changed >= (512 * 1024):  # 512KB threshold
                            if self.progress_callback:
                                # Don't report 100% until file is completely read
                                report_bytes = self.bytes_read
                                if report_bytes >= self.total_size:
                                    # File reading is complete, but don't show 100% yet
                                    # Let the caller decide when to show 100%
                                    report_bytes = max(0, self.total_size - 1)

                                self.progress_callback(report_bytes, self.total_size)

                            self.last_report_time = current_time
                            self.last_reported_bytes = self.bytes_read

                    # Always report final progress when file is completely read
                    if not data and self.bytes_read > 0 and self.progress_callback:
                        # File reading complete - report final progress (still capped at 99%)
                        report_bytes = min(self.bytes_read, max(0, self.total_size - 1))
                        self.progress_callback(report_bytes, self.total_size)

                    return data

                def __len__(self):
                    return self.total_size

                def __iter__(self):
                    """Make this iterable for streaming support"""
                    return self

                def __next__(self):
                    """Iterator protocol for streaming"""
                    data = self.read(self.chunk_size)
                    if not data:
                        raise StopIteration
                    return data

            # Upload with timed progress tracking, streaming, and retry logic for transient errors
            # The file-like object will be read chunk-by-chunk as data is sent over network
            retry_count = 0
            max_retries = 3
            timeout = 300  # 5 minutes default timeout
            last_error = None

            while retry_count <= max_retries:
                try:
                    with TimedProgressFile(local_path, progress_callback, file_size) as progress_file:
                        # Important: Set Content-Length header to enable proper streaming
                        # Without this, requests might buffer the entire file
                        headers = {"Content-Length": str(file_size)}
                        response = self.session.put(url, data=progress_file, headers=headers, timeout=timeout)

                    # Check if upload was successful
                    if response.status_code in [200, 201, 204]:
                        if retry_count > 0:
                            logger.info(f"Upload succeeded after {retry_count} retry/retries")
                        return True, ""

                    # Handle transient server errors that should be retried
                    elif response.status_code in [502, 503, 504]:
                        retry_count += 1
                        last_error = f"HTTP {response.status_code}: {response.reason}"

                        if retry_count <= max_retries:
                            wait_time = min(2 ** retry_count, 30)  # Exponential backoff, max 30 seconds
                            logger.warning(
                                f"Upload failed with HTTP {response.status_code} ({response.reason}). "
                                f"Retry {retry_count}/{max_retries} after {wait_time} seconds..."
                            )
                            time.sleep(wait_time)
                            continue

                        error_msg = f"HTTP {response.status_code} ({response.reason}): {response.text[:200]}"
                        logger.error(f"Upload failed after {max_retries} retries: {error_msg}")
                        return False, error_msg

                    # Handle permission errors and other client errors (4xx)
                    elif 400 <= response.status_code < 500:
                        error_msg = f"HTTP {response.status_code} ({response.reason}): {response.text[:200]}"
                        logger.error(f"Upload failed with client error: {error_msg}")
                        return False, error_msg

                    # Handle other server errors (5xx) that aren't in retry list
                    elif response.status_code >= 500:
                        error_msg = f"HTTP {response.status_code} ({response.reason}): {response.text[:200]}"
                        logger.error(f"Upload failed with server error: {error_msg}")
                        return False, error_msg

                    # Unexpected status code
                    else:
                        error_msg = f"Unexpected HTTP {response.status_code} ({response.reason}): {response.text[:200]}"
                        logger.error(f"Upload failed: {error_msg}")
                        return False, error_msg

                except Exception as e:
                    retry_count += 1
                    last_error = str(e)

                    if retry_count <= max_retries:
                        wait_time = min(2 ** retry_count, 30)
                        logger.warning(
                            f"Upload exception: {last_error}. "
                            f"Retry {retry_count}/{max_retries} after {wait_time} seconds..."
                        )
                        time.sleep(wait_time)
                        continue

                    error_msg = f"Upload failed after {max_retries} retries: {last_error}"
                    logger.error(error_msg)
                    return False, error_msg

            # Should never reach here, but just in case
            error_msg = last_error or "Upload failed for unknown reason"
            return False, error_msg

        except Exception as e:
            error_msg = str(e)
            logger.error(f"Error uploading file: {error_msg}")
            return False, error_msg

        except Exception as e:
            error_msg = str(e)
            logger.error(f"Error uploading file: {error_msg}")
            return False, error_msg

    def store_checksum(self, file_path: str, checksum: str) -> bool:
        """Store checksum metadata for a file on the remote server"""
        try:
            # Store checksum as extended attribute or in a companion .checksum file
            checksum_path = f"{file_path}.checksum"
            url = urljoin(self.url + "/", checksum_path.lstrip("/"))

            # Upload checksum as a small text file
            response = self.session.put(url, data=checksum.encode("utf-8"))

            if response.status_code in [200, 201, 204]:
                logger.debug(f"Stored checksum for {file_path}: {checksum}")
                return True
            else:
                logger.warning(f"Failed to store checksum for {file_path}: {response.status_code}")
                return False

        except Exception as e:
            logger.error(f"Error storing checksum for {file_path}: {e}")
            return False

    def get_stored_checksum(self, file_path: str) -> str | None:
        """Retrieve stored checksum for a file from the remote server"""
        try:
            checksum_path = f"{file_path}.checksum"
            url = urljoin(self.url + "/", checksum_path.lstrip("/"))

            response = self.session.get(url)

            if response.status_code == 200:
                checksum = response.text.strip()
                logger.debug(f"Retrieved stored checksum for {file_path}: {checksum}")
                return checksum
            elif response.status_code == 404:
                logger.debug(f"No stored checksum found for {file_path}")
                return None
            else:
                logger.warning(
                    f"Failed to retrieve checksum for {file_path}: {response.status_code}"
                )
                return None

        except Exception as e:
            logger.error(f"Error retrieving checksum for {file_path}: {e}")
            return None


class FileMonitorHandler(FileSystemEventHandler):
    """
    Handles file system events for real-time file monitoring.

    This class extends watchdog's FileSystemEventHandler to monitor directory
    changes and queue files for upload when they meet criteria:
    - File extension matches configured list
    - File is stable (not being written to)
    - File is not a system/hidden file

    Implements intelligent file stability detection to avoid uploading
    files that are still being written by other processes.
    """

    def __init__(
        self,
        extensions: list[str],
        file_queue: queue.Queue,
        monitor_subdirs: bool = True,
        app_instance=None,
    ):
        """
        Initialize file monitor with configuration.

        Args:
            extensions: List of file extensions to monitor (e.g., ['raw', 'mzML'])
            file_queue: Thread-safe queue for passing files to processor
            monitor_subdirs: Whether to monitor subdirectories recursively
            app_instance: Reference to main application for duplicate tracking
        """
        # Normalize extensions to lowercase with leading dots
        self.extensions = [
            ext.lower() if ext.startswith(".") else f".{ext.lower()}" for ext in extensions
        ]
        self.file_queue = file_queue
        self.monitor_subdirs = monitor_subdirs
        self.app_instance = app_instance
        self.pending_files = {}  # Track files being written with timestamps

        # Log configuration for debugging
        logger.info(f"FileMonitorHandler initialized with extensions: {self.extensions}")
        logger.info(f"Monitor subdirectories: {monitor_subdirs}")

    def on_created(self, event):
        """Handle file creation events."""
        try:
            if not event.is_directory:
                logger.debug(f"OS Event - File created: {event.src_path}")
                self._handle_file(event.src_path)
        except Exception as e:
            logger.error(
                f"Error handling file creation event for {getattr(event, 'src_path', 'unknown')}: {e}",
                exc_info=True,
            )

    def on_modified(self, event):
        """Handle file modification events."""
        try:
            if not event.is_directory:
                logger.debug(f"OS Event - File modified: {event.src_path}")
                self._handle_file(event.src_path)
        except Exception as e:
            logger.error(
                f"Error handling file modification event for {getattr(event, 'src_path', 'unknown')}: {e}",
                exc_info=True,
            )

    def on_moved(self, event):
        """Handle file move events."""
        try:
            if not event.is_directory:
                logger.debug(f"OS Event - File moved: {event.src_path} -> {event.dest_path}")
                self._handle_file(event.dest_path)
        except Exception as e:
            logger.error(
                f"Error handling file move event for {getattr(event, 'dest_path', 'unknown')}: {e}",
                exc_info=True,
            )

    def _handle_file(self, filepath):
        """
        Process file events and queue stable files for upload.

        Implements file stability detection by tracking file size changes
        over time. Only queues files when they haven't changed size for
        a specified period, indicating the write operation is complete.

        Args:
            filepath: Absolute path to the file that triggered the event
        """
        try:
            # Skip hidden files and system files (start with . or ~)
            filename = os.path.basename(filepath)
            if filename.startswith(".") or filename.startswith("~"):
                return

            # Check if file extension matches our monitored list
            if any(filepath.lower().endswith(ext) for ext in self.extensions):
                current_time = time.time()
                logger.info(f"File event detected: {filepath}")

                if filepath in self.pending_files:
                    # Check if file size is stable
                    try:
                        # Check if file still exists before accessing
                        if not os.path.exists(filepath):
                            logger.warning(
                                f"File no longer exists, removing from monitoring: {filepath}"
                            )
                            self.pending_files.pop(filepath, None)
                            return

                        current_size = os.path.getsize(filepath)
                        last_size, last_time = self.pending_files[filepath]

                        # Reduced stability timeout for faster detection
                        if current_size == last_size and current_time - last_time > 1:
                            # File is stable, check for duplicates before queueing
                            if self._should_queue_file(filepath):
                                logger.info(
                                    f"Queuing stable file: {filepath} (size: {current_size} bytes)"
                                )
                                self.file_queue.put(filepath)
                                # Add to transfer table safely using QMetaObject.invokeMethod for thread-safe UI calls
                                if self.app_instance:
                                    try:
                                        # Use QMetaObject.invokeMethod for safe cross-thread UI calls
                                        QMetaObject.invokeMethod(
                                            self.app_instance,
                                            "add_queued_file_to_table",
                                            Qt.ConnectionType.QueuedConnection,
                                            Q_ARG(str, filepath),
                                        )
                                        logger.debug(
                                            f"Successfully scheduled UI update for {filepath} via QMetaObject.invokeMethod"
                                        )
                                    except Exception as ui_error:
                                        logger.error(
                                            f"Error scheduling UI table update for {filepath}: {ui_error}"
                                        )
                                self.pending_files.pop(filepath, None)
                                logger.info(
                                    f"File queued for transfer: {filepath} (queue size now: {self.file_queue.qsize()})"
                                )
                            else:
                                self.pending_files.pop(filepath, None)
                                logger.info(
                                    f"File already queued or processing, skipping: {filepath}"
                                )
                        else:
                            # Update tracking
                            self.pending_files[filepath] = (current_size, current_time)
                            logger.debug(f"File size changed, continuing to monitor: {filepath}")
                    except (OSError, PermissionError) as e:
                        # Handle file access errors gracefully - common during copying
                        logger.warning(
                            f"File access error for {filepath} (likely being copied): {e}"
                        )
                        # Keep the file in monitoring for now, it might become available
                        if filepath not in self.pending_files:
                            self.pending_files[filepath] = (0, current_time)
                    except Exception as e:
                        logger.error(
                            f"Unexpected error checking file size for {filepath}: {e}",
                            exc_info=True,
                        )
                        # Remove from tracking to prevent repeated errors
                        self.pending_files.pop(filepath, None)
                else:
                    # New file, start tracking
                    try:
                        # Check if file exists and is accessible
                        if not os.path.exists(filepath):
                            logger.warning(f"New file event for non-existent file: {filepath}")
                            # Clean up queued_files if file was previously queued but no longer exists
                            if self.app_instance and filepath in self.app_instance.queued_files:
                                self.app_instance.queued_files.discard(filepath)
                                logger.info(
                                    f"Removed non-existent file from queued_files: {filepath}"
                                )
                            return

                        size = os.path.getsize(filepath)
                        self.pending_files[filepath] = (size, current_time)
                        logger.info(f"Started monitoring new file: {filepath} (size: {size} bytes)")

                        # For moved/copied files that are already complete,
                        # schedule a stability check in a few seconds
                        def delayed_check():
                            try:
                                time.sleep(1.5)  # Reduced from 3 to 1.5 seconds for faster response
                                # Use a safer check to avoid race conditions in tests
                                if (
                                    hasattr(self, "pending_files")
                                    and filepath in self.pending_files
                                ):
                                    try:
                                        # Check if file still exists
                                        if not os.path.exists(filepath):
                                            logger.info(
                                                f"File no longer exists during delayed check: {filepath}"
                                            )
                                            if hasattr(self, "pending_files"):
                                                self.pending_files.pop(filepath, None)
                                            return

                                        current_size = os.path.getsize(filepath)
                                        stored_info = self.pending_files.get(filepath)
                                        if stored_info and current_size == stored_info[0]:
                                            # File hasn't changed, check for duplicates before queueing
                                            if self._should_queue_file(filepath):
                                                logger.info(
                                                    f"Delayed check: Queuing stable file: {filepath} (size: {current_size} bytes)"
                                                )
                                                self.file_queue.put(filepath)
                                                # Add to transfer table safely using QMetaObject.invokeMethod for thread-safe UI calls
                                                if self.app_instance:
                                                    try:
                                                        # Use QMetaObject.invokeMethod for safe cross-thread UI calls
                                                        QMetaObject.invokeMethod(
                                                            self.app_instance,
                                                            "add_queued_file_to_table",
                                                            Qt.ConnectionType.QueuedConnection,
                                                            Q_ARG(str, filepath),
                                                        )
                                                        logger.debug(
                                                            f"Successfully scheduled UI update for {filepath} via QMetaObject.invokeMethod"
                                                        )
                                                    except Exception as ui_error:
                                                        logger.error(
                                                            f"Error scheduling UI table update for {filepath}: {ui_error}"
                                                        )
                                                if hasattr(self, "pending_files"):
                                                    self.pending_files.pop(filepath, None)
                                                logger.info(
                                                    f"File queued for transfer after stability check: {filepath} (queue size now: {self.file_queue.qsize()})"
                                                )
                                            else:
                                                if hasattr(self, "pending_files"):
                                                    self.pending_files.pop(filepath, None)
                                                logger.info(
                                                    f"File already queued or processing, skipping: {filepath}"
                                                )
                                    except (OSError, PermissionError) as e:
                                        logger.warning(
                                            f"File access error during delayed check for {filepath}: {e}"
                                        )
                                        # Keep monitoring, file might become available later
                                    except Exception as e:
                                        logger.error(
                                            f"Unexpected error in delayed stability check for {filepath}: {e}",
                                            exc_info=True,
                                        )
                                        # Clean up to prevent repeated errors
                                        if hasattr(self, "pending_files"):
                                            self.pending_files.pop(filepath, None)
                            except Exception as e:
                                logger.error(
                                    f"Critical error in delayed check thread for {filepath}: {e}",
                                    exc_info=True,
                                )
                                # Ensure cleanup even on critical errors
                                if hasattr(self, "pending_files"):
                                    self.pending_files.pop(filepath, None)

                        # Only start delayed check thread if not in test environment
                        # Check if we're running in pytest
                        import sys

                        if "pytest" not in sys.modules:
                            check_thread = threading.Thread(target=delayed_check, daemon=True)
                            check_thread.start()
                        else:
                            # In test environment, call delayed check directly without threading
                            # but without the sleep to make tests run faster
                            def immediate_check():
                                try:
                                    # Use a safer check to avoid race conditions in tests
                                    if (
                                        hasattr(self, "pending_files")
                                        and filepath in self.pending_files
                                    ):
                                        try:
                                            # Check if file still exists
                                            if not os.path.exists(filepath):
                                                logger.info(
                                                    f"File no longer exists during immediate check: {filepath}"
                                                )
                                                if hasattr(self, "pending_files"):
                                                    self.pending_files.pop(filepath, None)
                                                return

                                            current_size = os.path.getsize(filepath)
                                            stored_info = self.pending_files.get(filepath)
                                            if stored_info and current_size == stored_info[0]:
                                                # File hasn't changed, check for duplicates before queueing
                                                if self._should_queue_file(filepath):
                                                    logger.info(
                                                        f"Immediate check: Queuing stable file: {filepath} (size: {current_size} bytes)"
                                                    )
                                                    self.file_queue.put(filepath)
                                                    # Add to transfer table safely using QMetaObject.invokeMethod for thread-safe UI calls
                                                    if self.app_instance:
                                                        try:
                                                            # Use QMetaObject.invokeMethod for safe cross-thread UI calls
                                                            QMetaObject.invokeMethod(
                                                                self.app_instance,
                                                                "add_queued_file_to_table",
                                                                Qt.ConnectionType.QueuedConnection,
                                                                Q_ARG(str, filepath),
                                                            )
                                                            logger.debug(
                                                                f"Successfully scheduled UI update for {filepath} via QMetaObject.invokeMethod"
                                                            )
                                                        except Exception as ui_error:
                                                            logger.error(
                                                                f"Error scheduling UI table update for {filepath}: {ui_error}"
                                                            )
                                                    if hasattr(self, "pending_files"):
                                                        self.pending_files.pop(filepath, None)
                                                    logger.info(
                                                        f"File queued for transfer after immediate check: {filepath} (queue size now: {self.file_queue.qsize()})"
                                                    )
                                                else:
                                                    if hasattr(self, "pending_files"):
                                                        self.pending_files.pop(filepath, None)
                                                    logger.info(
                                                        f"File already queued or processing, skipping: {filepath}"
                                                    )
                                        except (OSError, PermissionError) as e:
                                            logger.warning(
                                                f"File access error during immediate check for {filepath}: {e}"
                                            )
                                            # Keep monitoring, file might become available later
                                        except Exception as e:
                                            logger.error(
                                                f"Unexpected error in immediate stability check for {filepath}: {e}",
                                                exc_info=True,
                                            )
                                            # Clean up to prevent repeated errors
                                            if hasattr(self, "pending_files"):
                                                self.pending_files.pop(filepath, None)
                                except Exception as e:
                                    logger.error(
                                        f"Critical error in immediate check for {filepath}: {e}",
                                        exc_info=True,
                                    )
                                    # Ensure cleanup even on critical errors
                                    if hasattr(self, "pending_files"):
                                        self.pending_files.pop(filepath, None)

                            immediate_check()
                            logger.debug(
                                f"Test environment detected, using immediate check for {filepath}"
                            )

                    except (OSError, PermissionError) as e:
                        logger.warning(
                            f"File access error when starting to monitor {filepath} (likely being copied): {e}"
                        )

                        # Schedule a retry for files that might be in the process of being copied
                        def retry_monitoring():
                            try:
                                time.sleep(2.0)  # Wait a bit longer for copy to complete
                                if os.path.exists(filepath) and filepath not in self.pending_files:
                                    self._handle_file(filepath)  # Retry monitoring
                            except Exception as retry_error:
                                logger.error(
                                    f"Error in retry monitoring for {filepath}: {retry_error}"
                                )

                        retry_thread = threading.Thread(target=retry_monitoring, daemon=True)
                        retry_thread.start()
                    except Exception as e:
                        logger.error(
                            f"Unexpected error starting to monitor file {filepath}: {e}",
                            exc_info=True,
                        )
            else:
                # Log files that don't match extensions for debugging
                try:
                    ext = os.path.splitext(filepath)[1]
                    logger.debug(
                        f"File ignored (extension '{ext}' not in {self.extensions}): {filepath}"
                    )
                except Exception as e:
                    logger.error(f"Error processing file extension for {filepath}: {e}")
        except Exception as e:
            logger.error(f"Critical error in file handler for {filepath}: {e}", exc_info=True)
            # Ensure cleanup on critical errors
            if filepath in self.pending_files:
                self.pending_files.pop(filepath, None)

    def _should_queue_file(self, filepath: str) -> bool:
        """
        Check if a file should be queued, preventing duplicates and avoiding re-upload of unchanged files.

        Args:
            filepath: Path to the file being considered for queueing

        Returns:
            True if file should be queued, False if already queued/processing or unchanged
        """
        if self.app_instance:
            # Check if file is already queued or being processed
            if filepath in self.app_instance.queued_files:
                logger.debug(f"File already queued, skipping: {filepath}")
                return False
            if filepath in self.app_instance.processing_files:
                logger.debug(f"File currently processing, skipping: {filepath}")
                return False

            # Check if file was already uploaded and hasn't changed
            if filepath in self.app_instance.upload_history:
                try:
                    # Calculate current file checksum using FileProcessor method
                    current_checksum = self.app_instance.file_processor.calculate_checksum(filepath)
                    stored_info = self.app_instance.upload_history[filepath]
                    stored_checksum = stored_info.get('checksum', '')

                    if current_checksum == stored_checksum:
                        logger.info(f"File unchanged since last upload, skipping: {filepath}")
                        return False
                    else:
                        logger.info(f"File modified since last upload, will re-upload: {filepath}")
                except Exception as e:
                    logger.warning(f"Error checking file checksum for {filepath}: {e}")
                    # Continue with upload if we can't verify the checksum

            # Add to queued files tracking
            self.app_instance.queued_files.add(filepath)
            return True
        else:
            # Fallback if no app instance - always queue (original behavior)
            return True


class FileProcessor(QThread):
    """
    Background thread for processing file transfers to WebDAV server.

    This QThread-based class handles the core file processing workflow:
    1. Retrieves files from the monitoring queue
    2. Calculates SHA256 checksums for upload metadata
    3. Checks for conflicts with existing remote files
    4. Handles user conflict resolution decisions
    5. Uploads files with progress tracking
    6. Verifies successful uploads
    7. Stores checksums for future reference

    Runs continuously in background to process files without blocking UI.
    Communicates with main thread via Qt signals for progress updates.
    """

    # Qt signals for communicating with main UI thread
    progress_update = pyqtSignal(str, int, int)  # filepath, bytes_transferred, total_bytes
    status_update = pyqtSignal(str, str, str)  # filename, status_message, filepath
    transfer_complete = pyqtSignal(
        str, str, bool, str
    )  # filename, filepath, success, result_message
    conflict_detected = pyqtSignal(str, dict, str)  # filepath, remote_info, local_checksum
    conflict_resolution_needed = pyqtSignal(
        str, str, str, dict
    )  # filename, filepath, remote_path, conflict_details

    def __init__(self, file_queue: queue.Queue, app_instance=None):
        """
        Initialize file processor thread.

        Args:
            file_queue: Thread-safe queue containing files to process
            app_instance: Reference to main application for tracking file states
        """
        super().__init__()
        self.file_queue = file_queue
        self.app_instance = app_instance
        self.webdav_client = None  # Set later via set_webdav_client()
        self.remote_base_path = "/"  # Remote directory base path
        self.running = True  # Control flag for thread loop
        self.preserve_structure = True  # Whether to preserve local directory structure
        self.local_base_path = ""  # Local directory base path
        self.conflict_resolution: str | None = None  # User's conflict resolution choice
        self.apply_to_all = False  # Apply resolution to all conflicts

    def set_webdav_client(self, client: WebDAVClient, remote_path: str):
        """
        Configure WebDAV client and remote path for transfers.

        Args:
            client: Configured WebDAVClient instance
            remote_path: Base remote directory path for uploads
        """
        self.webdav_client = client
        self.remote_base_path = remote_path.rstrip("/")

    def set_local_base(self, path: str):
        """
        Set local base path for directory structure preservation.

        Args:
            path: Local directory path to use as base for relative paths
        """
        self.local_base_path = path

    def calculate_checksum(
        self, filepath: str, algorithm: str = "sha256", chunk_size: int | None = None
    ) -> str:
        """
        Calculate file checksum for upload metadata with local caching.

        Uses local cache to avoid recalculating checksums for unchanged files.
        Cache key includes file path, size, and modification time.

        Args:
            filepath: Path to file to checksum
            algorithm: Hash algorithm to use (default: sha256)
            chunk_size: Bytes to read per chunk (default: 256KB)

        Returns:
            Hexadecimal checksum string
        """
        try:
            # Get file stats for cache key
            stat = os.stat(filepath)
            file_size = stat.st_size
            file_mtime = stat.st_mtime

            # Create cache key (file path + size + mtime)
            cache_key = f"{filepath}|{file_size}|{file_mtime:.0f}"

            # Check if we have a cached checksum for this exact file state
            if self.app_instance and hasattr(self.app_instance, "local_checksum_cache"):
                cached = self.app_instance.local_checksum_cache.get(cache_key)
                if cached:
                    logger.debug(
                        f"Using cached checksum for {os.path.basename(filepath)}: {cached[:8]}..."
                    )
                    return cached

            # Calculate new checksum
            logger.debug(
                f"Calculating new checksum for {os.path.basename(filepath)} ({file_size:,} bytes)"
            )

            if chunk_size is None:
                chunk_size = 256 * 1024  # 256KB chunks - optimal balance of speed and memory

            hash_obj = hashlib.new(algorithm)
            with open(filepath, "rb") as f:
                while chunk := f.read(chunk_size):
                    hash_obj.update(chunk)

            checksum = hash_obj.hexdigest()

            # Cache the result
            if self.app_instance and hasattr(self.app_instance, "local_checksum_cache"):
                self.app_instance.local_checksum_cache[cache_key] = checksum
                logger.debug(f"Cached checksum for {os.path.basename(filepath)}: {checksum[:8]}...")

                # Limit cache size to prevent memory issues
                if len(self.app_instance.local_checksum_cache) > 1000:
                    # Remove oldest entries (simple cleanup)
                    cache_items = list(self.app_instance.local_checksum_cache.items())
                    for key, _ in cache_items[:100]:  # Remove first 100 entries
                        del self.app_instance.local_checksum_cache[key]
                    logger.debug(
                        f"Cleaned checksum cache, now {len(self.app_instance.local_checksum_cache)} entries"
                    )

            return checksum

        except Exception as e:
            logger.error(f"Error calculating checksum for {filepath}: {e}")
            raise

    def run(self):
        """Main processing loop"""
        logger.info("FileProcessor thread started - beginning queue processing")
        while self.running:
            try:
                # Get file from queue (timeout allows checking self.running)
                file_item = self.file_queue.get(timeout=1)
                logger.info(f"FileProcessor: Retrieved item from queue: {file_item}")

                if self.webdav_client:
                    try:
                        # Handle both string paths and dict objects with resolution info
                        if isinstance(file_item, dict):
                            logger.info(f"Processing conflict resolution item: {file_item}")
                            self.process_file_with_resolution(file_item)
                        else:
                            logger.info(f"Processing regular file item: {file_item}")
                            self.process_file(file_item)
                    except Exception as process_error:
                        logger.error(
                            f"Error processing file item {file_item}: {process_error}",
                            exc_info=True,
                        )
                        # Update status to show error
                        filename = "Unknown"
                        filepath = ""
                        try:
                            if isinstance(file_item, dict):
                                filename = file_item.get("filename", "Unknown")
                                filepath = file_item.get("filepath", "")
                            else:
                                filename = os.path.basename(file_item)
                                filepath = file_item
                        except Exception as name_error:
                            logger.error(f"Error extracting filename from file_item: {name_error}")

                        # Clean up tracking and notify UI of failure
                        if filepath and self.app_instance:
                            self.app_instance.queued_files.discard(filepath)
                            self.app_instance.processing_files.discard(filepath)

                        self.status_update.emit(filename, "Error", filepath)
                        self.transfer_complete.emit(
                            filename, filepath, False, f"Processing error: {str(process_error)}"
                        )
                else:
                    logger.warning(
                        "FileProcessor: No WebDAV client configured - cannot process files"
                    )
                    # Remove from queued files if processing failed
                    if isinstance(file_item, str) and self.app_instance:
                        self.app_instance.queued_files.discard(file_item)
                        # Update status in table
                        filename = os.path.basename(file_item)
                        self.status_update.emit(filename, "Failed", file_item)
                        self.transfer_complete.emit(
                            filename, file_item, False, "No WebDAV connection configured"
                        )

            except queue.Empty:
                # No items in queue, continue loop
                continue
            except Exception as e:
                logger.error(f"Critical error in FileProcessor main loop: {e}", exc_info=True)
                # Don't break the loop - continue processing other files
                continue

    def process_file_with_resolution(self, file_item: dict):
        """Process a file that already has conflict resolution"""
        filepath = file_item["filepath"]
        filename = file_item["filename"]
        remote_path = file_item["remote_path"]
        resolution = file_item["resolution"]
        new_name = file_item.get("new_name")

        # Track that this file is now being processed
        if self.app_instance:
            self.app_instance.queued_files.discard(filepath)
            self.app_instance.processing_files.add(filepath)

        try:
            # Apply resolution
            if resolution == "skip":
                self.transfer_complete.emit(filename, filepath, True, "Skipped due to user choice")
                return
            elif resolution == "rename" and new_name:
                # Update remote path and filename for rename
                remote_dir = os.path.dirname(remote_path)
                remote_path = f"{remote_dir}/{new_name}".replace("//", "/")
                filename = new_name
            # For 'overwrite', use original remote_path

            # Proceed with upload
            self.upload_file(filepath, remote_path, filename)

        except Exception as e:
            logger.error(f"Error processing file with resolution {filepath}: {e}")
            self.transfer_complete.emit(filename, filepath, False, f"Error: {str(e)}")
        finally:
            # Always remove from processing files when done
            if self.app_instance:
                self.app_instance.processing_files.discard(filepath)

    def is_file_accessible(self, filepath: str) -> tuple[bool, str]:
        """Check if a file can be opened for reading"""
        try:
            with open(filepath, "rb") as f:
                # Try to read the first byte to ensure file is truly accessible
                f.read(1)
            return True, ""
        except PermissionError as e:
            return False, f"Permission denied: {str(e)}"
        except OSError as e:
            return False, f"IO error: {str(e)}"
        except Exception as e:
            return False, f"Access error: {str(e)}"

    def schedule_locked_file_retry(
        self, filepath: str, remote_path: str, filename: str, access_error: str
    ):
        """Schedule a retry for a locked file after appropriate wait time"""
        if not hasattr(self, "locked_file_retries"):
            self.locked_file_retries = {}

        # Track retry count for this file
        retry_key = filepath
        retry_count = self.locked_file_retries.get(retry_key, 0)
        max_retries = self.app_instance.max_retries_spin.value()

        if retry_count >= max_retries:
            # Give up after max retries
            self.transfer_complete.emit(
                filename,
                filepath,
                False,
                f"File remained locked after {max_retries} attempts over {int((self.app_instance.initial_wait_spin.value() * 60 + max_retries * self.app_instance.retry_interval_spin.value()) / 60)} minutes. File may still be in use by instrument or analysis software.",
            )
            self.locked_file_retries.pop(retry_key, None)
            return

        # Calculate wait time and create user-friendly status messages
        if retry_count == 0:
            # First attempt - use initial wait time
            wait_time_ms = (
                self.app_instance.initial_wait_spin.value() * 60 * 1000
            )  # Convert minutes to ms
            wait_minutes = self.app_instance.initial_wait_spin.value()
            status_msg = (
                f"File locked - waiting {wait_minutes} minutes for instrument to finish writing..."
            )
        else:
            # Subsequent attempts - use retry interval
            wait_time_ms = (
                self.app_instance.retry_interval_spin.value() * 1000
            )  # Convert seconds to ms
            wait_seconds = self.app_instance.retry_interval_spin.value()
            status_msg = f"File still locked - trying again in {wait_seconds}s (attempt {retry_count + 1} of {max_retries})"

        # Update status with clear, user-friendly message
        self.status_update.emit(filename, status_msg, filepath)

        # Schedule retry using QTimer
        self.locked_file_retries[retry_key] = retry_count + 1

        retry_timer = QTimer()
        retry_timer.setSingleShot(True)
        retry_timer.timeout.connect(
            lambda: self.retry_locked_file(filepath, remote_path, filename, retry_timer)
        )
        retry_timer.start(wait_time_ms)

        # Create a progress timer that updates status during wait (for initial long wait only)
        if retry_count == 0:
            wait_minutes = self.app_instance.initial_wait_spin.value()
            self._start_progress_countdown(
                filepath, filename, wait_time_ms, wait_minutes, "minutes"
            )

        logger.info(
            f"Scheduled locked file retry for {filename} (attempt {retry_count + 1}) in {wait_time_ms / 1000:.1f}s"
        )

    def _start_progress_countdown(
        self,
        filepath: str,
        filename: str,
        total_wait_ms: int,
        total_time_value: int,
        time_unit: str,
    ):
        """Start a countdown timer to show progress during file lock wait"""
        if not hasattr(self, "progress_timers"):
            self.progress_timers = {}

        # Update every 10 seconds for minutes, every second for seconds
        update_interval_ms = 10000 if time_unit == "minutes" else 1000
        elapsed_time = 0

        progress_timer = QTimer()
        progress_timer.timeout.connect(
            lambda: self._update_progress_countdown(
                filepath,
                filename,
                elapsed_time,
                total_wait_ms,
                total_time_value,
                time_unit,
                progress_timer,
            )
        )

        self.progress_timers[filepath] = {
            "timer": progress_timer,
            "elapsed": 0,
            "total_ms": total_wait_ms,
            "total_value": total_time_value,
            "unit": time_unit,
        }

        progress_timer.start(update_interval_ms)

    def _update_progress_countdown(
        self,
        filepath: str,
        filename: str,
        elapsed_ms: int,
        total_ms: int,
        total_value: int,
        unit: str,
        timer: QTimer,
    ):
        """Update the countdown progress display"""
        if filepath not in self.progress_timers:
            timer.stop()
            return

        progress_info = self.progress_timers[filepath]
        progress_info["elapsed"] += 10000 if unit == "minutes" else 1000

        remaining_ms = total_ms - progress_info["elapsed"]

        if remaining_ms <= 0:
            # Time's up, stop progress timer
            timer.stop()
            self.progress_timers.pop(filepath, None)
            return

        if unit == "minutes":
            elapsed_minutes = int(progress_info["elapsed"] / 60000)
            progress_msg = f"File locked - waiting for instrument ({elapsed_minutes}/{total_value} minutes elapsed)"
        else:
            remaining_seconds = max(1, int(remaining_ms / 1000))
            progress_msg = f"File still locked - trying again in {remaining_seconds}s..."

        self.status_update.emit(filename, progress_msg, filepath)

    def retry_locked_file(self, filepath: str, remote_path: str, filename: str, timer: QTimer):
        """Retry uploading a previously locked file"""
        timer.deleteLater()  # Clean up timer

        retry_key = filepath

        # Check if file still exists
        if not os.path.exists(filepath):
            self.transfer_complete.emit(filename, filepath, False, "File no longer exists")
            self.locked_file_retries.pop(retry_key, None)
            return

        # Check if we still have retry info
        if retry_key not in self.locked_file_retries:
            logger.warning(f"No retry info found for {filename}, attempting upload anyway")
            self.upload_file(filepath, remote_path, filename)
            return

        retry_info = self.locked_file_retries[retry_key]
        retry_info["attempts"] += 1

        # Clean up any existing progress timer
        if "progress_timer_id" in retry_info:
            timer_id = retry_info.pop("progress_timer_id")
            if (
                hasattr(self.app_instance, "progress_timers")
                and timer_id in self.app_instance.progress_timers
            ):
                self.app_instance.progress_timers[timer_id].stop()
                del self.app_instance.progress_timers[timer_id]

        # Check if file is still accessible
        accessible, access_error = self.is_file_accessible(filepath)
        if not accessible:
            # Still locked, check max retries
            if retry_info["attempts"] >= retry_info["max_retries"]:
                # Clean up retry info
                self.locked_file_retries.pop(retry_key, None)
                # Update status to failed
                self.status_update.emit(filename, "File locked - max retries exceeded", filepath)
                self.transfer_complete.emit(
                    filename,
                    filepath,
                    False,
                    f"File remained locked after {retry_info['attempts']} attempts: {access_error}",
                )
                return
            else:
                # Schedule another retry
                self.schedule_locked_file_retry(filepath, remote_path, filename, access_error)
                return

        # File is now accessible, clean up retry tracking
        self.locked_file_retries.pop(retry_key, None)

        # Try uploading again
        logger.info(f"Retrying locked file: {filename}")
        self.upload_file(filepath, remote_path, filename)

    def upload_file(self, filepath: str, remote_path: str, filename: str):
        """Upload file to remote path"""
        try:
            # Always check if file is accessible (locked file handling always enabled)
            accessible, access_error = self.is_file_accessible(filepath)
            local_size = os.path.getsize(filepath)
            local_date = datetime.fromtimestamp(os.path.getmtime(filepath))
            if not accessible:
                # File is locked, schedule retry
                self.schedule_locked_file_retry(filepath, remote_path, filename, access_error)
                return

            # Calculate checksum for verification - this will also fail if file is locked
            self.status_update.emit(filename, "Calculating checksum...", filepath)
            try:
                local_checksum = self.calculate_checksum(filepath)
            except (OSError, PermissionError) as e:
                # File became locked during checksum calculation (locked file handling always enabled)
                error_msg = f"File locked during checksum: {str(e)}"
                self.schedule_locked_file_retry(filepath, remote_path, filename, error_msg)
                return

            # Create remote directory if needed (check cache to avoid redundant attempts)
            remote_dir = os.path.dirname(remote_path)
            if remote_dir and remote_dir != "/" and self._should_create_directory(remote_dir):
                logger.info(f"Creating remote directory: {remote_dir}")
                success = self.webdav_client.create_directory(remote_dir)
                if success and self.app_instance:
                    self.app_instance.created_directories.add(remote_dir)

            # Upload file with detailed progress and status tracking
            self.status_update.emit(filename, "Preparing upload...", filepath)

            # Track last status percentage to avoid too many updates
            last_status_percentage = -1
            upload_completed = False

            def progress_callback(current, total):
                nonlocal last_status_percentage, upload_completed

                # Calculate percentage for progress bar updates
                if total > 0:
                    percentage = (current / total) * 100

                    # Don't let progress reach 100% until upload is truly complete
                    # Cap at 99% during upload, only show 100% when upload_completed flag is set
                    if percentage >= 100 and not upload_completed:
                        percentage = 99.9  # Almost complete but not quite
                        status_msg = "Uploading file... (finalizing)"
                    elif percentage >= 100:
                        status_msg = "Upload complete"
                    elif current > 0:
                        status_msg = "Uploading file..."
                    else:
                        status_msg = "Preparing upload..."

                    # Update status every 25% to avoid too many updates and reduce confusion
                    percentage_rounded = int(percentage / 25) * 25

                    if percentage_rounded != last_status_percentage:
                        self.status_update.emit(filename, status_msg, filepath)
                        last_status_percentage = percentage_rounded

                # Always pass through the progress (but cap at 99% until complete)
                progress_value = (
                    min(current, total - 1) if not upload_completed and total > 0 else current
                )
                self.progress_update.emit(filepath, progress_value, total)

            # First show file reading status
            self.status_update.emit(filename, "Reading file...", filepath)

            success, error = self.webdav_client.upload_file_chunked(
                filepath, remote_path, progress_callback
            )

            if success:
                # Mark upload as completed so progress can show 100%
                upload_completed = True
                # Show 100% completion now that upload is truly done
                self.progress_update.emit(
                    filepath, os.path.getsize(filepath), os.path.getsize(filepath)
                )
                self.status_update.emit(filename, "Upload complete", filepath)

                # Store checksum for future reference
                self.status_update.emit(filename, "Storing checksum...", filepath)
                try:
                    self.webdav_client.store_checksum(remote_path, local_checksum)
                except Exception as e:
                    logger.warning(f"Failed to store checksum for {remote_path}: {e}")

                # Verify upload if enabled
                if hasattr(self, "verify_uploads") and self.verify_uploads:
                    self.status_update.emit(filename, "Verifying upload...", filepath)
                    is_verified, verify_message = self.app_instance.verify_remote_file_integrity(
                        filepath, remote_path, local_checksum
                    ) if self.app_instance else (False, "verification unavailable")
                    if is_verified:
                        # Record successful upload in persistent history
                        if self.app_instance:
                            self.app_instance.record_successful_upload(
                                filepath, remote_path, local_checksum
                            )
                        # Include the specific verification method in the success message
                        self.transfer_complete.emit(
                            filename,
                            filepath,
                            True,
                            f"Upload verified successfully: {verify_message} (uploaded with checksum: {local_checksum[:8]}...)",
                        )
                    else:
                        # File does not exist remotely, remote file cannot be read or there was an
                        # unknown error during verification (this is considered a verification failure, not a conflict)
                        if ["verification error", "remote file not found", "cannot read remote file"] in verify_message:
                            # Regular verification failure (not a conflict)
                            self.transfer_complete.emit(
                                filename,
                                filepath,
                                False,
                                f"Upload verification failed: {verify_message}",
                            )
                        else:
                            # Get remote file info for conflict resolution
                            try:
                                remote_info = self.webdav_client.get_file_info(remote_path)
                                remote_date_str = remote_info.get("last_modified", "")
                                conflict_details = {
                                    "local_checksum": local_checksum,
                                    "local_size": local_size,
                                    "remote_size": remote_info.get("size", 0),
                                    "verification_failure": True,
                                    "reason": verify_message,
                                }

                                # Emit conflict resolution signal
                                self.conflict_resolution_needed.emit(
                                    filename, filepath, remote_path, conflict_details
                                )

                                # Update status to show conflict detected
                                self.status_update.emit(
                                    filename,
                                    "Upload conflict detected - awaiting user decision...",
                                    filepath
                                )
                                return  # Don't report as simple failure

                            except Exception as e:
                                logger.error(f"Failed to get remote info for conflict resolution: {e}")
                                # Fall through to regular failure handling

                        # Regular verification failure (not a conflict)
                        self.transfer_complete.emit(
                            filename,
                            filepath,
                            False,
                            f"Upload verification failed: {verify_message}",
                        )
                else:
                    # Record successful upload in persistent history (verification disabled)
                    if self.app_instance:
                        self.app_instance.record_successful_upload(
                            filepath, remote_path, local_checksum
                        )
                    self.transfer_complete.emit(
                        filename,
                        filepath,
                        True,
                        f"Uploaded successfully (checksum: {local_checksum[:8]}...)",
                    )
            else:
                self.transfer_complete.emit(filename, filepath, False, f"Upload failed: {error}")

        except Exception as e:
            logger.error(f"Error uploading file {filepath}: {e}")
            self.transfer_complete.emit(filename, filepath, False, f"Error: {str(e)}")

    def process_file(self, filepath: str):
        """Process a single file with conflict detection"""
        filename = os.path.basename(filepath)

        # Track that this file is now being processed
        if self.app_instance:
            self.app_instance.queued_files.discard(filepath)
            self.app_instance.processing_files.add(filepath)
            logger.info(f"File tracking: {filepath} moved from queued to processing")

        # Add debugging for path calculation
        logger.info(f"Processing file: {filepath}")
        logger.info(f"Preserve structure: {self.preserve_structure}")
        logger.info(f"Local base path: {self.local_base_path}")
        logger.info(f"Remote base path: {self.remote_base_path}")

        try:
            # Determine remote path first (needed for locked file retry)
            if self.preserve_structure and self.local_base_path:
                rel_path = os.path.relpath(filepath, self.local_base_path)
                remote_path = f"{self.remote_base_path}/{rel_path}".replace("\\", "/")
                logger.info(
                    f"Preserve structure: {filepath} -> {remote_path} (rel_path: {rel_path})"
                )
            else:
                remote_path = f"{self.remote_base_path}/{filename}"
                logger.info(f"No structure preservation: {filepath} -> {remote_path}")

            # Always check if file is accessible (locked file handling always enabled)
            accessible, access_error = self.is_file_accessible(filepath)
            if not accessible:
                # File is locked, schedule retry
                self.schedule_locked_file_retry(filepath, remote_path, filename, access_error)
                return

            # Calculate local checksum
            self.status_update.emit(filename, "Calculating checksum...", filepath)
            try:
                local_checksum = self.calculate_checksum(filepath)
            except (OSError, PermissionError) as e:
                # File became locked during checksum calculation - always handle locked files
                error_msg = f"File locked during checksum: {str(e)}"
                self.schedule_locked_file_retry(filepath, remote_path, filename, error_msg)
                return

            # Create remote directories if needed (check cache to avoid redundant attempts)
            remote_dir = os.path.dirname(remote_path)
            if (
                self.preserve_structure
                and remote_dir != self.remote_base_path
                and self._should_create_directory(remote_dir)
            ):
                success = self.webdav_client.create_directory(remote_dir)
                if success and self.app_instance:
                    self.app_instance.created_directories.add(remote_dir)

            # Check for duplicate upload attempts to same remote path
            if self.app_instance:
                if filepath in self.app_instance.file_remote_paths:
                    existing_remote_path = self.app_instance.file_remote_paths[filepath]
                    if existing_remote_path == remote_path:
                        logger.warning(
                            f"File {filepath} already processed/processing to {remote_path}, skipping duplicate"
                        )
                        self.transfer_complete.emit(
                            filename, filepath, True, "Skipped - already processed"
                        )
                        return
                    else:
                        logger.error(
                            f"File {filepath} being uploaded to different paths: existing={existing_remote_path}, new={remote_path}"
                        )

                # Track this file -> remote path mapping
                self.app_instance.file_remote_paths[filepath] = remote_path

            # Check if remote file exists and get info
            self.status_update.emit(filename, "Checking remote file...", filepath)
            remote_info = self.webdav_client.get_file_info(remote_path)

            if remote_info is None:
                # Error getting remote info, proceed with upload
                logger.warning(
                    f"Could not get remote file info for {remote_path}, proceeding with upload"
                )
                remote_info = {"exists": False}

            if remote_info.get("exists", True):
                # Remote file exists, perform comparison
                logger.debug(f"Remote file exists, comparing: {remote_path}")
                comparison_result, reason = self.app_instance.verify_remote_file_integrity(filepath, remote_path, local_checksum)

                if comparison_result:
                    # Files are identical, skip upload
                    self.transfer_complete.emit(
                        filename,
                        filepath,
                        True,
                        f"File already exists with same content (checksum: {local_checksum[:8]}...)",
                    )
                    return
                else:
                    # Conflict detected, need user resolution
                    logger.info(f"File conflict detected for: {filename}")
                    if self.apply_to_all and self.conflict_resolution:
                        # Use previous resolution
                        resolution = self.conflict_resolution
                        if resolution == "rename":
                            # Generate a new conflict name
                            new_name = f"conflict_{int(time.time())}_{filename}"
                    else:
                        # Emit signal for main thread to handle conflict resolution
                        self.status_update.emit(
                            filename, "Conflict detected - waiting for user input...", filepath
                        )

                        local_size = os.path.getsize(filepath)
                        # Request conflict resolution from main thread
                        conflict_details = {
                            "local_checksum": local_checksum,
                            "local_size": local_size,
                            "remote_size": remote_info.get("size", 0),
                        #    "verification_failure": True,
                            "reason": reason,
                        }
                        self.conflict_resolution_needed.emit(
                            filename, filepath, remote_path, conflict_details
                        )

                        # Wait for resolution (this would be handled by a proper signal/slot mechanism)
                        # For now, we'll return early and let the main thread handle the resolution
                        logger.info(f"Conflict resolution requested for: {filename}")
                        return

                    if resolution == "skip":
                        self.transfer_complete.emit(filename, filepath, True, "Skipped due to conflict")
                        return
                    elif resolution == "rename" and new_name:
                        # Update remote path with new name
                        remote_dir = os.path.dirname(remote_path)
                        remote_path = f"{remote_dir}/{new_name}".replace("//", "/")
                        filename = new_name  # Update filename for status updates
                    # For 'overwrite', continue with original remote_path
            else:
                logger.debug(f"No remote file exists at {remote_path}, proceeding with upload")

            # Use the upload_file method for consistent handling
            self.upload_file(filepath, remote_path, filename)

        except Exception as e:
            logger.error(f"Error processing file {filepath}: {e}")
            self.transfer_complete.emit(filename, filepath, False, f"Error: {str(e)}")
        finally:
            # Always remove from processing files when done
            if self.app_instance:
                self.app_instance.processing_files.discard(filepath)
                logger.info(f"File tracking: {filepath} removed from processing")
                # Keep the remote path mapping until transfer is complete
                # It will be cleaned up in on_transfer_complete

    def _should_create_directory(self, remote_dir: str) -> bool:
        """
        Check if a remote directory should be created, using cache to avoid redundant attempts.

        Args:
            remote_dir: Remote directory path

        Returns:
            True if directory should be created, False if already exists in cache
        """
        if self.app_instance:
            return remote_dir not in self.app_instance.created_directories
        else:
            # Fallback if no app instance - always attempt creation (original behavior)
            return True

    def stop(self):
        """Stop the processor thread"""
        self.running = False


class IntegrityCheckThread(QThread):
    """Thread for performing remote integrity checks"""
    progress_signal = pyqtSignal(str, int, int, str)  # current_file, checked_count, total_count, status
    finished_signal = pyqtSignal(dict, dict)  # results dict, error_details dict
    file_issue_signal = pyqtSignal(str, str, str)  # filepath, issue_type, details

    def __init__(self, files_to_check, main_window):
        super().__init__()
        self.files_to_check = files_to_check
        self.main_window = main_window
        self.results = {
            'total': len(files_to_check),
            'verified': 0,
            'missing': 0,
            'corrupted': 0,
            'changed': 0,
            'errors': 0
        }
        # Track detailed error information for better user feedback
        self.error_details = {
            'missing_remote': [],       # Files missing from remote server
            'changed_local': [],        # Files with conflicts (local/remote differences)
            'network_errors': [],       # Network/connection issues
            'other_errors': []          # Other unexpected errors
        }

    def run(self):
        """Perform the integrity check - ensure ALL local files are on remote server and intact"""
        for i, filepath in enumerate(self.files_to_check, 1):
            try:
                self.progress_signal.emit(filepath, i, self.results['total'], "Checking...")

                # Check if local file still exists
                if not os.path.exists(filepath):
                    filename = os.path.basename(filepath)
                    self.progress_signal.emit(filepath, i, self.results['total'], "Local file missing - removing from tracking")
                    # Remove from history if it exists
                    if filepath in self.main_window.upload_history:
                        del self.main_window.upload_history[filepath]
                    # This isn't really an error, just cleanup
                    continue

                # Calculate current checksum for this local file
                current_checksum = self.main_window.file_processor.calculate_checksum(filepath)

                # Determine remote path for this file
                if filepath in self.main_window.upload_history:
                    # File has been uploaded before - use tracked remote path
                    history_entry = self.main_window.upload_history[filepath]
                    remote_path = history_entry.get("remote_path")
                    stored_checksum = history_entry.get("checksum")
                else:
                    # File not uploaded yet - determine where it should go
                    remote_path = self.main_window.get_remote_path_for_file(filepath)
                    stored_checksum = None

                if not remote_path:
                    # Can't determine remote path - this is an error
                    filename = os.path.basename(filepath)
                    self.progress_signal.emit(filepath, i, self.results['total'], "Cannot determine remote path")
                    self.results['errors'] += 1
                    self.error_details['other_errors'].append({
                        'filepath': filepath,
                        'filename': filename,
                        'reason': 'Unable to determine remote path for file'
                    })
                    continue

                # Verify remote file integrity
                # Use the stored checksum if available, otherwise use current checksum
                checksum_for_verification = stored_checksum if stored_checksum else current_checksum
                remote_ok, reason = self.main_window.verify_remote_file_integrity(
                    filepath, remote_path, checksum_for_verification
                )

                if remote_ok:
                    # File is intact on remote server
                    if stored_checksum and stored_checksum.lower() != "unknown":
                        checksum_short = stored_checksum[:12]
                        verification_msg = f"Remote file verified by {reason} (upload checksum: {checksum_short}...)"
                    else:
                        # No stored checksum - using current file checksum for verification
                        checksum_short = current_checksum[:12]
                        verification_msg = f"Remote file verified by {reason} (current checksum: {checksum_short}...)"

                    self.progress_signal.emit(filepath, i, self.results['total'], verification_msg)
                    self.results['verified'] += 1
                else:
                    # File is missing or corrupted on remote server
                    filename = os.path.basename(filepath)
                    if "not found" in reason.lower():
                        # File is missing from remote server
                        if self.main_window.is_file_in_upload_queue(filepath):
                            # File is already queued for upload - that's good!
                            self.progress_signal.emit(filepath, i, self.results['total'],
                                                    "Missing from remote - already queued for upload")
                            # This isn't an error - it's expected behavior
                        else:
                            # File is missing but not queued - this needs attention
                            self.progress_signal.emit(filepath, i, self.results['total'],
                                                    "Missing from remote - adding to upload queue")
                            self.results['missing'] += 1
                            self.error_details['missing_remote'].append({
                                'filepath': filepath,
                                'filename': filename,
                                'reason': 'File missing from remote server and not in upload queue'
                            })
                            # Emit signal to notify UI about missing file
                            self.file_issue_signal.emit(filepath, "missing", "File not found on remote server")
                            # Add to upload queue
                            self.main_window.queue_file_for_upload(filepath, "missing from remote during integrity check")
                    else:
                        # File exists on remote but differs from expected
                        # We cannot determine if it's corruption or legitimate change
                        # Always treat as a conflict and use conflict resolution settings

                        if stored_checksum and current_checksum.lower() != stored_checksum.lower():
                            # Both local and remote have changed - definitely a conflict
                            conflict_reason = "Both local and remote files have changed since last sync"
                        else:
                            # Local unchanged, remote different - could be corruption or server-side change
                            conflict_reason = "Remote file differs from expected (possible corruption or server-side change)"

                        self.progress_signal.emit(filepath, i, self.results['total'],
                                                "File conflict detected - applying conflict resolution")
                        self.results['changed'] += 1
                        self.error_details['changed_local'].append({
                            'filepath': filepath,
                            'filename': filename,
                            'reason': conflict_reason
                        })
                        # Always trigger conflict resolution - let user decide what to do
                        self.file_issue_signal.emit(filepath, "changed", conflict_reason)

            except Exception as e:
                filename = os.path.basename(filepath)
                logger.error(f"Error checking integrity of {filepath}: {e}")
                self.progress_signal.emit(filepath, i, self.results['total'], f"Error: {str(e)}")
                self.results['errors'] += 1
                # Categorize the error based on the exception type/message
                error_msg = str(e).lower()
                if any(net_term in error_msg for net_term in ['connection', 'timeout', 'network', 'http']):
                    self.error_details['network_errors'].append({
                        'filepath': filepath,
                        'filename': filename,
                        'reason': f'Network error: {str(e)}'
                    })
                else:
                    self.error_details['other_errors'].append({
                        'filepath': filepath,
                        'filename': filename,
                        'reason': f'Unexpected error: {str(e)}'
                    })

        # Pass the detailed error information to the finished signal
        self.finished_signal.emit(self.results, self.error_details)


class FileConflictDialog(QDialog):
    """Dialog for resolving file conflicts"""

    def __init__(self, filename: str, conflict_details: dict, parent=None):
        super().__init__(parent)
        self.filename = filename
        self.conflict_details = conflict_details
        self.resolution = None

        self.setWindowTitle("File Conflict Detected")
        self.setMinimumSize(500, 450)
        self.setModal(True)

        self.setup_ui()

    def setup_ui(self):
        layout = QVBoxLayout()

        # Title
        title = QLabel("File Conflict Detected")
        title.setStyleSheet("font-size: 16px; font-weight: bold; color: #d32f2f;")
        layout.addWidget(title)

        # Conflict description
        conflict_text = QLabel(
            f"A file with the same name already exists on the server, "
            f"but the content appears to be different.\n\n"
            f"File: {self.filename}"
        )
        conflict_text.setWordWrap(True)
        layout.addWidget(conflict_text)

        # File comparison
        comparison_group = QGroupBox("File Comparison")
        comparison_layout = QGridLayout()

        # Headers
        comparison_layout.addWidget(QLabel(""), 0, 0)
        comparison_layout.addWidget(QLabel("Local File"), 0, 1)
        comparison_layout.addWidget(QLabel("Remote File"), 0, 2)

        # File sizes
        local_size = self.conflict_details.get("local_size", 0)
        local_size_str = f"{local_size:,} bytes"
        if local_size > 1024 * 1024:
            local_size_str += f" ({local_size / (1024 * 1024):.2f} MB)"

        remote_size = self.conflict_details.get("remote_size", 0)
        remote_size_str = f"{remote_size:,} bytes"
        if remote_size > 1024 * 1024:
            remote_size_str += f" ({remote_size / (1024 * 1024):.2f} MB)"

        comparison_layout.addWidget(QLabel("Size:"), 1, 0)
        comparison_layout.addWidget(QLabel(local_size_str), 1, 1)
        comparison_layout.addWidget(QLabel(remote_size_str), 1, 2)

        # Checksums
        local_checksum = self.conflict_details.get("local_checksum", "Unknown")
        remote_checksum = self.conflict_details.get("remote_checksum", "Unknown")

        comparison_layout.addWidget(QLabel("Checksum:"), 2, 0)
        comparison_layout.addWidget(
            QLabel(f"{local_checksum[:16]}..." if len(local_checksum) > 16 else local_checksum),
            2,
            1,
        )
        comparison_layout.addWidget(
            QLabel(f"{remote_checksum[:16]}..." if len(remote_checksum) > 16 else remote_checksum),
            2,
            2,
        )

        # Last modified dates
        local_date = self.conflict_details.get("local_date", "Unknown")
        remote_date = self.conflict_details.get("remote_date", "Unknown")

        comparison_layout.addWidget(QLabel("Modified:"), 3, 0)
        comparison_layout.addWidget(QLabel(str(local_date)), 3, 1)
        comparison_layout.addWidget(QLabel(str(remote_date)), 3, 2)

        comparison_group.setLayout(comparison_layout)
        layout.addWidget(comparison_group)

        # Resolution options
        resolution_group = QGroupBox("Resolution")
        resolution_layout = QVBoxLayout()

        # Add date comparison info if available
        local_date = self.conflict_details.get("local_date")
        remote_date = self.conflict_details.get("remote_date")

        date_info = ""
        default_choice = "skip"

        if local_date and remote_date and local_date != "Unknown" and remote_date != "Unknown":
            try:
                # Parse dates for comparison
                if isinstance(local_date, str):
                    local_dt = datetime.fromisoformat(local_date.replace("Z", "+00:00"))
                else:
                    local_dt = local_date

                if isinstance(remote_date, str):
                    remote_dt = datetime.fromisoformat(remote_date.replace("Z", "+00:00"))
                else:
                    remote_dt = remote_date

                time_diff = abs(local_dt - remote_dt)
                if time_diff.total_seconds() < 60: # Consider as same time if within 1 minute
                    date_info = " (same modification time)"
                elif local_dt > remote_dt:
                    date_info = " (local file is newer)"
                    default_choice = "overwrite"
                elif remote_dt > local_dt:
                    date_info = " (remote file is newer)"
                    default_choice = "skip"
            except Exception:
                date_info = ""

        self.skip_radio = QRadioButton(
            f"Skip - Don't upload this file{date_info if 'newer' in date_info and 'remote' in date_info else ''}"
        )
        self.overwrite_radio = QRadioButton(
            f"Overwrite - Replace the remote file{date_info if 'newer' in date_info and 'local' in date_info else ''}"
        )
        self.rename_radio = QRadioButton("Rename - Upload with a different name")

        # Set default based on date comparison
        if default_choice == "overwrite":
            self.overwrite_radio.setChecked(True)
        else:
            self.skip_radio.setChecked(True)

        resolution_layout.addWidget(self.skip_radio)
        resolution_layout.addWidget(self.overwrite_radio)
        resolution_layout.addWidget(self.rename_radio)

        # Rename input
        self.rename_layout = QHBoxLayout()
        self.rename_layout.addWidget(QLabel("New name:"))
        self.rename_input = QLineEdit()
        self.rename_input.setText(f"conflict_{int(time.time())}_{self.filename}")
        self.rename_input.setEnabled(False)
        self.rename_layout.addWidget(self.rename_input)

        resolution_layout.addLayout(self.rename_layout)

        # Connect radio button to enable/disable rename input
        self.rename_radio.toggled.connect(self.rename_input.setEnabled)

        resolution_group.setLayout(resolution_layout)
        layout.addWidget(resolution_group)

        # Buttons
        button_layout = QHBoxLayout()

        self.apply_to_all_check = QCheckBox("Apply this choice to all remaining conflicts")
        button_layout.addWidget(self.apply_to_all_check)

        button_layout.addStretch()

        ok_btn = QPushButton("OK")
        ok_btn.clicked.connect(self.accept)
        button_layout.addWidget(ok_btn)

        cancel_btn = QPushButton("Cancel")
        cancel_btn.clicked.connect(self.reject)
        button_layout.addWidget(cancel_btn)

        layout.addLayout(button_layout)
        self.setLayout(layout)

    def get_resolution(self):
        """Get the user's resolution choice"""
        if self.skip_radio.isChecked():
            return "skip", None, self.apply_to_all_check.isChecked()
        elif self.overwrite_radio.isChecked():
            return "overwrite", None, self.apply_to_all_check.isChecked()
        elif self.rename_radio.isChecked():
            return "rename", self.rename_input.text(), self.apply_to_all_check.isChecked()
        else:
            return "skip", None, False


class RemoteBrowserDialog(QDialog):
    """Dialog for browsing remote WebDAV directories"""

    def __init__(self, webdav_client: WebDAVClient, parent=None, initial_path: str = "/"):
        super().__init__(parent)
        self.webdav_client = webdav_client
        self.current_path = initial_path or "/"
        self.selected_path = initial_path or "/"

        self.setWindowTitle("Browse Remote Directory")
        self.setMinimumSize(500, 400)

        self.setup_ui()
        self.refresh_listing()

    def setup_ui(self):
        layout = QVBoxLayout()

        # Path display
        path_layout = QHBoxLayout()
        path_layout.addWidget(QLabel("Current Path:"))
        self.path_label = QLabel("/")
        self.path_label.setStyleSheet("font-weight: bold;")
        path_layout.addWidget(self.path_label)
        path_layout.addStretch()

        self.refresh_btn = QPushButton("Refresh")
        self.refresh_btn.clicked.connect(self.refresh_listing)
        path_layout.addWidget(self.refresh_btn)

        layout.addLayout(path_layout)

        # File/folder list
        self.tree = QTreeWidget()
        self.tree.setHeaderLabels(["Name", "Type", "Size"])
        self.tree.itemDoubleClicked.connect(self.on_item_double_click)
        layout.addWidget(self.tree)

        # Buttons
        button_layout = QHBoxLayout()

        self.new_folder_btn = QPushButton("New Folder")
        self.new_folder_btn.clicked.connect(self.create_new_folder)
        button_layout.addWidget(self.new_folder_btn)

        button_layout.addStretch()

        self.select_btn = QPushButton("Select This Folder")
        self.select_btn.clicked.connect(self.select_current_folder)
        button_layout.addWidget(self.select_btn)

        self.cancel_btn = QPushButton("Cancel")
        self.cancel_btn.clicked.connect(self.close)
        button_layout.addWidget(self.cancel_btn)

        layout.addLayout(button_layout)
        self.setLayout(layout)

    def refresh_listing(self):
        """Refresh the current directory listing"""
        self.tree.clear()
        self.path_label.setText(self.current_path)

        # Add parent directory option if not at root
        if self.current_path != "/":
            parent_item = QTreeWidgetItem(self.tree, ["[..]", "Parent", ""])
            parent_item.setData(0, Qt.ItemDataRole.UserRole, "..")

        # Get directory listing
        items = self.webdav_client.list_directory(self.current_path)
        logging.info(
            f"GUI: Retrieved {len(items)} items from list_directory for {self.current_path}"
        )

        for i, item in enumerate(items):
            logging.info(
                f"GUI: Processing item {i}: {item['name']} (is_dir: {item['is_dir']}, size: {item['size']})"
            )
            if item["is_dir"]:
                tree_item = QTreeWidgetItem(self.tree, [item["name"], "Folder", ""])
                tree_item.setData(0, Qt.ItemDataRole.UserRole, item["path"])
                logging.info(f"GUI: Added folder item: {item['name']}")
            else:
                size_mb = item["size"] / (1024 * 1024)
                tree_item = QTreeWidgetItem(self.tree, [item["name"], "File", f"{size_mb:.2f} MB"])
                tree_item.setData(0, Qt.ItemDataRole.UserRole, item["path"])
                logging.info(f"GUI: Added file item: {item['name']} ({size_mb:.2f} MB)")

        logging.info(f"GUI: Tree widget now has {self.tree.topLevelItemCount()} total items")

    def on_item_double_click(self, item, column):
        """Handle double-click on item"""
        path = item.data(0, Qt.ItemDataRole.UserRole)

        if path == "..":
            # Go to parent directory
            self.current_path = os.path.dirname(self.current_path.rstrip("/"))
            if not self.current_path:
                self.current_path = "/"
            self.refresh_listing()
        elif item.text(1) == "Folder":
            # Navigate into folder
            self.current_path = path
            self.refresh_listing()

    def create_new_folder(self):
        """Create a new folder in the current directory"""
        name, ok = QInputDialog.getText(self, "New Folder", "Folder name:")

        if ok and name:
            new_path = f"{self.current_path.rstrip('/')}/{name}"
            logger.info(f"Attempting to create folder: {new_path}")

            if self.webdav_client.create_directory(new_path):
                QMessageBox.information(self, "Success", f"Created folder: {name}")
                self.refresh_listing()
            else:
                # Provide more detailed error message based on the log
                error_msg = f"Failed to create folder: {name}\n\n"

                # Check recent log entries for specific error details
                try:
                    with open("panoramabridge.log") as f:
                        log_lines = f.readlines()
                        recent_errors = [
                            line
                            for line in log_lines[-10:]
                            if "ERROR" in line and "creating directory" in line
                        ]

                        if recent_errors:
                            latest_error = recent_errors[-1]
                            if "Permission denied" in latest_error:
                                error_msg += "Permission Denied (HTTP 403)\n\n"
                                error_msg += "This means you don't have write permissions to create folders in this directory.\n\n"
                                error_msg += "Possible solutions:\n"
                                error_msg += "• Contact your Panorama administrator to request write access\n"
                                error_msg += "• Try creating the folder in a different directory where you have permissions\n"
                                error_msg += "• Check if you're in the correct user folder\n\n"
                            elif "Conflict" in latest_error:
                                error_msg += "Path Conflict (HTTP 409)\n\n"
                                error_msg += "The parent directory may not exist.\n\n"
                            else:
                                error_msg += "Server Error\n\n"
                        else:
                            error_msg += "Possible reasons:\n"
                            error_msg += "• You may not have write permissions\n"
                            error_msg += "• The folder name may contain invalid characters\n"
                            error_msg += "• The server may have restrictions on folder creation\n\n"
                except Exception:
                    error_msg += "Possible reasons:\n"
                    error_msg += "• You may not have write permissions\n"
                    error_msg += "• The folder name may contain invalid characters\n"
                    error_msg += "• The server may have restrictions on folder creation\n\n"

                error_msg += (
                    "View the application logs (View → View Application Logs) for more details."
                )
                QMessageBox.warning(self, "Error", error_msg)

    def select_current_folder(self):
        """Select the current folder and close"""
        self.selected_path = self.current_path
        self.accept()

    def get_selected_path(self) -> str:
        """Get the selected path"""
        return self.selected_path


class MainWindow(QMainWindow):
    """
    Main application window for PanoramaBridge.

    This is the primary UI class that manages the complete application workflow:
    - Creates tabbed interface for configuration and monitoring
    - Manages file monitoring setup and control
    - Handles WebDAV connection configuration
    - Displays transfer progress and status
    - Coordinates between UI components and background processing
    - Manages application configuration and settings persistence

    The UI is organized into three main tabs:
    1. Local Monitoring - Configure directory monitoring and file settings
    2. Remote Settings - Configure WebDAV connection and upload settings
    3. Transfer Status - View active transfers and progress
    """

    def __init__(self):
        """Initialize the main application window and components."""
        super().__init__()
        self.setWindowTitle("PanoramaBridge - File Monitor and WebDAV Transfer Tool")
        self.setGeometry(100, 100, 900, 600)

        # Set application icon
        self.setup_application_icon()

        # Core application components
        self.file_queue = queue.Queue()  # Thread-safe queue for file processing
        self.file_processor = FileProcessor(self.file_queue, self)  # Background processing thread
        self.monitor_handler = None  # File system event handler
        self.observer = None  # Watchdog observer for file monitoring
        self.webdav_client = None  # WebDAV client instance
        self.transfer_rows = {}  # Track UI table rows for updates
        self.queued_files = set()  # Track files already queued to prevent duplicates
        self.processing_files = set()  # Track files currently being processed
        self.created_directories = set()  # Cache of successfully created remote directories
        self.failed_files = {}  # Track files that failed verification for re-upload
        self.file_remote_paths = {}  # Track filepath -> remote_path mappings to prevent duplicate uploads
        self.local_checksum_cache = {}  # Local checksum cache to avoid recalculation
        self.upload_history = {}  # Persistent tracking of successfully uploaded files {filepath: {checksum, timestamp, remote_path}}

        # Load persistent upload history
        self.load_upload_history()

        # Load application configuration from disk
        self.config = self.load_config()

        # Initialize UI components and layout
        self.setup_ui()
        self.setup_menu()
        self.load_settings()

        # Connect background processor signals to UI handlers
        self.file_processor.progress_update.connect(self.on_progress_update)
        self.file_processor.status_update.connect(self.on_status_update)
        self.file_processor.transfer_complete.connect(self.on_transfer_complete)
        self.file_processor.conflict_resolution_needed.connect(self.on_conflict_resolution_needed)
        self.file_processor.start()

        # Setup periodic UI updates
        self.queue_timer = QTimer()
        self.queue_timer.timeout.connect(self.update_queue_size)
        self.queue_timer.start(1000)  # Update queue size display every second

        # Setup periodic file polling as backup to watchdog events
        self.poll_timer = QTimer()
        self.poll_timer.timeout.connect(self.poll_for_new_files)
        # Timer will be started when monitoring begins

        # Setup periodic cache saving (every 5 minutes)
        self.cache_save_timer = QTimer()
        self.cache_save_timer.timeout.connect(self.save_checksum_cache)
        self.cache_save_timer.start(5 * 60 * 1000)  # 5 minutes in milliseconds

    def setup_application_icon(self):
        """Setup the application icon from the logo file"""
        try:
            # Get the path to the logo file - handle both development and bundled modes
            if getattr(sys, "frozen", False):
                # Running as bundled executable
                base_path = sys._MEIPASS
                logo_path = os.path.join(base_path, "screenshots", "panoramabridge-logo.png")
            else:
                # Running in development
                script_dir = os.path.dirname(os.path.abspath(__file__))
                logo_path = os.path.join(script_dir, "screenshots", "panoramabridge-logo.png")

            if os.path.exists(logo_path):
                icon = QIcon(logo_path)
                self.setWindowIcon(icon)

                logger.info(f"Application icon set from: {logo_path}")
            else:
                logger.warning(f"Logo file not found at: {logo_path}")
        except Exception as e:
            logger.error(f"Failed to set application icon: {e}")

    def setup_ui(self):
        central_widget = QWidget()
        self.setCentralWidget(central_widget)

        main_layout = QVBoxLayout()

        # Tab widget
        self.tabs = QTabWidget()

        # Local monitoring tab
        self.local_tab = self.create_local_tab()
        self.tabs.addTab(self.local_tab, "Local Monitoring")

        # Remote settings tab
        self.remote_tab = self.create_remote_tab()
        self.tabs.addTab(self.remote_tab, "Remote Settings")

        # Transfer status tab
        self.status_tab = self.create_status_tab()
        self.tabs.addTab(self.status_tab, "Transfer Status")

        main_layout.addWidget(self.tabs)

        # Control buttons
        control_layout = QHBoxLayout()

        self.start_btn = QPushButton("Start Monitoring")
        self.start_btn.clicked.connect(self.toggle_monitoring)
        control_layout.addWidget(self.start_btn)

        self.verify_btn = QPushButton("Remote Integrity Check")
        self.verify_btn.clicked.connect(self.start_remote_integrity_check)
        self.verify_btn.setToolTip("Check if all files in Transfer Status are correctly uploaded and intact on remote server")
        control_layout.addWidget(self.verify_btn)

        self.test_connection_btn = QPushButton("Test Connection")
        self.test_connection_btn.clicked.connect(self.test_connection)
        control_layout.addWidget(self.test_connection_btn)

        control_layout.addStretch()

        self.status_label = QLabel("Not monitoring")
        self.status_label.setStyleSheet("font-weight: bold; color: red;")
        control_layout.addWidget(self.status_label)

        main_layout.addLayout(control_layout)
        central_widget.setLayout(main_layout)

    def setup_menu(self):
        """Setup the application menu"""
        menubar = self.menuBar()

        # View menu
        view_menu = menubar.addMenu("View")

        # View logs action
        view_logs_action = view_menu.addAction("View Application Logs")
        view_logs_action.triggered.connect(self.view_full_logs)

        # Help menu
        help_menu = menubar.addMenu("Help")

        # About action
        about_action = help_menu.addAction("About")
        about_action.triggered.connect(self.show_about)

    def show_about(self):
        """Show about dialog with logo"""
        try:
            # Create custom about dialog
            dialog = QDialog(self)
            dialog.setWindowTitle("About PanoramaBridge")
            dialog.setFixedSize(400, 450)

            layout = QVBoxLayout()

            # Add logo - handle both development and bundled modes
            if getattr(sys, "frozen", False):
                # Running as bundled executable
                base_path = sys._MEIPASS
                logo_path = os.path.join(base_path, "screenshots", "panoramabridge-logo.png")
            else:
                # Running in development
                script_dir = os.path.dirname(os.path.abspath(__file__))
                logo_path = os.path.join(script_dir, "screenshots", "panoramabridge-logo.png")

            if os.path.exists(logo_path):
                logo_label = QLabel()
                pixmap = QIcon(logo_path).pixmap(128, 128)  # Scale to 128x128
                logo_label.setPixmap(pixmap)
                logo_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
                layout.addWidget(logo_label)

            # Add title
            title_label = QLabel("PanoramaBridge")
            title_font = QFont()
            title_font.setPointSize(18)
            title_font.setBold(True)
            title_label.setFont(title_font)
            title_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
            layout.addWidget(title_label)

            # Add version
            version_label = QLabel("Version 1.0")
            version_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
            layout.addWidget(version_label)

            layout.addWidget(QLabel(""))  # Spacer

            # Add description
            desc_label = QLabel(
                "A file monitoring and WebDAV transfer application\n"
                "for syncing files to Panorama servers.\n\n"
                "Developed in the MacCoss Lab\n"
                "Department of Genome Sciences\n"
                "University of Washington\n\n"
                "Lab website: https://maccosslab.org\n\n"
                "Logs are saved to: panoramabridge.log"
            )
            desc_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
            desc_label.setWordWrap(True)
            layout.addWidget(desc_label)

            layout.addStretch()

            # Add OK button
            button_layout = QHBoxLayout()
            button_layout.addStretch()
            ok_button = QPushButton("OK")
            ok_button.clicked.connect(dialog.accept)
            button_layout.addWidget(ok_button)
            button_layout.addStretch()
            layout.addLayout(button_layout)

            dialog.setLayout(layout)
            dialog.exec()

        except Exception as e:
            logger.error(f"Error showing about dialog: {e}")
            # Fallback to simple message box
            QMessageBox.about(
                self,
                "About PanoramaBridge",
                "PanoramaBridge v1.0\n\n"
                "A file monitoring and WebDAV transfer application\n"
                "for syncing files to Panorama servers.\n\n"
                "Developed in the MacCoss Lab\n"
                "Department of Genome Sciences\n"
                "University of Washington\n\n"
                "Lab website: https://maccosslab.org\n\n"
                "Logs are saved to: panoramabridge.log",
            )

    def create_local_tab(self):
        """Create the local monitoring settings tab"""
        widget = QWidget()
        layout = QVBoxLayout()

        # Directory selection
        dir_group = QGroupBox("Directory to Monitor")
        dir_layout = QVBoxLayout()

        browse_layout = QHBoxLayout()
        self.dir_input = QLineEdit()
        browse_layout.addWidget(self.dir_input)

        browse_btn = QPushButton("Browse...")
        browse_btn.clicked.connect(self.browse_local_directory)
        browse_layout.addWidget(browse_btn)

        dir_layout.addLayout(browse_layout)

        self.subdirs_check = QCheckBox("Monitor subdirectories")
        self.subdirs_check.setChecked(True)
        dir_layout.addWidget(self.subdirs_check)

        dir_group.setLayout(dir_layout)
        layout.addWidget(dir_group)

        # File extensions
        ext_group = QGroupBox("File Extensions to Monitor")
        ext_layout = QVBoxLayout()

        ext_info = QLabel("Enter file extensions separated by commas (e.g., txt, pdf, docx)")
        ext_layout.addWidget(ext_info)

        self.extensions_input = QLineEdit()
        # Default values are set via load_settings() method, not placeholder text
        ext_layout.addWidget(self.extensions_input)

        ext_group.setLayout(ext_layout)
        layout.addWidget(ext_group)

        # Advanced settings
        adv_group = QGroupBox("Advanced Settings")
        adv_layout = QGridLayout()

        adv_layout.addWidget(QLabel("File stability timeout (seconds):"), 0, 0)
        self.stability_spin = QSpinBox()
        self.stability_spin.setRange(1, 60)
        self.stability_spin.setValue(2)
        adv_layout.addWidget(self.stability_spin, 0, 1)

        # OS Event vs Polling settings
        self.enable_polling_check = QCheckBox("Enable backup file polling")
        self.enable_polling_check.setChecked(False)  # Default to OS events only
        self.enable_polling_check.setToolTip(
            "Enable periodic directory scanning as backup to OS file events.\n"
            "OS events are faster and more efficient. Only enable polling if:\n"
            "• Files aren't being detected automatically\n"
            "• Monitoring remote filesystems (network drives, SMB/CIFS shares, NFS)\n"
            "• Working with special file systems or cloud storage mounts\n"
            "• Running on WSL2 or virtual machines where OS events may be unreliable"
        )
        adv_layout.addWidget(self.enable_polling_check, 1, 0, 1, 2)

        adv_layout.addWidget(QLabel("Polling interval (minutes):"), 2, 0)
        self.polling_interval_spin = QSpinBox()
        self.polling_interval_spin.setRange(1, 30)
        self.polling_interval_spin.setValue(2)  # Default to 2 minutes instead of 30 seconds
        self.polling_interval_spin.setEnabled(False)  # Disabled by default
        self.polling_interval_spin.setToolTip("How often to scan directory when polling is enabled")
        adv_layout.addWidget(self.polling_interval_spin, 2, 1)

        # Connect polling checkbox to enable/disable interval setting
        self.enable_polling_check.toggled.connect(self.polling_interval_spin.setEnabled)

        # Locked file handling settings (always enabled, but configurable timing)
        locked_info = QLabel(
            "Locked File Handling - Wait for files to be fully written (e.g., mass spectrometers)"
        )
        locked_info.setStyleSheet("font-weight: bold; color: #444;")
        adv_layout.addWidget(locked_info, 3, 0, 1, 2)

        adv_layout.addWidget(QLabel("Initial wait time (minutes) for locked files:"), 4, 0)
        self.initial_wait_spin = QSpinBox()
        self.initial_wait_spin.setRange(1, 180)  # 1 minute to 3 hours
        self.initial_wait_spin.setValue(30)  # Default 30 minutes (typical LC-MS run)
        self.initial_wait_spin.setToolTip("Wait time before first retry (e.g., LC-MS run duration)")
        adv_layout.addWidget(self.initial_wait_spin, 4, 1)

        adv_layout.addWidget(QLabel("Retry interval (seconds):"), 5, 0)
        self.retry_interval_spin = QSpinBox()
        self.retry_interval_spin.setRange(10, 300)  # 10 seconds to 5 minutes
        self.retry_interval_spin.setValue(30)  # Default 30 seconds
        self.retry_interval_spin.setToolTip("How often to retry after initial wait period")
        adv_layout.addWidget(self.retry_interval_spin, 5, 1)

        adv_layout.addWidget(QLabel("Maximum retries:"), 6, 0)
        self.max_retries_spin = QSpinBox()
        self.max_retries_spin.setRange(1, 100)
        self.max_retries_spin.setValue(20)  # Default 20 retries = ~10 minutes of additional waiting
        self.max_retries_spin.setToolTip("Maximum retry attempts after initial wait")
        adv_layout.addWidget(self.max_retries_spin, 6, 1)

        adv_group.setLayout(adv_layout)
        layout.addWidget(adv_group)

        # Conflict resolution settings
        conflict_group = QGroupBox("File Conflict Resolution")
        conflict_layout = QVBoxLayout()

        conflict_info = QLabel(
            "What should happen when a file with the same name but different content already exists on the server?\n"
            "Files with identical content (same checksum) are automatically skipped to avoid redundant uploads."
        )
        conflict_info.setWordWrap(True)
        conflict_info.setStyleSheet("font-style: italic; color: #666;")
        conflict_layout.addWidget(conflict_info)

        self.conflict_ask_radio = QRadioButton("Ask me each time (recommended)")
        self.conflict_skip_radio = QRadioButton("Skip uploading the file")
        self.conflict_overwrite_radio = QRadioButton("Overwrite the remote file")
        self.conflict_rename_radio = QRadioButton("Upload with a new name (add conflict prefix)")

        self.conflict_ask_radio.setChecked(True)  # Default to asking

        conflict_layout.addWidget(self.conflict_ask_radio)
        conflict_layout.addWidget(self.conflict_skip_radio)
        conflict_layout.addWidget(self.conflict_overwrite_radio)
        conflict_layout.addWidget(self.conflict_rename_radio)

        conflict_group.setLayout(conflict_layout)
        layout.addWidget(conflict_group)

        layout.addStretch()
        widget.setLayout(layout)
        return widget

    def create_remote_tab(self):
        """Create the remote settings tab"""
        widget = QWidget()
        layout = QVBoxLayout()

        # Connection settings
        conn_group = QGroupBox("WebDAV Connection")
        conn_layout = QGridLayout()

        conn_layout.addWidget(QLabel("URL:"), 0, 0)
        self.url_input = QLineEdit()
        self.url_input.setPlaceholderText("https://panoramaweb.org")
        conn_layout.addWidget(self.url_input, 0, 1, 1, 2)

        conn_layout.addWidget(QLabel("Username:"), 1, 0)
        self.username_input = QLineEdit()
        conn_layout.addWidget(self.username_input, 1, 1, 1, 2)

        conn_layout.addWidget(QLabel("Password:"), 2, 0)
        self.password_input = QLineEdit()
        self.password_input.setEchoMode(QLineEdit.EchoMode.Password)
        conn_layout.addWidget(self.password_input, 2, 1, 1, 2)

        conn_layout.addWidget(QLabel("Auth Type:"), 3, 0)
        self.auth_combo = QComboBox()
        self.auth_combo.addItems(["Basic", "Digest"])
        conn_layout.addWidget(self.auth_combo, 3, 1)

        self.save_creds_check = QCheckBox("Save credentials (secure)")
        if not KEYRING_AVAILABLE:
            self.save_creds_check.setText("Save credentials (secure) - Not available")
            self.save_creds_check.setEnabled(False)
            self.save_creds_check.setToolTip(
                "Keyring library not available. Install 'keyring' package to enable secure credential storage."
            )
        conn_layout.addWidget(self.save_creds_check, 3, 2)

        conn_group.setLayout(conn_layout)
        layout.addWidget(conn_group)

        # Remote path
        path_group = QGroupBox("Remote Path")
        path_layout = QHBoxLayout()

        self.remote_path_input = QLineEdit()
        self.remote_path_input.setText("/_webdav")
        path_layout.addWidget(self.remote_path_input)

        browse_remote_btn = QPushButton("Browse Remote...")
        browse_remote_btn.clicked.connect(self.browse_remote_directory)
        path_layout.addWidget(browse_remote_btn)

        path_group.setLayout(path_layout)
        layout.addWidget(path_group)

        # Transfer settings
        transfer_group = QGroupBox("Transfer Settings")
        transfer_layout = QGridLayout()

        self.verify_uploads_check = QCheckBox(
            "Verify uploads using optimized multi-level integrity checks"
        )
        self.verify_uploads_check.setChecked(True)  # Default to enabled
        self.verify_uploads_check.setToolTip(
            "Multi-level verification approach:\n"
            "1. Size comparison: Verifies local and remote file sizes match\n"
            "2. Checksum verification: Compares .checksum file on server with local SHA256\n"
            "3. Accessibility check: Fallback - verifies file can be read (first 8KB)"
        )
        transfer_layout.addWidget(self.verify_uploads_check, 0, 0, 1, 2)

        transfer_group.setLayout(transfer_layout)
        layout.addWidget(transfer_group)

        layout.addStretch()
        widget.setLayout(layout)
        return widget

    def create_status_tab(self):
        """Create the transfer status tab"""
        widget = QWidget()
        layout = QVBoxLayout()

        # Queue status
        queue_layout = QHBoxLayout()
        queue_layout.addWidget(QLabel("Queue:"))
        self.queue_label = QLabel("0 files")
        self.queue_label.setStyleSheet("font-weight: bold;")
        queue_layout.addWidget(self.queue_label)
        queue_layout.addStretch()

        reupload_btn = QPushButton("Re-upload Failed")
        reupload_btn.clicked.connect(self.reupload_failed_files)
        reupload_btn.setToolTip("Re-upload files that failed verification")
        queue_layout.addWidget(reupload_btn)

        clear_btn = QPushButton("Clear Completed")
        clear_btn.clicked.connect(self.clear_completed_transfers)
        queue_layout.addWidget(clear_btn)

        layout.addLayout(queue_layout)

        # Transfer table
        self.transfer_table = QTableWidget()
        self.transfer_table.setColumnCount(4)
        self.transfer_table.setHorizontalHeaderLabels(["File", "Status", "Progress", "Message"])

        # Set column widths for better display
        header = self.transfer_table.horizontalHeader()
        header.resizeSection(0, 200)  # File name
        header.resizeSection(1, 120)  # Path
        header.resizeSection(2, 80)  # Status
        header.resizeSection(3, 100)  # Progress
        header.setStretchLastSection(True)  # Message column stretches

        # Add context menu for re-upload
        self.transfer_table.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        self.transfer_table.customContextMenuRequested.connect(self.show_transfer_context_menu)

        layout.addWidget(self.transfer_table)

        # Log area
        log_group = QGroupBox("Activity Log")
        log_layout = QVBoxLayout()

        # Log controls
        log_controls = QHBoxLayout()
        view_logs_btn = QPushButton("View Full Logs")
        view_logs_btn.clicked.connect(self.view_full_logs)
        log_controls.addWidget(view_logs_btn)
        log_controls.addStretch()
        log_layout.addLayout(log_controls)

        self.log_text = QTextEdit()
        self.log_text.setReadOnly(True)
        self.log_text.setMaximumHeight(150)
        log_layout.addWidget(self.log_text)

        log_group.setLayout(log_layout)
        layout.addWidget(log_group)

        widget.setLayout(layout)
        return widget

    def get_transfer_table_key(self, filename: str, filepath: str) -> str:
        """Generate consistent unique key for transfer table tracking"""
        # Use relative path from monitored directory for consistency
        if self.dir_input.text() and filepath.startswith(self.dir_input.text()):
            relative_path = os.path.relpath(filepath, self.dir_input.text())
            if not relative_path.startswith(".."):
                return f"{relative_path}|{filepath}"
        # Fallback to filename|filepath for files outside monitored directory
        return f"{filename}|{filepath}"

    @pyqtSlot(str)
    def add_queued_file_to_table(self, filepath: str):
        """Add a queued file to the transfer table with 'Queued' status at the top"""
        filename = os.path.basename(filepath)

        # Use consistent unique key format (same as on_status_update)
        unique_key = self.get_transfer_table_key(filename, filepath)
        if unique_key in self.transfer_rows:
            # File already in table, just update status to ensure it's marked as queued
            row = self.transfer_rows[unique_key]
            if row < self.transfer_table.rowCount():
                status_item = self.transfer_table.item(row, 1)  # Status is now column 1
                if status_item:
                    status_item.setText("Queued")
                message_item = self.transfer_table.item(row, 3)  # Message is now column 3
                if message_item:
                    message_item.setText("Waiting for processing...")
            return  # Don't create duplicate

        # Insert at the bottom (append) to fill table from row 1 downward
        row_count = self.transfer_table.rowCount()
        self.transfer_table.insertRow(row_count)

        # Create display path (relative to monitored directory if possible)
        display_path = filepath
        if self.dir_input.text() and filepath.startswith(self.dir_input.text()):
            relative_path = os.path.relpath(filepath, self.dir_input.text())
            if not relative_path.startswith(".."):
                display_path = relative_path

        # Set basic info in the new bottom row
        # Use display_path in File column (combines path and filename)
        self.transfer_table.setItem(row_count, 0, QTableWidgetItem(display_path))
        # Column 1 is now Status (was Path)
        self.transfer_table.setItem(row_count, 1, QTableWidgetItem("Queued"))

        # Add empty progress bar (now in column 2)
        progress_bar = QProgressBar()
        progress_bar.setValue(0)
        progress_bar.setVisible(False)  # Hide progress bar for queued files
        self.transfer_table.setCellWidget(row_count, 2, progress_bar)

        # Set message (now in column 3)
        self.transfer_table.setItem(row_count, 3, QTableWidgetItem("Waiting for processing..."))

        # Track the row (now at the bottom)
        self.transfer_rows[unique_key] = row_count

        # Auto-scroll to show the newly added item at the bottom
        self.transfer_table.scrollToBottom()

    def view_full_logs(self):
        """Open a dialog to view full application logs"""
        dialog = QDialog(self)
        dialog.setWindowTitle("Application Logs")
        dialog.setMinimumSize(800, 600)

        layout = QVBoxLayout()

        # Info label
        info_label = QLabel("Application logs (also saved to panoramabridge.log)")
        layout.addWidget(info_label)

        # Log content
        log_content = QTextEdit()
        log_content.setReadOnly(True)
        log_content.setFont(QFont("Courier", 9))

        # Try to read the log file
        try:
            with open("panoramabridge.log") as f:
                log_content.setText(f.read())
        except FileNotFoundError:
            log_content.setText(
                "No log file found yet. Logs will appear here as the application runs."
            )
        except Exception as e:
            log_content.setText(f"Error reading log file: {e}")

        layout.addWidget(log_content)

        # Close button
        close_btn = QPushButton("Close")
        close_btn.clicked.connect(dialog.close)
        layout.addWidget(close_btn)

        dialog.setLayout(layout)
        dialog.exec()

    def browse_local_directory(self):
        """Browse for local directory to monitor"""
        directory = QFileDialog.getExistingDirectory(self, "Select Directory to Monitor")
        if directory:
            self.dir_input.setText(directory)

    def browse_remote_directory(self):
        """Browse remote WebDAV directory"""
        if not self.webdav_client:
            # Try to connect first
            if not self.connect_webdav():
                QMessageBox.warning(
                    self, "Connection Required", "Please enter valid connection details first."
                )
                return

        # Open remote browser
        initial_path = self.remote_path_input.text() or "/"
        browser = RemoteBrowserDialog(self.webdav_client, self, initial_path)
        if browser.exec():
            selected = browser.get_selected_path()
            if selected:
                self.remote_path_input.setText(selected)

    def connect_webdav(self) -> bool:
        """Establish WebDAV connection"""
        url = self.url_input.text()
        username = self.username_input.text()
        password = self.password_input.text()
        auth_type = self.auth_combo.currentText().lower()

        if not all([url, username, password]):
            logger.warning("Missing connection details")
            return False

        try:
            logger.info(f"Attempting to connect to WebDAV at: {url}")
            self.webdav_client = WebDAVClient(url, username, password, auth_type)
            if self.webdav_client.test_connection():
                logger.info(f"Successfully connected to WebDAV server at: {self.webdav_client.url}")

                # Update the URL field if it was automatically modified (e.g., /webdav was appended)
                if self.webdav_client.url != url:
                    logger.info(f"URL was updated from {url} to {self.webdav_client.url}")
                    self.url_input.setText(self.webdav_client.url)

                # Update processor
                remote_path = self.remote_path_input.text() or "/"
                self.file_processor.set_webdav_client(self.webdav_client, remote_path)

                # Save credentials if requested
                if self.save_creds_check.isChecked():
                    if KEYRING_AVAILABLE and keyring is not None:
                        try:
                            keyring.set_password("PanoramaBridge", f"{url}_username", username)
                            keyring.set_password("PanoramaBridge", f"{url}_password", password)
                            logger.info("Credentials saved successfully")
                        except Exception as e:
                            logger.warning(f"Failed to save credentials: {e}")
                    else:
                        logger.warning("Keyring not available - cannot save credentials")

                return True
            else:
                logger.error(f"Failed to connect to WebDAV server at: {url}")
        except Exception as e:
            logger.error(f"Failed to connect: {e}")

        return False

    def test_connection(self):
        """Test WebDAV connection"""
        if self.connect_webdav():
            url = self.webdav_client.url if self.webdav_client else "Unknown"
            QMessageBox.information(self, "Success", f"Connection successful\nConnected to: {url}")
            self.log_text.append(
                f"{datetime.now().strftime('%H:%M:%S')} - Connected to WebDAV server at {url}"
            )
        else:
            QMessageBox.warning(
                self,
                "Failed",
                "Could not connect to WebDAV server.\nCheck your URL, username, and password.",
            )
            self.log_text.append(f"{datetime.now().strftime('%H:%M:%S')} - Connection failed")

    def toggle_monitoring(self):
        """Start or stop monitoring"""
        if self.observer and self.observer.is_alive():
            # Stop monitoring
            self.observer.stop()
            self.observer.join()
            self.observer = None
            self.poll_timer.stop()  # Stop polling timer
            self.start_btn.setText("Start Monitoring")
            self.status_label.setText("Not monitoring")
            self.status_label.setStyleSheet("font-weight: bold; color: red;")
            self.log_text.append(f"{datetime.now().strftime('%H:%M:%S')} - Stopped monitoring")

            # Improved queue management when stopping monitoring
            self.clear_queue_on_stop()

            # Note: Keep processing_files and transfer_rows intact as files may still be transferring
        else:
            # Start monitoring
            directory = self.dir_input.text()
            extensions = [e.strip() for e in self.extensions_input.text().split(",") if e.strip()]

            if not directory:
                QMessageBox.warning(self, "Error", "Please select a directory to monitor")
                return

            if not os.path.exists(directory):
                QMessageBox.warning(self, "Error", "Selected directory does not exist")
                return

            if not extensions:
                QMessageBox.warning(self, "Error", "Please specify at least one file extension")
                return

            # Check WebDAV connection
            if not self.webdav_client:
                if not self.connect_webdav():
                    QMessageBox.warning(self, "Error", "Please configure WebDAV connection first")
                    return

            # Update processor settings
            self.file_processor.set_local_base(directory)
            self.file_processor.preserve_structure = True  # Always preserve directory structure
            self.file_processor.verify_uploads = self.verify_uploads_check.isChecked()

            # Set conflict resolution preference
            conflict_setting = self.get_conflict_resolution_setting()
            if conflict_setting != "ask":
                self.file_processor.conflict_resolution = conflict_setting
                self.file_processor.apply_to_all = True
            else:
                self.file_processor.conflict_resolution = None
                self.file_processor.apply_to_all = False

            # Create handler and observer with error handling
            try:
                self.monitor_handler = FileMonitorHandler(
                    extensions,
                    self.file_queue,
                    self.subdirs_check.isChecked(),
                    self,  # Pass app instance for duplicate tracking
                )

                self.observer = Observer()
                self.observer.schedule(
                    self.monitor_handler, directory, recursive=self.subdirs_check.isChecked()
                )

                self.observer.start()
                logger.info(f"Started OS-level file monitoring for: {directory}")
            except Exception as monitor_error:
                logger.error(f"Failed to start file monitoring: {monitor_error}", exc_info=True)
                QMessageBox.critical(
                    self,
                    "Monitoring Error",
                    f"Failed to start file monitoring:\n{str(monitor_error)}\n\nCheck the log for details.",
                )
                # Clean up on error
                if hasattr(self, "observer") and self.observer:
                    try:
                        self.observer.stop()
                        self.observer = None
                    except Exception:
                        pass
                return

            # Only start polling if explicitly enabled by user
            if self.enable_polling_check.isChecked():
                polling_interval_ms = (
                    self.polling_interval_spin.value() * 60 * 1000
                )  # Convert minutes to ms
                self.poll_timer.start(polling_interval_ms)
                logger.info(
                    f"Started backup polling every {self.polling_interval_spin.value()} minutes"
                )
            else:
                logger.info("Backup polling disabled - relying on OS file events only")

            # Clear the transfer status table for a fresh start
            self.clear_transfer_table()

            # Scan for existing files in the directory
            self.scan_existing_files(directory, extensions, self.subdirs_check.isChecked())

            # Verify remote integrity of previously uploaded files
            # Disabled on 12/23/2025. This function will verify files that have been previously
            # uploaded from the monitored directory. This is already done in "self.scan_existing_files"
            # self.verify_remote_integrity_on_start(directory, extensions, self.subdirs_check.isChecked())

            self.start_btn.setText("Stop Monitoring")
            self.status_label.setText("Monitoring active")
            self.status_label.setStyleSheet("font-weight: bold; color: green;")

            self.log_text.append(
                f"{datetime.now().strftime('%H:%M:%S')} - Started monitoring {directory}"
            )
            self.log_text.append(f"Extensions: {', '.join(extensions)}")

            # Show upload history status
            if self.upload_history:
                self.log_text.append(
                    f"{datetime.now().strftime('%H:%M:%S')} - Upload history: {len(self.upload_history)} files previously uploaded"
                )

            # Log the actual monitoring configuration
            if self.enable_polling_check.isChecked():
                polling_interval = self.polling_interval_spin.value()
                self.log_text.append(
                    f"{datetime.now().strftime('%H:%M:%S')} - OS file events + backup polling every {polling_interval} minutes"
                )
            else:
                self.log_text.append(
                    f"{datetime.now().strftime('%H:%M:%S')} - OS file events only (backup polling disabled)"
                )

    def scan_existing_files(self, directory: str, extensions: list[str], recursive: bool):
        """Scan directory for existing files and add them to the queue"""
        logger.info(f"Scanning existing files in {directory}")
        logger.info(f"Recursive scanning: {recursive}")

        # Convert extensions to the same format as FileMonitorHandler
        formatted_extensions = [
            ext.lower() if ext.startswith(".") else f".{ext.lower()}" for ext in extensions
        ]
        logger.info(f"Scanning for extensions: {formatted_extensions}")

        files_found = 0

        try:
            if recursive:
                # Recursively scan all subdirectories
                logger.info("Starting recursive scan using os.walk")
                for root, dirs, files in os.walk(directory):
                    logger.debug(f"Scanning directory: {root}")
                    for file in files:
                        filepath = os.path.join(root, file)
                        logger.debug(f"Checking file: {filepath}")

                        # Skip hidden/system files
                        if file.startswith(".") or file.startswith("~"):
                            logger.debug(f"Skipping hidden/system file: {file}")
                            continue

                        if any(filepath.lower().endswith(ext) for ext in formatted_extensions):
                            files_found += 1
                            logger.info(f"Found existing file: {filepath}")

                            # Check if file is already uploaded
                            is_uploaded, reason = self.is_file_already_uploaded(filepath)
                            if is_uploaded:
                                # Add to table as "Completed" - already uploaded
                                self.add_completed_file_to_table(filepath, reason)
                                logger.debug(f"File already uploaded: {os.path.basename(filepath)} ({reason})")
                            else:
                                # Check for duplicates before queueing
                                if self._should_queue_file_scan_new(filepath):
                                    self.file_queue.put(filepath)
                                    logger.info(f"Queued existing file: {filepath}")
                                    # Add to transfer table with "Queued" status
                                    self.add_queued_file_to_table(filepath)
                                else:
                                    logger.debug(f"File already queued or processing, skipping: {filepath}")
                        else:
                            logger.debug(f"File {filepath} doesn't match extensions")
            else:
                # Scan only the top-level directory
                logger.info("Starting non-recursive scan")
                try:
                    for item in os.listdir(directory):
                        filepath = os.path.join(directory, item)
                        if os.path.isfile(filepath):
                            # Skip hidden/system files
                            if item.startswith(".") or item.startswith("~"):
                                logger.debug(f"Skipping hidden/system file: {item}")
                                continue

                            if any(filepath.lower().endswith(ext) for ext in formatted_extensions):
                                files_found += 1
                                logger.info(f"Found existing file: {filepath}")

                                # Check if file is already uploaded
                                is_uploaded, reason = self.is_file_already_uploaded(filepath)
                                if is_uploaded:
                                    # Add to table as "Completed" - already uploaded
                                    self.add_completed_file_to_table(filepath, reason)
                                    logger.debug(f"File already uploaded: {os.path.basename(filepath)} ({reason})")
                                else:
                                    # Check for duplicates before queueing
                                    if self._should_queue_file_scan_new(filepath):
                                        self.file_queue.put(filepath)
                                        logger.info(f"Queued existing file: {filepath}")
                                        # Add to transfer table with "Queued" status
                                        self.add_queued_file_to_table(filepath)
                                    else:
                                        logger.debug(f"File already queued or processing, skipping: {filepath}")
                except OSError as e:
                    logger.error(f"Error listing directory {directory}: {e}")
        except Exception as e:
            logger.error(f"Error scanning directory {directory}: {e}")

        logger.info(f"Scan complete: {files_found} existing files found")

        if files_found > 0:
            self.log_text.append(
                f"{datetime.now().strftime('%H:%M:%S')} - Found {files_found} existing files matching criteria"
            )
            logger.info(f"Scan complete: {files_found} existing files found")
        else:
            self.log_text.append(
                f"{datetime.now().strftime('%H:%M:%S')} - No existing files found matching criteria"
            )
            logger.info("Scan complete: no existing files found")

    def verify_remote_integrity_on_start(self, directory: str, extensions: list[str], recursive: bool):
        """Verify integrity of previously uploaded files and re-queue any that need fixing"""
        if not self.webdav_client or not self.upload_history:
            return

        logger.info("Starting remote integrity verification for previously uploaded files")
        self.log_text.append(
            f"{datetime.now().strftime('%H:%M:%S')} - Verifying remote file integrity..."
        )

        files_to_reupload = []
        files_verified = 0
        files_checked = 0

        # Check each file in upload history that might be in the current monitoring scope
        for filepath, history_entry in list(self.upload_history.items()):  # Use list() to avoid dict change during iteration
            try:
                # Check if file is within the monitoring directory and matches extensions
                if not self._is_file_in_monitoring_scope(filepath, directory, extensions, recursive):
                    continue

                files_checked += 1

                # Check if local file still exists
                if not os.path.exists(filepath):
                    logger.info(f"Local file no longer exists, removing from history: {filepath}")
                    del self.upload_history[filepath]
                    continue

                remote_path = history_entry.get("remote_path")
                expected_checksum = history_entry.get("checksum")

                if not remote_path or not expected_checksum:
                    logger.warning(f"Incomplete history entry for {filepath}, will re-verify")
                    files_to_reupload.append((filepath, "incomplete history"))
                    continue

                # Verify remote file integrity
                logger.debug(f"Verifying remote integrity for: {os.path.basename(filepath)}")
                remote_ok, reason = self.verify_remote_file_integrity(filepath, remote_path, expected_checksum)

                if remote_ok:
                    files_verified += 1
                    logger.debug(f"Remote file verified: {os.path.basename(filepath)} - {reason}")
                    # Add verified file to table as "Completed"
                    self.add_completed_file_to_table(filepath, f"verified intact - {reason}")
                else:
                    logger.warning(f"Remote integrity failed: {os.path.basename(filepath)} - {reason}")
                    files_to_reupload.append((filepath, reason))

            except Exception as e:
                logger.error(f"Error checking remote integrity for {filepath}: {e}")
                files_to_reupload.append((filepath, f"verification error: {e}"))

        # Queue files that need re-uploading
        requeued_count = 0
        for filepath, reason in files_to_reupload:
            if self._should_queue_file_for_reupload(filepath):
                self.file_queue.put(filepath)
                self.add_queued_file_to_table(filepath)
                requeued_count += 1
                logger.info(f"Re-queued file with remote issues: {os.path.basename(filepath)} ({reason})")

        # Save any history changes
        if files_to_reupload:
            self.save_upload_history()

        # Log summary
        if files_checked > 0:
            self.log_text.append(
                f"{datetime.now().strftime('%H:%M:%S')} - Integrity check complete: {files_verified} verified, {requeued_count} re-queued"
            )
            logger.info(f"Remote integrity check complete: checked={files_checked}, verified={files_verified}, requeued={requeued_count}")
        else:
            logger.info("No files in monitoring scope found in upload history")

    def _is_file_in_monitoring_scope(self, filepath: str, directory: str, extensions: list[str], recursive: bool) -> bool:
        """Check if a file is within the current monitoring scope"""
        try:
            # Check if file is under the monitoring directory
            filepath_abs = os.path.abspath(filepath)
            directory_abs = os.path.abspath(directory)

            if not filepath_abs.startswith(directory_abs):
                return False

            # If not recursive, check if file is directly in the directory (not in subdirectory)
            if not recursive:
                parent_dir = os.path.dirname(filepath_abs)
                if parent_dir != directory_abs:
                    return False

            # Check if file extension matches
            formatted_extensions = [
                ext.lower() if ext.startswith(".") else f".{ext.lower()}" for ext in extensions
            ]

            return any(filepath_abs.lower().endswith(ext) for ext in formatted_extensions)

        except Exception as e:
            logger.error(f"Error checking monitoring scope for {filepath}: {e}")
            return False

    def _should_queue_file_for_reupload(self, filepath: str) -> bool:
        """Check if file should be queued for re-upload (different from initial scan logic)"""
        # Don't queue if already queued or processing
        if filepath in self.queued_files or filepath in self.processing_files:
            return False

        # Add to tracking
        self.queued_files.add(filepath)
        return True

    def _should_queue_file_scan(self, filepath: str) -> bool:
        """
        Check if a file should be queued during scanning, preventing duplicates.

        Args:
            filepath: Path to the file being considered for queueing

        Returns:
            True if file should be queued, False if already queued/processing/uploaded
        """
        # Check if file is already queued or being processed
        if filepath in self.queued_files:
            logger.debug(f"File already queued, skipping: {filepath}")
            return False
        if filepath in self.processing_files:
            logger.debug(f"File already processing, skipping: {filepath}")
            return False

        # Check if file was already successfully uploaded
        is_uploaded, reason = self.is_file_already_uploaded(filepath)
        if is_uploaded:
            logger.info(f"File already uploaded, skipping: {os.path.basename(filepath)} ({reason})")
            return False
        else:
            logger.debug(
                f"File not in upload history or changed, queueing: {os.path.basename(filepath)} ({reason})"
            )

        # Add to queued files tracking
        self.queued_files.add(filepath)
        return True

    def _should_queue_file_scan_new(self, filepath: str) -> bool:
        """
        Check if a file should be queued during scanning (for files that need upload).
        This version only checks for duplicate queuing, not upload status.

        Args:
            filepath: Path to the file being considered for queueing

        Returns:
            True if file should be queued, False if already queued/processing
        """
        # Check if file is already queued or being processed
        if filepath in self.queued_files:
            logger.debug(f"File already queued, skipping: {filepath}")
            return False
        if filepath in self.processing_files:
            logger.debug(f"File already processing, skipping: {filepath}")
            return False

        # Add to queued files tracking
        self.queued_files.add(filepath)
        return True

    def add_completed_file_to_table(self, filepath: str, status_reason: str):
        """Add a completed (already uploaded) file to the transfer table"""
        relative_path = os.path.relpath(filepath, self.dir_input.text())

        # Get upload info from history
        history_entry = self.upload_history.get(filepath, {})
        checksum = history_entry.get('checksum', 'Unknown')
        upload_time = history_entry.get('timestamp', 'Unknown')
        remote_path = history_entry.get('remote_path', 'Unknown')

        # Format upload time
        if upload_time != 'Unknown' and isinstance(upload_time, int | float):
            try:
                upload_time_str = datetime.fromtimestamp(upload_time).strftime('%Y-%m-%d %H:%M:%S')
            except (OSError, ValueError):
                upload_time_str = 'Unknown'
        else:
            upload_time_str = str(upload_time)

        # Create detailed status message using consistent format
        if checksum != 'Unknown':
            status_msg = f"Remote file already exists with same content (checksum: {checksum[:12]}...)"
        else:
            status_msg = f"Completed - {status_reason}"

        # Add to table with "Completed" status
        if self.add_file_to_table_with_status(
            filepath=filepath,
            relative_path=relative_path,
            status="Completed",
            message=status_msg
        ):
            logger.debug(f"Added file to transfer table with completed status: {relative_path}")

    def add_file_to_table_with_status(self, filepath: str, relative_path: str, status: str, message: str):
        """Helper method to add a file to the transfer table with specific status"""
        # Avoid duplicates
        file_key = f"{relative_path}|{filepath}"
        if file_key in self.transfer_rows:
            logger.debug(f"File already in transfer table: {relative_path} with status {self.transfer_table.item(self.transfer_rows[file_key], 1).text()}")
            return False

        row = self.transfer_table.rowCount()
        self.transfer_table.insertRow(row)

        # File column
        self.transfer_table.setItem(row, 0, QTableWidgetItem(relative_path))

        # Status column
        status_item = QTableWidgetItem(status)
        if status == "Completed":
            status_item.setBackground(QColor(144, 238, 144))  # Light green
        elif status == "Queued":
            status_item.setBackground(QColor(255, 255, 224))  # Light yellow
        self.transfer_table.setItem(row, 1, status_item)

        # Progress column (empty for completed files)
        progress_item = QTableWidgetItem("")
        self.transfer_table.setItem(row, 2, progress_item)

        # Message column
        self.transfer_table.setItem(row, 3, QTableWidgetItem(message))

        # Store reference
        self.transfer_rows[file_key] = row

        # Auto-scroll to bottom to show new files
        self.transfer_table.scrollToBottom()

        return True

    def update_queue_size(self):
        """Update queue size display with enhanced debugging"""
        size = self.file_queue.qsize()
        queued_count = len(self.queued_files)
        processing_count = len(self.processing_files)

        self.queue_label.setText(f"{size} files")

        # Add diagnostic logging every 10 seconds (timer runs every 1 second)
        if not hasattr(self, "queue_debug_counter"):
            self.queue_debug_counter = 0
        self.queue_debug_counter += 1

        if self.queue_debug_counter >= 10:  # Every 10 seconds
            self.queue_debug_counter = 0
            if size > 0 or queued_count > 0 or processing_count > 0:
                logger.info(
                    f"Queue status: queue={size}, queued_files={queued_count}, processing_files={processing_count}"
                )
                logger.info(
                    f"FileProcessor running: {getattr(self.file_processor, 'running', 'Unknown')}"
                )
                logger.info(
                    f"WebDAV client configured: {self.file_processor.webdav_client is not None}"
                )
                if queued_count > 0:
                    logger.info(f"Sample queued files: {list(self.queued_files)[:3]}")
                if processing_count > 0:
                    logger.info(f"Sample processing files: {list(self.processing_files)[:3]}")

    def clear_queue_on_stop(self):
        """Clear queue and tracking sets when stopping monitoring with improved handling"""
        # Clear the queue itself - drain any pending files
        queue_count = 0
        try:
            while not self.file_queue.empty():
                try:
                    self.file_queue.get_nowait()
                    queue_count += 1
                except queue.Empty:
                    break
        except Exception as e:
            logger.warning(f"Error clearing queue: {e}")

        if queue_count > 0:
            logger.info(f"Cleared {queue_count} files from queue when stopping monitoring")

        # Clear tracking sets
        queued_count = len(self.queued_files)
        self.queued_files.clear()
        self.created_directories.clear()
        self.failed_files.clear()
        self.file_remote_paths.clear()

        if queued_count > 0:
            logger.info(f"Cleared {queued_count} files from tracking sets")

        # Update queue display
        self.update_queue_size()

    def clear_transfer_table(self):
        """Clear all rows from the transfer status table"""
        self.transfer_table.setRowCount(0)
        # Clear internal tracking
        self.transfer_rows.clear()
        logger.info("Cleared transfer status table for fresh monitoring start")

    def poll_for_new_files(self):
        """
        Periodic polling for new files as backup to OS file system events.

        This method serves as a fallback when OS file system events aren't
        working properly (common on network mounts, WSL, etc.). It scans
        the monitored directory for new files that haven't been processed.

        Note: This is only used when explicitly enabled by the user, as
        OS file events are much more efficient and responsive.
        """
        if not self.observer or not self.observer.is_alive():
            logger.warning("Polling attempted but observer is not running")
            return

        try:
            directory = self.dir_input.text()
            extensions = [e.strip() for e in self.extensions_input.text().split(",") if e.strip()]

            if not directory or not extensions:
                return

            # Convert extensions to the same format as FileMonitorHandler
            formatted_extensions = [
                ext.lower() if ext.startswith(".") else f".{ext.lower()}" for ext in extensions
            ]

            logger.debug(f"Backup polling scan: {directory}")
            files_found = 0

            if self.subdirs_check.isChecked():
                # Recursively scan all subdirectories
                for root, dirs, files in os.walk(directory):
                    for file in files:
                        filepath = os.path.join(root, file)

                        # Skip hidden/system files
                        if file.startswith(".") or file.startswith("~"):
                            continue

                        if any(filepath.lower().endswith(ext) for ext in formatted_extensions):
                            # Check if this is a new file we haven't seen
                            if self._should_queue_file_poll(filepath):
                                # Check if file is stable (not being written)
                                if self._is_file_stable(filepath):
                                    self.file_queue.put(filepath)
                                    files_found += 1
                                    logger.info(
                                        f"Polling backup found file (OS events missed): {filepath}"
                                    )
                                    # Add to transfer table with "Queued" status
                                    self.add_queued_file_to_table(filepath)
            else:
                # Scan only the main directory
                try:
                    files = os.listdir(directory)
                    for file in files:
                        filepath = os.path.join(directory, file)

                        # Skip directories, hidden files, system files
                        if os.path.isdir(filepath) or file.startswith(".") or file.startswith("~"):
                            continue

                        if any(filepath.lower().endswith(ext) for ext in formatted_extensions):
                            if self._should_queue_file_poll(filepath):
                                if self._is_file_stable(filepath):
                                    self.file_queue.put(filepath)
                                    files_found += 1
                                    logger.info(
                                        f"Polling backup found file (OS events missed): {filepath}"
                                    )
                                    # Add to transfer table with "Queued" status
                                    self.add_queued_file_to_table(filepath)
                except Exception as e:
                    logger.error(f"Error scanning directory {directory}: {e}")

            if files_found > 0:
                logger.info(f"Backup polling found {files_found} files that OS events missed")
            else:
                logger.debug("Backup polling scan complete - no new files found")

        except Exception as e:
            logger.error(f"Error in backup polling: {e}")

    def _should_queue_file_poll(self, filepath: str) -> bool:
        """
        Check if a file should be queued during polling, preventing duplicates and avoiding re-upload of unchanged files.

        Args:
            filepath: Path to the file being considered for queueing

        Returns:
            True if file should be queued, False if already queued/processing or unchanged
        """
        # Check if file is already queued or being processed
        if filepath in self.queued_files:
            logger.debug(f"Polling: File already queued, skipping: {filepath}")
            return False
        if filepath in self.processing_files:
            logger.debug(f"Polling: File currently processing, skipping: {filepath}")
            return False

        # Check if file was already uploaded and hasn't changed
        if filepath in self.upload_history:
            try:
                # Calculate current file checksum
                current_checksum = self.file_processor.calculate_checksum(filepath)
                stored_info = self.upload_history[filepath]
                stored_checksum = stored_info.get('checksum', '')

                if current_checksum == stored_checksum:
                    logger.info(f"Polling: File unchanged since last upload, skipping: {filepath}")
                    return False
                else:
                    logger.info(f"Polling: File modified since last upload, will re-upload: {filepath}")
            except Exception as e:
                logger.warning(f"Polling: Error checking file checksum for {filepath}: {e}")
                # Continue with upload if we can't verify the checksum

        # Add to queued files tracking
        self.queued_files.add(filepath)
        return True

    def _is_file_stable(self, filepath: str, stability_time: float = 2.0) -> bool:
        """
        Check if a file is stable (not being written to).

        Args:
            filepath: Path to the file to check
            stability_time: Time in seconds to wait for size stability

        Returns:
            True if file appears stable, False otherwise
        """
        try:
            # Get initial file stats
            stat1 = os.stat(filepath)
            time.sleep(0.1)  # Brief pause
            stat2 = os.stat(filepath)

            # Check if size and modification time are stable
            return (
                stat1.st_size == stat2.st_size
                and stat1.st_mtime == stat2.st_mtime
                and time.time() - stat1.st_mtime > stability_time
            )
        except OSError:
            return False

    @pyqtSlot(str, str, str)
    def on_status_update(self, filename: str, status: str, filepath: str):
        """Handle status updates from processor - updates existing entries, doesn't create new ones"""
        # Create unique key for files with same name in different directories
        unique_key = self.get_transfer_table_key(filename, filepath)

        if unique_key not in self.transfer_rows:
            # This shouldn't happen if files are properly queued first
            # But as a fallback, create the entry (this handles edge cases)
            logger.warning(f"Status update for file not in table, creating entry: {filename}")

            # Add new row at the bottom (consistent with add_queued_file_to_table)
            row_count = self.transfer_table.rowCount()
            self.transfer_table.insertRow(row_count)

            # Calculate relative path for display (use full relative path including filename)
            display_path = filepath
            if self.dir_input.text() and filepath.startswith(self.dir_input.text()):
                relative_path = os.path.relpath(filepath, self.dir_input.text())
                if not relative_path.startswith(".."):
                    display_path = relative_path

            # Use display_path in File column (combines path and filename)
            self.transfer_table.setItem(row_count, 0, QTableWidgetItem(display_path))
            # Column 1 is now Status (was Path)
            self.transfer_table.setItem(row_count, 1, QTableWidgetItem(status))

            progress_bar = QProgressBar()
            progress_bar.setMinimum(0)
            progress_bar.setMaximum(100)  # Always use percentage for consistency
            progress_bar.setValue(0)
            # Show progress bar only for active processing states
            if status in ["Queued", "Starting", "Pending"]:
                progress_bar.setVisible(False)
            else:
                progress_bar.setVisible(True)
            self.transfer_table.setCellWidget(row_count, 2, progress_bar)

            self.transfer_table.setItem(row_count, 3, QTableWidgetItem(""))

            self.transfer_rows[unique_key] = row_count

            # Auto-scroll to show active processing when starting
            if status not in ["Queued", "Starting", "Pending"]:
                self.transfer_table.scrollToItem(self.transfer_table.item(row_count, 0))

        else:
            # Update existing row - this is the normal case
            row = self.transfer_rows[unique_key]
            if row < self.transfer_table.rowCount():
                current_item = self.transfer_table.item(row, 1)  # Status is now column 1
                current_status = current_item.text() if current_item else ""
                if current_item:
                    current_item.setText(status)

                # Show progress bar when transitioning from queued to active processing
                if current_status == "Queued" and status not in ["Queued", "Starting", "Pending"]:
                    progress_bar = self.transfer_table.cellWidget(
                        row, 2
                    )  # Progress is now column 2
                    if progress_bar and hasattr(progress_bar, "setVisible"):
                        progress_bar.setVisible(True)
                        # Scroll to show the file that just started processing
                        self.transfer_table.scrollToItem(self.transfer_table.item(row, 0))

    @pyqtSlot(str, int, int)
    def on_progress_update(self, filepath: str, current: int, total: int):
        """Handle progress updates from processor"""
        filename = os.path.basename(filepath)
        unique_key = self.get_transfer_table_key(filename, filepath)
        if unique_key in self.transfer_rows:
            row = self.transfer_rows[unique_key]
            if row < self.transfer_table.rowCount():
                progress_bar = self.transfer_table.cellWidget(row, 2)  # Progress is now column 2
                if progress_bar and hasattr(progress_bar, "setValue"):
                    # Always use percentage (0 - 100) for consistent progress bar display
                    if total > 0:
                        percentage = int((current / total) * 100)
                        progress_bar.setValue(min(percentage, 100))  # Ensure it doesn't exceed 100
                    else:
                        progress_bar.setValue(0)

    @pyqtSlot(str, str, bool, str)
    def on_transfer_complete(self, filename: str, filepath: str, success: bool, message: str):
        """Handle transfer completion"""
        unique_key = self.get_transfer_table_key(filename, filepath)
        if unique_key in self.transfer_rows:
            row = self.transfer_rows[unique_key]
            if row < self.transfer_table.rowCount():
                status = "Complete" if success else "Failed"
                status_item = self.transfer_table.item(row, 1)  # Status is now column 1
                message_item = self.transfer_table.item(row, 3)  # Message is now column 3
                if status_item:
                    status_item.setText(status)
                if message_item:
                    message_item.setText(message)

                # Track failed files for re-upload (specifically verification failures)
                if not success and (
                    "verification failed" in message.lower() or "checksum" in message.lower()
                ):
                    self.failed_files[unique_key] = {
                        "filepath": filepath,
                        "filename": filename,
                        "message": message,
                        "row": row,
                    }
                elif success and unique_key in self.failed_files:
                    # Remove from failed files if now successful
                    del self.failed_files[unique_key]

                # Clean up remote path tracking when transfer is complete
                if filepath in self.file_remote_paths:
                    del self.file_remote_paths[filepath]

                # Update progress bar - ensure it shows 100% when complete
                progress_bar = self.transfer_table.cellWidget(row, 2)  # Progress is now column 2
                if progress_bar and hasattr(progress_bar, "setValue"):
                    if success:
                        progress_bar.setValue(100)  # Always show 100% for successful completion
                    else:
                        progress_bar.setStyleSheet("QProgressBar::chunk { background-color: red; }")

        # Log the event
        timestamp = datetime.now().strftime("%H:%M:%S")
        if success:
            self.log_text.append(f"{timestamp} - [OK] {filename}: {message}")
        else:
            self.log_text.append(f"{timestamp} - [FAIL] {filename}: {message}")

    @pyqtSlot(str, str, str, dict)
    def on_conflict_resolution_needed(
        self, filename: str, filepath: str, remote_path: str, conflict_details: dict
    ):
        """Handle conflict resolution requests from file processor"""
        # Show conflict resolution dialog with enhanced information
        dialog = FileConflictDialog(filename, conflict_details, self)
        result = dialog.exec()

        if result == QDialog.DialogCode.Accepted:
            # Get resolution from dialog
            resolution, new_name, apply_to_all = dialog.get_resolution()

            # Update file processor settings
            self.file_processor.conflict_resolution = resolution
            self.file_processor.apply_to_all = apply_to_all

            # Check for duplicates before re-queueing
            if filepath not in self.queued_files and filepath not in self.processing_files:
                # Re-queue the file for processing with the resolution
                self.file_queue.put(
                    {
                        "filepath": filepath,
                        "filename": filename,
                        "remote_path": remote_path,
                        "resolution": resolution,
                        "new_name": new_name,
                    }
                )

                # Add to tracking (conflict resolution files bypass normal duplicate detection)
                self.queued_files.add(filepath)

                logger.info(
                    f"Conflict resolution: Re-queuing {filepath} with remote_path={remote_path}, resolution={resolution}"
                )

                # Log the resolution
                timestamp = datetime.now().strftime("%H:%M:%S")
                action_text = {
                    "overwrite": "overwrite remote file",
                    "rename": "rename and upload",
                    "skip": "skip upload",
                }.get(resolution, resolution)

                self.log_text.append(
                    f"{timestamp} - Conflict resolved for {filename}: {action_text}"
                )
                if apply_to_all:
                    self.log_text.append(
                        f"{timestamp} - Resolution will be applied to all future conflicts"
                    )
            else:
                # File already being processed
                timestamp = datetime.now().strftime("%H:%M:%S")
                self.log_text.append(
                    f"{timestamp} - Conflict resolution skipped for {filename}: already being processed"
                )
        else:
            # User cancelled - skip this file
            timestamp = datetime.now().strftime("%H:%M:%S")
            self.log_text.append(
                f"{timestamp} - Conflict resolution cancelled for {filename}: skipped"
            )

    def clear_completed_transfers(self):
        """Clear completed transfers from the table"""
        rows_to_remove = []

        for unique_key, row in self.transfer_rows.items():
            status_item = self.transfer_table.item(row, 1)  # Status is now column 1
            if status_item and status_item.text() in ["Complete", "Failed"]:
                rows_to_remove.append((row, unique_key))

        # Sort in reverse order to remove from bottom up
        rows_to_remove.sort(reverse=True)

        for row, unique_key in rows_to_remove:
            self.transfer_table.removeRow(row)
            del self.transfer_rows[unique_key]

            # Remove from failed files tracking if present
            if unique_key in self.failed_files:
                del self.failed_files[unique_key]

            # Update remaining row numbers
            for key, r in self.transfer_rows.items():
                if r > row:
                    self.transfer_rows[key] = r - 1

    def reupload_failed_files(self):
        """Re-upload all files that failed verification"""
        if not self.failed_files:
            QMessageBox.information(
                self, "No Failed Files", "There are no files that failed verification to re-upload."
            )
            return

        failed_count = len(self.failed_files)
        reply = QMessageBox.question(
            self,
            "Re-upload Failed Files",
            f"Re-upload {failed_count} file(s) that failed verification?",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
        )

        if reply == QMessageBox.StandardButton.Yes:
            requeued = 0
            for unique_key, failed_info in list(self.failed_files.items()):
                filepath = failed_info["filepath"]

                # Check if file still exists
                if os.path.exists(filepath):
                    # Check for duplicates before re-queueing
                    if filepath not in self.queued_files and filepath not in self.processing_files:
                        # Reset the row status
                        row = failed_info["row"]
                        if row < self.transfer_table.rowCount():
                            status_item = self.transfer_table.item(row, 1)  # Status is now column 1
                            message_item = self.transfer_table.item(
                                row, 3
                            )  # Message is now column 3
                            if status_item:
                                status_item.setText("Queued")
                            if message_item:
                                message_item.setText("Re-upload requested")

                            # Reset progress bar
                            progress_bar = self.transfer_table.cellWidget(
                                row, 2
                            )  # Progress is now column 2
                            if progress_bar:
                                progress_bar.setValue(0)
                                progress_bar.setStyleSheet("")  # Clear any error styling

                        # Add to queue for re-processing and tracking
                        self.file_queue.put(filepath)
                        self.queued_files.add(filepath)  # Add to tracking
                        requeued += 1

                        # Remove from failed files (will be re-added if it fails again)
                        del self.failed_files[unique_key]
                    else:
                        # File already queued/processing, remove from failed files
                        del self.failed_files[unique_key]
                else:
                    # File no longer exists, remove from tracking
                    del self.failed_files[unique_key]

            timestamp = datetime.now().strftime("%H:%M:%S")
            self.log_text.append(f"{timestamp} - Re-queued {requeued} failed file(s) for upload")

    def show_transfer_context_menu(self, position):
        """Show context menu for transfer table"""
        item = self.transfer_table.itemAt(position)
        if item is None:
            return

        row = item.row()
        status_item = self.transfer_table.item(row, 1)  # Status is now column 1

        if not status_item:
            return

        status = status_item.text()

        menu = QMenu(self)

        if status == "Failed":
            # Find the unique key for this row
            unique_key = None
            for key, r in self.transfer_rows.items():
                if r == row:
                    unique_key = key
                    break

            if unique_key and unique_key in self.failed_files:
                reupload_action = menu.addAction("Re-upload File")
                reupload_action.triggered.connect(lambda: self.reupload_single_file(unique_key))

        if menu.actions():
            menu.exec(self.transfer_table.mapToGlobal(position))

    def reupload_single_file(self, unique_key: str):
        """Re-upload a single failed file"""
        if unique_key not in self.failed_files:
            return

        failed_info = self.failed_files[unique_key]
        filepath = failed_info["filepath"]
        filename = failed_info["filename"]

        # Check if file still exists
        if not os.path.exists(filepath):
            QMessageBox.warning(
                self,
                "File Not Found",
                f"The file {filename} no longer exists and cannot be re-uploaded.",
            )
            # Remove from failed files tracking
            del self.failed_files[unique_key]
            return

        # Check for duplicates before re-queueing
        if filepath in self.queued_files or filepath in self.processing_files:
            QMessageBox.information(
                self,
                "File Already Queued",
                f"The file {filename} is already queued or being processed.",
            )
            # Remove from failed files since it's being handled
            del self.failed_files[unique_key]
            return

        # Reset the row status
        row = failed_info["row"]
        if row < self.transfer_table.rowCount():
            status_item = self.transfer_table.item(row, 1)  # Status is now column 1
            message_item = self.transfer_table.item(row, 3)  # Message is now column 3
            if status_item:
                status_item.setText("Queued")
            if message_item:
                message_item.setText("Re-upload requested")

            # Reset progress bar
            progress_bar = self.transfer_table.cellWidget(row, 2)  # Progress is now column 2
            if progress_bar:
                progress_bar.setValue(0)
                progress_bar.setStyleSheet("")  # Clear any error styling

        # Add to queue for re-processing and tracking
        self.file_queue.put(filepath)
        self.queued_files.add(filepath)  # Add to tracking

        # Remove from failed files (will be re-added if it fails again)
        del self.failed_files[unique_key]

        timestamp = datetime.now().strftime("%H:%M:%S")
        self.log_text.append(f"{timestamp} - Re-queued {filename} for upload")

    def get_conflict_resolution_setting(self) -> str:
        """Get the current conflict resolution setting"""
        if self.conflict_ask_radio.isChecked():
            return "ask"
        elif self.conflict_skip_radio.isChecked():
            return "skip"
        elif self.conflict_overwrite_radio.isChecked():
            return "overwrite"
        elif self.conflict_rename_radio.isChecked():
            return "rename"
        else:
            return "ask"  # Default

    def set_conflict_resolution_setting(self, setting: str):
        """Set the conflict resolution setting"""
        if setting == "ask":
            self.conflict_ask_radio.setChecked(True)
        elif setting == "skip":
            self.conflict_skip_radio.setChecked(True)
        elif setting == "overwrite":
            self.conflict_overwrite_radio.setChecked(True)
        elif setting == "rename":
            self.conflict_rename_radio.setChecked(True)
        else:
            self.conflict_ask_radio.setChecked(True)  # Default

    def load_config(self) -> dict:
        """Load configuration from file"""
        config_file = Path.home() / ".panoramabridge" / "config.json"

        if config_file.exists():
            try:
                with open(config_file) as f:
                    return json.load(f)
            except Exception:
                pass
        else:
            # Check for old config file and migrate if found
            old_config_file = Path.home() / ".file_monitor_webdav" / "config.json"
            if old_config_file.exists():
                logger.info("Found old configuration, migrating to new location...")
                try:
                    with open(old_config_file) as f:
                        config = json.load(f)

                    # Create new config directory and save
                    config_dir = Path.home() / ".panoramabridge"
                    config_dir.mkdir(exist_ok=True)

                    with open(config_file, "w") as f:
                        json.dump(config, f, indent=2)

                    logger.info("Configuration migrated successfully")
                    return config
                except Exception as e:
                    logger.warning(f"Failed to migrate old configuration: {e}")

        return {}

    def save_config(self):
        """Save configuration to file"""
        config_dir = Path.home() / ".panoramabridge"
        config_dir.mkdir(exist_ok=True)
        config_file = config_dir / "config.json"

        config = {
            "local_directory": self.dir_input.text(),
            "monitor_subdirs": self.subdirs_check.isChecked(),
            "extensions": self.extensions_input.text(),
            "preserve_structure": True,  # Always preserve directory structure
            "webdav_url": self.url_input.text(),
            "webdav_username": (
                self.username_input.text() if not self.save_creds_check.isChecked() else ""
            ),
            "webdav_auth_type": self.auth_combo.currentText(),
            "remote_path": self.remote_path_input.text(),
            "verify_uploads": self.verify_uploads_check.isChecked(),
            "save_credentials": self.save_creds_check.isChecked(),
            "conflict_resolution": self.get_conflict_resolution_setting(),
            "local_checksum_cache": (
                dict(self.local_checksum_cache) if hasattr(self, "local_checksum_cache") else {}
            ),
        }

        try:
            with open(config_file, "w") as f:
                json.dump(config, f, indent=2)
        except Exception as e:
            logger.error(f"Failed to save config: {e}")

    def load_upload_history(self):
        """Load persistent upload history from disk"""
        history_file = os.path.join(os.path.expanduser("~"), ".panoramabridge_history.pkl")
        try:
            if os.path.exists(history_file):
                with open(history_file, "rb") as f:
                    self.upload_history = pickle.load(f)
                logger.info(f"Loaded upload history: {len(self.upload_history)} files tracked")
            else:
                self.upload_history = {}
                logger.info("No upload history file found, starting fresh")
        except Exception as e:
            logger.warning(f"Failed to load upload history: {e}, starting fresh")
            self.upload_history = {}

    def save_upload_history(self):
        """Save persistent upload history to disk"""
        history_file = os.path.join(os.path.expanduser("~"), ".panoramabridge_history.pkl")
        try:
            with open(history_file, "wb") as f:
                pickle.dump(self.upload_history, f)
            logger.debug(f"Saved upload history: {len(self.upload_history)} files tracked")
        except Exception as e:
            logger.error(f"Failed to save upload history: {e}")

    def record_successful_upload(self, filepath: str, remote_path: str, checksum: str):
        """Record a successful upload in persistent history"""
        self.upload_history[filepath] = {
            "checksum": checksum,
            "remote_path": remote_path,
            "timestamp": datetime.now().isoformat(),
            "file_size": os.path.getsize(filepath) if os.path.exists(filepath) else 0,
        }
        self.save_upload_history()
        logger.info(f"Recorded successful upload: {os.path.basename(filepath)} -> {remote_path}")

    def verify_remote_file_integrity(self, local_filepath: str, remote_path: str, expected_checksum: str) -> tuple[bool, str]:
        """Verify that a remote file exists and matches the expected local file.
        First checks that file sizes match.
        If a remote checksum file exists (.checksum). If found, downloads and compares checksums.
        If the remote checksum file is not found or cannot be read, falls back to accessibility check.
        (ie can read first 8KB of remote file).
        Returns: (is_intact, reason)
        """
        logger.debug(f"Verifying remote file integrity for {remote_path}")
        try:
            # Get remote file info
            remote_info = self.webdav_client.get_file_info(remote_path)
            if not remote_info or not remote_info.get("exists", False):
                return False, "remote file not found"

            # Level 1: Size comparison (fastest - immediate)
            local_size = os.path.getsize(local_filepath) if os.path.exists(local_filepath) else 0
            remote_size = remote_info.get("size", 0)

            if local_size != remote_size:
                logger.debug(f"Size mismatch for {remote_path}: local {local_size}, remote {remote_size}")
                return False, f"size mismatch (local: {local_size}, remote: {remote_size})"

            # Level 2: Checksum verification (if checksum file exists)
            checksum_path = remote_path + ".checksum"
            checksum_info = self.webdav_client.get_file_info(checksum_path)

            if checksum_info and checksum_info.get("exists", False):
                try:
                    # Download the checksum file (should be very small, < 100 bytes)
                    checksum_data = self.webdav_client.download_file_head(checksum_path, 1024)
                    logger.debug(f'Downloaded checksum data: {checksum_data} from checksum file {checksum_path}')
                    if checksum_data:
                        remote_checksum = checksum_data.decode('utf-8').strip()

                        # Compare checksums
                        if expected_checksum == remote_checksum:
                            logger.debug("Checksums match")
                            return True, "Size + checksum verified"
                        else:
                            logger.debug(f"Checksum mismatch: local {expected_checksum}, remote {remote_checksum}")
                            return False, f"checksum mismatch (local: {expected_checksum[:16]}..., remote: {remote_checksum[:16]}...)"
                    else:
                        logger.warning(f"Failed to download checksum file {checksum_path}, falling back to accessibility check")
                except Exception as e:
                    logger.warning(f"Error during checksum verification for {remote_path}: {e}, falling back to accessibility check")

            # Level 3: Accessibility check (download first 8KB)
            try:
                logger.debug(f"Performing accessibility check for {remote_path}")
                if self.webdav_client is not None:
                    # Download just the first 8KB to check if file is accessible
                    head_data = self.webdav_client.download_file_head(remote_path, 8192)
                    if head_data is None:
                        logger.debug(f"Failed to read remote file {remote_path} during accessibility check")
                        return False, "cannot read remote file"

                    logger.debug(f"Accessibility check passed for {remote_path}")
                    return True, "Size + accessibility"
            except Exception as e:
                logger.debug(f"Accessibility check failed for {remote_path}: {e}")
                return False, f"accessibility check failed: {str(e)}"

        except Exception as e:
            logger.warning(f"Error verifying remote file {remote_path}: {e}")
            return False, f"verification error: {str(e)}"

    def is_file_already_uploaded_quick(self, filepath: str) -> tuple[bool, str]:
        """Quick check if file was already uploaded using cached checksums - NO remote verification
        This is optimized for performance during file system event processing.
        Returns: (is_uploaded, reason)
        """
        if filepath not in self.upload_history:
            return False, "not in history"

        history_entry = self.upload_history[filepath]

        # Check if local file still exists
        if not os.path.exists(filepath):
            return False, "local file no longer exists"

        try:
            # Quick size check first (very fast)
            current_size = os.path.getsize(filepath)
            stored_size = history_entry.get("file_size", 0)
            if stored_size > 0 and current_size != stored_size:
                return False, "file size changed"

            # Only calculate checksum if we don't have a cached one or if forced
            stored_checksum = history_entry.get("checksum")
            if not stored_checksum:
                return False, "no stored checksum"

            # Check if we have a cached checksum to avoid recalculation
            cache_key = f"{filepath}_{current_size}_{os.path.getmtime(filepath)}"
            if cache_key in self.local_checksum_cache:
                current_checksum = self.local_checksum_cache[cache_key]
            else:
                # Calculate and cache the checksum
                current_checksum = self.file_processor.calculate_checksum(filepath)
                self.local_checksum_cache[cache_key] = current_checksum

            if current_checksum != stored_checksum:
                return False, "file content changed"

            return True, f"already uploaded on {history_entry.get('timestamp', 'unknown date')}"

        except Exception as e:
            logger.warning(f"Error in quick upload check for {filepath}: {e}")
            return False, "error checking history"

    def is_file_already_uploaded(self, filepath: str) -> tuple[bool, str]:
        """Check if file was already uploaded successfully AND verify remote integrity
        Returns: (is_uploaded, reason)
        """
        if filepath not in self.upload_history:
            return False, "not in history"

        history_entry = self.upload_history[filepath]

        # Check if local file still exists and hasn't changed
        if not os.path.exists(filepath):
            return False, "local file no longer exists"

        try:
            # Quick size check first
            current_size = os.path.getsize(filepath)
            if current_size != history_entry.get("file_size", 0):
                return False, "file size changed"

            # If size matches, check checksum
            current_checksum = self.file_processor.calculate_checksum(filepath)
            if current_checksum != history_entry.get("checksum"):
                return False, "file content changed"

            # Now verify the remote file is still intact
            remote_path = history_entry.get("remote_path")
            if remote_path and self.webdav_client:
                remote_ok, remote_reason = self.verify_remote_file_integrity(filepath, remote_path, current_checksum)
                if not remote_ok:
                    # Remove from history since remote file is compromised
                    logger.warning(f"Remote file integrity failed for {filepath}: {remote_reason}")
                    del self.upload_history[filepath]
                    self.save_upload_history()
                    return False, f"remote file issue: {remote_reason}"

            return True, f"already uploaded on {history_entry.get('timestamp', 'unknown date')}"
        except Exception as e:
            logger.warning(f"Error checking upload history for {filepath}: {e}")
            return False, "error checking history"

    def load_settings(self):
        """
        Load settings from configuration file or apply defaults for new installations.

        This method populates all UI fields with either saved configuration values
        or sensible defaults for first-time users. Ensures that default values
        are actual field values, not just placeholder text.
        """
        # Always apply settings, even if config is empty (new installation)
        # The .get() method will return defaults for missing keys
        self.dir_input.setText(self.config.get("local_directory", ""))
        self.subdirs_check.setChecked(self.config.get("monitor_subdirs", True))
        self.extensions_input.setText(self.config.get("extensions", "raw, sld, csv"))
        # Note: preserve_structure is always True (removed checkbox), locked file handling always enabled
        self.url_input.setText(self.config.get("webdav_url", "https://panoramaweb.org"))
        self.username_input.setText(self.config.get("webdav_username", ""))

        # Set authentication type combo box
        auth_type = self.config.get("webdav_auth_type", "Basic")
        index = self.auth_combo.findText(auth_type)
        if index >= 0:
            self.auth_combo.setCurrentIndex(index)

        self.remote_path_input.setText(self.config.get("remote_path", "/_webdav"))
        self.verify_uploads_check.setChecked(self.config.get("verify_uploads", True))
        self.save_creds_check.setChecked(self.config.get("save_credentials", False))

        # Load conflict resolution setting
        conflict_setting = self.config.get("conflict_resolution", "ask")
        self.set_conflict_resolution_setting(conflict_setting)

        # Load checksum cache
        if not hasattr(self, "local_checksum_cache"):
            self.local_checksum_cache = {}
        cached_checksums = self.config.get("local_checksum_cache", {})
        self.local_checksum_cache.update(cached_checksums)
        if cached_checksums:
            logger.info(f"Loaded {len(cached_checksums)} cached checksums from previous session")

        # Try to load saved credentials if enabled
        if self.save_creds_check.isChecked() and self.url_input.text():
            if KEYRING_AVAILABLE and keyring is not None:
                try:
                    url = self.url_input.text()
                    username = keyring.get_password("PanoramaBridge", f"{url}_username")
                    password = keyring.get_password("PanoramaBridge", f"{url}_password")

                    if username:
                        self.username_input.setText(username)
                    if password:
                        self.password_input.setText(password)
                except Exception as e:
                    logger.warning(f"Failed to load saved credentials: {e}")
            else:
                logger.info("Keyring not available - cannot load saved credentials")

    def save_settings(self):
        """Save current settings"""
        self.save_config()

    def save_checksum_cache(self):
        """Periodically save checksum cache to persist between sessions"""
        if hasattr(self, "local_checksum_cache") and self.local_checksum_cache:
            logger.debug(f"Saving {len(self.local_checksum_cache)} cached checksums")
            self.save_config()  # This will include the cache data

    def queue_file_for_upload(self, filepath, reason):
        """Add a file to the upload queue with a reason"""
        try:
            self.file_queue.put(filepath)
            logger.info(f"Queued file for upload: {os.path.basename(filepath)} - {reason}")
            self.log_text.append(
                f"{datetime.now().strftime('%H:%M:%S')} - File queued for upload: "
                f"{os.path.basename(filepath)} ({reason})"
            )
        except Exception as e:
            logger.error(f"Error queueing file for upload: {e}")

    def is_file_in_upload_queue(self, filepath):
        """Check if a file is currently being processed or in the transfer table"""
        # Note: We can't directly check the queue contents without consuming items,
        # so we check if the file is currently in the transfer table (which indicates
        # it's being processed or was recently processed)
        for file_key in self.transfer_rows:
            if '|' in file_key:
                _, absolute_path = file_key.split('|', 1)
                if absolute_path == filepath:
                    return True
        return False

    def get_remote_path_for_file(self, filepath):
        """Determine the remote path where a local file should be uploaded"""
        try:
            # Get the base remote directory from settings
            remote_base = self.remote_path_input.text().strip()
            if not remote_base:
                return None

            # Get the monitored directory
            local_base = self.dir_input.text().strip()
            if not local_base:
                return None

            # Calculate relative path from monitored directory
            relative_path = os.path.relpath(filepath, local_base)

            # Don't allow relative paths that go outside the monitored directory
            if relative_path.startswith('..'):
                return None

            # Combine remote base with relative path, using forward slashes for WebDAV
            remote_path = remote_base.rstrip('/') + '/' + relative_path.replace('\\', '/')
            return remote_path

        except Exception as e:
            logger.error(f"Error determining remote path for {filepath}: {e}")
            return None

    def start_remote_integrity_check(self):
        """Start a comprehensive remote integrity check of all files in Transfer Status table"""
        if not self.webdav_client:
            QMessageBox.warning(
                self,
                "Connection Error",
                "Please configure and test your WebDAV connection first."
            )
            return

        # Get all files currently in the Transfer Status table
        # Extract absolute paths from the transfer_rows dictionary keys
        files_in_table = []
        for file_key in self.transfer_rows:
            # file_key format is "relative_path|absolute_path"
            if '|' in file_key:
                _, absolute_path = file_key.split('|', 1)
                if absolute_path and os.path.exists(absolute_path):
                    files_in_table.append(absolute_path)

        if not files_in_table:
            QMessageBox.information(
                self,
                "No Files to Check",
                "No files found in Transfer Status table to verify."
            )
            return

        # Ask for confirmation
        reply = QMessageBox.question(
            self,
            "Remote Integrity Check",
            f"This will verify {len(files_in_table)} files are properly uploaded and intact.\n\n"
            f"The check will:\n"
            f"• Temporarily pause file monitoring\n"
            f"• Verify each local file exists and is intact on the remote server\n"
            f"• Check if missing files are already queued for upload\n"
            f"• Queue missing files for upload if not already queued\n"
            f"• Show conflict resolution dialogs for files changed locally\n"
            f"• Queue corrupted remote files for re-upload\n\n"
            f"This may take some time. Continue?",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
            QMessageBox.StandardButton.No
        )

        if reply != QMessageBox.StandardButton.Yes:
            return

        # Store current monitoring state
        self.monitoring_was_active = self.observer and self.observer.is_alive()

        # Temporarily stop monitoring if it's active
        if self.monitoring_was_active:
            self.log_text.append(
                f"{datetime.now().strftime('%H:%M:%S')} - Temporarily pausing monitoring for integrity check"
            )
            if self.observer:
                self.observer.stop()
                self.observer.join()
                self.observer = None
            if self.poll_timer:
                self.poll_timer.stop()

        # Update UI to show check is in progress
        self.verify_btn.setEnabled(False)
        self.verify_btn.setText("Checking...")
        self.start_btn.setEnabled(False)

        # Start the integrity check in a separate thread
        self.integrity_check_thread = IntegrityCheckThread(files_in_table, self)
        self.integrity_check_thread.progress_signal.connect(self.on_integrity_check_progress)
        self.integrity_check_thread.finished_signal.connect(self.on_integrity_check_finished)
        self.integrity_check_thread.file_issue_signal.connect(self.on_integrity_check_file_issue)
        self.integrity_check_thread.start()

        self.log_text.append(
            f"{datetime.now().strftime('%H:%M:%S')} - Starting remote integrity check for {len(files_in_table)} files"
        )

    def on_integrity_check_progress(self, current_file, checked_count, total_count, status):
        """Handle progress updates from integrity check thread"""
        self.verify_btn.setText(f"Checking... ({checked_count}/{total_count})")
        self.log_text.append(
            f"{datetime.now().strftime('%H:%M:%S')} - [{checked_count}/{total_count}] {os.path.basename(current_file)}: {status}"
        )

        # Update the table status message for verified files
        if "integrity confirmed" in status or "Verified" in status:
            self.update_file_message_in_table(current_file, status)

    def on_integrity_check_file_issue(self, filepath, issue_type, details):
        """Handle file issues found during integrity check"""
        if issue_type == "missing":
            # File is missing from remote - queue for re-upload
            self.log_text.append(
                f"{datetime.now().strftime('%H:%M:%S')} - Missing from remote: {os.path.basename(filepath)} - queuing for upload"
            )
            self.file_queue.put(filepath)
            # Update the table message to show it's queued for re-upload
            self.update_file_message_in_table(filepath, "Queued - missing from remote server")

        elif issue_type == "corrupted":
            # File exists but is corrupted - queue for re-upload
            self.log_text.append(
                f"{datetime.now().strftime('%H:%M:%S')} - Corrupted on remote: {os.path.basename(filepath)} ({details}) - queuing for re-upload"
            )
            # Remove from upload history so it gets uploaded fresh
            if filepath in self.upload_history:
                del self.upload_history[filepath]
            self.file_queue.put(filepath)
            self.update_file_message_in_table(filepath, f"Queued - remote file corrupted ({details})")

        elif issue_type == "changed":
            # Local file has changed since upload - apply conflict resolution setting
            conflict_setting = self.get_conflict_resolution_setting()

            if conflict_setting == "ask":
                # Show conflict resolution dialog
                self.show_file_conflict_resolution(filepath, details)
            elif conflict_setting == "overwrite":
                # Remove from history and re-upload (overwrite)
                self.log_text.append(
                    f"{datetime.now().strftime('%H:%M:%S')} - File changed locally: {os.path.basename(filepath)} - queuing for overwrite upload"
                )
                if filepath in self.upload_history:
                    del self.upload_history[filepath]
                self.file_queue.put(filepath)
                self.update_file_message_in_table(filepath, "Queued - file changed, will overwrite remote")
            elif conflict_setting == "rename":
                # Queue for rename upload
                self.log_text.append(
                    f"{datetime.now().strftime('%H:%M:%S')} - File changed locally: {os.path.basename(filepath)} - queuing for rename upload"
                )
                if filepath in self.upload_history:
                    del self.upload_history[filepath]
                self.file_queue.put(filepath)
                self.update_file_message_in_table(filepath, "Queued - file changed, will rename remote")
            elif conflict_setting == "skip":
                # Skip - just log it
                self.log_text.append(
                    f"{datetime.now().strftime('%H:%M:%S')} - File changed locally: {os.path.basename(filepath)} - skipped per conflict resolution setting"
                )
                self.update_file_message_in_table(filepath, "Skipped - file changed locally")

    def on_integrity_check_finished(self, results, error_details=None):
        """Handle completion of integrity check"""
        verified_count = results['verified']
        missing_count = results['missing']
        corrupted_count = results['corrupted']
        changed_count = results['changed']
        error_count = results['errors']
        total_count = results['total']

        # Update UI
        self.verify_btn.setEnabled(True)
        self.verify_btn.setText("Remote Integrity Check")
        self.start_btn.setEnabled(True)

        # Log results
        self.log_text.append(
            f"{datetime.now().strftime('%H:%M:%S')} - Integrity check complete: "
            f"{verified_count} verified, {missing_count} missing, {corrupted_count} corrupted, "
            f"{changed_count} changed locally, {error_count} errors"
        )

        # Show summary dialog
        if missing_count + corrupted_count + changed_count + error_count == 0:
            QMessageBox.information(
                self,
                "Integrity Check Complete",
                f"All {verified_count} files verified successfully!\n\n"
                f"All files in the Transfer Status table are correctly uploaded and intact on the remote server."
            )
        else:
            issues_text = []
            if missing_count > 0:
                issues_text.append(f"• {missing_count} files missing from remote (queued for upload)")
            if corrupted_count > 0:
                issues_text.append(f"• {corrupted_count} files corrupted on remote (queued for re-upload)")
            if changed_count > 0:
                issues_text.append(f"• {changed_count} files changed locally (conflict resolution shown)")
            if error_count > 0:
                # Create detailed error breakdown
                error_breakdown = self._create_error_breakdown(error_details)
                issues_text.append(f"• {error_count} files had verification errors:\n{error_breakdown}")

            QMessageBox.warning(
                self,
                "Integrity Check Results",
                "Integrity check completed with issues:\n\n" + "\n".join(issues_text) +
                f"\n\nVerified: {verified_count}/{total_count} files" +
                "\n\nRecommended Actions:\n" +
                self._get_recommended_actions(error_details, missing_count, corrupted_count)
            )

        # Restart monitoring after integrity check
        self._finish_integrity_check_restart_monitoring()

    def _create_error_breakdown(self, error_details):
        """Create a detailed breakdown of integrity check errors"""
        if not error_details:
            return "  - Various errors occurred during verification"

        breakdown_parts = []

        if error_details.get('missing_remote'):
            count = len(error_details['missing_remote'])
            files = error_details['missing_remote'][:3]  # Show first 3
            file_list = ", ".join([f['filename'] for f in files])
            if count > 3:
                file_list += f" (and {count - 3} more)"
            breakdown_parts.append(f"  - {count} files missing from remote: {file_list}")

        if error_details.get('changed_local'):
            count = len(error_details['changed_local'])
            files = error_details['changed_local'][:3]
            file_list = ", ".join([f['filename'] for f in files])
            if count > 3:
                file_list += f" (and {count - 3} more)"
            breakdown_parts.append(f"  - {count} files with conflicts (local/remote differences): {file_list}")

        if error_details.get('network_errors'):
            count = len(error_details['network_errors'])
            files = error_details['network_errors'][:2]
            file_list = ", ".join([f['filename'] for f in files])
            if count > 2:
                file_list += f" (and {count - 2} more)"
            breakdown_parts.append(f"  - {count} network/connection errors: {file_list}")

        if error_details.get('other_errors'):
            count = len(error_details['other_errors'])
            files = error_details['other_errors'][:2]
            file_list = ", ".join([f['filename'] for f in files])
            if count > 2:
                file_list += f" (and {count - 2} more)"
            breakdown_parts.append(f"  - {count} other errors: {file_list}")

        return "\n".join(breakdown_parts) if breakdown_parts else "  - Various errors occurred"

    def _get_recommended_actions(self, error_details, missing_count, corrupted_count):
        """Generate recommended actions based on the types of issues found"""
        actions = []

        # Handle files missing from remote
        if error_details and error_details.get('missing_remote'):
            count = len(error_details['missing_remote'])
            actions.append(f"• {count} missing files have been automatically queued for upload")

        # Handle file conflicts (renamed from corrupted since we don't assume corruption)
        if error_details and error_details.get('changed_local'):
            count = len(error_details['changed_local'])
            actions.append(f"• {count} files have differences - conflict resolution dialogs will appear")

        # Handle network errors
        if error_details and error_details.get('network_errors'):
            actions.append("• Check your network connection and retry integrity check")

        # Handle other errors
        if error_details and error_details.get('other_errors'):
            count = len(error_details['other_errors'])
            actions.append(f"• {count} files had other errors - check the log for details")

        # General monitoring status
        if missing_count > 0 or corrupted_count > 0:
            actions.append("• File monitoring will continue and process queued uploads automatically")
        else:
            actions.append("• File monitoring continues - your conflict resolution settings will be applied")

        return "\n".join(actions) if actions else "• No immediate action required"

    # Continue with the rest of the integrity check completion
    def _finish_integrity_check_restart_monitoring(self):
        """Restart monitoring if it was active before integrity check"""
        # Restart monitoring if it was active before
        if self.monitoring_was_active:
            self.log_text.append(
                f"{datetime.now().strftime('%H:%M:%S')} - Resuming file monitoring"
            )
            # Restart monitoring with current settings
            directory = self.dir_input.text()
            extensions = [e.strip() for e in self.extensions_input.text().split(",") if e.strip()]
            recursive = self.subdirs_check.isChecked()

            try:
                self.monitor_handler = FileMonitorHandler(
                    extensions,
                    self.file_queue,
                    recursive,
                    self,
                )

                self.observer = Observer()
                self.observer.schedule(self.monitor_handler, directory, recursive=recursive)
                self.observer.start()

                if self.enable_polling_check.isChecked():
                    polling_interval_ms = self.polling_interval_spin.value() * 60 * 1000
                    self.poll_timer.start(polling_interval_ms)

                self.start_btn.setText("Stop Monitoring")
                self.status_label.setText("Monitoring active")
                self.status_label.setStyleSheet("font-weight: bold; color: green;")

            except Exception as e:
                logger.error(f"Failed to restart monitoring after integrity check: {e}")
                self.log_text.append(
                    f"{datetime.now().strftime('%H:%M:%S')} - Error restarting monitoring: {e}"
                )

        # Save any history updates
        self.save_upload_history()

    def show_file_conflict_resolution(self, filepath, details):
        """Show conflict resolution dialog for a locally changed file"""
        filename = os.path.basename(filepath)

        dialog = QMessageBox(self)
        dialog.setWindowTitle("File Conflict Detected")
        dialog.setText(f"Local file has changed since upload:\n{filename}")
        dialog.setDetailedText(f"File: {filepath}\nIssue: {details}")
        dialog.setIcon(QMessageBox.Icon.Question)

        # Add custom buttons
        overwrite_btn = dialog.addButton("Upload New Version", QMessageBox.ButtonRole.AcceptRole)
        keep_remote_btn = dialog.addButton("Keep Remote Version", QMessageBox.ButtonRole.RejectRole)
        skip_btn = dialog.addButton("Skip This File", QMessageBox.ButtonRole.NoRole)

        dialog.exec()
        clicked_button = dialog.clickedButton()

        if clicked_button == overwrite_btn:
            self.log_text.append(
                f"{datetime.now().strftime('%H:%M:%S')} - User chose to upload new version of {filename}"
            )
            # Remove from history and re-queue
            if filepath in self.upload_history:
                del self.upload_history[filepath]
            self.file_queue.put(filepath)
            self.update_file_status_in_table(filepath, "Queued")

        elif clicked_button == keep_remote_btn:
            self.log_text.append(
                f"{datetime.now().strftime('%H:%M:%S')} - User chose to keep remote version of {filename}"
            )
            # Update local history with current local file checksum to match
            if os.path.exists(filepath):
                current_checksum = self.file_processor.calculate_checksum(filepath)
                if filepath in self.upload_history:
                    self.upload_history[filepath]["checksum"] = current_checksum
                    self.upload_history[filepath]["timestamp"] = datetime.now().isoformat()

        else:  # skip
            self.log_text.append(
                f"{datetime.now().strftime('%H:%M:%S')} - User chose to skip {filename}"
            )

    def update_file_status_in_table(self, filepath, status):
        """Update the status of a file in the Transfer Status table"""
        for row in range(self.transfer_table.rowCount()):
            filepath_item = self.transfer_table.item(row, 0)
            if filepath_item and filepath_item.text() == filepath:
                status_item = self.transfer_table.item(row, 3)  # Status is in column 3
                if status_item:
                    status_item.setText(status)
                    # Color code the status
                    if status == "Queued":
                        status_item.setBackground(QColor(255, 255, 0, 50))  # Light yellow
                    elif status == "Uploading":
                        status_item.setBackground(QColor(0, 0, 255, 50))  # Light blue
                    elif status == "Complete":
                        status_item.setBackground(QColor(0, 255, 0, 50))  # Light green
                break

    def update_file_message_in_table(self, filepath, message):
        """Update the message of a file in the Transfer Status table"""
        # Need to find the file using the transfer_rows dictionary
        for file_key, row in self.transfer_rows.items():
            # file_key format is "relative_path|absolute_path"
            if '|' in file_key:
                _, absolute_path = file_key.split('|', 1)
                if absolute_path == filepath:
                    if row < self.transfer_table.rowCount():
                        message_item = self.transfer_table.item(row, 3)  # Message is in column 3
                        if message_item:
                            message_item.setText(message)
                    break

    def closeEvent(self, event):
        """Handle application close"""
        # Stop monitoring
        if self.observer and self.observer.is_alive():
            self.observer.stop()
            self.observer.join()

        # Stop processor
        self.file_processor.stop()
        self.file_processor.wait()

        # Save settings and upload history
        self.save_settings()
        self.save_upload_history()

        event.accept()


def main():
    app = QApplication(sys.argv)
    app.setStyle("Fusion")  # Modern look

    # Set application icon for the executable
    try:
        script_dir = os.path.dirname(os.path.abspath(__file__))
        logo_path = os.path.join(script_dir, "screenshots", "panoramabridge-logo.png")
        if os.path.exists(logo_path):
            icon = QIcon(logo_path)
            app.setWindowIcon(icon)
    except Exception as e:
        logger.error(f"Failed to set application icon in main: {e}")

    window = MainWindow()
    window.show()

    sys.exit(app.exec())


if __name__ == "__main__":
    main()
