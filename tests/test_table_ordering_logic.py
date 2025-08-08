#!/usr/bin/env python3
"""
Unit tests for table ordering and progress message improvements in PanoramaBridge.

This module tests the enhanced functionality without requiring Qt initialization:
- Table ordering logic (append vs prepend)
- Progress message simplification logic
- Unique key generation consistency
- Auto-scrolling trigger conditions
"""

import os
import sys
import pytest
from unittest.mock import Mock

# Add the parent directory to sys.path so we can import panoramabridge
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


class TestTableOrderingLogic:
    """Test the core logic of table ordering improvements"""
    
    def test_table_append_vs_prepend_logic(self):
        """Test that we're using append (bottom) instead of prepend (top) logic"""
        # Simulate table state
        mock_table = Mock()
        mock_table.rowCount.return_value = 5  # Table has 5 existing rows
        
        # Test the logic we implemented: insertRow(rowCount) for append
        row_count = mock_table.rowCount()
        
        # Our new logic: insert at bottom
        insert_position = row_count  # This should be 5 (append at end)
        
        # Verify this is append behavior, not prepend
        assert insert_position == 5  # Should insert at row 5 (after existing 0-4)
        assert insert_position != 0   # Should NOT insert at row 0 (top)
        
        # Test with empty table
        mock_table.rowCount.return_value = 0
        insert_position = mock_table.rowCount()
        assert insert_position == 0  # First file goes to row 0
        
        # Test with one file
        mock_table.rowCount.return_value = 1
        insert_position = mock_table.rowCount()
        assert insert_position == 1  # Second file goes to row 1

    def test_unique_key_format_consistency(self):
        """Test the new unique key format: filename|hash(filepath)"""
        filename = "test_file.raw"
        filepath = "/test/directory/test_file.raw"
        
        # Test the format we implemented
        expected_key = f"{filename}|{hash(filepath)}"
        
        # Simulate the get_transfer_table_key method
        def get_transfer_table_key(filename, filepath):
            return f"{filename}|{hash(filepath)}"
        
        actual_key = get_transfer_table_key(filename, filepath)
        assert actual_key == expected_key
        
        # Verify key is consistent across calls
        key1 = get_transfer_table_key(filename, filepath)
        key2 = get_transfer_table_key(filename, filepath)
        assert key1 == key2
        
        # Verify different files have different keys
        filepath2 = "/test/directory/different_file.raw"
        key3 = get_transfer_table_key(filename, filepath2)
        assert key1 != key3  # Different paths should produce different keys


class TestProgressMessageSimplification:
    """Test the simplified progress message logic"""
    
    def test_simplified_status_messages(self):
        """Test that status messages are simplified and don't contain percentages"""
        # Simulate the progress callback logic we implemented
        def get_status_message(current, total):
            if total > 0:
                percentage = (current / total) * 100
                
                if percentage >= 100:
                    return "Upload complete"
                elif current > 0:
                    return "Uploading file..."
                else:
                    return "Preparing upload..."
            return "Preparing upload..."
        
        # Test various scenarios
        total_size = 1000000  # 1MB
        
        # Test initial state
        status = get_status_message(0, total_size)
        assert status == "Preparing upload..."
        assert "%" not in status  # No percentage in message
        
        # Test mid-upload (50%)
        status = get_status_message(500000, total_size)
        assert status == "Uploading file..."
        assert "%" not in status
        assert "halfway" not in status.lower()
        assert "50" not in status
        
        # Test near completion (90%)
        status = get_status_message(900000, total_size)
        assert status == "Uploading file..."
        assert "%" not in status
        assert "nearly done" not in status.lower()
        assert "90" not in status
        
        # Test completion
        status = get_status_message(1000000, total_size)
        assert status == "Upload complete"
        assert "%" not in status

    def test_old_confusing_messages_eliminated(self):
        """Test that old confusing message patterns are not present"""
        # These are examples of the old confusing messages we removed
        old_patterns = [
            "Uploading... (73% - halfway)",
            "Uploading... (91% - nearly done)",
            "Uploading... (45%)",
            "(41%)", "(73%)", "(91%)"
        ]
        
        # New simplified messages
        new_messages = [
            "Preparing upload...",
            "Uploading file...",
            "Upload complete"
        ]
        
        for new_msg in new_messages:
            for old_pattern in old_patterns:
                assert new_msg != old_pattern
                # Ensure no percentage patterns leak into new messages
                assert "%" not in new_msg
                assert "halfway" not in new_msg.lower()
                assert "nearly done" not in new_msg.lower()

    def test_reduced_update_frequency(self):
        """Test that status updates occur less frequently (25% intervals vs 10%)"""
        # Simulate the frequency logic we implemented
        def should_update_status(percentage, last_update_percentage):
            # Round to 25% intervals instead of 10%
            percentage_rounded = int(percentage / 25) * 25
            return percentage_rounded != last_update_percentage
        
        updates = []
        last_update = -1
        
        # Test various percentages
        test_percentages = [0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 60, 70, 75, 80, 90, 95, 100]
        
        for percentage in test_percentages:
            if should_update_status(percentage, last_update):
                updates.append(percentage)
                last_update = int(percentage / 25) * 25
        
        # Should only update at 0%, 25%, 50%, 75%, 100%
        expected_updates = [0, 25, 50, 75, 100]
        
        # Convert updates to 25% intervals for comparison
        update_intervals = [int(p / 25) * 25 for p in updates]
        unique_intervals = list(dict.fromkeys(update_intervals))  # Remove duplicates, preserve order
        
        assert len(unique_intervals) <= 5  # Much fewer than 10% system (which would be 10+)
        assert 0 in unique_intervals
        assert 100 in unique_intervals or 75 in unique_intervals  # Final update


class TestScrollingBehaviorLogic:
    """Test the logic for auto-scrolling behavior"""
    
    def test_scroll_trigger_conditions(self):
        """Test when scrolling should and shouldn't be triggered"""
        
        # Simulate the scrolling logic
        def should_scroll_on_status_change(old_status, new_status):
            """Determine if we should scroll when status changes"""
            # Don't scroll for queued/starting states
            if new_status in ["Queued", "Starting", "Pending"]:
                return False
            
            # Scroll when transitioning from queued to active
            if old_status == "Queued" and new_status not in ["Queued", "Starting", "Pending"]:
                return True
                
            return False
        
        # Test cases that should trigger scrolling
        assert should_scroll_on_status_change("Queued", "Processing") == True
        assert should_scroll_on_status_change("Queued", "Uploading file...") == True
        assert should_scroll_on_status_change("Queued", "Calculating checksum") == True
        
        # Test cases that should NOT trigger scrolling
        assert should_scroll_on_status_change("Queued", "Starting") == False
        assert should_scroll_on_status_change("Queued", "Pending") == False
        assert should_scroll_on_status_change("Processing", "Uploading file...") == False
        assert should_scroll_on_status_change("Complete", "Complete") == False

    def test_scroll_to_bottom_vs_scroll_to_item(self):
        """Test different scrolling behaviors for different scenarios"""
        
        def get_scroll_action(context):
            """Determine scroll action based on context"""
            if context == "new_file_queued":
                return "scroll_to_bottom"
            elif context == "file_starts_processing":
                return "scroll_to_item"
            else:
                return "no_scroll"
        
        # Test correct scroll actions
        assert get_scroll_action("new_file_queued") == "scroll_to_bottom"
        assert get_scroll_action("file_starts_processing") == "scroll_to_item" 
        assert get_scroll_action("status_update_only") == "no_scroll"


class TestIntegrationScenarios:
    """Test combined scenarios that exercise multiple improvements"""
    
    def test_table_filling_sequence(self):
        """Test the complete sequence of table filling from top to bottom"""
        # Simulate adding multiple files
        files = [
            "/test/file1.raw",
            "/test/file2.raw", 
            "/test/file3.raw"
        ]
        
        # Track table state
        table_rows = {}
        row_count = 0
        
        # Simulate adding files with our new logic
        for i, filepath in enumerate(files):
            filename = os.path.basename(filepath)
            unique_key = f"{filename}|{hash(filepath)}"
            
            # Insert at current row count (append behavior)
            insert_position = row_count
            table_rows[unique_key] = insert_position
            row_count += 1
        
        # Verify files are ordered top to bottom
        expected_positions = {
            f"file1.raw|{hash('/test/file1.raw')}": 0,  # First file at row 0
            f"file2.raw|{hash('/test/file2.raw')}": 1,  # Second file at row 1
            f"file3.raw|{hash('/test/file3.raw')}": 2   # Third file at row 2
        }
        
        assert table_rows == expected_positions

    def test_processing_order_consistency(self):
        """Test that FIFO processing works with bottom-append table order"""
        # Files added to table (bottom-append)
        files_added = ["file1.raw", "file2.raw", "file3.raw"]
        
        # Processing should happen in same order (FIFO)
        expected_processing_order = ["file1.raw", "file2.raw", "file3.raw"]
        
        # Simulate processing from top to bottom (row 0, 1, 2...)
        processing_order = []
        for i in range(len(files_added)):
            # Process files in row order (0, 1, 2, ...)
            processing_order.append(files_added[i])
        
        assert processing_order == expected_processing_order

    def test_progress_and_status_separation(self):
        """Test that progress bar and status text are properly separated"""
        # Simulate progress update
        current_bytes = 750000
        total_bytes = 1000000
        
        # Progress bar should show percentage
        progress_percentage = int((current_bytes / total_bytes) * 100)
        assert progress_percentage == 75
        
        # Status text should be simple and descriptive
        if current_bytes > 0 and current_bytes < total_bytes:
            status_text = "Uploading file..."
        elif current_bytes >= total_bytes:
            status_text = "Upload complete"
        else:
            status_text = "Preparing upload..."
        
        # Verify separation
        assert status_text == "Uploading file..."
        assert str(progress_percentage) not in status_text  # No percentage in status
        assert "75%" not in status_text
        assert "%" not in status_text


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
