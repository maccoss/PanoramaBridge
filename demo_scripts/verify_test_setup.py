#!/usr/bin/env python3
"""
VS Code Test Integration Verification Script
Run this to verify that the test framework is properly configured.
"""

import json
import os
import subprocess
import sys
from pathlib import Path


def check_vscode_configuration():
    """Check VS Code configuration files."""
    print("🔍 Checking VS Code Configuration...")

    vscode_dir = Path(".vscode")
    if not vscode_dir.exists():
        print("❌ .vscode directory not found")
        return False

    required_files = ["settings.json", "launch.json", "tasks.json", "extensions.json"]
    for file_name in required_files:
        file_path = vscode_dir / file_name
        if file_path.exists():
            print(f"✅ {file_name} found")
        else:
            print(f"❌ {file_name} missing")

    return True


def check_pytest_installation():
    """Check if pytest is properly installed."""
    print("\n🔍 Checking pytest installation...")

    venv_python = Path(".venv/bin/python")
    if not venv_python.exists():
        print("❌ Virtual environment not found at .venv/bin/python")
        return False

    try:
        result = subprocess.run(
            [str(venv_python), "-m", "pytest", "--version"], capture_output=True, text=True, cwd="."
        )
        if result.returncode == 0:
            print(f"✅ pytest installed: {result.stdout.strip()}")
            return True
        else:
            print(f"❌ pytest not working: {result.stderr}")
            return False
    except Exception as e:
        print(f"❌ Error checking pytest: {e}")
        return False


def check_test_discovery():
    """Check if pytest can discover tests."""
    print("\n🔍 Checking test discovery...")

    try:
        result = subprocess.run(
            [".venv/bin/python", "-m", "pytest", "--collect-only", "-q"],
            capture_output=True,
            text=True,
            cwd=".",
        )

        if result.returncode == 0:
            lines = result.stdout.strip().split("\n")
            test_count = 0
            for line in lines:
                if "::test_" in line:
                    test_count += 1

            print(f"✅ Test discovery successful: {test_count} tests found")
            return True
        else:
            print(f"❌ Test discovery failed: {result.stderr}")
            return False
    except Exception as e:
        print(f"❌ Error during test discovery: {e}")
        return False


def run_sample_test():
    """Run a sample test to verify everything works."""
    print("\n🔍 Running sample WebDAV tests...")

    try:
        result = subprocess.run(
            [".venv/bin/python", "-m", "pytest", "tests/test_webdav_client.py", "-v", "--tb=short"],
            capture_output=True,
            text=True,
            cwd=".",
        )

        if "PASSED" in result.stdout and result.returncode == 0:
            passed_count = result.stdout.count("PASSED")
            print(f"✅ Sample tests successful: {passed_count} tests passed")
            return True
        else:
            print("❌ Sample tests failed")
            print("STDOUT:", result.stdout)
            print("STDERR:", result.stderr)
            return False
    except Exception as e:
        print(f"❌ Error running sample tests: {e}")
        return False


def main():
    """Main verification function."""
    print("=== VS Code Python Test Explorer Setup Verification ===\n")

    all_good = True
    all_good &= check_vscode_configuration()
    all_good &= check_pytest_installation()
    all_good &= check_test_discovery()
    all_good &= run_sample_test()

    print("\n" + "=" * 60)
    if all_good:
        print("🎉 SUCCESS: VS Code Test Explorer is ready!")
        print("\nNext steps:")
        print("1. Open this project in VS Code")
        print("2. Install recommended extensions when prompted")
        print(
            "3. Open the Test Explorer panel (Ctrl+Shift+P → 'Test: Focus on Test Explorer View')"
        )
        print("4. Your tests should appear automatically!")
        print("5. Right-click on tests to run, debug, or view them")
    else:
        print("❌ ISSUES FOUND: Please fix the above problems before using Test Explorer")

    print("=" * 60)
    return all_good


if __name__ == "__main__":
    success = main()
    sys.exit(0 if success else 1)
