#!/usr/bin/env python3
"""
Integration tests that use the actual PanoramaBridge methods with mocked Qt components.
"""

import os
import sys
import json
import tempfile
import pytest
from unittest.mock import Mock, patch, MagicMock

# Add the parent directory to sys.path
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# Import the module but don't instantiate
import panoramabridge


class TestRealMethodsWithMocks:
    """Test the actual methods from PanoramaBridge with mocked Qt components"""
    
    @pytest.fixture
    def mock_window_for_save_config(self):
        """Create a mock window for testing save_config method"""
        mock_window = MagicMock()
        
        # Mock all the UI components that save_config accesses
        mock_window.dir_input.text.return_value = "/test/directory"
        mock_window.subdirs_check.isChecked.return_value = True
        mock_window.extensions_input.text.return_value = "raw,sld"
        mock_window.url_input.text.return_value = "https://panoramaweb.org"
        mock_window.username_input.text.return_value = "testuser"
        mock_window.save_creds_check.isChecked.return_value = False
        mock_window.auth_combo.currentText.return_value = "Basic"
        mock_window.remote_path_input.text.return_value = "/_webdav/test"
        mock_window.chunk_spin.value.return_value = 15
        mock_window.verify_uploads_check.isChecked.return_value = True
        mock_window.get_conflict_resolution_setting.return_value = "overwrite"
        
        # Set up checksum cache
        mock_window.local_checksum_cache = {
            "test1.raw:12345:1640995200": "checksum1",
            "test2.raw:67890:1640995300": "checksum2"
        }
        
        # Bind the real method
        mock_window.save_config = panoramabridge.MainWindow.save_config.__get__(mock_window)
        
        return mock_window
    
    @pytest.fixture
    def mock_window_for_load_settings(self):
        """Create a mock window for testing load_settings method"""
        mock_window = MagicMock()
        
        # Mock UI components that load_settings modifies
        mock_window.dir_input = Mock()
        mock_window.subdirs_check = Mock()
        mock_window.extensions_input = Mock()
        mock_window.url_input = Mock()
        mock_window.username_input = Mock()
        mock_window.auth_combo = Mock()
        mock_window.auth_combo.findText.return_value = 1
        mock_window.remote_path_input = Mock()
        mock_window.chunk_spin = Mock()
        mock_window.verify_uploads_check = Mock()
        mock_window.save_creds_check = Mock()
        mock_window.set_conflict_resolution_setting = Mock()
        
        # Set up test config
        mock_window.config = {
            "local_directory": "/loaded/directory",
            "monitor_subdirs": False,
            "extensions": "raw,wiff",
            "webdav_url": "https://loaded.com",
            "webdav_username": "loadeduser",
            "webdav_auth_type": "Digest",
            "remote_path": "/_webdav/loaded",
            "chunk_size_mb": 20,
            "verify_uploads": False,
            "save_credentials": True,
            "conflict_resolution": "skip",
            "local_checksum_cache": {
                "loaded1.raw:111:1640995100": "loaded_checksum1",
                "loaded2.raw:222:1640995200": "loaded_checksum2",
                "loaded3.raw:333:1640995300": "loaded_checksum3"
            }
        }
        
        # Bind the real method
        mock_window.load_settings = panoramabridge.MainWindow.load_settings.__get__(mock_window)
        
        return mock_window
    
    @patch('pathlib.Path.mkdir')
    @patch('json.dump')
    @patch('builtins.open')
    def test_real_save_config_method(self, mock_open, mock_json_dump, mock_mkdir, mock_window_for_save_config):
        """Test the real save_config method with mocked file operations"""
        # Call the real method
        mock_window_for_save_config.save_config()
        
        # Verify directory creation was attempted
        mock_mkdir.assert_called_once()
        
        # Verify file was opened for writing
        mock_open.assert_called_once()
        
        # Verify JSON dump was called
        mock_json_dump.assert_called_once()
        
        # Get the actual config that would be saved
        saved_config = mock_json_dump.call_args[0][0]
        
        # Verify all expected keys are present
        expected_keys = [
            "local_directory", "monitor_subdirs", "extensions", "preserve_structure",
            "webdav_url", "webdav_username", "webdav_auth_type", "remote_path",
            "chunk_size_mb", "verify_uploads", "save_credentials", "conflict_resolution",
            "local_checksum_cache"
        ]
        
        for key in expected_keys:
            assert key in saved_config, f"Missing key: {key}"
        
        # Verify specific values
        assert saved_config["local_directory"] == "/test/directory"
        assert saved_config["monitor_subdirs"] == True
        assert saved_config["extensions"] == "raw,sld"
        assert saved_config["webdav_url"] == "https://panoramaweb.org"
        assert saved_config["chunk_size_mb"] == 15
        assert saved_config["conflict_resolution"] == "overwrite"
        
        # Verify checksum cache was included correctly
        assert "local_checksum_cache" in saved_config
        assert saved_config["local_checksum_cache"] == mock_window_for_save_config.local_checksum_cache
        assert len(saved_config["local_checksum_cache"]) == 2
    
    def test_real_load_settings_method(self, mock_window_for_load_settings):
        """Test the real load_settings method"""
        # Call the real method
        mock_window_for_load_settings.load_settings()
        
        # Verify UI components were updated with config values
        mock_window_for_load_settings.dir_input.setText.assert_called_with("/loaded/directory")
        mock_window_for_load_settings.subdirs_check.setChecked.assert_called_with(False)
        mock_window_for_load_settings.extensions_input.setText.assert_called_with("raw,wiff")
        mock_window_for_load_settings.url_input.setText.assert_called_with("https://loaded.com")
        mock_window_for_load_settings.username_input.setText.assert_called_with("loadeduser")
        mock_window_for_load_settings.remote_path_input.setText.assert_called_with("/_webdav/loaded")
        mock_window_for_load_settings.chunk_spin.setValue.assert_called_with(20)
        mock_window_for_load_settings.verify_uploads_check.setChecked.assert_called_with(False)
        mock_window_for_load_settings.save_creds_check.setChecked.assert_called_with(True)
        
        # Verify auth combo was set
        mock_window_for_load_settings.auth_combo.findText.assert_called_with("Digest")
        mock_window_for_load_settings.auth_combo.setCurrentIndex.assert_called_with(1)
        
        # Verify conflict resolution was set
        mock_window_for_load_settings.set_conflict_resolution_setting.assert_called_with("skip")
        
        # Verify checksum cache was loaded
        assert hasattr(mock_window_for_load_settings, 'local_checksum_cache')
        expected_cache = {
            "loaded1.raw:111:1640995100": "loaded_checksum1",
            "loaded2.raw:222:1640995200": "loaded_checksum2",
            "loaded3.raw:333:1640995300": "loaded_checksum3"
        }
        assert mock_window_for_load_settings.local_checksum_cache == expected_cache
    
    @patch('pathlib.Path.mkdir')
    @patch('json.dump')
    @patch('builtins.open')
    def test_real_save_checksum_cache_method(self, mock_open, mock_json_dump, mock_mkdir):
        """Test the real save_checksum_cache method"""
        mock_window = MagicMock()
        mock_window.local_checksum_cache = {
            "periodic_save_test.raw:999:1640995999": "periodic_checksum"
        }
        
        # Mock the save_config method to be callable
        mock_window.save_config = panoramabridge.MainWindow.save_config.__get__(mock_window)
        
        # Mock all UI components for save_config
        mock_window.dir_input.text.return_value = "/periodic/test"
        mock_window.subdirs_check.isChecked.return_value = True
        mock_window.extensions_input.text.return_value = "raw"
        mock_window.url_input.text.return_value = "https://periodic.test"
        mock_window.username_input.text.return_value = "periodic_user"
        mock_window.save_creds_check.isChecked.return_value = False
        mock_window.auth_combo.currentText.return_value = "Basic"
        mock_window.remote_path_input.text.return_value = "/_webdav/periodic"
        mock_window.chunk_spin.value.return_value = 10
        mock_window.verify_uploads_check.isChecked.return_value = True
        mock_window.get_conflict_resolution_setting.return_value = "ask"
        
        # Bind the real save_checksum_cache method
        mock_window.save_checksum_cache = panoramabridge.MainWindow.save_checksum_cache.__get__(mock_window)
        
        # Call the real method
        mock_window.save_checksum_cache()
        
        # Verify that save_config was called (which triggers file operations)
        mock_json_dump.assert_called_once()
        
        # Verify the cache was included in the saved config
        saved_config = mock_json_dump.call_args[0][0]
        assert "local_checksum_cache" in saved_config
        assert saved_config["local_checksum_cache"]["periodic_save_test.raw:999:1640995999"] == "periodic_checksum"
    
    def test_real_save_checksum_cache_empty_cache(self):
        """Test save_checksum_cache with empty cache doesn't save"""
        mock_window = MagicMock()
        mock_window.local_checksum_cache = {}
        mock_window.save_config = Mock()
        
        # Bind the real method
        mock_window.save_checksum_cache = panoramabridge.MainWindow.save_checksum_cache.__get__(mock_window)
        
        # Call the method
        mock_window.save_checksum_cache()
        
        # Verify save_config was not called for empty cache
        mock_window.save_config.assert_not_called()


class TestEndToEndCacheWorkflow:
    """Test the complete cache persistence workflow"""
    
    def test_complete_cache_roundtrip_with_real_methods(self):
        """Test saving and loading cache using real methods"""
        with tempfile.NamedTemporaryFile(mode='w', suffix='.json', delete=False) as temp_file:
            temp_config_path = temp_file.name
        
        try:
            # === SAVE PHASE ===
            save_window = MagicMock()
            
            # Set up cache data to save
            save_window.local_checksum_cache = {
                "roundtrip1.raw:1001:1640996001": "roundtrip_checksum1",
                "roundtrip2.raw:1002:1640996002": "roundtrip_checksum2",
                "large_file.raw:7000000000:1640996003": "large_checksum"
            }
            
            # Mock UI components for save_config
            save_window.dir_input.text.return_value = "/roundtrip/test"
            save_window.subdirs_check.isChecked.return_value = True
            save_window.extensions_input.text.return_value = "raw,wiff"
            save_window.url_input.text.return_value = "https://roundtrip.test"
            save_window.username_input.text.return_value = "roundtrip_user"
            save_window.save_creds_check.isChecked.return_value = False
            save_window.auth_combo.currentText.return_value = "Basic"
            save_window.remote_path_input.text.return_value = "/_webdav/roundtrip"
            save_window.chunk_spin.value.return_value = 25
            save_window.verify_uploads_check.isChecked.return_value = True
            save_window.get_conflict_resolution_setting.return_value = "ask"
            
            # Use real save_config method but with our temp file
            with patch('pathlib.Path.home') as mock_home:
                mock_home.return_value.joinpath().joinpath.return_value = temp_config_path
                save_window.save_config = panoramabridge.MainWindow.save_config.__get__(save_window)
                
                with patch('builtins.open', lambda f, m: open(temp_config_path, m)):
                    save_window.save_config()
            
            # === LOAD PHASE ===
            load_window = MagicMock()
            
            # Read the saved config
            with open(temp_config_path, 'r') as f:
                load_window.config = json.load(f)
            
            # Mock UI components for load_settings
            load_window.dir_input = Mock()
            load_window.subdirs_check = Mock()
            load_window.extensions_input = Mock()
            load_window.url_input = Mock()
            load_window.username_input = Mock()
            load_window.auth_combo = Mock()
            load_window.auth_combo.findText.return_value = 0
            load_window.remote_path_input = Mock()
            load_window.chunk_spin = Mock()
            load_window.verify_uploads_check = Mock()
            load_window.save_creds_check = Mock()
            load_window.set_conflict_resolution_setting = Mock()
            
            # Use real load_settings method
            load_window.load_settings = panoramabridge.MainWindow.load_settings.__get__(load_window)
            load_window.load_settings()
            
            # === VERIFY ROUNDTRIP ===
            # Check that cache was properly loaded
            assert hasattr(load_window, 'local_checksum_cache')
            loaded_cache = load_window.local_checksum_cache
            
            assert len(loaded_cache) == 3
            assert loaded_cache["roundtrip1.raw:1001:1640996001"] == "roundtrip_checksum1"
            assert loaded_cache["roundtrip2.raw:1002:1640996002"] == "roundtrip_checksum2"
            assert loaded_cache["large_file.raw:7000000000:1640996003"] == "large_checksum"
            
            # Verify other config was also preserved
            load_window.dir_input.setText.assert_called_with("/roundtrip/test")
            load_window.chunk_spin.setValue.assert_called_with(25)
            load_window.extensions_input.setText.assert_called_with("raw,wiff")
            
        finally:
            # Clean up
            os.unlink(temp_config_path)


if __name__ == '__main__':
    pytest.main([__file__, '-v'])
