# PanoramaBridge v26.8.0 Release Notes

Two changes to the window, and nothing to how files are found, sent or verified. The button that
starts and stops monitoring is now colored by which of those it will do, and the settings tabs
explain themselves when you hover a setting rather than in a paragraph under every one of them —
Local Monitoring had grown longer than the screen, which made the settings hard to find among the
explanation of them.

Installed copies update themselves. Nothing needs reinstalling, and no setting changes.

## New Features

- **The monitoring button is now green to start and red to stop.** It is one button that changes
  what it does, and the color says which of the two it is about to do without having to read the
  label. The text is bold. When starting is not possible the button drops the color entirely
  rather than showing a grayed-out green, which still reads as an invitation.

- **The settings tabs explain themselves on hover instead of in gray paragraphs.** Local
  Monitoring and Remote Settings carried more explanation than settings, which buried the things
  you came to change — the whole of Local Monitoring now fits on screen without scrolling. None of
  the explanation is lost: hover a setting, or its label, or the space beside it, and it appears.
  A few settings that had no explanation before have gained a short one, and the note about
  TLS-inspecting proxies now points at the Advanced section rather than saying "below".

  One exception stays on screen: the advice beside the "Files at once" slider, which changes as
  you move the slider and would be no use hidden behind a hover.
