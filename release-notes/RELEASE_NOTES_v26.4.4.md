# PanoramaBridge v26.4.4 Release Notes

A patch release. The messages the Uploads tab shows after a decision were sometimes invisible,
sometimes wrong, and sometimes absent; they are now worked out from what actually happened. Also
fixes a rare case where settling a conflict could let PanoramaBridge restart during an upload.

Installed copies update themselves. Nothing needs reinstalling, and no setting changes.

## Bug Fixes

- **Settling a conflict could let PanoramaBridge restart mid-upload.** If a transfer picked a file
  up in the moment between your decision being recorded and the display being updated, the file
  was dropped from the list of work in progress — and the update prompt and the tray **Exit** both
  read that list to decide whether anything was moving. Both could then restart the application
  during the upload. A transfer that has started is never dropped now.

- **The explanation after a decision could be invisible.** It was drawn inside the panel that only
  appears while files are still held, so in the case it exists for — a decision that settled or
  found nothing — the panel had just closed and took the message with it. The button looked as
  though it had done nothing. It now has its own place on the tab.

- **A file left out because its contents are damaged is named as such.** Depending on which button
  you pressed, it was either not mentioned at all or described as one a transfer had picked up,
  which sent you looking for something that never ran. All three buttons now report it the same
  way.

- **A tick is no longer used up by a decision that was refused.** If a transfer had picked the file
  up, the decision was correctly refused but the tick was cleared anyway, so pressing the button
  again could apply to every held file rather than the one still shown ticked.

## Performance

- **Settling a large batch no longer freezes the window.** Clearing several hundred conflicts
  recalculated the whole transfer summary once per file. It is now done once, when the display
  next refreshes.
