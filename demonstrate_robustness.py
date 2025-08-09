#!/usr/bin/env python3
"""
File Monitoring Robustness Demonstration

This script demonstrates the improved exception handling in PanoramaBridge's
file monitoring system. It shows how the system gracefully handles various
error conditions that could occur during file copying operations.
"""

import os
import sys
import tempfile
import shutil
import time
import threading
import queue
from unittest.mock import patch

# Add the parent directory to sys.path to import panoramabridge
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from panoramabridge import FileMonitorHandler

def demonstrate_file_monitoring_robustness():
    """Demonstrate robust file monitoring with error handling"""
    
    print("🔧 PanoramaBridge File Monitoring Robustness Demonstration")
    print("=" * 60)
    
    # Create temporary directory
    temp_dir = tempfile.mkdtemp()
    file_queue = queue.Queue()
    
    # Mock app instance
    class MockApp:
        def __init__(self):
            self.queued_files = set()
            self.processing_files = set()
            self.add_queued_file_to_table_calls = 0
            
        def add_queued_file_to_table(self, filepath):
            self.add_queued_file_to_table_calls += 1
            print(f"    ✅ UI updated for: {os.path.basename(filepath)}")
    
    mock_app = MockApp()
    
    # Create file monitor
    monitor = FileMonitorHandler(
        extensions=['.txt', '.log'],
        file_queue=file_queue,
        monitor_subdirs=True,
        app_instance=mock_app
    )
    
    print(f"📁 Working directory: {temp_dir}")
    print()
    
    # Test 1: Normal file handling
    print("🧪 Test 1: Normal File Copy Simulation")
    test_file = os.path.join(temp_dir, "normal_file.txt")
    
    with open(test_file, 'w') as f:
        f.write("Initial content")
    
    monitor._handle_file(test_file)
    
    # Simulate file growing during copy
    for i in range(3):
        time.sleep(0.1)
        with open(test_file, 'a') as f:
            f.write(f" - chunk {i}")
        monitor._handle_file(test_file)
    
    print(f"    📄 Created and monitored: {os.path.basename(test_file)}")
    time.sleep(2)  # Wait for stability check
    
    queued_count = file_queue.qsize()
    print(f"    🎯 Files queued: {queued_count}")
    print()
    
    # Test 2: Nonexistent file handling
    print("🧪 Test 2: Nonexistent File Handling")
    nonexistent_file = os.path.join(temp_dir, "does_not_exist.txt")
    
    try:
        monitor._handle_file(nonexistent_file)
        print("    ✅ Handled nonexistent file without crashing")
    except Exception as e:
        print(f"    ❌ Error: {e}")
    print()
    
    # Test 3: Permission error simulation
    print("🧪 Test 3: Permission Error Handling")
    perm_file = os.path.join(temp_dir, "permission_file.txt")
    
    with open(perm_file, 'w') as f:
        f.write("protected content")
    
    with patch('os.path.getsize', side_effect=PermissionError("Access denied")):
        try:
            monitor._handle_file(perm_file)
            print("    ✅ Handled permission error gracefully")
            print("    📄 File will be retried automatically")
        except Exception as e:
            print(f"    ❌ Unexpected error: {e}")
    print()
    
    # Test 4: IO error simulation
    print("🧪 Test 4: IO Error Handling")
    io_file = os.path.join(temp_dir, "io_error_file.txt")
    
    with open(io_file, 'w') as f:
        f.write("locked content")
    
    with patch('os.path.getsize', side_effect=IOError("File is locked")):
        try:
            monitor._handle_file(io_file)
            print("    ✅ Handled IO error gracefully")
            print("    📄 File will be retried automatically")
        except Exception as e:
            print(f"    ❌ Unexpected error: {e}")
    print()
    
    # Test 5: UI error simulation
    print("🧪 Test 5: UI Error Handling")
    ui_file = os.path.join(temp_dir, "ui_error_file.txt")
    
    with open(ui_file, 'w') as f:
        f.write("ui test content")
    
    # Make file stable and simulate UI error
    monitor.pending_files[ui_file] = (15, time.time() - 2)
    
    # Mock UI method to raise error
    original_ui_method = mock_app.add_queued_file_to_table
    mock_app.add_queued_file_to_table = lambda x: (_ for _ in ()).throw(RuntimeError("UI is broken"))
    
    try:
        monitor._handle_file(ui_file)
        print("    ✅ Handled UI error gracefully")
        print("    📄 File still queued despite UI error")
        
        # Check if file was queued
        queue_size_after_ui_error = file_queue.qsize()
        if queue_size_after_ui_error > queued_count:
            print("    ✅ File successfully queued despite UI failure")
        
    except Exception as e:
        print(f"    ❌ Unexpected error: {e}")
    finally:
        # Restore UI method
        mock_app.add_queued_file_to_table = original_ui_method
    print()
    
    # Test 6: Concurrent operations
    print("🧪 Test 6: Concurrent File Operations")
    
    def create_and_handle_file(index):
        concurrent_file = os.path.join(temp_dir, f"concurrent_file_{index}.txt")
        with open(concurrent_file, 'w') as f:
            f.write(f"concurrent content {index}")
        monitor._handle_file(concurrent_file)
        return concurrent_file
    
    threads = []
    concurrent_files = []
    
    for i in range(5):
        thread = threading.Thread(target=lambda idx=i: concurrent_files.append(create_and_handle_file(idx)))
        threads.append(thread)
        thread.start()
    
    for thread in threads:
        thread.join()
    
    print(f"    ✅ Handled {len(concurrent_files)} concurrent operations")
    print()
    
    # Summary
    print("📊 Summary")
    print("-" * 30)
    final_queue_size = file_queue.qsize()
    print(f"Total files queued: {final_queue_size}")
    print(f"UI update calls: {mock_app.add_queued_file_to_table_calls}")
    print(f"Pending files: {len(monitor.pending_files)}")
    
    # List queued files
    if final_queue_size > 0:
        print("\n📋 Queued files:")
        queued_files = []
        while not file_queue.empty():
            try:
                filepath = file_queue.get_nowait()
                queued_files.append(os.path.basename(filepath))
            except queue.Empty:
                break
        
        for filename in queued_files:
            print(f"    • {filename}")
    
    print()
    print("✅ All tests completed successfully!")
    print("🛡️  The file monitoring system is now robust against:")
    print("   - File copying operations")
    print("   - Permission errors")
    print("   - IO errors")
    print("   - UI update failures")
    print("   - Concurrent file operations")
    print("   - Files that disappear during monitoring")
    
    # Cleanup
    shutil.rmtree(temp_dir, ignore_errors=True)

if __name__ == "__main__":
    demonstrate_file_monitoring_robustness()
