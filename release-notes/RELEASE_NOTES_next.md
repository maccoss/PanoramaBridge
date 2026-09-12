# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

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

- A settings file that was briefly in use by something else -- antivirus opening it, or a backup
  agent walking your profile -- was treated as though its contents were bad: moved aside and
  replaced with defaults, so the monitored folder, the destination and the sign-in all appeared to
  vanish. A file that cannot be read right now is now retried briefly and then left exactly where
  it is. A file whose contents really are bad is still kept for inspection, as before.
- A settings save that could not replace the file left a `settings.json.tmp` beside it, one for
  every save that lost that race.
- **And the defaults from a settings file that could not be read are no longer written back over
  it.** Leaving the file alone was only half the job: what the window then showed was defaults,
  and the next thing that saved would have put those in the file. Saving is refused, with a
  message saying what to close and to start again, until the settings have actually been read.
- On the Configurations tab, starting or stopping a configuration when the settings file could
  not be written left the button showing a change that had not happened. It now goes back and the
  reason is shown under the list.
- **Adding a configuration blanked the destination of the one you were looking at.** The
  where-to-upload box emptied itself whenever the list of configurations was reloaded, so after
  adding one, the previous configuration showed no destination -- and the next save would have
  written that blank. It also made the list report the configuration as needing attention while
  it was still transferring quite happily, because the destination it was being judged on was the
  blank one on screen rather than the one it was actually using.
- A sequence file the Thermo data system names for itself -- a bare GUID, like
  `203b13ca-0743-4a98-bed4-5830bfc7d826.sld` -- is no longer transferred. Sequence files somebody
  named and saved still are; only the generated ones are skipped.
- **Adding a configuration stopped monitoring starting at all.** A new configuration was switched
  on, and it has no folder and no destination, so the settings as a whole were invalid and Start
  monitoring refused on the first problem it found -- including for the configurations that were
  already working. A configuration is now added switched off, and the list shows it as "Not set
  up" until you have filled it in and ticked it.

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
