#!/usr/bin/env python3
"""
Demo: Multiple ETag Format Support

This demo shows how PanoramaBridge now supports multiple ETag formats:
1. SHA256 ETags (our preferred format)
2. MD5 ETags (Apache server default)
3. Unknown ETag formats (fallback to size + accessibility)
4. No ETag available (size + accessibility only)

The verification messages have been cleaned up to be more concise and clear.
"""

import hashlib
import os
import tempfile


class MockWebDAVClient:
    """Mock WebDAV client to simulate different server ETag scenarios"""

    def __init__(self, etag_format="sha256"):
        self.etag_format = etag_format

    def get_file_info(self, remote_path):
        """Simulate getting file info from server with different ETag formats"""
        # Create a test file to get real size
        test_file = "/tmp/test_file.txt"
        with open(test_file, "w") as f:
            f.write("This is test file content for ETag demonstration.\n" * 100)

        file_size = os.path.getsize(test_file)

        if self.etag_format == "sha256":
            # SHA256 ETag (matches our local checksum)
            with open(test_file, "rb") as f:
                sha256_hash = hashlib.sha256(f.read()).hexdigest()
            return {"exists": True, "size": file_size, "etag": f'"{sha256_hash}"'}

        elif self.etag_format == "md5":
            # MD5 ETag (Apache default)
            with open(test_file, "rb") as f:
                md5_hash = hashlib.md5(f.read()).hexdigest()
            return {"exists": True, "size": file_size, "etag": f'"{md5_hash}"'}

        elif self.etag_format == "unknown":
            # Unknown format ETag
            return {"exists": True, "size": file_size, "etag": '"abc123-def456-ghi789"'}

        elif self.etag_format == "none":
            # No ETag provided
            return {"exists": True, "size": file_size, "etag": None}

        # Clean up
        if os.path.exists(test_file):
            os.unlink(test_file)
        return None

    def download_file_head(self, remote_path, bytes_to_read):
        """Simulate downloading first few bytes for accessibility check"""
        return b"This is test file content"

class PanoramaBridgeDemo:
    """Simplified version of PanoramaBridge for demonstration"""

    def __init__(self, webdav_client):
        self.webdav_client = webdav_client

    def verify_remote_file_integrity(self, local_filepath: str, remote_path: str, expected_checksum: str) -> tuple[bool, str]:
        """Enhanced verification with multiple ETag format support"""
        try:
            # Get remote file info
            remote_info = self.webdav_client.get_file_info(remote_path)
            if not remote_info or not remote_info.get("exists", False):
                return False, "remote file not found"

            # Level 1: Size comparison
            local_size = os.path.getsize(local_filepath) if os.path.exists(local_filepath) else 0
            remote_size = remote_info.get("size", 0)

            if local_size != remote_size:
                return False, f"size mismatch (local: {local_size}, remote: {remote_size})"

            # Level 2: Enhanced ETag verification with multiple format support
            remote_etag = remote_info.get("etag")
            if remote_etag and expected_checksum:
                # Clean ETag (remove quotes and weak indicators)
                clean_etag = remote_etag.strip('"').replace("W/", "")

                # Check if ETag matches our SHA256 checksum directly
                if clean_etag.lower() == expected_checksum.lower():
                    return True, "ETag (SHA256 format)"
                elif len(clean_etag) == len(expected_checksum):
                    # Same length suggests same hash algorithm but different content - INTEGRITY PROBLEM
                    return False, f"ETag mismatch - file integrity problem (expected: {expected_checksum[:8]}..., etag: {clean_etag[:8]}...)"
                elif len(clean_etag) == 32:  # Likely MD5 hash (Apache default)
                    # Convert our file to MD5 for comparison with Apache-style ETags
                    try:
                        with open(local_filepath, 'rb') as f:
                            md5_hash = hashlib.md5(f.read()).hexdigest()
                        if clean_etag.lower() == md5_hash.lower():
                            return True, "ETag (MD5 format)"
                        else:
                            return False, f"ETag mismatch - file integrity problem (expected MD5: {md5_hash[:8]}..., etag: {clean_etag[:8]}...)"
                    except Exception as e:
                        # Fall through to size + accessibility verification
                        pass
                else:
                    # Different length - unknown ETag format from server
                    # For large files, we can't afford to download to verify, so trust size + accessibility
                    if local_size > 100 * 1024 * 1024:  # > 100MB
                        try:
                            head_data = self.webdav_client.download_file_head(remote_path, 8192)
                            if head_data is None:
                                return False, "cannot read remote file"
                            return True, "Size + accessibility (server uses unknown ETag format)"
                        except Exception as e:
                            return False, f"accessibility check failed: {str(e)}"

            # Level 3: No ETag available - fallback verification
            if remote_etag is None and local_size > 100 * 1024 * 1024:  # Large file with no ETag
                try:
                    if self.webdav_client is not None:
                        head_data = self.webdav_client.download_file_head(remote_path, 8192)
                        if head_data is None:
                            return False, "cannot read remote file"
                    return True, "Size + accessibility (ETag unavailable)"
                except Exception as e:
                    return False, f"accessibility check failed: {str(e)}"

            # Level 4: All files - final fallback to size + accessibility verification
            try:
                if self.webdav_client is not None:
                    # Download just the first 8KB to check if file is accessible
                    head_data = self.webdav_client.download_file_head(remote_path, 8192)
                    if head_data is None:
                        return False, "cannot read remote file"

                # For files without ETag, make it clear this is limited verification
                if remote_etag is None:
                    return True, "Size + accessibility (ETag unavailable)"
                else:
                    return True, "Size + accessibility"
            except Exception as e:
                return False, f"accessibility check failed: {str(e)}"

        except Exception as e:
            return False, f"verification error: {str(e)}"

def run_demo():
    """Run demonstration of multiple ETag format support"""

    print("=== PanoramaBridge Multiple ETag Format Support Demo ===\n")

    # Create a test file
    test_file = "/tmp/demo_file.txt"
    with open(test_file, "w") as f:
        f.write("This is test file content for ETag demonstration.\n" * 100)

    # Calculate expected checksum (SHA256)
    with open(test_file, "rb") as f:
        expected_checksum = hashlib.sha256(f.read()).hexdigest()

    print(f"Test file: {test_file}")
    print(f"File size: {os.path.getsize(test_file):,} bytes")
    print(f"SHA256 checksum: {expected_checksum[:16]}...\n")

    # Test scenarios
    scenarios = [
        ("SHA256 ETag Match", "sha256"),
        ("Apache MD5 ETag", "md5"),
        ("Unknown ETag Format", "unknown"),
        ("No ETag Available", "none"),
    ]

    for scenario_name, etag_format in scenarios:
        print(f"--- {scenario_name} ---")

        # Create client with specific ETag format
        client = MockWebDAVClient(etag_format)
        bridge = PanoramaBridgeDemo(client)

        # Test verification
        success, message = bridge.verify_remote_file_integrity(
            test_file,
            "/remote/path/demo_file.txt",
            expected_checksum
        )

        print(f"Status: {'✅ SUCCESS' if success else '❌ FAILED'}")
        print(f"Message: Remote file verified by {message}")

        # Show what ETag the "server" provided
        remote_info = client.get_file_info("/remote/path/demo_file.txt")
        etag = remote_info.get("etag", "None")
        if etag:
            clean_etag = etag.strip('"').replace("W/", "")
            print(f"Server ETag: {clean_etag[:16]}... (length: {len(clean_etag)})")
        else:
            print("Server ETag: Not provided")
        print()

    # Show upload success message format
    print("--- Upload Success Message Example ---")
    print(f"Upload verified successfully: ETag (SHA256 format) (uploaded with checksum: {expected_checksum[:8]}...)")

    # Cleanup
    if os.path.exists(test_file):
        os.unlink(test_file)

    print("\n=== Key Improvements ===")
    print("✅ Removed expensive checksum verification - we generate checksums for upload metadata only")
    print("✅ Enhanced ETag support - handles SHA256, MD5, and unknown formats")
    print("✅ Cleaner verification messages - more concise and informative")
    print("✅ Multiple server compatibility - works with Apache, nginx, and custom servers")
    print("\nNote: Checksums are still generated and stored with uploads for metadata purposes,")
    print("but they are not used for verification due to performance cost.")

if __name__ == "__main__":
    run_demo()
