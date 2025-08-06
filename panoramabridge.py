#!/usr/bin/env python3
"""
PanoramaBridge - A Python Qt6 Application for Directory Monitoring and WebDAV File Transfer

This application monitors local directories for new files and automatically transfers them 
to WebDAV servers (like Panorama) with comprehensive features:

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
    QSpinBox, QInputDialog, QDialog, QRadioButton
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
        url = urljoin(self.url, quote(path))
        
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
            response = self.session.request('PROPFIND', url, headers=headers, data=body)
            if response.status_code == 207:  # Multi-Status
                logger.info(f"PROPFIND successful for {path}, parsing response...")
                return self._parse_propfind_response(response.text, path)
            else:
                logger.error(f"Failed to list directory: {response.status_code}")
                return []
        except Exception as e:
            logger.error(f"Error listing directory: {e}")
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
        items = []
        try:
            root = ET.fromstring(xml_response)
            
            # Define namespace
            ns = {'d': 'DAV:'}
            
            for response in root.findall('.//d:response', ns):
                href = response.find('d:href', ns)
                if href is None:
                    continue
                    
                href_text = href.text
                if href_text is None:
                    continue
                    
                # Skip the base path itself (compare unquoted paths)
                if unquote(href_text.rstrip('/')) == base_path.rstrip('/'):
                    continue
                
                props = response.find('.//d:prop', ns)
                if props is None:
                    continue
                
                item = {
                    'name': os.path.basename(unquote(href_text.rstrip('/'))),
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
                
                # Filter out system files and directories
                if not self._should_show_item(item['name'], item['is_dir']):
                    logger.info(f"Filtering out: {item['name']} (is_dir: {item['is_dir']})")
                    continue
                
                logger.info(f"Including item: {item['name']} (is_dir: {item['is_dir']}, size: {item['size']})")
                items.append(item)
                
        except Exception as e:
            logger.error(f"Error parsing PROPFIND response: {e}")
        
        logger.info(f"Total items returned: {len(items)}")
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
        """Upload a file in chunks with progress callback"""
        try:
            file_size = os.path.getsize(local_path)
            url = urljoin(self.url, quote(remote_path))
            
            # For files smaller than chunk size, upload directly
            if file_size <= self.chunk_size:
                with open(local_path, 'rb') as f:
                    if progress_callback:
                        progress_callback(0, file_size)  # Start progress
                    response = self.session.put(url, data=f)
                    if progress_callback:
                        progress_callback(file_size, file_size)  # Complete progress
                    return response.status_code in [200, 201, 204], ""
            
            # For larger files, use chunked upload with better progress tracking
            bytes_uploaded = 0
            
            with open(local_path, 'rb') as f:
                # Create a custom file-like object that tracks progress
                class ProgressFile:
                    def __init__(self, file_obj, total_size, callback):
                        self.file_obj = file_obj
                        self.total_size = total_size
                        self.callback = callback
                        self.bytes_read = 0
                    
                    def read(self, size=-1):
                        data = self.file_obj.read(size)
                        if data and self.callback:
                            self.bytes_read += len(data)
                            self.callback(self.bytes_read, self.total_size)
                        return data
                    
                    def __len__(self):
                        return self.total_size
                    
                    def seek(self, pos, whence=0):
                        result = self.file_obj.seek(pos, whence)
                        if whence == 0:  # SEEK_SET
                            self.bytes_read = pos
                        elif whence == 1:  # SEEK_CUR
                            self.bytes_read += pos
                        elif whence == 2:  # SEEK_END
                            self.bytes_read = self.total_size + pos
                        return result
                    
                    def tell(self):
                        return self.file_obj.tell()
                
                progress_file = ProgressFile(f, file_size, progress_callback)
                if progress_callback:
                    progress_callback(0, file_size)  # Initialize progress
                
                response = self.session.put(url, data=progress_file)
                
            return response.status_code in [200, 201, 204], ""
            
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
                 monitor_subdirs: bool = True):
        """
        Initialize file monitor with configuration.
        
        Args:
            extensions: List of file extensions to monitor (e.g., ['raw', 'mzML'])
            file_queue: Thread-safe queue for passing files to processor
            monitor_subdirs: Whether to monitor subdirectories recursively
        """
        # Normalize extensions to lowercase with leading dots
        self.extensions = [ext.lower() if ext.startswith('.') else f'.{ext.lower()}' 
                          for ext in extensions]
        self.file_queue = file_queue
        self.monitor_subdirs = monitor_subdirs
        self.pending_files = {}  # Track files being written with timestamps
        
        # Log configuration for debugging
        logger.info(f"FileMonitorHandler initialized with extensions: {self.extensions}")
        logger.info(f"Monitor subdirectories: {monitor_subdirs}")
        
    def on_created(self, event):
        """Handle file creation events."""
        if not event.is_directory:
            self._handle_file(event.src_path)
    
    def on_modified(self, event):
        """Handle file modification events."""
        if not event.is_directory:
            self._handle_file(event.src_path)
    
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
                        # File is stable, add to queue
                        self.file_queue.put(filepath)
                        del self.pending_files[filepath]
                        logger.info(f"File queued for transfer: {filepath}")
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
                    import threading
                    def delayed_check():
                        import time
                        time.sleep(1.5)  # Reduced from 3 to 1.5 seconds for faster response
                        if filepath in self.pending_files:
                            try:
                                current_size = os.path.getsize(filepath)
                                stored_size, _ = self.pending_files[filepath]
                                if current_size == stored_size:
                                    # File hasn't changed, queue it
                                    self.file_queue.put(filepath)
                                    del self.pending_files[filepath]
                                    logger.info(f"File queued for transfer after stability check: {filepath}")
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
    progress_update = pyqtSignal(str, int, int)      # filename, bytes_transferred, total_bytes
    status_update = pyqtSignal(str, str, str)        # filename, status_message, filepath
    transfer_complete = pyqtSignal(str, bool, str)   # filename, success, result_message
    conflict_detected = pyqtSignal(str, dict, str)   # filepath, remote_info, local_checksum
    conflict_resolution_needed = pyqtSignal(str, str, str, dict)  # filename, filepath, remote_path, conflict_details
    
    def __init__(self, file_queue: queue.Queue):
        """
        Initialize file processor thread.
        
        Args:
            file_queue: Thread-safe queue containing files to process
        """
        super().__init__()
        self.file_queue = file_queue
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
        Calculate file checksum for integrity verification.
        
        Uses chunked reading to handle large files efficiently without
        loading entire file into memory.
        
        Args:
            filepath: Path to file to checksum
            algorithm: Hash algorithm to use (default: sha256)
            chunk_size: Bytes to read per chunk (default: 256KB)
            
        Returns:
            Hexadecimal checksum string
        """
        if chunk_size is None:
            chunk_size = 256 * 1024  # 256KB chunks - optimal balance of speed and memory
        
        hash_obj = hashlib.new(algorithm)
        with open(filepath, 'rb') as f:
            while chunk := f.read(chunk_size):
                hash_obj.update(chunk)
        return hash_obj.hexdigest()
    
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
                import tempfile
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
        """Compare local and remote files to detect conflicts
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
            'etag_match': False
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
                # Even with size mismatch, check dates for user decision
                return self._check_file_dates(comparison_details)
        except Exception as e:
            logger.warning(f"Could not compare file sizes: {e}")
            
        # Compare ETags if available (may contain checksum)
        remote_etag = remote_info.get('etag')
        stored_checksum = None
        
        # Try to get stored checksum first
        try:
            stored_checksum = self.webdav_client.get_stored_checksum(remote_info.get('path', ''))
            if stored_checksum:
                logger.debug(f"Found stored checksum: {stored_checksum}")
                if stored_checksum.lower() == local_checksum.lower():
                    logger.info(f"Files match via stored checksum")
                    comparison_details['stored_checksum_match'] = True
                    return 'identical', comparison_details
                else:
                    logger.info(f"Files differ via stored checksum")
                    comparison_details['stored_checksum_match'] = False
                    comparison_details['remote_checksum'] = stored_checksum
                    comparison_details['local_checksum'] = local_checksum
                    return self._check_file_dates(comparison_details)
        except Exception as e:
            logger.debug(f"Could not retrieve stored checksum: {e}")
            
        if remote_etag:
            # Some servers include MD5 or SHA256 in ETags
            # Clean ETag (remove quotes and weak indicators)
            clean_etag = remote_etag.strip('"').replace('W/', '')
            
            # Check if ETag matches our checksum
            if clean_etag.lower() == local_checksum.lower():
                logger.info(f"Files match via ETag comparison")
                comparison_details['etag_match'] = True
                return 'identical', comparison_details
            elif len(clean_etag) == len(local_checksum):
                # Same length suggests same hash algorithm but different content
                logger.info(f"Files differ via ETag comparison")
                comparison_details['etag_match'] = False
                return self._check_file_dates(comparison_details)
                
        # If we can't determine by content, check dates
        logger.info(f"Cannot determine file similarity by content, checking dates")
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
        while self.running:
            try:
                # Get file from queue (timeout allows checking self.running)
                file_item = self.file_queue.get(timeout=1)
                
                if self.webdav_client:
                    # Handle both string paths and dict objects with resolution info
                    if isinstance(file_item, dict):
                        self.process_file_with_resolution(file_item)
                    else:
                        self.process_file(file_item)
                else:
                    logger.warning("No WebDAV client configured")
                    
            except queue.Empty:
                continue
            except Exception as e:
                logger.error(f"Error in processor: {e}")
    
    def process_file_with_resolution(self, file_item: dict):
        """Process a file that already has conflict resolution"""
        filepath = file_item['filepath']
        filename = file_item['filename']
        remote_path = file_item['remote_path']
        resolution = file_item['resolution']
        new_name = file_item.get('new_name')
        
        try:
            # Apply resolution
            if resolution == 'skip':
                self.transfer_complete.emit(filename, True, "Skipped due to user choice")
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
            self.transfer_complete.emit(filename, False, f"Error: {str(e)}")
    
    def upload_file(self, filepath: str, remote_path: str, filename: str):
        """Upload file to remote path"""
        try:
            # Calculate checksum for verification
            self.status_update.emit(filename, "Calculating checksum...", filepath)
            local_checksum = self.calculate_checksum(filepath)
            
            # Create remote directory if needed
            remote_dir = os.path.dirname(remote_path)
            if remote_dir and remote_dir != '/':
                logger.info(f"Creating remote directory: {remote_dir}")
                self.webdav_client.create_directory(remote_dir)
            
            # Upload file
            self.status_update.emit(filename, "Uploading...", filepath)
            
            def progress_callback(current, total):
                self.progress_update.emit(filename, current, total)
            
            success, error = self.webdav_client.upload_file_chunked(
                filepath, remote_path, progress_callback
            )
            
            if success:
                # Store checksum for future reference
                try:
                    self.webdav_client.store_checksum(remote_path, local_checksum)
                except Exception as e:
                    logger.warning(f"Failed to store checksum for {remote_path}: {e}")
                
                # Verify upload if enabled
                if hasattr(self, 'verify_uploads') and self.verify_uploads:
                    self.status_update.emit(filename, "Verifying upload...", filepath)
                    if self.verify_uploaded_file(filepath, remote_path, local_checksum):
                        self.transfer_complete.emit(
                            filename, True, 
                            f"Upload verified successfully (checksum: {local_checksum[:8]}...)"
                        )
                    else:
                        self.transfer_complete.emit(
                            filename, False, "Upload verification failed - checksums don't match"
                        )
                else:
                    self.transfer_complete.emit(
                        filename, True, 
                        f"Uploaded successfully (checksum: {local_checksum[:8]}...)"
                    )
            else:
                self.transfer_complete.emit(filename, False, f"Upload failed: {error}")
                
        except Exception as e:
            logger.error(f"Error uploading file {filepath}: {e}")
            self.transfer_complete.emit(filename, False, f"Error: {str(e)}")
    
    def process_file(self, filepath: str):
        """Process a single file with conflict detection"""
        filename = os.path.basename(filepath)
        
        try:
            # Calculate local checksum
            self.status_update.emit(filename, "Calculating checksum...", filepath)
            local_checksum = self.calculate_checksum(filepath)
            
            # Determine remote path
            if self.preserve_structure and self.local_base_path:
                rel_path = os.path.relpath(filepath, self.local_base_path)
                remote_path = f"{self.remote_base_path}/{rel_path}".replace('\\', '/')
                
                # Create remote directories if needed
                remote_dir = os.path.dirname(remote_path)
                if remote_dir != self.remote_base_path:
                    self.webdav_client.create_directory(remote_dir)
            else:
                remote_path = f"{self.remote_base_path}/{filename}"
            
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
                    filename, True, 
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
                        filename, True, "Skipped due to conflict"
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
            self.transfer_complete.emit(filename, False, f"Error: {str(e)}")
    
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
        
        for item in items:
            if item['is_dir']:
                tree_item = QTreeWidgetItem(self.tree, [
                    item['name'],
                    "Folder",
                    ""
                ])
                tree_item.setData(0, Qt.ItemDataRole.UserRole, item['path'])
            else:
                size_mb = item['size'] / (1024 * 1024)
                tree_item = QTreeWidgetItem(self.tree, [
                    item['name'],
                    "File",
                    f"{size_mb:.2f} MB"
                ])
                tree_item.setData(0, Qt.ItemDataRole.UserRole, item['path'])
    
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
        
        # Core application components
        self.file_queue = queue.Queue()                    # Thread-safe queue for file processing
        self.file_processor = FileProcessor(self.file_queue)  # Background processing thread
        self.monitor_handler = None                        # File system event handler
        self.observer = None                              # Watchdog observer for file monitoring
        self.webdav_client = None                         # WebDAV client instance
        self.transfer_rows = {}                           # Track UI table rows for updates
        
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
        
        self.rescan_btn = QPushButton("Rescan Files")
        self.rescan_btn.clicked.connect(self.manual_rescan)
        self.rescan_btn.setEnabled(False)  # Only enabled when monitoring
        self.rescan_btn.setToolTip("Manually scan directory for files to upload")
        control_layout.addWidget(self.rescan_btn)

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
        """Show about dialog"""
        QMessageBox.about(self, "About PanoramaBridge", 
                         "PanoramaBridge v1.0\n\n"
                         "A file monitoring and WebDAV transfer application\n"
                         "for syncing files to Panorama servers.\n\n"
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
        self.extensions_input.setPlaceholderText("raw, sld, csv")
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
        
        self.preserve_structure_check = QCheckBox("Preserve directory structure on remote")
        self.preserve_structure_check.setChecked(True)
        adv_layout.addWidget(self.preserve_structure_check, 1, 0, 1, 2)
        
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
            self.start_btn.setText("Start Monitoring")
            self.rescan_btn.setEnabled(False)
            self.status_label.setText("Not monitoring")
            self.status_label.setStyleSheet("font-weight: bold; color: red;")
            self.log_text.append(f"{datetime.now().strftime('%H:%M:%S')} - Stopped monitoring")
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
            self.file_processor.preserve_structure = self.preserve_structure_check.isChecked()
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
                self.subdirs_check.isChecked()
            )
            
            self.observer = Observer()
            self.observer.schedule(
                self.monitor_handler,
                directory,
                recursive=self.subdirs_check.isChecked()
            )
            
            self.observer.start()
            
            # Scan for existing files in the directory
            self.scan_existing_files(directory, extensions, self.subdirs_check.isChecked())
            
            self.start_btn.setText("Stop Monitoring")
            self.rescan_btn.setEnabled(True)
            self.status_label.setText("Monitoring active")
            self.status_label.setStyleSheet("font-weight: bold; color: green;")
            
            self.log_text.append(f"{datetime.now().strftime('%H:%M:%S')} - Started monitoring {directory}")
            self.log_text.append(f"Extensions: {', '.join(extensions)}")
    
    def manual_rescan(self):
        """Manually rescan directory for files"""
        if not self.observer:
            QMessageBox.information(self, "Info", "Monitoring is not active")
            return
            
        directory = self.dir_input.text()
        extensions = [e.strip() for e in self.extensions_input.text().split(',') if e.strip()]
        
        self.log_text.append(f"{datetime.now().strftime('%H:%M:%S')} - Manual rescan requested")
        self.scan_existing_files(directory, extensions, self.subdirs_check.isChecked())
    
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
                            # Add existing file to queue
                            self.file_queue.put(filepath)
                            files_found += 1
                            logger.info(f"Queued existing file: {filepath}")
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
                                # Add existing file to queue
                                self.file_queue.put(filepath)
                                files_found += 1
                                logger.info(f"Queued existing file: {filepath}")
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

    def update_queue_size(self):
        """Update queue size display"""
        size = self.file_queue.qsize()
        self.queue_label.setText(f"{size} files")
    
    @pyqtSlot(str, str, str)
    def on_status_update(self, filename: str, status: str, filepath: str):
        """Handle status updates from processor"""
        if filename not in self.transfer_rows:
            # Add new row at the bottom (chronological order - oldest first, newest last)
            row = self.transfer_table.rowCount()
            self.transfer_table.insertRow(row)
            
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
            
            self.transfer_table.setItem(row, 0, QTableWidgetItem(filename))
            self.transfer_table.setItem(row, 1, QTableWidgetItem(display_path))
            self.transfer_table.setItem(row, 2, QTableWidgetItem(status))
            
            progress_bar = QProgressBar()
            progress_bar.setMinimum(0)
            progress_bar.setMaximum(100)  # Always use percentage for consistency
            progress_bar.setValue(0)
            self.transfer_table.setCellWidget(row, 3, progress_bar)
            
            self.transfer_table.setItem(row, 4, QTableWidgetItem(""))
            
            self.transfer_rows[filename] = row
        else:
            # Update existing row
            row = self.transfer_rows[filename]
            self.transfer_table.item(row, 2).setText(status)
    
    @pyqtSlot(str, int, int)
    def on_progress_update(self, filename: str, current: int, total: int):
        """Handle progress updates from processor"""
        if filename in self.transfer_rows:
            row = self.transfer_rows[filename]
            progress_bar = self.transfer_table.cellWidget(row, 3)
            if progress_bar:
                # Always use percentage (0-100) for consistent progress bar display
                if total > 0:
                    percentage = int((current / total) * 100)
                    progress_bar.setValue(min(percentage, 100))  # Ensure it doesn't exceed 100
                else:
                    progress_bar.setValue(0)
    
    @pyqtSlot(str, bool, str)
    def on_transfer_complete(self, filename: str, success: bool, message: str):
        """Handle transfer completion"""
        if filename in self.transfer_rows:
            row = self.transfer_rows[filename]
            
            status = "Complete" if success else "Failed"
            self.transfer_table.item(row, 2).setText(status)
            self.transfer_table.item(row, 4).setText(message)
            
            # Update progress bar - ensure it shows 100% when complete
            progress_bar = self.transfer_table.cellWidget(row, 3)
            if progress_bar:
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
            
            # Re-queue the file for processing with the resolution
            self.file_queue.put({
                'filepath': filepath,
                'filename': filename,
                'remote_path': remote_path,
                'resolution': resolution,
                'new_name': new_name
            })
            
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
            # User cancelled - skip this file
            timestamp = datetime.now().strftime('%H:%M:%S')
            self.log_text.append(f"{timestamp} - Conflict resolution cancelled for {filename}: skipped")
    
    def clear_completed_transfers(self):
        """Clear completed transfers from the table"""
        rows_to_remove = []
        
        for filename, row in self.transfer_rows.items():
            status_item = self.transfer_table.item(row, 2)  # Status is now column 2
            if status_item and status_item.text() in ["Complete", "Failed"]:
                rows_to_remove.append((row, filename))
        
        # Sort in reverse order to remove from bottom up
        rows_to_remove.sort(reverse=True)
        
        for row, filename in rows_to_remove:
            self.transfer_table.removeRow(row)
            del self.transfer_rows[filename]
            
            # Update remaining row numbers
            for fname, r in self.transfer_rows.items():
                if r > row:
                    self.transfer_rows[fname] = r - 1
    
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
            "preserve_structure": self.preserve_structure_check.isChecked(),
            "webdav_url": self.url_input.text(),
            "webdav_username": self.username_input.text() if not self.save_creds_check.isChecked() else "",
            "webdav_auth_type": self.auth_combo.currentText(),
            "remote_path": self.remote_path_input.text(),
            "chunk_size_mb": self.chunk_spin.value(),
            "verify_uploads": self.verify_uploads_check.isChecked(),
            "save_credentials": self.save_creds_check.isChecked(),
            "conflict_resolution": self.get_conflict_resolution_setting()
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
        self.preserve_structure_check.setChecked(self.config.get("preserve_structure", True))
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
    
    window = MainWindow()
    window.show()
    
    sys.exit(app.exec())


if __name__ == '__main__':
    main()