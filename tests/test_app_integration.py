"""
Simple application integration tests using pytest-qt.
"""

import os
import sys
from unittest.mock import patch

import pytest

# Import the modules we're testing
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from panoramabridge import MainWindow


class TestMainWindow:
    """Test main application functionality."""

    @pytest.fixture(autouse=True)
    def setup_mocks(self):
        """Set up mocks for external dependencies."""
        with patch("panoramabridge.keyring") as mock_keyring:
            mock_keyring.get_password.return_value = None
            with patch("os.makedirs"):
                yield

    def test_app_initialization(self, qtbot):
        """Test application initialization."""
        window = MainWindow()
        qtbot.addWidget(window)

        # Test that essential components are initialized
        assert hasattr(window, 'upload_history')
        assert hasattr(window, 'file_processor')
        assert hasattr(window, 'webdav_client')

    def test_settings_methods(self, qtbot):
        """Test settings persistence methods."""
        window = MainWindow()
        qtbot.addWidget(window)

        # Test that save_config and load_settings methods exist
        assert hasattr(window, 'save_config')
        assert hasattr(window, 'load_settings')
        assert callable(window.save_config)
        assert callable(window.load_settings)

    def test_connection_testing(self, qtbot):
        """Test connection testing infrastructure."""
        window = MainWindow()
        qtbot.addWidget(window)

        # Test that connection testing infrastructure exists
        assert hasattr(window, 'test_connection')
        assert callable(window.test_connection)

    def test_monitoring_infrastructure(self, qtbot):
        """Test file monitoring infrastructure."""
        window = MainWindow()
        qtbot.addWidget(window)

        # Test that monitoring methods exist
        assert hasattr(window, 'toggle_monitoring')
        assert callable(window.toggle_monitoring)

    def test_browser_integration(self, qtbot):
        """Test browser integration infrastructure."""
        window = MainWindow()
        qtbot.addWidget(window)

        # Test that browser integration infrastructure exists
        assert hasattr(window, 'browse_remote_directory')
        assert callable(window.browse_remote_directory)

    def test_conflict_handling(self, qtbot):
        """Test conflict handling infrastructure."""
        window = MainWindow()
        qtbot.addWidget(window)

        # Test conflict handling infrastructure exists
        assert hasattr(window, 'on_conflict_resolution_needed')
        assert callable(window.on_conflict_resolution_needed)

    def test_settings_infrastructure(self, qtbot):
        """Test settings infrastructure exists."""
        window = MainWindow()
        qtbot.addWidget(window)

        # Test that settings infrastructure exists
        assert hasattr(window, 'load_settings')
        assert hasattr(window, 'save_settings')
        assert callable(window.load_settings)
        assert callable(window.save_settings)
