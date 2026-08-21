# PanoramaBridge

[![CI](https://github.com/maccoss/PanoramaBridge/actions/workflows/ci.yml/badge.svg)](https://github.com/maccoss/PanoramaBridge/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/maccoss/PanoramaBridge?label=release)](https://github.com/maccoss/PanoramaBridge/releases/latest)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)

Watches the folder your mass spectrometer writes into and transfers each acquisition to a
Panorama (LabKey) server as it finishes, confirming every upload against the checksum the server
computes over the bytes it stored.

A native Windows application built on .NET 8 and WPF. It runs on the instrument computer
alongside the acquisition software, so it is built to stay out of the way: transfers run at
below-normal priority, and watching a folder costs about 0.03% of one processor core.

## Installing

Download `MacCossLab.PanoramaBridge-win-Setup.exe` from the
[latest release](https://github.com/maccoss/PanoramaBridge/releases/latest) and run it. It
installs for the current user only and needs no administrator rights, so it works on a
locked-down instrument PC. Nothing else has to be installed first.

The build is not code-signed, so SmartScreen warns on first run: choose **More info**, then
**Run anyway**. `SHA256SUMS.txt` is published with every release if you would like to check the
download.

Installed copies update themselves: they check at startup and every four hours, download in the
background, and apply on the next restart. An upload in progress is never interrupted. If a copy
falls below the minimum version published for the lab it says so and refuses to start new
uploads until it has updated, while letting transfers already running finish.

A portable `.zip` is published as well, for machines where installing is not an option.

## Getting started

1. **Remote Settings** -- enter your Panorama server and an API key (User menu, then External
   Tool Access on Panorama), and press **Test connection**. It reports whether the destination is
   writable before you start a six-hour transfer rather than after.
2. **Local Monitoring** -- choose the folder your instrument writes into and the extensions to
   transfer: `.raw`, `.wiff`, `.mzML`, `.sld`, or whatever else that instrument produces.
3. **Start monitoring**. Files are transferred as they finish being written.

**Upload now** does a single pass instead, for anyone who would rather drive it by hand.

## What it does

- **Watches continuously.** The folder is walked in full every fifteen minutes by default, and
  that walk is what guarantees nothing is missed. Windows change notifications are used as well,
  so a file usually starts within seconds of appearing, but they are treated as a bonus: they are
  dropped under load and are server-dependent over a network share.
- **Never uploads a half-written file.** A file transfers only once nothing else holds it open
  *and* its size has stopped changing. Both are required: an instrument often leaves its output
  readable while still writing, and Windows does not keep a file's recorded size up to date while
  a write handle is open.
- **Never overstates what it checked.** The record distinguishes *Verified (server MD5)* from
  *Uploaded - size only* from *Not verified*, because those are three different claims.
- **Leaves a record with the data.** A small `.md5` file is written beside each upload holding its
  checksum, its size and the date the instrument wrote it. The first line is exactly what `md5sum`
  writes, so `md5sum -c run.raw.md5` works years later with no special tooling.
- **Keeps the collection date.** A file on Panorama shows the date it was acquired, not the date
  it was transferred.
- **Remembers.** The Uploads tab reads a durable record rather than the transfer list, so "did
  that actually get uploaded?" is still answerable next week or on a rebuilt machine.
- **Keeps watching when you close the window.** Closing leaves it running in the notification
  area, so monitoring is not something you can end by tidying your taskbar. Click the icon to
  bring the window back -- or just start PanoramaBridge again, which does the same thing rather
  than opening a second copy. Exit on the icon's menu stops it. Turn the whole behaviour off in
  Remote Settings if you would rather closing meant closing.

`pbctl`, a command-line harness, ships alongside for scripted transfers and for measuring what
monitoring costs on a given machine.

## What it looks like

Four tabs: settings on the first two, and what is happening on the last two.

### Local Monitoring

Where to watch, and when a file counts as finished. The path shown is a UNC path because a mapped
drive was chosen and resolved to the share it stands for -- a drive letter belongs to one Windows
sign-in, so storing one would make the folder invisible to a service or a scheduled task.

![The Local Monitoring tab](screenshots/localmonitoring.png)

### Remote Settings

Where to upload, and how to sign in. An API key is preferred over the account password: it can be
revoked without changing the password, limited to a role, and expires on its own.

![The Remote Settings tab](screenshots/remotesettings.png)

### Transfer Status

What is happening now. Rows are grouped by what they are doing rather than when they turned up:
transfers in progress first, then anything needing a decision, then what has finished, then files
still waiting.

![The Transfer Status tab](screenshots/transferstatus.png)

### Uploads

The durable record. It filters, searches, and exports to CSV. Note the Checked column: *Server
MD5* and *Not verified* are deliberately not the same claim.

![The Uploads tab](screenshots/uploads.png)

## Support

1. **Logs** -- **View**, then **Application logs**; or `%LOCALAPPDATA%\PanoramaBridge\logs`.
   Credentials are scrubbed from them, so a log is safe to attach to a support request.
2. **Test connection** -- reports whether the server is reachable and the destination writable.
3. **[Open an issue](https://github.com/maccoss/PanoramaBridge/issues)** -- please include the
   version from the title bar and the relevant log.

## Building from source

```bash
dotnet build PanoramaBridge.sln -c Release
dotnet test  PanoramaBridge.sln -c Release      # no network required
src/PanoramaBridge.App/bin/Release/net8.0-windows/PanoramaBridge.exe
```

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

## Additional documentation

The topics that used to have a page each are answered in
[`docs/DOTNET_PORT_HANDOFF.md`](docs/DOTNET_PORT_HANDOFF.md), which is kept current. One page
that matches the code beats a dozen that quietly stop matching it -- which is what happened to
the set this replaces.

| Question | Where it is answered |
|---|---|
| How files are found, and why a full sweep rather than file-system events alone | [Handoff, §7 Continuous monitoring](docs/DOTNET_PORT_HANDOFF.md#7-continuous-monitoring-as-built) |
| When a file counts as finished, and why two independent signals are required | [Handoff, §6 Correctness](docs/DOTNET_PORT_HANDOFF.md#correctness) |
| How an upload is verified, and what *Verified (server MD5)* claims that *Uploaded* does not | [Handoff, §5 Verified server facts](docs/DOTNET_PORT_HANDOFF.md#5-verified-server-facts--do-not-re-discover-these) |
| What checksums are cached, where, and what the fast path avoids re-reading | [Handoff, §6 Correctness](docs/DOTNET_PORT_HANDOFF.md#correctness) |
| What monitoring costs on an instrument computer, measured | [Handoff, §7 What it costs](docs/DOTNET_PORT_HANDOFF.md#what-it-costs) and [§6 Resource use](docs/DOTNET_PORT_HANDOFF.md#resource-use-on-an-instrument-computer) |
| Which project a given piece of logic belongs in | [Handoff, §3 Layout](docs/DOTNET_PORT_HANDOFF.md#3-layout) and [`CLAUDE.md`](CLAUDE.md#layout) |
| Running the tests, and the suites that need a server or a share | [`CLAUDE.md`](CLAUDE.md#building-and-testing) and [Handoff, §2](docs/DOTNET_PORT_HANDOFF.md#opt-in-test-suites) |
| Driving the transport by hand, without the UI | [`pbctl`](docs/DOTNET_PORT_HANDOFF.md#pbctl-the-headless-harness) |
| How a release is built and published | [`release-notes/README.md`](release-notes/README.md) |
| Conventions to follow when changing this code | [`CLAUDE.md`](CLAUDE.md#house-style) |

Builds and releases are produced by [`ci.yml`](.github/workflows/ci.yml) and
[`release.yml`](.github/workflows/release.yml); pushing a `v*` tag is what creates a GitHub
Release, and nothing is built locally for distribution.

The retired `panoramabridge` PyPI package is a different application and shares no settings or
history with this one. Install the Windows installer above instead; nothing needs uninstalling
first, and the old `~/.panoramabridge/` directory can be deleted. Its source remains fetchable
from the `v0.1.9rc4` tag.
