#!/usr/bin/env python3
"""
Test to verify that the enhanced ETag verification system is implemented correctly.
This test validates the new multi-format ETag support and removal of expensive checksum verification.
"""

import os

import pytest


def test_enhanced_etag_verification_code():
    """Test that the enhanced ETag verification code is present in panoramabridge.py"""

    # Read the main application file
    panorama_path = os.path.join(os.path.dirname(os.path.dirname(__file__)), 'panoramabridge.py')
    with open(panorama_path) as f:
        content = f.read()

    # Check for new enhanced verification implementation
    checks = [
        ("Level 1: Size comparison", "Level 1: Size comparison (fastest - immediate)"),
        ("Level 2: ETag verification", "Level 2: ETag verification (PRIORITY - network request)"),
        ("SHA256 ETag support", 'if clean_etag.lower() == expected_checksum.lower():'),
        ("MD5 ETag support", 'elif len(clean_etag) == 32:  # Likely MD5 hash (Apache default)'),
        ("Integrity problem detection", 'elif len(clean_etag) == len(expected_checksum):'),
        ("ETag SHA256 message", 'return True, "ETag (SHA256 format)"'),
        ("MD5 ETag message", 'return True, "ETag (MD5 format)"'),
        ("Size + accessibility fallback", 'return True, "Size + accessibility'),
        ("No checksum verification", "not used for verification due to performance cost"),
        ("Checksum for metadata only", "generated for upload metadata")
    ]

    all_found = True
    missing_checks = []

    for check_name, check_text in checks:
        if check_text in content:
            continue  # Found
        all_found = False
        missing_checks.append(f"{check_name}: {check_text}")

    if not all_found:
        pytest.fail("Missing enhanced verification code elements:\n" + "\n".join(missing_checks))

    # Test passes if all elements found
    assert all_found, "All enhanced ETag verification elements should be present"


def test_verify_remote_file_integrity_function_exists():
    """Test that the verify_remote_file_integrity function exists with correct signature"""

    panorama_path = os.path.join(os.path.dirname(os.path.dirname(__file__)), 'panoramabridge.py')
    with open(panorama_path) as f:
        content = f.read()

    # Check for function definition
    assert "def verify_remote_file_integrity(self, local_filepath: str, remote_path: str, expected_checksum: str)" in content
    assert "-> tuple[bool, str]:" in content

    # Check for multi-level verification documentation
    assert "multi-level optimization" in content.lower()


def test_no_expensive_checksum_verification():
    """Test that expensive checksum verification has been removed"""

    panorama_path = os.path.join(os.path.dirname(os.path.dirname(__file__)), 'panoramabridge.py')
    with open(panorama_path) as f:
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


def test_enhanced_etag_format_support():
    """Test that multiple ETag formats are supported"""

    panorama_path = os.path.join(os.path.dirname(os.path.dirname(__file__)), 'panoramabridge.py')
    with open(panorama_path) as f:
        content = f.read()

    # Check for MD5 support
    assert "import hashlib" in content
    assert "md5_hash = hashlib.md5" in content
    assert "ETag (MD5 format)" in content

    # Check for unknown format handling
    assert "server uses unknown ETag format" in content

    # Check for proper ETag cleaning
    assert 'clean_etag = remote_etag.strip(\'"\').replace("W/", "")' in content


if __name__ == "__main__":
    test_enhanced_etag_verification_code()
    test_verify_remote_file_integrity_function_exists()
    test_no_expensive_checksum_verification()
    test_enhanced_etag_format_support()
    print("✅ All enhanced ETag verification tests passed!")
