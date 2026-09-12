# PanoramaBridge v26.10.2 Release Notes

Two things the 26.10.1 fix made visible, both reported from an instrument.

## Bug Fixes

- **The status line kept saying files were waiting after they had all transferred.**
  "Monitoring - 5 file(s) to transfer." stayed on screen with all five verified and the window
  idle, until the next folder check fifteen minutes later replaced it. A folder check reports what
  it found at the time and then says nothing more, so the count never came down. It now falls as
  the files go, and reads as up to date once they have gone.

- **A failed transfer could never be cleared from the Uploads list.** The record of an attempt
  stays until the file transfers, which is right while there is any prospect of that happening --
  and wrong once there is not. Files this version deliberately skips, such as the sequence files
  Thermo names for itself, will never be attempted again, so their old failures sat under
  "needs attention" permanently with nothing to be done about them.

  Selecting such a row now enables a **Dismiss** button on the Uploads tab, which removes it after
  asking. Nothing on Panorama changes, and a file that is still being monitored is simply found
  again on the next check and sent.

  Only a failed, conflicted or superseded row can be dismissed. A row saying a file reached the
  server is refused, by the record itself and not merely by the button: on a rebuilt machine that
  row is the only evidence the file is there.
