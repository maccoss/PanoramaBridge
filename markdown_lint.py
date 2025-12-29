#!/usr/bin/env python3
"""
Simplified Markdown Linter for PanoramaBridge
Only checks for the most important markdown issues without noise.
"""

import sys
from pathlib import Path


def check_markdown_file(filepath: Path) -> list[tuple[int, str, str]]:
    """Check a markdown file for important issues only."""
    issues = []

    try:
        with open(filepath, encoding="utf-8") as f:
            lines = f.readlines()
    except Exception as e:
        issues.append((0, "FILE_ERROR", f"Could not read file: {e}"))
        return issues

    for i, line in enumerate(lines, 1):
        line_stripped = line.rstrip()

        # Check for extremely long lines (>200 chars) - only problematic cases
        if len(line_stripped) > 200:
            truncated = line_stripped[:100] + "..."
            msg = f"Line extremely long ({len(line_stripped)} chars): "
            msg += truncated
            issues.append((i, "LONG_LINE", msg))

        # Check for fenced code blocks without language
        if line_stripped.startswith("```") and len(line_stripped) == 3:
            msg = "Code block missing language specification"
            issues.append((i, "NO_CODE_LANG", msg))

        # Check for trailing spaces (except markdown line breaks)
        if line_stripped != line.rstrip() and not line_stripped.endswith("  "):
            spaces = len(line) - len(line.rstrip())
            msg = f"Trailing spaces ({spaces} spaces)"
            issues.append((i, "TRAILING_SPACE", msg))

    return issues


def main():
    """Main linting function."""
    if len(sys.argv) < 2:
        print("Usage: python markdown_lint.py <file1.md> [file2.md] ...")
        sys.exit(1)

    total_issues = 0

    for filepath_str in sys.argv[1:]:
        filepath = Path(filepath_str)
        if not filepath.exists():
            print(f"Error: File not found: {filepath}")
            continue

        if filepath.suffix.lower() != ".md":
            print(f"Warning: Skipping non-markdown file: {filepath}")
            continue

        issues = check_markdown_file(filepath)

        if issues:
            print(f"\n{filepath}:")
            for line_num, issue_type, message in issues:
                print(f"  {line_num:4d}: {issue_type:15s} {message}")
            total_issues += len(issues)
        else:
            print(f"OK: {filepath}: No issues found")

    if total_issues == 0:
        print("\nAll markdown files are clean!")
        sys.exit(0)
    else:
        print(f"\nFound {total_issues} issues in total.")
        print("For comprehensive markdown linting, use:")
        print("   pymarkdown scan *.md")
        sys.exit(1)


if __name__ == "__main__":
    main()
