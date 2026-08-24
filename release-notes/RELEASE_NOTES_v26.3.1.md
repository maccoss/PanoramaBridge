# PanoramaBridge v26.3.1 Release Notes

A patch release. Monitoring could stop without saying so when a network share hiccupped; that is
fixed, along with three smaller faults in the folder-acquisition support v26.3.0 introduced.

Installed copies update themselves. Nothing needs reinstalling, and no setting changes.

## Bug Fixes

- **Monitoring could stop, quietly, if a network share hiccupped.** While checking whether
  anything inside a folder acquisition was still being written, a momentary failure to read the
  share was not handled. It stopped monitoring for **every** folder being watched, not just the
  one that failed, and nothing in the window said so — the application went on looking healthy
  with nothing being transferred.

  If you have seen monitoring appear to be running while nothing moved, and restarting it fixed
  the problem, this is the most likely explanation.

  A folder that cannot be read is now treated as one that might still be being written: it is
  held back rather than either sent or given up on, and it is retried.

- **An acquisition folder could be judged finished on a single look.** PanoramaBridge normally
  requires a folder to be seen unchanged more than once before it is sent. If a `.d` was renamed
  away and a new acquisition of exactly the same size, file count and timestamp was later written
  to the same path, the second one could be treated as though it had already been watched, and
  sent immediately.

  Sending an acquisition before it is finished is the one thing this application must never do,
  so this is fixed regardless of how narrow it is — and it is narrow: it needs the replacement
  to match the original in all three numbers.

- **An empty acquisition folder was checked for ever.** A `.d` created but never written into —
  an aborted run, most often — was re-examined on every pass for as long as the application was
  open, doing real disk work on the instrument computer for a folder that would never be sent.
  It is now left alone after a short wait, and picked up normally if an acquisition is written
  into it later.

- **Some messages left the folder name blank.** Depending on how a path was written, a message
  about a folder could read `'' is no longer there` instead of naming it. It now names it.

- **A folder still being written now says what changed.** The message reported only the folder's
  size, so a folder that had gained a file without changing size said "1,048,576 to 1,048,576
  bytes" — naming the one number that had not moved. It now reports whichever of size, file
  count or last-write time actually changed.
