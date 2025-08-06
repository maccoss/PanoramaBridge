#!/usr/bin/env python3

import os
import time
from watchdog.observers import Observer
from watchdog.events import FileSystemEventHandler

class TestHandler(FileSystemEventHandler):
    def on_any_event(self, event):
        print(f"Event: {event.event_type} - {event.src_path} - Is Dir: {event.is_directory}")

def test_directory_monitoring():
    directory = "/mnt/c/Users/macco/Documents/test-panoramabridge-local"
    
    print(f"Testing directory monitoring for: {directory}")
    print("Recursive monitoring enabled")
    
    # Test os.walk first
    print("\n=== Testing os.walk (manual scan) ===")
    for root, dirs, files in os.walk(directory):
        print(f"Root: {root}")
        for file in files:
            if file.endswith('.raw'):
                filepath = os.path.join(root, file)
                print(f"  Found .raw file: {filepath}")
    
    print("\n=== Testing watchdog observer ===")
    handler = TestHandler()
    observer = Observer()
    observer.schedule(handler, directory, recursive=True)
    observer.start()
    
    print("Monitoring started. Try creating/modifying files in subdirectories...")
    print("Press Ctrl+C to stop")
    
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        observer.stop()
        print("\nStopping monitor...")
    
    observer.join()

if __name__ == "__main__":
    test_directory_monitoring()
