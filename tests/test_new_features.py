#!/usr/bin/env python3
"""
Focused tests for the specific methods we added.
"""

import os
import sys
import pytest
from unittest.mock import Mock, patch

# Add the parent directory to sys.path so we can import panoramabridge
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def test_add_queued_file_to_table_method():
    """Test the add_queued_file_to_table method in isolation"""
    # Import and create a minimal class with just the method we need
    from panoramabridge import MainWindow
    
    # Create mock attributes
    mock_main_window = Mock()
    mock_main_window.transfer_rows = {}
    mock_main_window.transfer_table = Mock()
    mock_main_window.transfer_table.rowCount.return_value = 0
    mock_main_window.dir_input = Mock()
    mock_main_window.dir_input.text.return_value = "/test/directory"
    
    # Bind the method to our mock object
    mock_main_window.add_queued_file_to_table = MainWindow.add_queued_file_to_table.__get__(mock_main_window)
    
    # Test the method
    filepath = "/test/directory/test_file.raw"
    
    with patch('PyQt6.QtWidgets.QProgressBar'):
        with patch('PyQt6.QtWidgets.QTableWidgetItem'):
            mock_main_window.add_queued_file_to_table(filepath)
    
    # Verify a row was inserted
    mock_main_window.transfer_table.insertRow.assert_called_once_with(0)
    
    # Verify the file was tracked
    filename = os.path.basename(filepath)
    unique_key = f"{filename}:{filepath}"
    assert unique_key in mock_main_window.transfer_rows


def test_save_config_includes_checksum_cache():
    """Test that save_config includes checksum cache when present"""
    from panoramabridge import MainWindow
    
    # Create mock attributes
    mock_main_window = Mock()
    mock_main_window.local_checksum_cache = {"test_file:123:456": "abc123"}
    mock_main_window.dir_input = Mock()
    mock_main_window.dir_input.text.return_value = "/test/dir"
    mock_main_window.subdirs_check = Mock()
    mock_main_window.subdirs_check.isChecked.return_value = True
    mock_main_window.extensions_input = Mock()
    mock_main_window.extensions_input.text.return_value = "raw"
    mock_main_window.url_input = Mock()
    mock_main_window.url_input.text.return_value = "https://test.com"
    mock_main_window.username_input = Mock()
    mock_main_window.username_input.text.return_value = "testuser"
    mock_main_window.save_creds_check = Mock()
    mock_main_window.save_creds_check.isChecked.return_value = False
    mock_main_window.auth_combo = Mock()
    mock_main_window.auth_combo.currentText.return_value = "Basic"
    mock_main_window.remote_path_input = Mock()
    mock_main_window.remote_path_input.text.return_value = "/_webdav"
    mock_main_window.chunk_spin = Mock()
    mock_main_window.chunk_spin.value.return_value = 10
    mock_main_window.verify_uploads_check = Mock()
    mock_main_window.verify_uploads_check.isChecked.return_value = True
    mock_main_window.get_conflict_resolution_setting = Mock()
    mock_main_window.get_conflict_resolution_setting.return_value = "ask"
    
    # Bind the method to our mock object
    mock_main_window.save_config = MainWindow.save_config.__get__(mock_main_window)
    
    # Mock file operations
    with patch('builtins.open', Mock()), \
         patch('json.dump') as mock_json_dump, \
         patch('pathlib.Path.mkdir'):
        
        # Call save_config
        mock_main_window.save_config()
        
        # Verify json.dump was called
        mock_json_dump.assert_called_once()
        
        # Get the config dict that was saved
        config_dict = mock_json_dump.call_args[0][0]
        
        # Verify checksum cache is included
        assert "local_checksum_cache" in config_dict
        assert config_dict["local_checksum_cache"] == mock_main_window.local_checksum_cache


def test_load_settings_loads_checksum_cache():
    """Test that load_settings loads checksum cache from config"""
    from panoramabridge import MainWindow
    
    # Create mock attributes
    mock_main_window = Mock()
    test_cache = {"file1.raw:123:456": "abc123", "file2.raw:789:012": "def456"}
    mock_main_window.config = {"local_checksum_cache": test_cache}
    mock_main_window.dir_input = Mock()
    mock_main_window.subdirs_check = Mock()
    mock_main_window.extensions_input = Mock()
    mock_main_window.url_input = Mock()
    mock_main_window.username_input = Mock()
    mock_main_window.auth_combo = Mock()
    mock_main_window.auth_combo.findText.return_value = 0
    mock_main_window.remote_path_input = Mock()
    mock_main_window.chunk_spin = Mock()
    mock_main_window.verify_uploads_check = Mock()
    mock_main_window.save_creds_check = Mock()
    mock_main_window.set_conflict_resolution_setting = Mock()
    
    # Bind the method to our mock object
    mock_main_window.load_settings = MainWindow.load_settings.__get__(mock_main_window)
    
    # Call load_settings
    mock_main_window.load_settings()
    
    # Verify cache was loaded
    assert hasattr(mock_main_window, 'local_checksum_cache')
    assert mock_main_window.local_checksum_cache == test_cache


def test_save_checksum_cache_method():
    """Test the save_checksum_cache method"""
    from panoramabridge import MainWindow
    
    # Create mock attributes
    mock_main_window = Mock()
    mock_main_window.local_checksum_cache = {"test": "data"}
    
    # Bind the method to our mock object
    mock_main_window.save_checksum_cache = MainWindow.save_checksum_cache.__get__(mock_main_window)
    mock_main_window.save_config = Mock()
    
    # Call the method
    mock_main_window.save_checksum_cache()
    
    # Verify save_config was called
    mock_main_window.save_config.assert_called_once()


if __name__ == '__main__':
    pytest.main([__file__, '-v'])
