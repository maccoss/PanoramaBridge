# PanoramaBridge Documentation

PanoramaBridge is a native Windows application built on .NET 8 and WPF.

| Document | What it covers |
|---|---|
| **[.NET port handoff](DOTNET_PORT_HANDOFF.md)** | The one to read first. Architecture, verified server behaviour, measured costs, and the traps that cost real time to learn. |
| **[AI development guide](../CLAUDE.md)** | House style, layout, build and release commands. |
| **[Release process](../release-notes/README.md)** | Versioning, writing release notes, and how a release is actually published. |
| **[Release notes](../release-notes/)** | One file per version; each becomes the GitHub Release body. |

Architecture questions -- how files are found, when a file counts as finished, how an upload is
verified, what any of it costs -- are answered in the port handoff rather than in separate
documents. Keeping one page current is what stopped the previous set of a dozen topic documents
from quietly disagreeing with the code and with each other.

## The retired Python application

A Python/PyQt6 implementation preceded this one. It was removed from the repository after
v26.1.0 shipped and is not a reference: it was never put into production, so agreement with it
proves nothing and disagreement is not evidence of a regression.

Its source and its documentation remain fetchable from the `v0.1.9rc4` tag:

```bash
git show v0.1.9rc4:panoramabridge.py
git show v0.1.9rc4:docs/README.md      # index of the documentation as it stood
```
