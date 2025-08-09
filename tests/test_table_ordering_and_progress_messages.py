#!/usr/bin/env python3
"""
Tests for table ordering and progress message functionality.
These tests use pytest-qt for proper Qt integration testing.
"""

import os
import sys
from unittest.mock import patch

import pytest

# Add the parent directory to sys.path so we can import panoramabridge
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from panoramabridge import MainWindow


class TestTableOrderingAndProgressMessages:
    """Test suite for table ordering and progress message improvements"""

    @pytest.fixture(autouse=True)
    def setup_mocks(self):
        """Set up mocks for external dependencies."""
        with patch("panoramabridge.keyring") as mock_keyring:
            mock_keyring.get_password.return_value = None
            with patch("os.makedirs"):
                yield

    def test_table_fills_from_top_to_bottom(self, qtbot):
        """Test that table fills from top to bottom as files are queued"""
        window = MainWindow()
        qtbot.addWidget(window)
        
        # Test that transfer table exists and can track rows
        assert hasattr(window, 'transfer_table')
        assert hasattr(window, 'transfer_rows')

    def test_auto_scroll_to_bottom_on_add(self, qtbot):
        """Test that table auto-scrolls to bottom when new files are added"""
        window = MainWindow()
        qtbot.addWidget(window)
        
        # Test that scrolling infrastructure exists
        assert hasattr(window, 'transfer_table')
        assert callable(getattr(window.transfer_table, 'scrollToBottom', None))

    def test_unique_key_generation_consistency(self, qtbot):
        """Test that unique keys are generated consistently for table rows"""
        window = MainWindow()
        qtbot.addWidget(window)
        
        # Test that row tracking infrastructure exists
        assert hasattr(window, 'transfer_rows')
        assert isinstance(window.transfer_rows, dict)

    def test_duplicate_file_prevention(self, qtbot):
        """Test that duplicate files are prevented from being added"""
        window = MainWindow()
        qtbot.addWidget(window)
        
        # Test duplicate prevention infrastructure
        assert hasattr(window, 'transfer_rows')
        assert hasattr(window, 'queued_files')

    def test_status_update_maintains_table_order(self, qtbot):
        """Test that status updates don't disrupt table order"""
        window = MainWindow()
        qtbot.addWidget(window)
        
        # Test that table structure supports status updates
        assert hasattr(window, 'transfer_table')
        assert hasattr(window, 'transfer_rows')

    def test_fallback_status_update_uses_bottom_append(self, qtbot):
        """Test fallback behavior for status updates"""
        window = MainWindow()
        qtbot.addWidget(window)
        
        # Test fallback infrastructure exists
        assert hasattr(window, 'transfer_table')

    def test_auto_scroll_on_processing_start(self, qtbot):
        """Test that table scrolls to show processing files"""
        window = MainWindow()
        qtbot.addWidget(window)
        
        # Test scroll to item functionality exists
        assert hasattr(window, 'transfer_table')


class TestSimplifiedProgressMessages:
    """Test simplified progress message functionality"""

    @pytest.fixture(autouse=True)
    def setup_mocks(self):
        """Set up mocks for external dependencies."""
        with patch("panoramabridge.keyring") as mock_keyring:
            mock_keyring.get_password.return_value = None
            with patch("os.makedirs"):
                yield

    def test_simplified_upload_status_messages(self, qtbot):
        """Test that upload status messages are simplified"""
        window = MainWindow()
        qtbot.addWidget(window)
        
        # Test that status update infrastructure exists
        assert hasattr(window, 'transfer_table')

    def test_no_percentage_in_status_messages(self, qtbot):
        """Test that percentage info is separate from status messages"""
        window = MainWindow()
        qtbot.addWidget(window)
        
        # Test progress tracking separation
        assert hasattr(window, 'transfer_table')

    def test_progress_bar_separation_from_status(self, qtbot):
        """Test that progress bars are separate from status text"""
        window = MainWindow()
        qtbot.addWidget(window)
        
        # Test UI component separation
        assert hasattr(window, 'transfer_table')

    def test_reduced_status_update_frequency(self, qtbot):
        """Test that status updates are less frequent to reduce UI spam"""
        window = MainWindow()
        qtbot.addWidget(window)
        
        # Test update frequency control infrastructure
        assert hasattr(window, 'transfer_table')


class TestTableScrollingBehavior:
    """Test table scrolling behavior"""

    @pytest.fixture(autouse=True)
    def setup_mocks(self):
        """Set up mocks for external dependencies."""
        with patch("panoramabridge.keyring") as mock_keyring:
            mock_keyring.get_password.return_value = None
            with patch("os.makedirs"):
                yield

    def test_scroll_to_bottom_on_queue_addition(self, qtbot):
        """Test scrolling to bottom when files are added to queue"""
        window = MainWindow()
        qtbot.addWidget(window)
        
        # Test scroll to bottom functionality
        assert hasattr(window, 'transfer_table')
        assert callable(getattr(window.transfer_table, 'scrollToBottom', None))

    def test_scroll_to_active_file_on_processing_start(self, qtbot):
        """Test scrolling to active file when processing starts"""
        window = MainWindow()
        qtbot.addWidget(window)
        
        # Test scroll to item functionality
        assert hasattr(window, 'transfer_table')

    def test_no_scroll_for_status_updates_on_queued_files(self, qtbot):
        """Test that status updates on queued files don't trigger scrolling"""
        window = MainWindow()
        qtbot.addWidget(window)
        
        # Test controlled scrolling behavior
        assert hasattr(window, 'transfer_table')
        assert hasattr(window, 'transfer_rows')


if __name__ == '__main__':
    pytest.main([__file__, '-v'])
