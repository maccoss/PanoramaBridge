# PanoramaBridge v26.4.2 Release Notes

A patch release, fixing faults in the conflict handling that v26.4.1 introduced or left behind.
The most important one made the three decision buttons stop working after the first time you used
them on a picked file.

Installed copies update themselves. Nothing needs reinstalling, and no setting changes.

## Bug Fixes

- **The decision buttons could stop working for the rest of the session.** If you ticked a file,
  decided about it, and then tried to decide about anything else, nothing happened — and nothing
  said why. The tick was remembered after the file stopped being held, and it could not be cleared
  by hand because the tick box is switched off once a file is settled. Every later decision then
  meant "the held files you have ticked", of which there were none.

  If you have pressed one of these buttons and seen nothing change, this is why. Nothing was lost:
  the files were still held, and can be decided about now.

- **A rename that could not be carried out forgot where the file already lived.** If a file had
  been sent alongside as `run (2).raw` and a second rename was refused because the new name was
  taken too, the file lost the record of its own copy. Pressing **Replace what is on the server**
  afterwards then replaced the *original* file instead — the one you chose to keep.

- **A rename refused because a transfer had started is now reported.** Replace and Keep already
  said so; rename did not, so files could quietly stay held while you believed they had been
  renamed.

- **Settling a conflict no longer leaves the progress display busy.** Choosing **Replace** or
  **Send alongside** added a row to the transfer list that nothing ever removed, so the display
  kept refreshing several times a second indefinitely and the overall progress bar sat at zero.

- **Files kept from the Uploads tab now appear with the finished transfers** rather than among the
  ones still waiting, and are cleared away with them.

- **A decision that could not be applied says less, and means it.** The message named one cause as
  fact; there are several, and they need different remedies.

## Performance

- **Deciding about picked files no longer reads every conflict on the machine.** The picked files
  are fetched by name, and the Uploads tab counts the record once per refresh instead of twice.
