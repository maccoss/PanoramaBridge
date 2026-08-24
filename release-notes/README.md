# Release Notes

This directory contains per-version release notes for PanoramaBridge.

## Versioning Scheme

PanoramaBridge uses a `YY.feature.patch` versioning convention:

- **YY**: Two-digit year (e.g., `26` for 2026)
- **feature**: Incremented for each release containing new features, restarting each year
- **patch**: Incremented for bug-fix-only releases within the same feature version

Examples: `26.1.0` (first feature release of 2026), `26.1.1` (patch), `26.2.0` (second feature release).

The version lives in exactly one place, `Directory.Build.props`, and is updated at release time
rather than during development. `release.yml` refuses to publish when the tag and that property
disagree.

> The retired Python package used a `0.1.x` scheme and its own `v0.1.*` tags. The .NET
> application continues in the same `v*` tag namespace under CalVer, so the numbers move from
> `0.1.9rc4` to `26.1.0`. There is no overlap.

## File Format

Each release gets one file: `RELEASE_NOTES_v{version}.md`. During development, the unreleased
draft lives in `RELEASE_NOTES_next.md` and gets renamed at release time.

```text
release-notes/
  README.md                      # this file
  RELEASE_NOTES_next.md          # working draft for the next release
  RELEASE_NOTES_v26.1.0.md
  RELEASE_NOTES_v26.1.1.md
```

## Writing Release Notes

### During Development

Maintain `RELEASE_NOTES_next.md` as a working draft for the next planned version. Append entries
as features and fixes land — **in the same commit as the change**, not at release time. A
`PreToolUse` hook in `.claude/settings.json` prints a reminder when a commit touches `src/`
without touching this directory; it never blocks, because it cannot tell a refactor from a fix.
See the release-note section of `CLAUDE.md` for what counts as user-visible. The file stays unversioned until the release is finalized so the
target version can still change (a planned patch release becomes a feature release once new
functionality lands).

### Content Structure

```markdown
# PanoramaBridge v{version} Release Notes

One-sentence summary of the release.

## New Features

- Grouped by area (Monitoring, Transfers, Verification, Updates)
- Focus on what changed from the user's perspective, not implementation details

## Bug Fixes

- The bug, its impact, and what was fixed

## Performance

- Improvements with context ("A 5 GB .raw now uploads in one disk pass instead of two")

## Breaking Changes

- Anything requiring user action. Omit the section when there is nothing.
```

Sections can be omitted if empty.

### Style

- Write in past tense ("Added", "Fixed", "Removed")
- Lead with user impact, not implementation details
- Include specific numbers where relevant (transfer rates, file sizes, request counts)
- Reference settings by the label the user sees in the UI

## Release Process

> [!IMPORTANT]
> **This file becomes the GitHub Release description.** `release.yml` publishes
> `release-notes/RELEASE_NOTES_v{version}.md` verbatim as the Release body, so write it for the
> people reading the Releases page. The rename in step 1 therefore has to happen **before**
> tagging: the workflow resolves the path from the tag and fails with an explicit message if the
> file is missing, after the artifacts have already been built.

1. Finalize `RELEASE_NOTES_next.md`; rename it to `RELEASE_NOTES_v{version}.md` and update its
   heading; create a fresh empty `RELEASE_NOTES_next.md`
2. Bump `<Version>` in `Directory.Build.props` to `{version}`
3. Commit and merge to `main`
4. Tag: `git tag v{version}`
5. Push the tag: `git push origin v{version}` — **pushing the tag both builds the artifacts and
   creates the GitHub Release.** Do not hand-create the Release.

A tag containing `alpha`, `beta`, or `rc` is published as a GitHub prerelease. A tag containing
`beta` additionally goes to the `win-beta` update channel, so it can be piloted on one instrument
before the whole lab sees it; every other tag goes to the stable `win` channel.

To fix an existing Release:

```bash
gh release edit v<version> --notes-file release-notes/RELEASE_NOTES_v<version>.md
```

## What the Release Workflow Produces

Velopack packs the publish folder into:

| Asset | Purpose |
|---|---|
| `MacCossLab.PanoramaBridge-win-Setup.exe` | Per-user installer, no administrator rights needed |
| `MacCossLab.PanoramaBridge-{version}-full.nupkg` | Full package, used for first install and as a delta base |
| `MacCossLab.PanoramaBridge-{version}-delta.nupkg` | Difference from the previous release, typically a few hundred KB |
| `MacCossLab.PanoramaBridge-win-Portable.zip` | Portable copy for machines where installing is not an option |
| `releases.win.json` | The update feed installed builds read |
| `SHA256SUMS.txt` | Checksums for every asset |

Installed copies check this feed at startup and every four hours, download in the background, and
apply the update on the next restart. An upload in progress is never interrupted.
