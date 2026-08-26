# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

## New Features

- **A held file now tells you what to do about it.** The Transfers tab said *"N transfer(s) need
  attention"* and pointed you at the reason on each row; each row's state said *"Needs a
  decision"*, which named per-file buttons removed in v26.5.0. Between them the window asked for
  an action it no longer offered anywhere, and never named the setting that actually resolves
  these files.

  The state now reads **Held**, and the banner says what each kind of row is waiting for. For a
  held file: choose an option under **When a different file is already on the server** on the
  Local Monitoring tab, save, and then stop and start monitoring — that setting is read when
  monitoring starts, so saving on its own does not reach files that are already held. It also
  says the two things that were easy to get wrong: that the choice applies to every held file at
  once, and that renaming or moving one file is how you handle it on its own.

- **"Ask me" is now "Hold it and show it under Needs attention".** Nothing asked: there is no
  prompt and no per-file button, so the old label promised an interaction that never arrives.

## Bug Fixes

- **Records left by the withdrawn per-file conflict choices are carried over every time, not
  once.** A file you chose to keep the server's copy of, or sent under a new name, in
  v26.3.0—v26.4.6 becomes an ordinary held file under **Needs attention**, with an explanation
  of where it came from. This now happens on every start rather than only on the first update, so
  it still applies if such a record is written after moving back to an older version and coming
  forward again.

- **A file that cannot be placed on the server says why, in words you can act on.** If you
  change the folder being monitored, PanoramaBridge still holds records for files under the old
  one, and there is nowhere on the server those belong. They were retried and shown with a
  programmer's error ending in *(Parameter 'localFilePath')*. Each now fails once and says which
  folder it is outside of, that nothing was sent, that the file has not been touched, and what
  would change that. Files rejected for other reasons — a semicolon in the name, which Panorama
  truncates at — keep their own advice, which tells you to rename the file.

- **A folder is recorded as not sendable rather than quietly ignored.** Sending a folder as a
  single archive was withdrawn. Such a folder was then dropped with nothing written down, so
  anything already tracking it stayed as it was and it was picked up and dropped again on every
  check — and one interrupted mid-upload by an older version sat at *uploading* forever. Both
  now say why, and the folder itself is untouched. Its files can still be sent individually if
  their types are listed.

- **A transfer waiting on a drive that is not there says so.** These are left alone until the
  drive or share comes back, which was already true, but the row said only that an upload was in
  progress — it appeared under neither *Verified* nor *Needs attention* and the reason was only
  in the log. The row now carries it.

- **A damaged file stays held whichever setting you choose, and whichever way it is found.** A
  file held because reading it shows it ends before its data does was released by *Leave the copy
  on the server alone* and by *Replace the copy on the server*, and released again if you simply
  saved it and the folder watcher noticed. Neither setting is an answer to a broken file, so it
  now stays held until the file itself changes — which is the way out, and works as soon as you
  re-copy it. Holding one costs no request to the server.

  Nothing about it was ever at risk of being sent.

- **Nothing is written off while its drive or share is unreachable.** Interrupted transfers are
  checked at startup, which on a network share is often before the share is available — and an
  unreachable file answers exactly as a deleted one. They are now left as they are until the drive
  or share can be seen, instead of being marked failed with a message saying the data no longer
  exists. This covers transfers recorded while a different folder was being monitored, because the
  record of them outlives that setting. A drive that is reachable with the folder genuinely gone is
  still recorded as gone.

## Performance

- **A held file no longer costs a folder listing and a full read of the acquisition.** Every check
  of a file that was being held asked the server what was at its destination and, when the name and
  size matched, read the whole acquisition through to compare it — to reach the answer it already
  had. On a folder of held acquisitions that was gigabytes of reading per check, repeated for as
  long as they stayed held. Holding one now costs nothing.

- **Starting up no longer reads the whole upload record.** A check for records left by the
  withdrawn per-file conflict choices runs at every start and was reading every row to find them.
  It is two indexed lookups now, which on a ledger of a few hundred thousand acquisitions is the
  difference between reading the table at every launch and touching almost nothing.

## Breaking Changes
