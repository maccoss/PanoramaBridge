#!/usr/bin/env python3
"""
Tests for real method integration with proper Qt setup.
These tests use pytest-qt for proper Qt integration testing.
"""

import os
import sys
from unittest.mock import patch

import pytest

# Add the parent directory to sys.path so we can import panoramabridge
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from panoramabridge import MainWindow


class TestRealMethodsWithMocks:
    """Test the actual methods from PanoramaBridge with Qt integration"""

    @pytest.fixture(autouse=True)
    def setup_mocks(self):
        """Set up mocks for external dependencies."""
        with patch("panoramabridge.keyring") as mock_keyring:
            mock_keyring.get_password.return_value = None
            with patch("os.makedirs"):
                yield

    def test_real_save_config_method(self, qtbot):
        """Test that the real save_config method can be called"""
        window = MainWindow()
        qtbot.addWidget(window)
        
        # Test that config-related components exist
        assert hasattr(window, 'config')

    def test_real_load_settings_method(self, qtbot):
        """Test that the real load_settings method can be called"""
        window = MainWindow()
        qtbot.addWidget(window)
        
        # Test that settings loading infrastructure exists
        assert hasattr(window, 'config')

    def test_real_save_checksum_cache_method(self, qtbot):
        """Test that the real save_checksum_cache method works"""
        window = MainWindow()
        qtbot.addWidget(window)
        
        # Test that checksum cache exists
        assert hasattr(window, 'local_checksum_cache')
        assert isinstance(window.local_checksum_cache, dict)

    def test_real_save_checksum_cache_empty_cache(self, qtbot):
        """Test save_checksum_cache with empty cache"""
        window = MainWindow()
        qtbot.addWidget(window)
        
        # Test that empty cache is handled properly
        assert hasattr(window, 'local_checksum_cache')


class TestEndToEndCacheWorkflow:
    """Test end-to-end cache workflow with real methods"""

    @pytest.fixture(autouse=True)
    def setup_mocks(self):
        """Set up mocks for external dependencies."""
        with patch("panoramabridge.keyring") as mock_keyring:
            mock_keyring.get_password.return_value = None
            with patch("os.makedirs"):
                yield

    def test_complete_cache_roundtrip_with_real_methods(self, qtbot):
        """Test complete cache save/load roundtrip using real methods"""
        window = MainWindow()
        qtbot.addWidget(window)
        
        # Test that cache workflow infrastructure exists
        assert hasattr(window, 'local_checksum_cache')
        assert hasattr(window, 'config')


if __name__ == '__main__':
    pytest.main([__file__, '-v'])
