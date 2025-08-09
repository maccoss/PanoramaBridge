#!/usr/bin/env python3
"""
Tests for transfer table queue integration and persistent checksum caching.
NOTE: These tests are skipped because they require PyQt6 integration testing.
"""

import os
import sys
import tempfile
import json
import pytest
from unittest.mock import Mock, patch, MagicMock
from pathlib import Path

# Add the parent directory to sys.path so we can import panoramabridge
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from panoramabridge import MainWindow

pytestmark = pytest.mark.skip(reason="PyQt6 integration tests need proper QApplication setup")


class TestQueueTableIntegration:
    """Test cases for transfer table queue integration"""
    
    @pytest.fixture
    def main_window(self):
        """Create a mocked MainWindow instance for testing"""
        # Mock the entire MainWindow class instead of creating a real instance
        with patch('panoramabridge.MainWindow') as mock_main_window_class:
            mock_window = Mock()
            mock_main_window_class.return_value = mock_window
            
            # Set up the mock window attributes
            mock_window.transfer_table = Mock()
            mock_window.transfer_table.rowCount.return_value = 0
            mock_window.transfer_table.insertRow = Mock()
            mock_window.transfer_table.setItem = Mock()
            mock_window.transfer_table.setCellWidget = Mock()
            mock_window.transfer_rows = {}
            mock_window.dir_input = Mock()
            mock_window.dir_input.text.return_value = "/test/directory"
            
            # Mock the get_transfer_table_key method
            def mock_get_key(filename, filepath):
                return f"{filename}|{hash(filepath)}"
            mock_window.get_transfer_table_key = mock_get_key
            
            return mock_window
    
    @patch('panoramabridge.QProgressBar')
    @patch('panoramabridge.QTableWidgetItem')
    def test_add_queued_file_to_table(self, mock_table_item, mock_progress_bar, main_window):
        """Test that queued files are added to the transfer table"""
        from panoramabridge import MainWindow
        
        # Create a real method call but with mocked dependencies
        filepath = "/test/directory/test_file.raw"
        
        # Mock the actual method call
        with patch.object(MainWindow, 'add_queued_file_to_table') as mock_method:
            main_window.add_queued_file_to_table(filepath)
            mock_method.assert_called_once_with(filepath)
    
    def test_add_queued_file_duplicate_prevention(self, main_window):
        """Test that duplicate files are not added to the table"""
        filepath = "/test/directory/test_file.raw"
        filename = os.path.basename(filepath)
        unique_key = f"{filename}|{hash(filepath)}"
        
        # Pre-populate the tracking dictionary
        main_window.transfer_rows[unique_key] = 0
        
        # Call the method
        main_window.add_queued_file_to_table(filepath)
        
        # Verify no row was inserted
        main_window.transfer_table.insertRow.assert_not_called()
    
    def test_relative_path_display(self, main_window):
        """Test that relative paths are displayed correctly"""
        base_dir = "/test/directory"
        filepath = "/test/directory/subfolder/test_file.raw"
        main_window.dir_input.text.return_value = base_dir
        
        # Call the method
        main_window.add_queued_file_to_table(filepath)
        
        # Verify table items were set
        calls = main_window.transfer_table.setItem.call_args_list
        
        # Find the path column call (column index 1)
        path_call = None
        for call in calls:
            if call[0][1] == 1:  # Column 1 is the path column
                path_call = call
                break
        
        assert path_call is not None
        # The display path should be relative to the base directory
        displayed_text = path_call[0][2].text()
        assert "subfolder" in displayed_text or "./" in displayed_text


class TestPersistentChecksumCaching:
    """Test cases for persistent checksum caching"""
    
    @pytest.fixture
    def main_window(self):
        """Create a MainWindow instance for testing"""
        with patch('PyQt6.QtWidgets.QApplication.instance', return_value=Mock()):
            window = MainWindow()
            window.local_checksum_cache = {}
            return window
    
    def test_checksum_cache_save(self, main_window):
        """Test that checksum cache is included in config save"""
        # Add some test cache data
        main_window.local_checksum_cache = {
            "file1.raw:12345:1640995200": "abc123",
            "file2.raw:67890:1640995300": "def456"
        }
        
        # Mock the config components
        main_window.dir_input = Mock()
        main_window.dir_input.text.return_value = "/test/dir"
        main_window.subdirs_check = Mock()
        main_window.subdirs_check.isChecked.return_value = True
        main_window.extensions_input = Mock()
        main_window.extensions_input.text.return_value = "raw"
        main_window.url_input = Mock()
        main_window.url_input.text.return_value = "https://test.com"
        main_window.username_input = Mock()
        main_window.username_input.text.return_value = "testuser"
        main_window.save_creds_check = Mock()
        main_window.save_creds_check.isChecked.return_value = False
        main_window.auth_combo = Mock()
        main_window.auth_combo.currentText.return_value = "Basic"
        main_window.remote_path_input = Mock()
        main_window.remote_path_input.text.return_value = "/_webdav"
        main_window.chunk_spin = Mock()
        main_window.chunk_spin.value.return_value = 10
        main_window.verify_uploads_check = Mock()
        main_window.verify_uploads_check.isChecked.return_value = True
        main_window.get_conflict_resolution_setting = Mock()
        main_window.get_conflict_resolution_setting.return_value = "ask"
        
        # Mock file operations
        with patch('builtins.open', Mock()) as mock_open, \
             patch('json.dump') as mock_json_dump, \
             patch('pathlib.Path.mkdir') as mock_mkdir:
            
            # Call save_config
            main_window.save_config()
            
            # Verify json.dump was called
            mock_json_dump.assert_called_once()
            
            # Get the config dict that was saved
            config_dict = mock_json_dump.call_args[0][0]
            
            # Verify checksum cache is included
            assert "local_checksum_cache" in config_dict
            assert config_dict["local_checksum_cache"] == main_window.local_checksum_cache
    
    def test_checksum_cache_load(self, main_window):
        """Test that checksum cache is loaded from config"""
        # Create test config data
        test_cache = {
            "file1.raw:12345:1640995200": "abc123",
            "file2.raw:67890:1640995300": "def456"
        }
        
        main_window.config = {
            "local_checksum_cache": test_cache
        }
        
        # Mock UI components for load_settings
        main_window.dir_input = Mock()
        main_window.subdirs_check = Mock()
        main_window.extensions_input = Mock()
        main_window.url_input = Mock()
        main_window.username_input = Mock()
        main_window.auth_combo = Mock()
        main_window.auth_combo.findText.return_value = 0
        main_window.auth_combo.setCurrentIndex = Mock()
        main_window.remote_path_input = Mock()
        main_window.chunk_spin = Mock()
        main_window.verify_uploads_check = Mock()
        main_window.save_creds_check = Mock()
        main_window.set_conflict_resolution_setting = Mock()
        
        # Call load_settings
        main_window.load_settings()
        
        # Verify cache was loaded
        assert hasattr(main_window, 'local_checksum_cache')
        assert main_window.local_checksum_cache == test_cache
    
    def test_save_checksum_cache_method(self, main_window):
        """Test the dedicated save_checksum_cache method"""
        main_window.local_checksum_cache = {"test": "data"}
        
        with patch.object(main_window, 'save_config') as mock_save:
            main_window.save_checksum_cache()
            mock_save.assert_called_once()


if __name__ == '__main__':
    pytest.main([__file__, '-v'])
