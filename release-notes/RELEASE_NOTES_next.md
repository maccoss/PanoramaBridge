# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

## New Features

## New Features

- **A held file now tells you what to do about it.** The Transfers tab said *"N transfer(s) need
  attention"* and pointed you at the reason on each row; each row's state said *"Needs a
  decision"*, which named per-file buttons removed in v26.5.0. Between them the window asked for
  an action it no longer offered anywhere, and never named the setting that actually resolves
  these files.

  The state now reads **Held**, and the banner says what to do: choose an option under **When a
  different file is already on the server** on the Local Monitoring tab, save, and press **Check
  now**. It also says the two things that were easy to get wrong — that the choice applies to
  every held file at once, and that renaming or moving one file is how you handle it on its own.

- **"Ask me" is now "Hold it and show it under Needs attention".** Nothing asked: there is no
  prompt and no per-file button, so the old label promised an interaction that never arrives.

## Bug Fixes

- **A decision made in an older version can no longer be undone by the conflict setting.** Files
  you chose to keep the server's copy of, or sent under a new name, in v26.3.0—v26.4.6 are held
  under **Needs attention**. Setting **Replace the copy on the server** used to release them on
  the next folder check and send each file to its *original* name — replacing the very
  copy your earlier choice existed to preserve, without asking. They now stay held under that
  setting. **Leave the copy on the server alone** still clears them, safely, because it sends
  nothing; and changing the file itself reopens the question.

  This also holds if such a record is written after moving back to an older version and updating
  again — previously it was converted only once, on the first update.

- **A file held for a reason the setting cannot answer stays held, whichever route reaches it.**
  Two holds — a damaged file, and a decision carried over from v26.3.0—v26.4.6 — were only
  applied during a folder check, and only while the file's state had not moved on. So there were
  two ways past them: set *Leave the copy on the server alone* once and then change to *Replace
  the copy on the server*, or simply save the file again and let the folder watcher pick it up.
  Either sent a file that was being protected. The check now travels with the file rather than
  with how it was found, and a held file costs no request to the server while it waits.

  Replacing the file itself still reopens the question, as before. That is the way out of both
  holds.

- **A damaged file is no longer re-examined on every folder check.** A file held because reading
  it shows it ends before its data does was released by the *Leave alone* and *Replace* settings,
  re-checked against the server, and held again — on every check, indefinitely. Neither setting
  is an answer to a broken file, so it now stays held, whatever the setting, until the file
  changes. It was never at risk of being sent.

- **Interrupted transfers under a folder you no longer monitor are left alone too.** The check
  for an unreachable folder looked only at the folder being monitored now, while the record of
  interrupted transfers outlives that setting — so a transfer recorded under a previous folder
  was still written off as deleted whenever that folder happened to be offline. Each folder is
  now checked in its own right, and once per folder rather than once per file, so an offline
  server does not slow startup.

- **Nothing is written off while the monitored folder is unreachable.** Interrupted transfers are
  checked at startup, which on a network share is often before the share is available — and an
  unreachable file answers exactly as a deleted one. They are now left as they are until the
  folder can be seen, instead of being marked failed with a message saying the data no longer
  exists.

- **An interrupted folder upload from an older version now fails with a reason.** Sending folders
  as a single archive has been removed, so such an upload cannot be resumed — but it was being
  quietly re-queued and dropped on every start, staying "uploading" forever. It is now marked
  failed with an explanation, and the folder itself is untouched.

## Performance

## Breaking Changes
