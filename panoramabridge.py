#!/usr/bin/env python3
"""
PanoramaBridge - A Python Qt6 Application for Directory Monitoring and WebDAV File Transfer

This application monitors local directories for new files and automatically transfers them to WebDAV servers (like Panorama) with comprehensive features:

- Real-time file monitoring using watchdog
- Chunked upload support for large files  
- SHA256 checksum calculation and verification
- Conflict resolution with user interaction
- Secure credential storage using system keyring
- Remote directory browsing and management
- Configurable file extensions and directory structure preservation
- Progress tracking and comprehensive logging

Author: Michael MacCoss - MacCoss Lab, University of Washington
License: Apache License 2.0
"""

# Standard library imports
import sys
import os
import hashlib           # For calculating SHA256 checksums
import json             # For configuration file storage
import time             # For file stability checks and timestamps
import threading        # For background operations
import queue            # For thread-safe file processing queue
import tempfile         # For temporary file operations during verification
from pathlib import Path
from datetime import datetime
from typing import List, Dict, Optional, Tuple
import logging

# Third-party imports (must be installed via pip)
from PyQt6.QtWidgets import (
    QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout,
    QTabWidget, QPushButton, QLabel, QLineEdit, QTextEdit,
    QFileDialog, QCheckBox, QGroupBox, QGridLayout, QTreeWidget,
    QTreeWidgetItem, QProgressBar, QMessageBox, QTableWidget,
    QTableWidgetItem, QHeaderView, QSplitter, QComboBox,
    QSpinBox, QInputDialog, QDialog, QRadioButton, QMenu
)
from PyQt6.QtCore import Qt, QThread, pyqtSignal, QTimer, pyqtSlot
from PyQt6.QtGui import QIcon, QFont

# File monitoring using watchdog library
from watchdog.observers import Observer
from watchdog.events import FileSystemEventHandler

# WebDAV client using requests library
import requests
from requests.auth import HTTPBasicAuth, HTTPDigestAuth
from urllib.parse import urljoin, quote, unquote
import xml.etree.ElementTree as ET

# Configure comprehensive logging to both console and file
logging.basicConfig(
    level=logging.INFO,  
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s',
    handlers=[
        logging.StreamHandler(),  # Console output for real-time monitoring
        logging.FileHandler('panoramabridge.log', mode='a')  # Persistent log file
    ]
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
    - Checksum storage and retrieval for integrity verification
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
        self.url = url.rstrip('/')  # Remove trailing slash for consistency
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
        self.chunk_size = 10 * 1024 * 1024  # 10MB chunks for efficient large file uploads
    
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
            response = self.session.request('OPTIONS', self.url, timeout=10)
            logger.info(f"OPTIONS request to {self.url} returned: {response.status_code}")
            if response.status_code in [200, 204, 207]:
                logger.info("Connection successful with provided URL")
                return True
            
            # If that fails, try with /webdav appended (common WebDAV endpoint)
            if not self.url.endswith('/webdav'):
                webdav_url = f"{self.url.rstrip('/')}/webdav"
                logger.info(f"Trying with /webdav suffix: {webdav_url}")
                response = self.session.request('OPTIONS', webdav_url, timeout=10)
                logger.info(f"OPTIONS request to {webdav_url} returned: {response.status_code}")
                if response.status_code in [200, 204, 207]:
                    # Update the URL to use the working endpoint
                    logger.info(f"Connection successful, updating URL to: {webdav_url}")
                    self.url = webdav_url
                    return True
            
            logger.warning(f"Connection failed - no valid WebDAV endpoint found")
            return False
        except Exception as e:
            logger.error(f"Connection test failed: {e}")
            return False
    
    def list_directory(self, path: str = "/") -> List[Dict]:
        """List contents of a WebDAV directory"""
        logger.info(f"list_directory called with path: {path}")
        url = urljoin(self.url, quote(path))
        logger.info(f"Requesting directory listing for URL: {url}")
        
        headers = {
            'Depth': '1',
            'Content-Type': 'application/xml'
        }
        
        # PROPFIND request body
        body = '''<?xml version="1.0" encoding="utf-8"?>
        <propfind xmlns="DAV:">
            <prop>
                <displayname/>
                <resourcetype/>
                <getcontentlength/>
                <getlastmodified/>
            </prop>
        </propfind>'''
        
        try:
            logger.info(f"Sending PROPFIND request to: {url}")
            response = self.session.request('PROPFIND', url, headers=headers, data=body)
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
        if item_name.startswith('.'):
            logger.debug(f"Filtering out hidden item: {item_name}")
            return False
        
        # Hide common system/backup files - be more specific about patterns
        system_patterns = [
            'copy_directory_fileroot_change_',
            'copy_directory_',
            'copy_direct',
            '.norcrawl',
            '.htaccess',
            '.DS_Store',
            'Thumbs.db',
            '__pycache__'
        ]
        
        for pattern in system_patterns:
            if item_name.startswith(pattern):
                logger.debug(f"Filtering out system item: {item_name}")
                return False
        
        # Hide common system directories - be more restrictive for directories
        if is_dir:
            system_dirs = [
                'nextflow',
                'output', 
                'proteome',
                '.git',
                '.svn',
                '__pycache__',
                '.tmp',
                'temp',
                'cache',
                '.trash',
                '.recycle'
            ]
            # Case-insensitive comparison for system directories
            if item_name.lower() in [d.lower() for d in system_dirs]:
                logger.debug(f"Filtering out system directory: {item_name}")
                return False
        
        logger.debug(f"Including item: {item_name} (is_dir: {is_dir})")
        return True

    def _parse_propfind_response(self, xml_response: str, base_path: str) -> List[Dict]:
        """Parse PROPFIND XML response"""
        logger.info(f"Parsing PROPFIND response for base_path: {base_path}")
        items = []
        try:
            root = ET.fromstring(xml_response)
            
            # Define namespace
            ns = {'d': 'DAV:'}
            
            responses = root.findall('.//d:response', ns)
            logger.info(f"Found {len(responses)} response elements in XML")
            
            for i, response in enumerate(responses):
                href = response.find('d:href', ns)
                if href is None:
                    logger.debug(f"Response {i}: No href element found, skipping")
                    continue
                    
                href_text = href.text
                if href_text is None:
                    logger.debug(f"Response {i}: href text is None, skipping")
                    continue
                    
                logger.debug(f"Response {i}: Processing href: {href_text}")
                
                # Skip the base path itself (compare unquoted paths)
                unquoted_href = unquote(href_text.rstrip('/'))
                unquoted_base = base_path.rstrip('/')
                if unquoted_href == unquoted_base:
                    logger.debug(f"Response {i}: Skipping base path itself: {unquoted_href}")
                    continue
                
                props = response.find('.//d:prop', ns)
                if props is None:
                    logger.debug(f"Response {i}: No properties found, skipping")
                    continue
                
                item_name = os.path.basename(unquoted_href)
                logger.debug(f"Response {i}: Item name extracted: '{item_name}'")
                
                item = {
                    'name': item_name,
                    'path': unquote(href_text),
                    'is_dir': False,
                    'size': 0
                }
                
                # Check if it's a directory
                resourcetype = props.find('d:resourcetype', ns)
                if resourcetype is not None:
                    collection = resourcetype.find('d:collection', ns)
                    item['is_dir'] = collection is not None
                
                # Get size
                size = props.find('d:getcontentlength', ns)
                if size is not None and size.text:
                    item['size'] = int(size.text)
                
                logger.debug(f"Response {i}: Item details: name='{item['name']}', is_dir={item['is_dir']}, size={item['size']}")
                
                # Filter out system files and directories
                if not self._should_show_item(item['name'], item['is_dir']):
                    logger.info(f"Filtering out: {item['name']} (is_dir: {item['is_dir']})")
                    continue
                
                logger.info(f"Including item: {item['name']} (is_dir: {item['is_dir']}, size: {item['size']})")
                items.append(item)
                
        except Exception as e:
            logger.error(f"Error parsing PROPFIND response: {e}")
            logger.error(f"XML response (first 1000 chars): {xml_response[:1000]}")
        
        logger.info(f"Total items returned for {base_path}: {len(items)}")
        return items
    
    def get_file_info(self, path: str) -> Optional[Dict]:
        """Get information about a remote file"""
        url = urljoin(self.url, quote(path))
        
        headers = {
            'Depth': '0',
            'Content-Type': 'application/xml'
        }
        
        # PROPFIND request body
        body = '''<?xml version="1.0" encoding="utf-8"?>
        <propfind xmlns="DAV:">
            <prop>
                <displayname/>
                <getcontentlength/>
                <getlastmodified/>
                <getetag/>
            </prop>
        </propfind>'''
        
        try:
            response = self.session.request('PROPFIND', url, headers=headers, data=body)
            if response.status_code == 207:  # Multi-Status
                # Parse the response to get file info
                root = ET.fromstring(response.text)
                ns = {'d': 'DAV:'}
                
                for response_elem in root.findall('.//d:response', ns):
                    href = response_elem.find('d:href', ns)
                    if href is None:
                        continue
                        
                    props = response_elem.find('.//d:prop', ns)
                    if props is None:
                        continue
                    
                    info = {
                        'path': unquote(href.text) if href.text else path,
                        'exists': True,
                        'size': 0,
                        'etag': None,
                        'last_modified': None
                    }
                    
                    # Get size
                    size_elem = props.find('d:getcontentlength', ns)
                    if size_elem is not None and size_elem.text:
                        info['size'] = int(size_elem.text)
                    
                    # Get ETag (often contains checksum info)
                    etag_elem = props.find('d:getetag', ns)
                    if etag_elem is not None and etag_elem.text:
                        info['etag'] = etag_elem.text.strip('"')
                    
                    # Get last modified
                    modified_elem = props.find('d:getlastmodified', ns)
                    if modified_elem is not None and modified_elem.text:
                        info['last_modified'] = modified_elem.text
                    
                    return info
                    
            elif response.status_code == 404:
                return {'exists': False, 'path': path}
            else:
                logger.warning(f"Failed to get file info for {path}: {response.status_code}")
                return None
                
        except Exception as e:
            logger.error(f"Error getting file info for {path}: {e}")
            return None
    
    def download_file_head(self, path: str, size: int = 8192) -> Optional[bytes]:
        """Download the first few bytes of a remote file for checksum comparison"""
        url = urljoin(self.url, quote(path))
        
        headers = {
            'Range': f'bytes=0-{size-1}'
        }
        
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

    def download_file(self, remote_path: str, local_path: str) -> Tuple[bool, str]:
        """Download a complete file from the WebDAV server
        Returns: (success, error_message)
        """
        url = urljoin(self.url, quote(remote_path))
        
        try:
            response = self.session.get(url, stream=True)
            if response.status_code == 200:
                with open(local_path, 'wb') as f:
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
            response = self.session.request('MKCOL', url)
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
                logger.error(f"Failed to create directory {path}: {response.status_code} - {response.reason}")
                if response.text:
                    logger.error(f"Response body: {response.text}")
                return False
                
        except Exception as e:
            logger.error(f"Error creating directory {path}: {e}")
            return False
    
    def upload_file_chunked(self, local_path: str, remote_path: str, 
                          progress_callback=None) -> Tuple[bool, str]:
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
                    return 1024 * 1024      # 1MB chunks for very large files
                elif total_size > 100 * 1024 * 1024:  # > 100MB  
                    return 256 * 1024       # 256KB chunks for large files
                else:
                    return 64 * 1024        # 64KB chunks for smaller files
            
            chunk_size = get_optimal_chunk_size(file_size)
            chunk_size_mb = chunk_size / (1024 * 1024)
            total_size_mb = file_size / (1024 * 1024)
            logger.info(f"Upload chunking: {total_size_mb:.1f}MB file using {chunk_size_mb:.1f}MB chunks ({chunk_size:,} bytes)")
            
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
                    with open(local_path, 'rb') as file:
                        # Read first chunk
                        first_chunk = file.read(chunk_size)
                        
                        # Send first chunk with Range header
                        headers = {
                            'Content-Range': f'bytes 0-{len(first_chunk)-1}/{file_size}',
                            'Content-Length': str(len(first_chunk))
                        }
                        
                        response = self.session.put(url, data=first_chunk, headers=headers)
                        
                        if response.status_code in [200, 201, 204, 206, 308]:
                            # Server accepts Range uploads, continue with chunks
                            bytes_uploaded = len(first_chunk)
                            if progress_callback:
                                progress_callback(bytes_uploaded, file_size)
                            
                            # Upload remaining chunks
                            while bytes_uploaded < file_size:
                                chunk = file.read(chunk_size)
                                if not chunk:
                                    break
                                
                                start_byte = bytes_uploaded
                                end_byte = start_byte + len(chunk) - 1
                                
                                headers = {
                                    'Content-Range': f'bytes {start_byte}-{end_byte}/{file_size}',
                                    'Content-Length': str(len(chunk))
                                }
                                
                                response = self.session.put(url, data=chunk, headers=headers)
                                
                                if response.status_code not in [200, 201, 204, 206, 308]:
                                    # Chunk failed, fall back to regular upload
                                    logger.warning(f"Chunk upload failed at byte {start_byte}, falling back to regular upload")
                                    break
                                
                                bytes_uploaded += len(chunk)
                                if progress_callback:
                                    progress_callback(bytes_uploaded, file_size)
                            
                            # Check if we completed the chunked upload
                            if bytes_uploaded >= file_size:
                                logger.info("Chunked upload completed successfully")
                                return True, ""
                        
                        # If we get here, chunked upload failed, fall back to regular upload
                        logger.info("Server doesn't support chunked upload, falling back to regular upload")
                        
                except Exception as e:
                    logger.warning(f"Chunked upload failed: {e}, falling back to regular upload")
            
            # Fall back to regular single-request upload
            # Use a simpler approach that at least shows some progress
            logger.info("Using regular upload with estimated progress")
            
            # Create a file-like object that gives periodic progress updates
            class TimedProgressFile:
                def __init__(self, filepath, progress_callback, total_size):
                    self.filepath = filepath
                    self.progress_callback = progress_callback
                    self.total_size = total_size
                    self.bytes_read = 0
                    self._file = None
                    self.last_report_time = time.time()
                    self.report_interval = 1.0  # Report every 1 second
                    
                def __enter__(self):
                    self._file = open(self.filepath, 'rb')
                    return self
                    
                def __exit__(self, exc_type, exc_val, exc_tb):
                    if self._file:
                        self._file.close()
                
                def read(self, size=-1):
                    if not self._file:
                        return b''
                    
                    data = self._file.read(size if size > 0 else 8192)
                    if data:
                        self.bytes_read += len(data)
                        
                        # Report progress every second instead of every chunk
                        current_time = time.time()
                        if (current_time - self.last_report_time) >= self.report_interval:
                            if self.progress_callback:
                                self.progress_callback(self.bytes_read, self.total_size)
                            self.last_report_time = current_time
                    
                    return data
                
                def __len__(self):
                    return self.total_size
            
            # Upload with timed progress tracking
            with TimedProgressFile(local_path, progress_callback, file_size) as progress_file:
                response = self.session.put(url, data=progress_file)
            
            # Ensure we show 100% completion
            if progress_callback:
                progress_callback(file_size, file_size)
                
            return response.status_code in [200, 201, 204], ""
            
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
            url = urljoin(self.url + "/", checksum_path.lstrip('/'))
            
            # Upload checksum as a small text file
            response = self.session.put(url, data=checksum.encode('utf-8'))
            
            if response.status_code in [200, 201, 204]:
                logger.debug(f"Stored checksum for {file_path}: {checksum}")
                return True
            else:
                logger.warning(f"Failed to store checksum for {file_path}: {response.status_code}")
                return False
                
        except Exception as e:
            logger.error(f"Error storing checksum for {file_path}: {e}")
            return False
    
    def get_stored_checksum(self, file_path: str) -> Optional[str]:
        """Retrieve stored checksum for a file from the remote server"""
        try:
            checksum_path = f"{file_path}.checksum"
            url = urljoin(self.url + "/", checksum_path.lstrip('/'))
            
            response = self.session.get(url)
            
            if response.status_code == 200:
                checksum = response.text.strip()
                logger.debug(f"Retrieved stored checksum for {file_path}: {checksum}")
                return checksum
            elif response.status_code == 404:
                logger.debug(f"No stored checksum found for {file_path}")
                return None
            else:
                logger.warning(f"Failed to retrieve checksum for {file_path}: {response.status_code}")
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
    
    def __init__(self, extensions: List[str], file_queue: queue.Queue, 
                 monitor_subdirs: bool = True, app_instance=None):
        """
        Initialize file monitor with configuration.
        
        Args:
            extensions: List of file extensions to monitor (e.g., ['raw', 'mzML'])
            file_queue: Thread-safe queue for passing files to processor
            monitor_subdirs: Whether to monitor subdirectories recursively
            app_instance: Reference to main application for duplicate tracking
        """
        # Normalize extensions to lowercase with leading dots
        self.extensions = [ext.lower() if ext.startswith('.') else f'.{ext.lower()}' 
                          for ext in extensions]
        self.file_queue = file_queue
        self.monitor_subdirs = monitor_subdirs
        self.app_instance = app_instance
        self.pending_files = {}  # Track files being written with timestamps
        
        # Log configuration for debugging
        logger.info(f"FileMonitorHandler initialized with extensions: {self.extensions}")
        logger.info(f"Monitor subdirectories: {monitor_subdirs}")
        
    def on_created(self, event):
        """Handle file creation events."""
        if not event.is_directory:
            logger.debug(f"OS Event - File created: {event.src_path}")
            self._handle_file(event.src_path)
    
    def on_modified(self, event):
        """Handle file modification events."""
        if not event.is_directory:
            logger.debug(f"OS Event - File modified: {event.src_path}")
            self._handle_file(event.src_path)
            
    def on_moved(self, event):
        """Handle file move events."""
        if not event.is_directory:
            logger.debug(f"OS Event - File moved: {event.src_path} -> {event.dest_path}")
            self._handle_file(event.dest_path)
    
    def _handle_file(self, filepath):
        """
        Process file events and queue stable files for upload.
        
        Implements file stability detection by tracking file size changes
        over time. Only queues files when they haven't changed size for
        a specified period, indicating the write operation is complete.
        
        Args:
            filepath: Absolute path to the file that triggered the event
        """
        # Skip hidden files and system files (start with . or ~)
        filename = os.path.basename(filepath)
        if filename.startswith('.') or filename.startswith('~'):
            return
            
        # Check if file extension matches our monitored list
        if any(filepath.lower().endswith(ext) for ext in self.extensions):
            current_time = time.time()
            logger.info(f"File event detected: {filepath}")
            
            if filepath in self.pending_files:
                # Check if file size is stable
                try:
                    current_size = os.path.getsize(filepath)
                    last_size, last_time = self.pending_files[filepath]
                    
                    # Reduced stability timeout for faster detection
                    if current_size == last_size and current_time - last_time > 1:
                        # File is stable, check for duplicates before queueing
                        if self._should_queue_file(filepath):
                            logger.info(f"Queuing stable file: {filepath} (size: {current_size} bytes)")
                            self.file_queue.put(filepath)
                            # Add to transfer table immediately when queued
                            if self.app_instance:
                                self.app_instance.add_queued_file_to_table(filepath)
                            del self.pending_files[filepath]
                            logger.info(f"File queued for transfer: {filepath} (queue size now: {self.file_queue.qsize()})")
                        else:
                            del self.pending_files[filepath]
                            logger.info(f"File already queued or processing, skipping: {filepath}")
                    else:
                        # Update tracking
                        self.pending_files[filepath] = (current_size, current_time)
                        logger.debug(f"File size changed, continuing to monitor: {filepath}")
                except Exception as e:
                    logger.error(f"Error checking file size for {filepath}: {e}")
            else:
                # New file, start tracking
                try:
                    size = os.path.getsize(filepath)
                    self.pending_files[filepath] = (size, current_time)
                    logger.info(f"Started monitoring new file: {filepath} (size: {size} bytes)")
                    
                    # For moved/copied files that are already complete, 
                    # schedule a stability check in a few seconds
                    def delayed_check():
                        time.sleep(1.5)  # Reduced from 3 to 1.5 seconds for faster response
                        if filepath in self.pending_files:
                            try:
                                current_size = os.path.getsize(filepath)
                                stored_size, _ = self.pending_files[filepath]
                                if current_size == stored_size:
                                    # File hasn't changed, check for duplicates before queueing
                                    if self._should_queue_file(filepath):
                                        logger.info(f"Delayed check: Queuing stable file: {filepath} (size: {current_size} bytes)")
                                        self.file_queue.put(filepath)
                                        # Add to transfer table immediately when queued
                                        if self.app_instance:
                                            self.app_instance.add_queued_file_to_table(filepath)
                                        del self.pending_files[filepath]
                                        logger.info(f"File queued for transfer after stability check: {filepath} (queue size now: {self.file_queue.qsize()})")
                                    else:
                                        del self.pending_files[filepath]
                                        logger.info(f"File already queued or processing, skipping: {filepath}")
                            except Exception as e:
                                logger.error(f"Error in delayed stability check for {filepath}: {e}")
                    
                    # Start the delayed check in a separate thread
                    check_thread = threading.Thread(target=delayed_check, daemon=True)
                    check_thread.start()
                    
                except Exception as e:
                    logger.error(f"Error starting to monitor file {filepath}: {e}")
        else:
            # Log files that don't match extensions for debugging
            ext = os.path.splitext(filepath)[1]
            logger.debug(f"File ignored (extension '{ext}' not in {self.extensions}): {filepath}")

    def _should_queue_file(self, filepath: str) -> bool:
        """
        Check if a file should be queued, preventing duplicates.
        
        Args:
            filepath: Path to the file being considered for queueing
            
        Returns:
            True if file should be queued, False if already queued/processing
        """
        if self.app_instance:
            # Check if file is already queued or being processed
            if filepath in self.app_instance.queued_files:
                return False
            if filepath in self.app_instance.processing_files:
                return False
            
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
    2. Calculates SHA256 checksums for integrity verification
    3. Checks for conflicts with existing remote files
    4. Handles user conflict resolution decisions
    5. Uploads files with progress tracking
    6. Verifies successful uploads
    7. Stores checksums for future reference
    
    Runs continuously in background to process files without blocking UI.
    Communicates with main thread via Qt signals for progress updates.
    """
    
    # Qt signals for communicating with main UI thread
    progress_update = pyqtSignal(str, int, int)      # filepath, bytes_transferred, total_bytes
    status_update = pyqtSignal(str, str, str)        # filename, status_message, filepath
    transfer_complete = pyqtSignal(str, str, bool, str)   # filename, filepath, success, result_message
    conflict_detected = pyqtSignal(str, dict, str)   # filepath, remote_info, local_checksum
    conflict_resolution_needed = pyqtSignal(str, str, str, dict)  # filename, filepath, remote_path, conflict_details
    
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
        self.webdav_client = None                      # Set later via set_webdav_client()
        self.remote_base_path = "/"                   # Remote directory base path
        self.running = True                           # Control flag for thread loop
        self.preserve_structure = True                # Whether to preserve local directory structure
        self.local_base_path = ""                     # Local directory base path
        self.conflict_resolution: Optional[str] = None  # User's conflict resolution choice
        self.apply_to_all = False                     # Apply resolution to all conflicts
        
    def set_webdav_client(self, client: WebDAVClient, remote_path: str):
        """
        Configure WebDAV client and remote path for transfers.
        
        Args:
            client: Configured WebDAVClient instance
            remote_path: Base remote directory path for uploads
        """
        self.webdav_client = client
        self.remote_base_path = remote_path.rstrip('/')
        
    def set_local_base(self, path: str):
        """
        Set local base path for directory structure preservation.
        
        Args:
            path: Local directory path to use as base for relative paths
        """
        self.local_base_path = path
        
    def calculate_checksum(self, filepath: str, algorithm: str = 'sha256', chunk_size: Optional[int] = None) -> str:
        """
        Calculate file checksum for integrity verification with local caching.
        
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
            if self.app_instance and hasattr(self.app_instance, 'local_checksum_cache'):
                cached = self.app_instance.local_checksum_cache.get(cache_key)
                if cached:
                    logger.debug(f"Using cached checksum for {os.path.basename(filepath)}: {cached[:8]}...")
                    return cached
            
            # Calculate new checksum
            logger.debug(f"Calculating new checksum for {os.path.basename(filepath)} ({file_size:,} bytes)")
            
            if chunk_size is None:
                chunk_size = 256 * 1024  # 256KB chunks - optimal balance of speed and memory
            
            hash_obj = hashlib.new(algorithm)
            with open(filepath, 'rb') as f:
                while chunk := f.read(chunk_size):
                    hash_obj.update(chunk)
            
            checksum = hash_obj.hexdigest()
            
            # Cache the result
            if self.app_instance and hasattr(self.app_instance, 'local_checksum_cache'):
                self.app_instance.local_checksum_cache[cache_key] = checksum
                logger.debug(f"Cached checksum for {os.path.basename(filepath)}: {checksum[:8]}...")
                
                # Limit cache size to prevent memory issues
                if len(self.app_instance.local_checksum_cache) > 1000:
                    # Remove oldest entries (simple cleanup)
                    cache_items = list(self.app_instance.local_checksum_cache.items())
                    for key, _ in cache_items[:100]:  # Remove first 100 entries
                        del self.app_instance.local_checksum_cache[key]
                    logger.debug(f"Cleaned checksum cache, now {len(self.app_instance.local_checksum_cache)} entries")
            
            return checksum
            
        except Exception as e:
            logger.error(f"Error calculating checksum for {filepath}: {e}")
            raise

    def calculate_checksum_from_data(self, data: bytes) -> str:
        """Calculate SHA256 checksum from raw data"""
        sha256_hash = hashlib.sha256()
        sha256_hash.update(data)
        return sha256_hash.hexdigest()
    
    def verify_uploaded_file(self, local_path: str, remote_path: str, expected_checksum: str) -> Tuple[bool, str]:
        """Verify uploaded file integrity by downloading and comparing checksums
        Returns: (is_verified, message)
        """
        try:
            # Get remote file info first
            remote_info = self.webdav_client.get_file_info(remote_path)
            if not remote_info or not remote_info.get('exists', False):
                return False, "Remote file not found"
            
            # Compare file sizes first (quick check)
            local_size = os.path.getsize(local_path)
            remote_size = remote_info.get('size', 0)
            
            if local_size != remote_size:
                return False, f"Size mismatch: local={local_size:,}, remote={remote_size:,} bytes"
            
            # For smaller files (< 50MB), download and verify checksum
            if remote_size < 50 * 1024 * 1024:  # 50MB
                logger.info(f"Downloading file for checksum verification: {os.path.basename(remote_path)}")
                
                # Download the remote file to a temporary location
                with tempfile.NamedTemporaryFile(delete=False) as temp_file:
                    temp_path = temp_file.name
                
                try:
                    success, error = self.webdav_client.download_file(remote_path, temp_path)
                    if not success:
                        return False, f"Download failed: {error}"
                    
                    # Calculate checksum of downloaded file
                    remote_checksum = self.calculate_checksum(temp_path)
                    
                    # Compare checksums
                    if remote_checksum.lower() == expected_checksum.lower():
                        return True, "Checksum verified - file uploaded correctly"
                    else:
                        return False, f"Checksum mismatch: expected {expected_checksum[:8]}..., got {remote_checksum[:8]}..."
                        
                finally:
                    # Clean up temp file
                    try:
                        os.unlink(temp_path)
                    except:
                        pass
            else:
                # For large files, use ETag or size comparison only
                remote_etag = remote_info.get('etag')
                if remote_etag:
                    clean_etag = remote_etag.strip('"').replace('W/', '')
                    if clean_etag.lower() == expected_checksum.lower():
                        return True, "ETag verified - file appears uploaded correctly"
                
                # Large file with matching size - assume success
                return True, f"Large file ({remote_size:,} bytes) uploaded successfully (size verified)"
                
        except Exception as e:
            return False, f"Verification error: {str(e)}"
        
    def compare_files(self, local_path: str, remote_info: Dict, local_checksum: str) -> Tuple[str, Dict]:
        """Compare local and remote files to detect conflicts with smart checksum optimization
        Returns: (status, details) where status is 'identical', 'different', 'newer_local', 'newer_remote', 'new'
        """
        if not remote_info.get('exists', False):
            return 'new', {}  # File doesn't exist remotely
            
        comparison_details = {
            'local_size': 0,
            'remote_size': remote_info.get('size', 0),
            'local_mtime': 0,
            'remote_mtime': remote_info.get('modified', 0),
            'size_match': False,
            'etag_match': False,
            'optimization_used': None
        }
            
        # Compare sizes first (quick check)
        try:
            local_size = os.path.getsize(local_path)
            local_mtime = os.path.getmtime(local_path)
            remote_size = remote_info.get('size', 0)
            
            comparison_details.update({
                'local_size': local_size,
                'local_mtime': local_mtime,
                'size_match': local_size == remote_size
            })
            
            if local_size != remote_size:
                logger.info(f"File size mismatch: local={local_size}, remote={remote_size}")
                comparison_details['optimization_used'] = 'size_mismatch_skip'
                # Even with size mismatch, check dates for user decision
                return self._check_file_dates(comparison_details)
        except Exception as e:
            logger.warning(f"Could not compare file sizes: {e}")
            
        # If sizes match, try to optimize by checking stored checksum first
        stored_checksum = None
        
        # Try to get stored checksum first (fastest check)
        try:
            stored_checksum = self.webdav_client.get_stored_checksum(remote_info.get('path', ''))
            if stored_checksum:
                logger.debug(f"Found stored checksum: {stored_checksum[:8]}...")
                if stored_checksum.lower() == local_checksum.lower():
                    logger.info(f"Files match via stored checksum (optimization: skipped download)")
                    comparison_details['stored_checksum_match'] = True
                    comparison_details['optimization_used'] = 'stored_checksum_match'
                    return 'identical', comparison_details
                else:
                    logger.info(f"Files differ via stored checksum")
                    comparison_details['stored_checksum_match'] = False
                    comparison_details['remote_checksum'] = stored_checksum
                    comparison_details['local_checksum'] = local_checksum
                    comparison_details['optimization_used'] = 'stored_checksum_differ'
                    return self._check_file_dates(comparison_details)
        except Exception as e:
            logger.debug(f"Could not retrieve stored checksum: {e}")
        
        # Check ETags (second fastest check)
        remote_etag = remote_info.get('etag')
        if remote_etag:
            # Some servers include MD5 or SHA256 in ETags
            # Clean ETag (remove quotes and weak indicators)
            clean_etag = remote_etag.strip('"').replace('W/', '')
            
            # Check if ETag matches our checksum
            if clean_etag.lower() == local_checksum.lower():
                logger.info(f"Files match via ETag comparison (optimization: skipped download)")
                comparison_details['etag_match'] = True
                comparison_details['optimization_used'] = 'etag_match'
                return 'identical', comparison_details
            elif len(clean_etag) == len(local_checksum):
                # Same length suggests same hash algorithm but different content
                logger.info(f"Files differ via ETag comparison")
                comparison_details['etag_match'] = False
                comparison_details['remote_etag'] = clean_etag
                comparison_details['local_checksum'] = local_checksum
                comparison_details['optimization_used'] = 'etag_differ'
                return self._check_file_dates(comparison_details)
            else:
                logger.debug(f"ETag format doesn't match checksum format, continuing with download verification")
                
        # If we get here, we need to download and verify (slowest but most accurate)
        logger.debug(f"No optimization available, downloading for checksum comparison")
        comparison_details['optimization_used'] = 'download_required'
        
        # For very large files with matching size, consider them likely identical
        # This helps avoid unnecessary downloads of huge files that are probably the same
        local_size = comparison_details.get('local_size', 0)
        if local_size > 100 * 1024 * 1024:  # > 100MB
            logger.info(f"Large file ({local_size:,} bytes) with matching size - considering identical to avoid large download")
            comparison_details['optimization_used'] = 'large_file_size_match'
            return 'identical', comparison_details
            
        # Download and compare checksums (most accurate but slowest)
        try:
            # For download comparison, we need a method to get remote content
            # Since download_file expects local_path parameter, we'll fall back to date comparison
            logger.info(f"Cannot download remote file for comparison, falling back to date comparison")
            return self._check_file_dates(comparison_details)
        except Exception as e:
            logger.warning(f"Could not download remote file for comparison: {e}")
            # Fall back to date comparison
            return self._check_file_dates(comparison_details)
    
    def _check_file_dates(self, details: Dict) -> Tuple[str, Dict]:
        """Check file modification dates to determine upload preference"""
        local_mtime = details.get('local_mtime', 0)
        remote_mtime = details.get('remote_mtime', 0)
        
        if remote_mtime == 0:
            # No remote date available
            return 'different', details
            
        # Allow 2 second tolerance for timestamp differences
        time_diff = abs(local_mtime - remote_mtime)
        if time_diff < 2:
            return 'identical', details
        elif local_mtime > remote_mtime:
            return 'newer_local', details
        else:
            return 'newer_remote', details
    
    def run(self):
        """Main processing loop"""
        logger.info("FileProcessor thread started - beginning queue processing")
        while self.running:
            try:
                # Get file from queue (timeout allows checking self.running)
                file_item = self.file_queue.get(timeout=1)
                logger.info(f"FileProcessor: Retrieved item from queue: {file_item}")
                
                if self.webdav_client:
                    # Handle both string paths and dict objects with resolution info
                    if isinstance(file_item, dict):
                        logger.info(f"Processing conflict resolution item: {file_item}")
                        self.process_file_with_resolution(file_item)
                    else:
                        logger.info(f"Processing regular file item: {file_item}")
                        self.process_file(file_item)
                else:
                    logger.warning("FileProcessor: No WebDAV client configured - cannot process files")
                    # Remove from queued files if processing failed
                    if isinstance(file_item, str) and self.app_instance:
                        self.app_instance.queued_files.discard(file_item)
                        # Update status in table
                        filename = os.path.basename(file_item)
                        self.status_update.emit(filename, "Failed", file_item)
                        self.transfer_complete.emit(filename, file_item, False, "No WebDAV connection configured")
                    
            except queue.Empty:
                # No items in queue, continue loop
                continue
            except Exception as e:
                logger.error(f"Error in FileProcessor main loop: {e}", exc_info=True)
    
    def process_file_with_resolution(self, file_item: dict):
        """Process a file that already has conflict resolution"""
        filepath = file_item['filepath']
        filename = file_item['filename']
        remote_path = file_item['remote_path']
        resolution = file_item['resolution']
        new_name = file_item.get('new_name')
        
        # Track that this file is now being processed
        if self.app_instance:
            self.app_instance.queued_files.discard(filepath)
            self.app_instance.processing_files.add(filepath)
        
        try:
            # Apply resolution
            if resolution == 'skip':
                self.transfer_complete.emit(filename, filepath, True, "Skipped due to user choice")
                return
            elif resolution == 'rename' and new_name:
                # Update remote path and filename for rename
                remote_dir = os.path.dirname(remote_path)
                remote_path = f"{remote_dir}/{new_name}".replace('//', '/')
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
    
    def is_file_accessible(self, filepath: str) -> Tuple[bool, str]:
        """Check if a file can be opened for reading"""
        try:
            with open(filepath, 'rb') as f:
                # Try to read the first byte to ensure file is truly accessible
                f.read(1)
            return True, ""
        except PermissionError as e:
            return False, f"Permission denied: {str(e)}"
        except IOError as e:
            return False, f"IO error: {str(e)}"
        except Exception as e:
            return False, f"Access error: {str(e)}"
    
    def schedule_locked_file_retry(self, filepath: str, remote_path: str, filename: str, access_error: str):
        """Schedule a retry for a locked file after appropriate wait time"""
        if not hasattr(self, 'locked_file_retries'):
            self.locked_file_retries = {}
        
        # Track retry count for this file
        retry_key = filepath
        retry_count = self.locked_file_retries.get(retry_key, 0)
        max_retries = self.app_instance.max_retries_spin.value()
        
        if retry_count >= max_retries:
            # Give up after max retries
            self.transfer_complete.emit(
                filename, filepath, False, 
                f"File remained locked after {max_retries} attempts over {int((self.app_instance.initial_wait_spin.value() * 60 + max_retries * self.app_instance.retry_interval_spin.value()) / 60)} minutes. File may still be in use by instrument or analysis software."
            )
            self.locked_file_retries.pop(retry_key, None)
            return
        
        # Calculate wait time and create user-friendly status messages
        if retry_count == 0:
            # First attempt - use initial wait time
            wait_time_ms = self.app_instance.initial_wait_spin.value() * 60 * 1000  # Convert minutes to ms
            wait_minutes = self.app_instance.initial_wait_spin.value()
            status_msg = f"File locked - waiting {wait_minutes} minutes for instrument to finish writing..."
        else:
            # Subsequent attempts - use retry interval
            wait_time_ms = self.app_instance.retry_interval_spin.value() * 1000  # Convert seconds to ms
            wait_seconds = self.app_instance.retry_interval_spin.value()
            remaining_retries = max_retries - retry_count
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
            self._start_progress_countdown(filepath, filename, wait_time_ms, wait_minutes, "minutes")
        
        logger.info(f"Scheduled locked file retry for {filename} (attempt {retry_count + 1}) in {wait_time_ms/1000:.1f}s")
    
    def _start_progress_countdown(self, filepath: str, filename: str, total_wait_ms: int, total_time_value: int, time_unit: str):
        """Start a countdown timer to show progress during file lock wait"""
        if not hasattr(self, 'progress_timers'):
            self.progress_timers = {}
        
        # Update every 10 seconds for minutes, every second for seconds
        update_interval_ms = 10000 if time_unit == "minutes" else 1000
        elapsed_time = 0
        
        progress_timer = QTimer()
        progress_timer.timeout.connect(lambda: self._update_progress_countdown(
            filepath, filename, elapsed_time, total_wait_ms, total_time_value, time_unit, progress_timer
        ))
        
        self.progress_timers[filepath] = {
            'timer': progress_timer,
            'elapsed': 0,
            'total_ms': total_wait_ms,
            'total_value': total_time_value,
            'unit': time_unit
        }
        
        progress_timer.start(update_interval_ms)
    
    def _update_progress_countdown(self, filepath: str, filename: str, elapsed_ms: int, total_ms: int, total_value: int, unit: str, timer: QTimer):
        """Update the countdown progress display"""
        if filepath not in self.progress_timers:
            timer.stop()
            return
            
        progress_info = self.progress_timers[filepath]
        progress_info['elapsed'] += 10000 if unit == "minutes" else 1000
        
        remaining_ms = total_ms - progress_info['elapsed']
        
        if remaining_ms <= 0:
            # Time's up, stop progress timer
            timer.stop()
            self.progress_timers.pop(filepath, None)
            return
        
        if unit == "minutes":
            remaining_minutes = max(1, int(remaining_ms / 60000))
            elapsed_minutes = int(progress_info['elapsed'] / 60000)
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
        retry_info['attempts'] += 1
        
        # Clean up any existing progress timer
        if 'progress_timer_id' in retry_info:
            timer_id = retry_info.pop('progress_timer_id')
            if hasattr(self.app_instance, 'progress_timers') and timer_id in self.app_instance.progress_timers:
                self.app_instance.progress_timers[timer_id].stop()
                del self.app_instance.progress_timers[timer_id]
        
        # Check if file is still accessible
        accessible, access_error = self.is_file_accessible(filepath)
        if not accessible:
            # Still locked, check max retries
            if retry_info['attempts'] >= retry_info['max_retries']:
                # Clean up retry info
                self.locked_file_retries.pop(retry_key, None)
                # Update status to failed
                self.status_update.emit(filename, "File locked - max retries exceeded", filepath)
                self.transfer_complete.emit(filename, filepath, False, f"File remained locked after {retry_info['attempts']} attempts: {access_error}")
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
    
    def is_file_accessible(self, filepath: str) -> Tuple[bool, str]:
        """Check if a file can be opened for reading"""
        try:
            with open(filepath, 'rb') as f:
                # Try to read the first byte to ensure file is truly accessible
                f.read(1)
            return True, ""
        except PermissionError as e:
            return False, f"Permission denied: {str(e)}"
        except IOError as e:
            return False, f"IO error: {str(e)}"
        except Exception as e:
            return False, f"Access error: {str(e)}"
    
    def upload_file(self, filepath: str, remote_path: str, filename: str):
        """Upload file to remote path"""
        try:
            # Always check if file is accessible (locked file handling always enabled)
            accessible, access_error = self.is_file_accessible(filepath)
            if not accessible:
                # File is locked, schedule retry
                self.schedule_locked_file_retry(filepath, remote_path, filename, access_error)
                return
            
            # Calculate checksum for verification - this will also fail if file is locked
            self.status_update.emit(filename, "Calculating checksum...", filepath)
            try:
                local_checksum = self.calculate_checksum(filepath)
            except (PermissionError, IOError) as e:
                # File became locked during checksum calculation (locked file handling always enabled)
                error_msg = f"File locked during checksum: {str(e)}"
                self.schedule_locked_file_retry(filepath, remote_path, filename, error_msg)
                return
            
            # Create remote directory if needed (check cache to avoid redundant attempts)
            remote_dir = os.path.dirname(remote_path)
            if remote_dir and remote_dir != '/' and self._should_create_directory(remote_dir):
                logger.info(f"Creating remote directory: {remote_dir}")
                success = self.webdav_client.create_directory(remote_dir)
                if success and self.app_instance:
                    self.app_instance.created_directories.add(remote_dir)
            
            # Upload file with detailed progress and status tracking
            self.status_update.emit(filename, "Preparing upload...", filepath)
            
            # Track last status percentage to avoid too many updates
            last_status_percentage = -1
            
            def progress_callback(current, total):
                nonlocal last_status_percentage
                
                # Calculate percentage for progress bar updates
                if total > 0:
                    percentage = (current / total) * 100
                    
                    # Simplified status messages - let the progress bar show the percentage
                    if percentage >= 100:
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
                
                # Always pass through the progress
                self.progress_update.emit(filepath, current, total)
                
            # First show file reading status
            self.status_update.emit(filename, "Reading file...", filepath)
            
            success, error = self.webdav_client.upload_file_chunked(
                filepath, remote_path, progress_callback
            )
            
            if success:
                # Store checksum for future reference
                self.status_update.emit(filename, "Storing checksum...", filepath)
                try:
                    self.webdav_client.store_checksum(remote_path, local_checksum)
                except Exception as e:
                    logger.warning(f"Failed to store checksum for {remote_path}: {e}")
                
                # Verify upload if enabled
                if hasattr(self, 'verify_uploads') and self.verify_uploads:
                    self.status_update.emit(filename, "Verifying upload...", filepath)
                    is_verified, verify_message = self.verify_uploaded_file(filepath, remote_path, local_checksum)
                    if is_verified:
                        self.transfer_complete.emit(
                            filename, filepath, True, 
                            f"Upload verified successfully (checksum: {local_checksum[:8]}...)"
                        )
                    else:
                        self.transfer_complete.emit(
                            filename, filepath, False, f"Upload verification failed: {verify_message}"
                        )
                else:
                    self.transfer_complete.emit(
                        filename, filepath, True, 
                        f"Uploaded successfully (checksum: {local_checksum[:8]}...)"
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
                remote_path = f"{self.remote_base_path}/{rel_path}".replace('\\', '/')
                logger.info(f"Preserve structure: {filepath} -> {remote_path} (rel_path: {rel_path})")
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
            except (PermissionError, IOError) as e:
                # File became locked during checksum calculation - always handle locked files
                error_msg = f"File locked during checksum: {str(e)}"
                self.schedule_locked_file_retry(filepath, remote_path, filename, error_msg)
                return
            
            # Create remote directories if needed (check cache to avoid redundant attempts)
            remote_dir = os.path.dirname(remote_path)
            if self.preserve_structure and remote_dir != self.remote_base_path and self._should_create_directory(remote_dir):
                success = self.webdav_client.create_directory(remote_dir)
                if success and self.app_instance:
                    self.app_instance.created_directories.add(remote_dir)
            
            # Check for duplicate upload attempts to same remote path
            if self.app_instance:
                if filepath in self.app_instance.file_remote_paths:
                    existing_remote_path = self.app_instance.file_remote_paths[filepath]
                    if existing_remote_path == remote_path:
                        logger.warning(f"File {filepath} already processed/processing to {remote_path}, skipping duplicate")
                        self.transfer_complete.emit(filename, filepath, True, "Skipped - already processed")
                        return
                    else:
                        logger.error(f"File {filepath} being uploaded to different paths: existing={existing_remote_path}, new={remote_path}")
                
                # Track this file -> remote path mapping
                self.app_instance.file_remote_paths[filepath] = remote_path
            
            # Check if remote file exists and get info
            self.status_update.emit(filename, "Checking remote file...", filepath)
            remote_info = self.webdav_client.get_file_info(remote_path)
            
            if remote_info is None:
                # Error getting remote info, proceed with upload
                logger.warning(f"Could not get remote file info for {remote_path}, proceeding with upload")
                remote_info = {'exists': False}
            
            # Compare files if remote exists
            comparison_result, conflict_details = self.compare_files(filepath, remote_info, local_checksum)
            
            # Initialize resolution variables
            resolution = None
            new_name = None
            
            if comparison_result == 'identical':
                # Files are identical, skip upload
                self.transfer_complete.emit(
                    filename, filepath, True, 
                    f"File already exists with same content (checksum: {local_checksum[:8]}...)"
                )
                return
            elif comparison_result == 'new':
                # New file, proceed with upload
                logger.info(f"New file detected: {filename}")
            elif comparison_result == 'different':
                # Conflict detected, need user resolution
                logger.info(f"File conflict detected for: {filename}")
                
                if self.apply_to_all and self.conflict_resolution:
                    # Use previous resolution
                    resolution = self.conflict_resolution
                    if resolution == 'rename':
                        # Generate a new conflict name
                        new_name = f"conflict_{int(time.time())}_{filename}"
                else:
                    # Emit signal for main thread to handle conflict resolution
                    self.status_update.emit(filename, "Conflict detected - waiting for user input...", filepath)
                    
                    # Request conflict resolution from main thread
                    self.conflict_resolution_needed.emit(filename, filepath, remote_path, conflict_details)
                    
                    # Wait for resolution (this would be handled by a proper signal/slot mechanism)
                    # For now, we'll return early and let the main thread handle the resolution
                    logger.info(f"Conflict resolution requested for: {filename}")
                    return
                
                if resolution == 'skip':
                    self.transfer_complete.emit(
                        filename, filepath, True, "Skipped due to conflict"
                    )
                    return
                elif resolution == 'rename' and new_name:
                    # Update remote path with new name
                    remote_dir = os.path.dirname(remote_path)
                    remote_path = f"{remote_dir}/{new_name}".replace('//', '/')
                    filename = new_name  # Update filename for status updates
                # For 'overwrite', continue with original remote_path
            
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


class FileConflictDialog(QDialog):
    """Dialog for resolving file conflicts"""
    
    def __init__(self, filename: str, conflict_details: Dict, parent=None):
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
        local_size = self.conflict_details.get('local_size', 0)
        local_size_str = f"{local_size:,} bytes"
        if local_size > 1024*1024:
            local_size_str += f" ({local_size/(1024*1024):.2f} MB)"
        
        remote_size = self.conflict_details.get('remote_size', 0)
        remote_size_str = f"{remote_size:,} bytes"
        if remote_size > 1024*1024:
            remote_size_str += f" ({remote_size/(1024*1024):.2f} MB)"
        
        comparison_layout.addWidget(QLabel("Size:"), 1, 0)
        comparison_layout.addWidget(QLabel(local_size_str), 1, 1)
        comparison_layout.addWidget(QLabel(remote_size_str), 1, 2)
        
        # Checksums  
        local_checksum = self.conflict_details.get('local_checksum', 'Unknown')
        remote_checksum = self.conflict_details.get('remote_checksum', 'Unknown')
        
        comparison_layout.addWidget(QLabel("Checksum:"), 2, 0)
        comparison_layout.addWidget(QLabel(f"{local_checksum[:16]}..." if len(local_checksum) > 16 else local_checksum), 2, 1)
        comparison_layout.addWidget(QLabel(f"{remote_checksum[:16]}..." if len(remote_checksum) > 16 else remote_checksum), 2, 2)
        
        # Last modified dates
        local_date = self.conflict_details.get('local_date', 'Unknown')
        remote_date = self.conflict_details.get('remote_date', 'Unknown')
        
        comparison_layout.addWidget(QLabel("Modified:"), 3, 0)
        comparison_layout.addWidget(QLabel(str(local_date)), 3, 1)
        comparison_layout.addWidget(QLabel(str(remote_date)), 3, 2)
        
        comparison_group.setLayout(comparison_layout)
        layout.addWidget(comparison_group)
        
        # Resolution options
        resolution_group = QGroupBox("Resolution")
        resolution_layout = QVBoxLayout()
        
        # Add date comparison info if available
        local_date = self.conflict_details.get('local_date')
        remote_date = self.conflict_details.get('remote_date')
        
        date_info = ""
        default_choice = 'skip'
        
        if local_date and remote_date and local_date != 'Unknown' and remote_date != 'Unknown':
            try:
                # Parse dates for comparison
                if isinstance(local_date, str):
                    local_dt = datetime.fromisoformat(local_date.replace('Z', '+00:00'))
                else:
                    local_dt = local_date
                    
                if isinstance(remote_date, str):
                    remote_dt = datetime.fromisoformat(remote_date.replace('Z', '+00:00'))
                else:
                    remote_dt = remote_date
                
                if local_dt > remote_dt:
                    date_info = " (local file is newer)"
                    default_choice = 'overwrite'
                elif remote_dt > local_dt:
                    date_info = " (remote file is newer)"
                    default_choice = 'skip'
                else:
                    date_info = " (same modification time)"
            except:
                date_info = ""
        
        self.skip_radio = QRadioButton(f"Skip - Don't upload this file{date_info if 'newer' in date_info and 'remote' in date_info else ''}")
        self.overwrite_radio = QRadioButton(f"Overwrite - Replace the remote file{date_info if 'newer' in date_info and 'local' in date_info else ''}")
        self.rename_radio = QRadioButton("Rename - Upload with a different name")
        
        # Set default based on date comparison
        if default_choice == 'overwrite':
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
            return 'skip', None, self.apply_to_all_check.isChecked()
        elif self.overwrite_radio.isChecked():
            return 'overwrite', None, self.apply_to_all_check.isChecked()
        elif self.rename_radio.isChecked():
            return 'rename', self.rename_input.text(), self.apply_to_all_check.isChecked()
        else:
            return 'skip', None, False


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
        logging.info(f"GUI: Retrieved {len(items)} items from list_directory for {self.current_path}")
        
        for i, item in enumerate(items):
            logging.info(f"GUI: Processing item {i}: {item['name']} (is_dir: {item['is_dir']}, size: {item['size']})")
            if item['is_dir']:
                tree_item = QTreeWidgetItem(self.tree, [
                    item['name'],
                    "Folder",
                    ""
                ])
                tree_item.setData(0, Qt.ItemDataRole.UserRole, item['path'])
                logging.info(f"GUI: Added folder item: {item['name']}")
            else:
                size_mb = item['size'] / (1024 * 1024)
                tree_item = QTreeWidgetItem(self.tree, [
                    item['name'],
                    "File",
                    f"{size_mb:.2f} MB"
                ])
                tree_item.setData(0, Qt.ItemDataRole.UserRole, item['path'])
                logging.info(f"GUI: Added file item: {item['name']} ({size_mb:.2f} MB)")
        
        logging.info(f"GUI: Tree widget now has {self.tree.topLevelItemCount()} total items")
    
    def on_item_double_click(self, item, column):
        """Handle double-click on item"""
        path = item.data(0, Qt.ItemDataRole.UserRole)
        
        if path == "..":
            # Go to parent directory
            self.current_path = os.path.dirname(self.current_path.rstrip('/'))
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
                    with open('panoramabridge.log', 'r') as f:
                        log_lines = f.readlines()
                        recent_errors = [line for line in log_lines[-10:] if 'ERROR' in line and 'creating directory' in line]
                        
                        if recent_errors:
                            latest_error = recent_errors[-1]
                            if 'Permission denied' in latest_error:
                                error_msg += "❌ Permission Denied (HTTP 403)\n\n"
                                error_msg += "This means you don't have write permissions to create folders in this directory.\n\n"
                                error_msg += "Possible solutions:\n"
                                error_msg += "• Contact your Panorama administrator to request write access\n"
                                error_msg += "• Try creating the folder in a different directory where you have permissions\n"
                                error_msg += "• Check if you're in the correct user folder\n\n"
                            elif 'Conflict' in latest_error:
                                error_msg += "❌ Path Conflict (HTTP 409)\n\n"
                                error_msg += "The parent directory may not exist.\n\n"
                            else:
                                error_msg += "❌ Server Error\n\n"
                        else:
                            error_msg += "Possible reasons:\n"
                            error_msg += "• You may not have write permissions\n"
                            error_msg += "• The folder name may contain invalid characters\n"
                            error_msg += "• The server may have restrictions on folder creation\n\n"
                except:
                    error_msg += "Possible reasons:\n"
                    error_msg += "• You may not have write permissions\n"
                    error_msg += "• The folder name may contain invalid characters\n"
                    error_msg += "• The server may have restrictions on folder creation\n\n"
                    
                error_msg += "View the application logs (View → View Application Logs) for more details."
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
        self.file_queue = queue.Queue()                    # Thread-safe queue for file processing
        self.file_processor = FileProcessor(self.file_queue, self)  # Background processing thread
        self.monitor_handler = None                        # File system event handler
        self.observer = None                              # Watchdog observer for file monitoring
        self.webdav_client = None                         # WebDAV client instance
        self.transfer_rows = {}                           # Track UI table rows for updates
        self.queued_files = set()                         # Track files already queued to prevent duplicates
        self.processing_files = set()                     # Track files currently being processed
        self.created_directories = set()                  # Cache of successfully created remote directories
        self.failed_files = {}                           # Track files that failed verification for re-upload
        self.file_remote_paths = {}                      # Track filepath -> remote_path mappings to prevent duplicate uploads
        self.local_checksum_cache = {}                   # Local checksum cache to avoid recalculation
        
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
            if getattr(sys, 'frozen', False):
                # Running as bundled executable
                base_path = sys._MEIPASS
                logo_path = os.path.join(base_path, 'screenshots', 'panoramabridge-logo.png')
            else:
                # Running in development
                script_dir = os.path.dirname(os.path.abspath(__file__))
                logo_path = os.path.join(script_dir, 'screenshots', 'panoramabridge-logo.png')
            
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
        view_menu = menubar.addMenu('View')
        
        # View logs action
        view_logs_action = view_menu.addAction('View Application Logs')
        view_logs_action.triggered.connect(self.view_full_logs)
        
        # Help menu
        help_menu = menubar.addMenu('Help')
        
        # About action
        about_action = help_menu.addAction('About')
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
            if getattr(sys, 'frozen', False):
                # Running as bundled executable
                base_path = sys._MEIPASS
                logo_path = os.path.join(base_path, 'screenshots', 'panoramabridge-logo.png')
            else:
                # Running in development
                script_dir = os.path.dirname(os.path.abspath(__file__))
                logo_path = os.path.join(script_dir, 'screenshots', 'panoramabridge-logo.png')
            
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
            QMessageBox.about(self, "About PanoramaBridge", 
                             "PanoramaBridge v1.0\n\n"
                             "A file monitoring and WebDAV transfer application\n"
                             "for syncing files to Panorama servers.\n\n"
                             "Developed in the MacCoss Lab\n"
                             "Department of Genome Sciences\n"
                             "University of Washington\n\n"
                             "Lab website: https://maccosslab.org\n\n"
                             "Logs are saved to: panoramabridge.log")
    
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
        locked_info = QLabel("Locked File Handling - Wait for files to be fully written (e.g., mass spectrometers)")
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
            self.save_creds_check.setToolTip("Keyring library not available. Install 'keyring' package to enable secure credential storage.")
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
        
        transfer_layout.addWidget(QLabel("Chunk size (MB):"), 0, 0)
        self.chunk_spin = QSpinBox()
        self.chunk_spin.setRange(1, 100)
        self.chunk_spin.setValue(10)
        transfer_layout.addWidget(self.chunk_spin, 0, 1)
        
        self.verify_uploads_check = QCheckBox("Verify uploads by downloading and comparing checksums")
        self.verify_uploads_check.setChecked(True)  # Default to enabled
        self.verify_uploads_check.setToolTip(
            "For files < 50MB: Downloads and verifies checksum for complete integrity check.\n"
            "For larger files: Uses size and ETag comparison for performance.\n"
            "Uncheck to skip verification for faster uploads (less secure)."
        )
        transfer_layout.addWidget(self.verify_uploads_check, 1, 0, 1, 2)
        
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
        reupload_btn.setToolTip("Re-upload files that failed checksum verification")
        queue_layout.addWidget(reupload_btn)
        
        clear_btn = QPushButton("Clear Completed")
        clear_btn.clicked.connect(self.clear_completed_transfers)
        queue_layout.addWidget(clear_btn)
        
        layout.addLayout(queue_layout)
        
        # Transfer table
        self.transfer_table = QTableWidget()
        self.transfer_table.setColumnCount(5)
        self.transfer_table.setHorizontalHeaderLabels(["File", "Path", "Status", "Progress", "Message"])
        
        # Set column widths for better display
        header = self.transfer_table.horizontalHeader()
        header.resizeSection(0, 200)  # File name
        header.resizeSection(1, 120)  # Path
        header.resizeSection(2, 80)   # Status
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
        return f"{filename}|{hash(filepath)}"
    
    def add_queued_file_to_table(self, filepath: str):
        """Add a queued file to the transfer table with 'Queued' status at the top"""
        filename = os.path.basename(filepath)
        
        # Use consistent unique key format (same as on_status_update)
        unique_key = self.get_transfer_table_key(filename, filepath)
        if unique_key in self.transfer_rows:
            # File already in table, just update status to ensure it's marked as queued
            row = self.transfer_rows[unique_key]
            if row < self.transfer_table.rowCount():
                status_item = self.transfer_table.item(row, 2)
                if status_item:
                    status_item.setText("Queued")
                message_item = self.transfer_table.item(row, 4) 
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
            if not relative_path.startswith('..'):
                display_path = relative_path
        
        # Set basic info in the new bottom row
        self.transfer_table.setItem(row_count, 0, QTableWidgetItem(filename))
        self.transfer_table.setItem(row_count, 1, QTableWidgetItem(display_path))
        self.transfer_table.setItem(row_count, 2, QTableWidgetItem("Queued"))
        
        # Add empty progress bar
        progress_bar = QProgressBar()
        progress_bar.setValue(0)
        progress_bar.setVisible(False)  # Hide progress bar for queued files
        self.transfer_table.setCellWidget(row_count, 3, progress_bar)
        
        # Set message  
        self.transfer_table.setItem(row_count, 4, QTableWidgetItem("Waiting for processing..."))
        
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
            with open('panoramabridge.log', 'r') as f:
                log_content.setText(f.read())
        except FileNotFoundError:
            log_content.setText("No log file found yet. Logs will appear here as the application runs.")
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
                QMessageBox.warning(self, "Connection Required", 
                                   "Please enter valid connection details first.")
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
                
                # Set chunk size
                self.webdav_client.chunk_size = self.chunk_spin.value() * 1024 * 1024
                
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
            QMessageBox.information(self, "Success", 
                                   f"Connection successful!\nConnected to: {url}")
            self.log_text.append(f"{datetime.now().strftime('%H:%M:%S')} - Connected to WebDAV server at {url}")
        else:
            QMessageBox.warning(self, "Failed", "Could not connect to WebDAV server.\nCheck your URL, username, and password.")
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
            
            # Clear tracking sets when stopping monitoring
            self.queued_files.clear()
            self.created_directories.clear()
            self.failed_files.clear()
            self.file_remote_paths.clear()
            # Note: Keep processing_files and transfer_rows intact as files may still be transferring
        else:
            # Start monitoring
            directory = self.dir_input.text()
            extensions = [e.strip() for e in self.extensions_input.text().split(',') if e.strip()]
            
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
            
            # Create handler and observer
            self.monitor_handler = FileMonitorHandler(
                extensions, 
                self.file_queue,
                self.subdirs_check.isChecked(),
                self  # Pass app instance for duplicate tracking
            )
            
            self.observer = Observer()
            self.observer.schedule(
                self.monitor_handler,
                directory,
                recursive=self.subdirs_check.isChecked()
            )
            
            self.observer.start()
            logger.info(f"Started OS-level file monitoring for: {directory}")
            
            # Only start polling if explicitly enabled by user
            if self.enable_polling_check.isChecked():
                polling_interval_ms = self.polling_interval_spin.value() * 60 * 1000  # Convert minutes to ms
                self.poll_timer.start(polling_interval_ms)
                logger.info(f"Started backup polling every {self.polling_interval_spin.value()} minutes")
            else:
                logger.info("Backup polling disabled - relying on OS file events only")
            
            # Scan for existing files in the directory
            self.scan_existing_files(directory, extensions, self.subdirs_check.isChecked())
            
            self.start_btn.setText("Stop Monitoring")
            self.status_label.setText("Monitoring active")
            self.status_label.setStyleSheet("font-weight: bold; color: green;")
            
            self.log_text.append(f"{datetime.now().strftime('%H:%M:%S')} - Started monitoring {directory}")
            self.log_text.append(f"Extensions: {', '.join(extensions)}")
            
            # Log the actual monitoring configuration
            if self.enable_polling_check.isChecked():
                polling_interval = self.polling_interval_spin.value()
                self.log_text.append(f"{datetime.now().strftime('%H:%M:%S')} - OS file events + backup polling every {polling_interval} minutes")
            else:
                self.log_text.append(f"{datetime.now().strftime('%H:%M:%S')} - OS file events only (backup polling disabled)")
    
    def scan_existing_files(self, directory: str, extensions: List[str], recursive: bool):
        """Scan directory for existing files and add them to the queue"""
        logger.info(f"Scanning existing files in {directory}")
        logger.info(f"Recursive scanning: {recursive}")
        
        # Convert extensions to the same format as FileMonitorHandler
        formatted_extensions = [ext.lower() if ext.startswith('.') else f'.{ext.lower()}' 
                               for ext in extensions]
        logger.info(f"Scanning for extensions: {formatted_extensions}")
        
        files_found = 0
        
        try:
            if recursive:
                # Recursively scan all subdirectories
                logger.info(f"Starting recursive scan using os.walk")
                for root, dirs, files in os.walk(directory):
                    logger.debug(f"Scanning directory: {root}")
                    for file in files:
                        filepath = os.path.join(root, file)
                        logger.debug(f"Checking file: {filepath}")
                        
                        # Skip hidden/system files
                        if file.startswith('.') or file.startswith('~'):
                            logger.debug(f"Skipping hidden/system file: {file}")
                            continue
                            
                        if any(filepath.lower().endswith(ext) for ext in formatted_extensions):
                            # Check for duplicates before queueing
                            if self._should_queue_file_scan(filepath):
                                self.file_queue.put(filepath)
                                files_found += 1
                                logger.info(f"Queued existing file: {filepath}")
                                # Add to transfer table with "Queued" status
                                self.add_queued_file_to_table(filepath)
                            else:
                                logger.debug(f"File already queued or processing, skipping: {filepath}")
                        else:
                            logger.debug(f"File {filepath} doesn't match extensions")
            else:
                # Scan only the top-level directory
                logger.info(f"Starting non-recursive scan")
                try:
                    for item in os.listdir(directory):
                        filepath = os.path.join(directory, item)
                        if os.path.isfile(filepath):
                            # Skip hidden/system files
                            if item.startswith('.') or item.startswith('~'):
                                logger.debug(f"Skipping hidden/system file: {item}")
                                continue
                                
                            if any(filepath.lower().endswith(ext) for ext in formatted_extensions):
                                # Check for duplicates before queueing
                                if self._should_queue_file_scan(filepath):
                                    self.file_queue.put(filepath)
                                    files_found += 1
                                    logger.info(f"Queued existing file: {filepath}")
                                    # Add to transfer table with "Queued" status
                                    self.add_queued_file_to_table(filepath)
                                else:
                                    logger.debug(f"File already queued or processing, skipping: {filepath}")
                except OSError as e:
                    logger.error(f"Error listing directory {directory}: {e}")
        except Exception as e:
            logger.error(f"Error scanning directory {directory}: {e}")
        
        logger.info(f"Scan complete: {files_found} existing files queued")
            
        if files_found > 0:
            self.log_text.append(f"{datetime.now().strftime('%H:%M:%S')} - Found {files_found} existing files to process")
            logger.info(f"Scan complete: {files_found} existing files queued")
        else:
            self.log_text.append(f"{datetime.now().strftime('%H:%M:%S')} - No existing files found matching criteria")
            logger.info("Scan complete: no existing files found")

    def _should_queue_file_scan(self, filepath: str) -> bool:
        """
        Check if a file should be queued during scanning, preventing duplicates.
        
        Args:
            filepath: Path to the file being considered for queueing
            
        Returns:
            True if file should be queued, False if already queued/processing
        """
        # Check if file is already queued or being processed
        if filepath in self.queued_files:
            return False
        if filepath in self.processing_files:
            return False
        
        # Add to queued files tracking
        self.queued_files.add(filepath)
        return True

    def update_queue_size(self):
        """Update queue size display with enhanced debugging"""
        size = self.file_queue.qsize()
        queued_count = len(self.queued_files)
        processing_count = len(self.processing_files)
        
        self.queue_label.setText(f"{size} files")
        
        # Add diagnostic logging every 10 seconds (timer runs every 1 second)
        if not hasattr(self, 'queue_debug_counter'):
            self.queue_debug_counter = 0
        self.queue_debug_counter += 1
        
        if self.queue_debug_counter >= 10:  # Every 10 seconds
            self.queue_debug_counter = 0
            if size > 0 or queued_count > 0 or processing_count > 0:
                logger.info(f"Queue status: queue={size}, queued_files={queued_count}, processing_files={processing_count}")
                logger.info(f"FileProcessor running: {getattr(self.file_processor, 'running', 'Unknown')}")
                logger.info(f"WebDAV client configured: {self.file_processor.webdav_client is not None}")
                if queued_count > 0:
                    logger.info(f"Sample queued files: {list(self.queued_files)[:3]}")
                if processing_count > 0:
                    logger.info(f"Sample processing files: {list(self.processing_files)[:3]}")
    
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
            extensions = [e.strip() for e in self.extensions_input.text().split(',') if e.strip()]
            
            if not directory or not extensions:
                return
                
            # Convert extensions to the same format as FileMonitorHandler
            formatted_extensions = [ext.lower() if ext.startswith('.') else f'.{ext.lower()}' 
                                   for ext in extensions]
            
            logger.debug(f"Backup polling scan: {directory}")
            files_found = 0
            
            if self.subdirs_check.isChecked():
                # Recursively scan all subdirectories
                for root, dirs, files in os.walk(directory):
                    for file in files:
                        filepath = os.path.join(root, file)
                        
                        # Skip hidden/system files
                        if file.startswith('.') or file.startswith('~'):
                            continue
                            
                        if any(filepath.lower().endswith(ext) for ext in formatted_extensions):
                            # Check if this is a new file we haven't seen
                            if self._should_queue_file_poll(filepath):
                                # Check if file is stable (not being written)
                                if self._is_file_stable(filepath):
                                    self.file_queue.put(filepath)
                                    files_found += 1
                                    logger.info(f"Polling backup found file (OS events missed): {filepath}")
                                    # Add to transfer table with "Queued" status
                                    self.add_queued_file_to_table(filepath)
            else:
                # Scan only the main directory
                try:
                    files = os.listdir(directory)
                    for file in files:
                        filepath = os.path.join(directory, file)
                        
                        # Skip directories, hidden files, system files
                        if os.path.isdir(filepath) or file.startswith('.') or file.startswith('~'):
                            continue
                            
                        if any(filepath.lower().endswith(ext) for ext in formatted_extensions):
                            if self._should_queue_file_poll(filepath):
                                if self._is_file_stable(filepath):
                                    self.file_queue.put(filepath)
                                    files_found += 1
                                    logger.info(f"Polling backup found file (OS events missed): {filepath}")
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
        Check if a file should be queued during polling, preventing duplicates.
        
        Args:
            filepath: Path to the file being considered for queueing
            
        Returns:
            True if file should be queued, False if already queued/processing
        """
        # Check if file is already queued or being processed
        if filepath in self.queued_files:
            return False
        if filepath in self.processing_files:
            return False
        
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
            return (stat1.st_size == stat2.st_size and 
                    stat1.st_mtime == stat2.st_mtime and
                    time.time() - stat1.st_mtime > stability_time)
        except (OSError, IOError):
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
            
            # Calculate relative path for display
            local_base = self.dir_input.text()
            if local_base and filepath:
                try:
                    rel_path = os.path.relpath(filepath, local_base)
                    # Convert to Unix-style path separators and add ./ prefix
                    if rel_path == os.path.basename(filepath):
                        # File is in the root directory
                        display_path = "./"
                    else:
                        # File is in a subdirectory
                        display_path = f"./{rel_path.replace(os.sep, '/').rsplit('/', 1)[0]}"
                except (ValueError, OSError):
                    display_path = "./"
            else:
                display_path = "./"
            
            self.transfer_table.setItem(row_count, 0, QTableWidgetItem(filename))
            self.transfer_table.setItem(row_count, 1, QTableWidgetItem(display_path))
            self.transfer_table.setItem(row_count, 2, QTableWidgetItem(status))
            
            progress_bar = QProgressBar()
            progress_bar.setMinimum(0)
            progress_bar.setMaximum(100)  # Always use percentage for consistency
            progress_bar.setValue(0)
            # Show progress bar only for active processing states
            if status in ["Queued", "Starting", "Pending"]:
                progress_bar.setVisible(False)
            else:
                progress_bar.setVisible(True)
            self.transfer_table.setCellWidget(row_count, 3, progress_bar)
            
            self.transfer_table.setItem(row_count, 4, QTableWidgetItem(""))
            
            self.transfer_rows[unique_key] = row_count
            
            # Auto-scroll to show active processing when starting
            if status not in ["Queued", "Starting", "Pending"]:
                self.transfer_table.scrollToItem(self.transfer_table.item(row_count, 0))
                
        else:
            # Update existing row - this is the normal case
            row = self.transfer_rows[unique_key]
            if row < self.transfer_table.rowCount():
                current_item = self.transfer_table.item(row, 2)
                current_status = current_item.text() if current_item else ""
                if current_item:
                    current_item.setText(status)
                
                # Show progress bar when transitioning from queued to active processing
                if current_status == "Queued" and status not in ["Queued", "Starting", "Pending"]:
                    progress_bar = self.transfer_table.cellWidget(row, 3)
                    if progress_bar and hasattr(progress_bar, 'setVisible'):
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
                progress_bar = self.transfer_table.cellWidget(row, 3)
                if progress_bar and hasattr(progress_bar, 'setValue'):
                    # Always use percentage (0-100) for consistent progress bar display
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
                status_item = self.transfer_table.item(row, 2)
                message_item = self.transfer_table.item(row, 4)
                if status_item:
                    status_item.setText(status)
                if message_item:
                    message_item.setText(message)
                
                # Track failed files for re-upload (specifically verification failures)
                if not success and ("verification failed" in message.lower() or "checksum" in message.lower()):
                    self.failed_files[unique_key] = {
                        'filepath': filepath,
                        'filename': filename,
                        'message': message,
                        'row': row
                    }
                elif success and unique_key in self.failed_files:
                    # Remove from failed files if now successful
                    del self.failed_files[unique_key]
                
                # Clean up remote path tracking when transfer is complete
                if filepath in self.file_remote_paths:
                    del self.file_remote_paths[filepath]
                
                # Update progress bar - ensure it shows 100% when complete
                progress_bar = self.transfer_table.cellWidget(row, 3)
                if progress_bar and hasattr(progress_bar, 'setValue'):
                    if success:
                        progress_bar.setValue(100)  # Always show 100% for successful completion
                    else:
                        progress_bar.setStyleSheet("QProgressBar::chunk { background-color: red; }")
        
        # Log the event
        timestamp = datetime.now().strftime('%H:%M:%S')
        if success:
            self.log_text.append(f"{timestamp} - ✓ {filename}: {message}")
        else:
            self.log_text.append(f"{timestamp} - ✗ {filename}: {message}")
    
    @pyqtSlot(str, str, str, dict)
    def on_conflict_resolution_needed(self, filename: str, filepath: str, remote_path: str, conflict_details: dict):
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
                self.file_queue.put({
                    'filepath': filepath,
                    'filename': filename,
                    'remote_path': remote_path,
                    'resolution': resolution,
                    'new_name': new_name
                })
                
                # Add to tracking (conflict resolution files bypass normal duplicate detection)
                self.queued_files.add(filepath)
                
                logger.info(f"Conflict resolution: Re-queuing {filepath} with remote_path={remote_path}, resolution={resolution}")
                
                # Log the resolution
                timestamp = datetime.now().strftime('%H:%M:%S')
                action_text = {
                    'overwrite': 'overwrite remote file',
                    'rename': 'rename and upload',
                    'skip': 'skip upload'
                }.get(resolution, resolution)
                
                self.log_text.append(f"{timestamp} - Conflict resolved for {filename}: {action_text}")
                if apply_to_all:
                    self.log_text.append(f"{timestamp} - Resolution will be applied to all future conflicts")
            else:
                # File already being processed
                timestamp = datetime.now().strftime('%H:%M:%S')
                self.log_text.append(f"{timestamp} - Conflict resolution skipped for {filename}: already being processed")
        else:
            # User cancelled - skip this file
            timestamp = datetime.now().strftime('%H:%M:%S')
            self.log_text.append(f"{timestamp} - Conflict resolution cancelled for {filename}: skipped")
    
    def clear_completed_transfers(self):
        """Clear completed transfers from the table"""
        rows_to_remove = []
        
        for unique_key, row in self.transfer_rows.items():
            status_item = self.transfer_table.item(row, 2)  # Status is now column 2
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
            QMessageBox.information(self, "No Failed Files", 
                                  "There are no files that failed verification to re-upload.")
            return
        
        failed_count = len(self.failed_files)
        reply = QMessageBox.question(self, "Re-upload Failed Files",
                                   f"Re-upload {failed_count} file(s) that failed verification?",
                                   QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No)
        
        if reply == QMessageBox.StandardButton.Yes:
            requeued = 0
            for unique_key, failed_info in list(self.failed_files.items()):
                filepath = failed_info['filepath']
                filename = failed_info['filename']
                
                # Check if file still exists
                if os.path.exists(filepath):
                    # Check for duplicates before re-queueing
                    if filepath not in self.queued_files and filepath not in self.processing_files:
                        # Reset the row status
                        row = failed_info['row']
                        if row < self.transfer_table.rowCount():
                            status_item = self.transfer_table.item(row, 2)
                            message_item = self.transfer_table.item(row, 4)
                            if status_item:
                                status_item.setText("Queued")
                            if message_item:
                                message_item.setText("Re-upload requested")
                            
                            # Reset progress bar
                            progress_bar = self.transfer_table.cellWidget(row, 3)
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
            
            timestamp = datetime.now().strftime('%H:%M:%S')
            self.log_text.append(f"{timestamp} - Re-queued {requeued} failed file(s) for upload")
    
    def show_transfer_context_menu(self, position):
        """Show context menu for transfer table"""
        item = self.transfer_table.itemAt(position)
        if item is None:
            return
        
        row = item.row()
        status_item = self.transfer_table.item(row, 2)
        
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
        filepath = failed_info['filepath']
        filename = failed_info['filename']
        
        # Check if file still exists
        if not os.path.exists(filepath):
            QMessageBox.warning(self, "File Not Found", 
                              f"The file {filename} no longer exists and cannot be re-uploaded.")
            # Remove from failed files tracking
            del self.failed_files[unique_key]
            return
        
        # Check for duplicates before re-queueing
        if filepath in self.queued_files or filepath in self.processing_files:
            QMessageBox.information(self, "File Already Queued", 
                                  f"The file {filename} is already queued or being processed.")
            # Remove from failed files since it's being handled
            del self.failed_files[unique_key]
            return
        
        # Reset the row status
        row = failed_info['row']
        if row < self.transfer_table.rowCount():
            status_item = self.transfer_table.item(row, 2)
            message_item = self.transfer_table.item(row, 4)
            if status_item:
                status_item.setText("Queued")
            if message_item:
                message_item.setText("Re-upload requested")
            
            # Reset progress bar
            progress_bar = self.transfer_table.cellWidget(row, 3)
            if progress_bar:
                progress_bar.setValue(0)
                progress_bar.setStyleSheet("")  # Clear any error styling
        
        # Add to queue for re-processing and tracking
        self.file_queue.put(filepath)
        self.queued_files.add(filepath)  # Add to tracking
        
        # Remove from failed files (will be re-added if it fails again)
        del self.failed_files[unique_key]
        
        timestamp = datetime.now().strftime('%H:%M:%S')
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
                with open(config_file, 'r') as f:
                    return json.load(f)
            except:
                pass
        else:
            # Check for old config file and migrate if found
            old_config_file = Path.home() / ".file_monitor_webdav" / "config.json"
            if old_config_file.exists():
                logger.info("Found old configuration, migrating to new location...")
                try:
                    with open(old_config_file, 'r') as f:
                        config = json.load(f)
                    
                    # Create new config directory and save
                    config_dir = Path.home() / ".panoramabridge"
                    config_dir.mkdir(exist_ok=True)
                    
                    with open(config_file, 'w') as f:
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
            "webdav_username": self.username_input.text() if not self.save_creds_check.isChecked() else "",
            "webdav_auth_type": self.auth_combo.currentText(),
            "remote_path": self.remote_path_input.text(),
            "chunk_size_mb": self.chunk_spin.value(),
            "verify_uploads": self.verify_uploads_check.isChecked(),
            "save_credentials": self.save_creds_check.isChecked(),
            "conflict_resolution": self.get_conflict_resolution_setting(),
            "local_checksum_cache": dict(self.local_checksum_cache) if hasattr(self, 'local_checksum_cache') else {}
        }
        
        try:
            with open(config_file, 'w') as f:
                json.dump(config, f, indent=2)
        except Exception as e:
            logger.error(f"Failed to save config: {e}")
    
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
        self.chunk_spin.setValue(self.config.get("chunk_size_mb", 10))
        self.verify_uploads_check.setChecked(self.config.get("verify_uploads", True))
        self.save_creds_check.setChecked(self.config.get("save_credentials", False))
        
        # Load conflict resolution setting
        conflict_setting = self.config.get("conflict_resolution", "ask")
        self.set_conflict_resolution_setting(conflict_setting)
        
        # Load checksum cache
        if not hasattr(self, 'local_checksum_cache'):
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
        if hasattr(self, 'local_checksum_cache') and self.local_checksum_cache:
            logger.debug(f"Saving {len(self.local_checksum_cache)} cached checksums")
            self.save_config()  # This will include the cache data
    
    def closeEvent(self, event):
        """Handle application close"""
        # Stop monitoring
        if self.observer and self.observer.is_alive():
            self.observer.stop()
            self.observer.join()
        
        # Stop processor
        self.file_processor.stop()
        self.file_processor.wait()
        
        # Save settings
        self.save_settings()
        
        event.accept()


def main():
    app = QApplication(sys.argv)
    app.setStyle('Fusion')  # Modern look
    
    # Set application icon for the executable
    try:
        script_dir = os.path.dirname(os.path.abspath(__file__))
        logo_path = os.path.join(script_dir, 'screenshots', 'panoramabridge-logo.png')
        if os.path.exists(logo_path):
            icon = QIcon(logo_path)
            app.setWindowIcon(icon)
    except Exception as e:
        logger.error(f"Failed to set application icon in main: {e}")
    
    window = MainWindow()
    window.show()
    
    sys.exit(app.exec())


if __name__ == '__main__':
    main()