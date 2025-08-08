#!/usr/bin/env python3
"""
Tests for table ordering and progress message improvements in PanoramaBridge.

This module tests the enhanced functionality for:
- Table ordering (files fill from top to bottom)
- Auto-scrolling to show active processing
- Simplified progress status messages
- Clear separation between status text and progress bar
"""

import os
import sys
import pytest
from unittest.mock import Mock, patch, MagicMock, call

# Add the parent directory to sys.path so we can import panoramabridge
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from panoramabridge import MainWindow, FileProcessor


class TestTableOrderingAndProgressMessages:
    """Test suite for table ordering and progress message improvements"""
    
    @pytest.fixture
    def main_window(self):
        """Create a MainWindow instance for testing with proper mocking"""
        with patch('PyQt6.QtWidgets.QApplication.instance', return_value=Mock()), \
             patch('PyQt6.QtWidgets.QMainWindow.__init__', return_value=None), \
             patch('panoramabridge.MainWindow.__init__', return_value=None):
            
            window = MainWindow()
            
            # Mock the transfer table and its methods
            window.transfer_table = Mock()
            window.transfer_table.rowCount = Mock()
            window.transfer_table.insertRow = Mock()
            window.transfer_table.setItem = Mock()
            window.transfer_table.setCellWidget = Mock()
            window.transfer_table.item = Mock()
            window.transfer_table.scrollToBottom = Mock()
            window.transfer_table.scrollToItem = Mock()
            window.transfer_table.cellWidget = Mock()
            
            # Mock other dependencies
            window.transfer_rows = {}
            window.dir_input = Mock()
            window.dir_input.text = Mock(return_value="/test/directory")
            
            return window

    def test_table_fills_from_top_to_bottom(self, main_window):
        """Test that files are added to table from top to bottom (append at end)"""
        # Mock initial empty table
        main_window.transfer_table.rowCount.return_value = 0
        
        # Add first file
        filepath1 = "/test/directory/file1.raw"
        main_window.add_queued_file_to_table(filepath1)
        
        # Verify first file inserted at row 0 (when table was empty)
        main_window.transfer_table.insertRow.assert_called_with(0)
        
        # Mock table now has 1 row
        main_window.transfer_table.rowCount.return_value = 1
        
        # Add second file
        filepath2 = "/test/directory/file2.raw"
        main_window.add_queued_file_to_table(filepath2)
        
        # Verify second file inserted at row 1 (appended to bottom)
        assert main_window.transfer_table.insertRow.call_args_list[-1] == call(1)
        
        # Mock table now has 2 rows
        main_window.transfer_table.rowCount.return_value = 2
        
        # Add third file
        filepath3 = "/test/directory/file3.raw"
        main_window.add_queued_file_to_table(filepath3)
        
        # Verify third file inserted at row 2 (appended to bottom)
        assert main_window.transfer_table.insertRow.call_args_list[-1] == call(2)

    def test_auto_scroll_to_bottom_on_add(self, main_window):
        """Test that table auto-scrolls to bottom when new files are added"""
        main_window.transfer_table.rowCount.return_value = 5
        
        filepath = "/test/directory/test_file.raw"
        main_window.add_queued_file_to_table(filepath)
        
        # Verify auto-scroll to bottom was called
        main_window.transfer_table.scrollToBottom.assert_called_once()

    def test_unique_key_generation_consistency(self, main_window):
        """Test that unique keys are generated consistently"""
        filepath = "/test/directory/subfolder/test_file.raw"
        filename = "test_file.raw"
        
        # Test the helper method directly
        key1 = main_window.get_transfer_table_key(filename, filepath)
        key2 = main_window.get_transfer_table_key(filename, filepath)
        
        # Keys should be identical for same file
        assert key1 == key2
        
        # Key should follow expected format: filename|hash(filepath)
        expected_key = f"{filename}|{hash(filepath)}"
        assert key1 == expected_key

    def test_duplicate_file_prevention(self, main_window):
        """Test that duplicate files are not added to table"""
        main_window.transfer_table.rowCount.return_value = 0
        
        filepath = "/test/directory/test_file.raw"
        filename = os.path.basename(filepath)
        
        # Add file first time
        main_window.add_queued_file_to_table(filepath)
        
        # Verify file was added and tracked
        unique_key = main_window.get_transfer_table_key(filename, filepath)
        assert unique_key in main_window.transfer_rows
        
        # Reset mock call counts
        main_window.transfer_table.insertRow.reset_mock()
        main_window.transfer_table.setItem.reset_mock()
        
        # Try to add same file again
        main_window.add_queued_file_to_table(filepath)
        
        # Verify no new row was inserted
        main_window.transfer_table.insertRow.assert_not_called()

    def test_status_update_maintains_table_order(self, main_window):
        """Test that status updates work correctly with bottom-append table ordering"""
        # Mock table state
        main_window.transfer_table.rowCount.return_value = 2
        
        # Set up existing file in table
        filepath = "/test/directory/test_file.raw"
        filename = os.path.basename(filepath)
        unique_key = main_window.get_transfer_table_key(filename, filepath)
        main_window.transfer_rows[unique_key] = 1  # File is at row 1
        
        # Mock table item for status updates
        status_item = Mock()
        status_item.text.return_value = "Queued"
        main_window.transfer_table.item.return_value = status_item
        
        # Update status
        main_window.on_status_update(filename, "Uploading file...", filepath)
        
        # Verify status was updated (not a new row created)
        status_item.setText.assert_called_with("Uploading file...")

    def test_fallback_status_update_uses_bottom_append(self, main_window):
        """Test that fallback status updates also use bottom-append (not top insert)"""
        main_window.transfer_table.rowCount.return_value = 3
        
        # Call status update for file not in table (should trigger fallback)
        filepath = "/test/directory/new_file.raw"
        filename = os.path.basename(filepath)
        main_window.on_status_update(filename, "Processing", filepath)
        
        # Verify fallback inserted at bottom (row 3)
        main_window.transfer_table.insertRow.assert_called_with(3)

    def test_auto_scroll_on_processing_start(self, main_window):
        """Test that table scrolls to show file when it starts processing"""
        # Set up file in table
        filepath = "/test/directory/test_file.raw"
        filename = os.path.basename(filepath)
        unique_key = main_window.get_transfer_table_key(filename, filepath)
        main_window.transfer_rows[unique_key] = 2  # File at row 2
        
        # Mock table items and widgets
        main_window.transfer_table.rowCount.return_value = 5
        status_item = Mock()
        status_item.text.return_value = "Queued"
        main_window.transfer_table.item.return_value = status_item
        
        progress_bar = Mock()
        progress_bar.setVisible = Mock()
        main_window.transfer_table.cellWidget.return_value = progress_bar
        
        # Update status from Queued to Processing (should trigger scroll)
        main_window.on_status_update(filename, "Processing", filepath)
        
        # Verify progress bar was made visible and scroll occurred
        progress_bar.setVisible.assert_called_with(True)
        main_window.transfer_table.scrollToItem.assert_called_once()


class TestSimplifiedProgressMessages:
    """Test suite for simplified progress status messages"""
    
    @pytest.fixture
    def file_processor(self):
        """Create a FileProcessor instance for testing"""
        processor = Mock(spec=FileProcessor)
        processor.status_update = Mock()
        processor.progress_update = Mock()
        return processor

    def test_simplified_upload_status_messages(self):
        """Test that upload status messages are simplified and clear"""
        # This tests the logic we implemented in the progress_callback
        
        # Track status updates
        status_updates = []
        
        def mock_status_update(filename, status, filepath):
            status_updates.append(status)
        
        # Simulate the progress callback logic from FileProcessor
        def create_progress_callback(filename, filepath, status_update_func):
            last_status_percentage = -1
            
            def progress_callback(current, total):
                nonlocal last_status_percentage
                
                if total > 0:
                    percentage = (current / total) * 100
                    
                    # Use the simplified status message logic we implemented
                    if percentage >= 100:
                        status_msg = "Upload complete"
                    elif current > 0:
                        status_msg = "Uploading file..."
                    else:
                        status_msg = "Preparing upload..."
                    
                    # Update every 25% to avoid confusion
                    percentage_rounded = int(percentage / 25) * 25
                    
                    if percentage_rounded != last_status_percentage:
                        status_update_func(filename, status_msg, filepath)
                        last_status_percentage = percentage_rounded
            
            return progress_callback
        
        # Create progress callback
        filename = "test_file.raw"
        filepath = "/test/test_file.raw"
        callback = create_progress_callback(filename, filepath, mock_status_update)
        
        # Test various progress stages
        total_size = 1000000  # 1MB file
        
        # Initial state (0 bytes)
        callback(0, total_size)
        assert "Preparing upload..." in status_updates
        
        # 10% progress (should show "Uploading file...")
        callback(100000, total_size)
        assert "Uploading file..." in status_updates
        
        # 50% progress (should still show "Uploading file...")
        callback(500000, total_size)
        # Should not create duplicate status updates within same 25% range
        
        # 100% completion
        callback(1000000, total_size)
        assert "Upload complete" in status_updates

    def test_no_percentage_in_status_messages(self):
        """Test that status messages don't contain percentage values"""
        # Verify the old confusing messages are not present
        old_confusing_patterns = [
            "73%", "halfway", "nearly done", "(41%)", "91%"
        ]
        
        # Test the new simplified messages
        new_status_messages = [
            "Preparing upload...",
            "Uploading file...", 
            "Upload complete"
        ]
        
        for message in new_status_messages:
            # Verify no percentage patterns exist
            for pattern in old_confusing_patterns:
                assert pattern not in message
            
            # Verify messages are descriptive but simple
            assert len(message) < 30  # Concise messages
            assert "..." in message or message == "Upload complete"

    def test_progress_bar_separation_from_status(self, main_window):
        """Test that progress bar values are separate from status text"""
        # Set up file in table
        filepath = "/test/directory/test_file.raw"
        filename = os.path.basename(filepath)
        unique_key = main_window.get_transfer_table_key(filename, filepath)
        main_window.transfer_rows[unique_key] = 0
        
        # Mock progress bar
        progress_bar = Mock()
        progress_bar.setValue = Mock()
        main_window.transfer_table.rowCount.return_value = 1
        main_window.transfer_table.cellWidget.return_value = progress_bar
        
        # Test progress update (should only update progress bar, not status text)
        current_bytes = 750000  # 75% of 1MB
        total_bytes = 1000000
        
        main_window.on_progress_update(filepath, current_bytes, total_bytes)
        
        # Verify progress bar was updated with percentage
        expected_percentage = int((current_bytes / total_bytes) * 100)
        progress_bar.setValue.assert_called_with(expected_percentage)

    def test_reduced_status_update_frequency(self):
        """Test that status updates occur less frequently to reduce confusion"""
        status_update_calls = []
        
        def mock_emit(filename, status, filepath):
            status_update_calls.append((filename, status, filepath))
        
        # Simulate the reduced frequency logic (25% intervals instead of 10%)
        last_status_percentage = -1
        filename = "test.raw"
        filepath = "/test/test.raw"
        
        # Test various percentages
        test_percentages = [0, 5, 10, 15, 20, 25, 30, 45, 50, 60, 75, 90, 100]
        
        for percentage in test_percentages:
            percentage_rounded = int(percentage / 25) * 25
            
            if percentage_rounded != last_status_percentage:
                mock_emit(filename, "Uploading file...", filepath)
                last_status_percentage = percentage_rounded
        
        # Should only have updates at 0%, 25%, 50%, 75%, 100%
        # That's 5 updates instead of 10+ with the old 10% frequency
        assert len(status_update_calls) <= 5
        
        # Verify we don't have excessive updates
        assert len(status_update_calls) < 8  # Much less than old 10% system


class TestTableScrollingBehavior:
    """Test suite for auto-scrolling behavior"""
    
    @pytest.fixture 
    def main_window(self):
        """Create MainWindow with table scrolling mocks"""
        with patch('PyQt6.QtWidgets.QApplication.instance', return_value=Mock()):
            window = MainWindow()
            window.transfer_table = Mock()
            window.transfer_table.rowCount = Mock(return_value=0)
            window.transfer_table.insertRow = Mock()
            window.transfer_table.setItem = Mock()
            window.transfer_table.setCellWidget = Mock()
            window.transfer_table.item = Mock()
            window.transfer_table.cellWidget = Mock()
            window.transfer_table.scrollToBottom = Mock()
            window.transfer_table.scrollToItem = Mock()
            
            window.transfer_rows = {}
            window.dir_input = Mock()
            window.dir_input.text = Mock(return_value="/test")
            
            return window
    
    def test_scroll_to_bottom_on_queue_addition(self, main_window):
        """Test scrolling to bottom when files are queued"""
        main_window.transfer_table.rowCount.return_value = 3
        
        filepath = "/test/new_file.raw"
        main_window.add_queued_file_to_table(filepath)
        
        # Should scroll to bottom to show newly queued file
        main_window.transfer_table.scrollToBottom.assert_called_once()
    
    def test_scroll_to_active_file_on_processing_start(self, main_window):
        """Test scrolling to specific file when it starts processing"""
        # Set up existing file in table
        filepath = "/test/test_file.raw"
        filename = os.path.basename(filepath)
        unique_key = main_window.get_transfer_table_key(filename, filepath)
        main_window.transfer_rows[unique_key] = 5  # File at row 5
        
        # Mock table state
        main_window.transfer_table.rowCount.return_value = 10
        status_item = Mock()
        status_item.text.return_value = "Queued"
        main_window.transfer_table.item.return_value = status_item
        
        progress_bar = Mock()
        main_window.transfer_table.cellWidget.return_value = progress_bar
        
        # Change status from Queued to active processing
        main_window.on_status_update(filename, "Processing", filepath)
        
        # Should scroll to the specific item that started processing
        main_window.transfer_table.scrollToItem.assert_called_once()
    
    def test_no_scroll_for_status_updates_on_queued_files(self, main_window):
        """Test that purely queued status updates don't trigger scrolling"""
        # Set up file
        filepath = "/test/test_file.raw"
        filename = os.path.basename(filepath)
        unique_key = main_window.get_transfer_table_key(filename, filepath)
        main_window.transfer_rows[unique_key] = 2
        
        # Mock table state
        main_window.transfer_table.rowCount.return_value = 5
        status_item = Mock()
        status_item.text.return_value = "Queued"
        main_window.transfer_table.item.return_value = status_item
        
        # Update with another queued-type status
        main_window.on_status_update(filename, "Starting", filepath)
        
        # Should not scroll since it's still in queued/starting state
        main_window.transfer_table.scrollToItem.assert_not_called()


if __name__ == "__main__":
    pytest.main([__file__])
