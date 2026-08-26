# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

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

- **Records left by the withdrawn per-file conflict choices are carried over every time, not
  once.** A file you chose to keep the server's copy of, or sent under a new name, in
  v26.3.0—v26.4.6 becomes an ordinary held file under **Needs attention**, with an explanation
  of where it came from. This now happens on every start rather than only on the first update, so
  it still applies if such a record is written after moving back to an older version and coming
  forward again.

- **A damaged file stays held whichever setting you choose, and whichever way it is found.** A
  file held because reading it shows it ends before its data does was released by *Leave the copy
  on the server alone* and by *Replace the copy on the server*, and released again if you simply
  saved it and the folder watcher noticed. Neither setting is an answer to a broken file, so it
  now stays held until the file itself changes — which is the way out, and works as soon as you
  re-copy it. Holding one costs no request to the server.

  Previously it was also re-checked against the server on every folder check, reading the whole
  acquisition each time, only to be held again.

- **A damaged file is no longer re-examined on every folder check.** A file held because reading
  it shows it ends before its data does was released by the *Leave alone* and *Replace* settings,
  re-checked against the server, and held again — on every check, indefinitely. Neither setting
  is an answer to a broken file, so it now stays held, whatever the setting, until the file
  changes. It was never at risk of being sent.

- **Nothing is written off while its drive or share is unreachable.** Interrupted transfers are
  checked at startup, which on a network share is often before the share is available — and an
  unreachable file answers exactly as a deleted one. They are now left as they are until the drive
  or share can be seen, instead of being marked failed with a message saying the data no longer
  exists. This covers transfers recorded while a different folder was being monitored, because the
  record of them outlives that setting. A drive that is reachable with the folder genuinely gone is
  still recorded as gone.

- **An interrupted folder upload from an older version now fails with a reason.** Sending folders
  as a single archive has been removed, so such an upload cannot be resumed — but it was being
  quietly re-queued and dropped on every start, staying "uploading" forever. It is now marked
  failed with an explanation, and the folder itself is untouched.

## Performance

## Breaking Changes
