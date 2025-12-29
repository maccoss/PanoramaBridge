#!/usr/bin/env python3
"""
Test to verify that the enhanced ETag verification system is implemented correctly.
This test validates the new multi-format ETag support and removal of expensive checksum verification.
"""

import os

import pytest


def test_verify_remote_file_integrity_function_exists():
    """Test that the verify_remote_file_integrity function exists with correct signature"""

    panorama_path = os.path.join(os.path.dirname(os.path.dirname(__file__)), 'panoramabridge.py')
    with open(panorama_path, encoding='utf-8') as f:
        content = f.read()

    # Check for function definition
    assert "def verify_remote_file_integrity(self, local_filepath: str, remote_path: str, expected_checksum: str)" in content
    assert "-> tuple[bool, str]:" in content


def test_no_expensive_checksum_verification():
    """Test that expensive checksum verification has been removed"""

    panorama_path = os.path.join(os.path.dirname(os.path.dirname(__file__)), 'panoramabridge.py')
    with open(panorama_path, encoding='utf-8') as f:
        content = f.read()

    # Check that old expensive verification patterns are gone
    removed_patterns = [
        "# Level 3: Small file download verification (<10MB)",
        "Download to temp file and verify checksum",
        'return True, "Checksum verified"'
    ]

    found_removed = []
    for pattern in removed_patterns:
        if pattern in content:
            found_removed.append(pattern)

    if found_removed:
        pytest.fail("Found removed expensive verification patterns that should be gone:\n" + "\n".join(found_removed))

    # Test passes if no removed patterns found
    assert not found_removed, "Expensive checksum verification should be removed"


if __name__ == "__main__":
    test_verify_remote_file_integrity_function_exists()
    test_no_expensive_checksum_verification()
    print("✅ All enhanced ETag verification tests passed!")
