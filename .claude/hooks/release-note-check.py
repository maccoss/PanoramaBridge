"""Reminds, before a commit, that a user-visible change needs a release note.

Not a gate. Plenty of commits legitimately need no note -- refactors, comments,
test-only changes, the handoff document. It cannot tell those from the ones that
matter, and a check that blocks on a judgement it cannot make would be turned off
within a week. So it prints and gets out of the way; exit status is always 0.

It exists because the rule already existed and was still missed. release-notes/README.md
has said "append entries as features and fixes land" since the directory was created,
and five commits went by without one -- including a fix for monitoring stopping
silently. Writing the rule down a third time was not going to be what changed that.
"""

import json
import subprocess
import sys


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return 0

    command = (payload.get("tool_input") or {}).get("command") or ""

    if "git commit" not in command:
        return 0

    try:
        staged = subprocess.run(
            ["git", "diff", "--cached", "--name-only"],
            capture_output=True,
            text=True,
            timeout=10,
            check=False,
        ).stdout.split()
    except Exception:
        return 0

    # Tests do not ship, so a test-only change is not a candidate for a note.
    code = [p for p in staged if p.startswith("src/") and "PanoramaBridge.Tests" not in p]

    if not code or any(p.startswith("release-notes/") for p in staged):
        return 0

    print(json.dumps({
        "systemMessage": (
            "Release note check: this commit changes src/ but not release-notes/."
        ),
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "additionalContext": (
                "This commit touches src/ but not release-notes/. If it changes anything "
                "a user of PanoramaBridge can observe -- behaviour, a message they read, a "
                "setting, performance they would notice -- add the entry to "
                "release-notes/RELEASE_NOTES_next.md now, in this same commit, rather than "
                "reconstructing it from git log at release time. If the change is internal "
                "only, say so in one clause of the commit message and carry on. "
                "This is a reminder, not a gate: do not re-run the commit to satisfy it."
            ),
        },
    }))

    return 0


if __name__ == "__main__":
    sys.exit(main())
