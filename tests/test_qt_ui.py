"""
Proper Qt UI tests using pytest-qt for testing actual GUI behavior.
This demonstrates the correct way to test PyQt6 applications.
"""

import os
import sys
import tempfile
from unittest.mock import patch

import pytest

# Import the modules we're testing
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from panoramabridge import MainWindow


class TestQtApplication:
    """Test Qt UI functionality using pytest-qt."""

    @pytest.fixture(autouse=True)
    def setup_mocks(self):
        """Set up mocks for external dependencies."""
        self.temp_dir = tempfile.mkdtemp()

        # Mock external services that don't need to work for UI tests
        with patch("panoramabridge.keyring") as mock_keyring:
            mock_keyring.get_password.return_value = None
            with patch("os.makedirs"):
                yield

        # Clean up temp directory
        import shutil
        if os.path.exists(self.temp_dir):
            shutil.rmtree(self.temp_dir)

    def test_mainwindow_creation(self, qtbot):
        """Test that MainWindow can be created successfully."""
        # Create the main window - qtbot will handle Qt lifecycle
        window = MainWindow()
        qtbot.addWidget(window)

        # Test basic window properties
        assert window.windowTitle() == "PanoramaBridge - File Monitor and WebDAV Transfer Tool"
        assert window.isVisible() is False  # Not shown by default

        # Test that essential attributes exist
        assert hasattr(window, 'upload_history')
        assert isinstance(window.upload_history, dict)

    def test_upload_history_functionality(self, qtbot):
        """Test upload history functionality in a real UI context."""
        window = MainWindow()
        qtbot.addWidget(window)

        # Test upload history operations
        test_data = {
            'test_file.raw': {
                'checksum': 'abc123',
                'timestamp': '2024-01-01T12:00:00',
                'remote_path': '/test/test_file.raw'
            }
        }

        window.upload_history.update(test_data)
        assert window.upload_history['test_file.raw']['checksum'] == 'abc123'

        # Test history persistence methods if they exist
        if hasattr(window, 'save_upload_history'):
            # This would test the actual save functionality
            window.save_upload_history()

    def test_ui_components_initialization(self, qtbot):
        """Test that essential UI components are properly initialized."""
        window = MainWindow()
        qtbot.addWidget(window)

        # Test for essential attributes that should exist after initialization
        essential_attrs = [
            'upload_history',
            'file_queue',
            'file_processor',
            'webdav_client',
            'transfer_rows',
            'queued_files',
            'processing_files',
            'created_directories',
            'failed_files',
            'file_remote_paths',
            'local_checksum_cache'
        ]

        for attr in essential_attrs:
            assert hasattr(window, attr), f"Missing essential attribute: {attr}"

        # Test specific types
        assert isinstance(window.upload_history, dict)
        assert isinstance(window.transfer_rows, dict)
        assert isinstance(window.queued_files, set)
        assert isinstance(window.processing_files, set)
        assert isinstance(window.created_directories, set)

    def test_window_geometry(self, qtbot):
        """Test that window geometry is set correctly."""
        window = MainWindow()
        qtbot.addWidget(window)

        # Test window geometry
        geometry = window.geometry()
        assert geometry.width() == 900
        assert geometry.height() == 600

    @pytest.mark.skipif(os.getenv('CI') == 'true', reason="Skip UI interaction tests in CI")
    def test_window_show_hide(self, qtbot):
        """Test showing and hiding the window."""
        window = MainWindow()
        qtbot.addWidget(window)

        # Initially not visible
        assert not window.isVisible()

        # Show window
        window.show()
        with qtbot.waitExposed(window):
            pass
        assert window.isVisible()

        # Hide window
        window.hide()
        assert not window.isVisible()


if __name__ == '__main__':
    # Run tests with pytest
    pytest.main([__file__, '-v'])
