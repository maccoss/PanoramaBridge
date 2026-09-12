# PanoramaBridge v26.10.0 Release Notes

## New Features

- **Each configuration has its own Run button.** Press Run on a row to start that folder
  transferring and Stop to stand it down; green means it will start, red means it will stop. This
  replaces the On tick and the Start monitoring button, which were two switches in series for one
  outcome -- a configuration ran only when both agreed, and neither said so. Stop all is still in
  the toolbar for standing everything down at once.
  - **Nothing starts by itself, including after a restart.** A configuration runs because somebody
    pressed its Run button this session.
  - The status column says which is which: Running; Ready for one that is complete and stopped;
    Needs attention for one with a folder and something wrong with it; and Not set up for one
    nobody has filled in yet.
  - A configuration that cannot start now says so on its own row and leaves the others running. It
    used to take the whole attempt down with it, so one folder nobody had finished setting up
    stopped everything.

## Bug Fixes

- **Adding a configuration stopped monitoring starting at all.** A new configuration was switched
  on, and it has no folder and no destination, so the settings as a whole were invalid and Start
  monitoring refused on the first problem it found -- including for the configurations that were
  already working. A configuration is now added switched off, and the list shows it as "Not set
  up" until you have filled it in and pressed Run.
- **Adding a configuration blanked the destination of the one you were looking at.** The
  where-to-upload box emptied itself whenever the list of configurations was reloaded, so after
  adding one, the previous configuration showed no destination -- and the next save would have
  written that blank. It also made the list report the configuration as needing attention while
  it was still transferring quite happily, because the destination it was being judged on was the
  blank one on screen rather than the one it was actually using.
- **Deleting a configuration did not stop it.** If it was running, it carried on watching its
  folder and transferring to its destination after the row was gone, with nothing left on screen
  that could stop it. Changing the folder, destination or server of a running configuration left
  the same thing behind, still watching the old folder. Either change now stands the configuration
  down, and says so under the list; press Run to start it again.
- **Stopping one configuration could stop another.** If you had started retyping a folder or
  destination for a configuration that was transferring, and had not saved it, pressing Stop on a
  different row stood that one down too. What ought to be running is now judged by what has been
  saved, not by what is currently in the boxes.
- **A settings file that was briefly in use by something else** -- antivirus opening it, or a
  backup agent walking your profile -- was treated as though its contents were bad: moved aside
  and replaced with defaults, so the monitored folder, the destination and the sign-in all
  appeared to vanish. A file that cannot be read right now is retried briefly and then left
  exactly where it is, and PanoramaBridge will not write to it until it has been read, so the
  defaults on screen cannot replace settings it never saw. A file whose contents really are bad is
  still kept for inspection, as before.
- A settings save that could not replace the file left a `settings.json.tmp` beside it, one for
  every save that lost that race.
- Starting or stopping a configuration when the settings file could not be written left the
  control showing a change that had not happened. It now goes back, and the reason is shown under
  the list.
- A sequence file the Thermo data system names for itself -- a bare GUID, like
  `203b13ca-0743-4a98-bed4-5830bfc7d826.sld` -- is no longer transferred. Sequence files somebody
  named and saved still are; only the generated ones are skipped.

## Performance

- Several configurations sending to one Panorama server as the same account now share one
  connection instead of opening one each. Pressing Upload now with eight configurations was
  building and discarding eight connection pools, which meant repeating the TLS handshake for
  every file.

## Breaking Changes

- `pbctl watch --also` now requires `--no-upload`. Every watched folder mirrored into the same
  remote directory, so two files with the same name in different folders overwrote each other on
  the server. The switch exists to measure what several watchers cost, which is what
  `--no-upload` does; to transfer several folders, run `pbctl` once per folder.
