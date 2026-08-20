# Retiring the Python application

The Python/PyQt6 application is superseded by the .NET one as of **v26.1.0**. It is kept in the
repository for now and removed in stages, so that nothing is deleted before the thing that
replaces it has been shown to work on real instruments.

Nothing here is urgent. The reason to write it down is that a retirement left implicit tends to
be half-done for years: the code stops being maintained but stays in the build, in the test
suite, in the security scanning surface, and in every search result someone gets while looking
for the current implementation.

---

## Where it stands today

| | |
|---|---|
| Source | `panoramabridge.py` and its modules, still on `main` and in git history |
| Package | `panoramabridge` on PyPI, last published as `0.1.9rc4` |
| Publishing | `publish-pypi.yml`, trigger reduced to `workflow_dispatch` only |
| Documentation | The retired section at the bottom of `README.md` |
| Build scripts | `build_scripts/build_windows.ps1`, `build_windows_arm64.ps1` |

`publish-pypi.yml` no longer fires on release. That was deliberate and is the one piece already
done: left alone it would have tried to publish the Python package on every .NET release.

---

## Stage 1 — while 26.1.0 is being adopted

**Do nothing to the code.** Anyone who hits a problem with the new application needs to be able
to fall back, and "reinstall the old one" is only an answer while the old one still exists.

- [x] `publish-pypi.yml` no longer triggers automatically
- [x] `README.md` leads with the .NET application; the Python content sits under a clear banner
- [x] Release notes state plainly that the Python version is superseded
- [ ] Publish one final PyPI release whose description points at the GitHub Releases page, so
      somebody running `pip install panoramabridge` next year is told where to go

## Stage 2 — once the lab is on the .NET application

The trigger is **every instrument that was running the Python application is running the .NET
one, and has been for a full month of normal acquisition.** Not a date.

- [ ] Mark the PyPI project as deprecated
- [ ] Remove `publish-pypi.yml`
- [ ] Remove the Python build scripts from `build_scripts/`
- [ ] Remove the Python test suite and its CI wiring, if any remains
- [ ] Remove `panoramabridge.py` and its modules, `pyproject.toml`, `requirements*.txt`,
      `.venv*` guidance
- [ ] Cut the retired section out of `README.md`, leaving one line pointing at the last tag that
      contained it

## Stage 3 — housekeeping

- [ ] Delete `docs/` pages that describe only the Python implementation
- [ ] Remove this file

---

## What must be preserved

- **The git history.** Nothing is rewritten and no tags are deleted. `v0.1.9rc4` stays fetchable,
  so the retired application can always be recovered from the tag rather than from a stale copy
  in the working tree.
- **The record of why.** The Python version was never in production and is not a specification;
  that judgement and its consequences are in `CLAUDE.md` and the port handoff, and those stay
  after the code goes.

## What must not happen

- **Do not benchmark the .NET application against the Python one.** It was never trusted in
  production, so agreement with it proves nothing and disagreement is not evidence of a
  regression. This is stated in `CLAUDE.md` and repeated here because a removal plan is exactly
  when somebody reaches for the old code to check something against.
- **Do not delete before Stage 2's trigger is actually met.** The cost of keeping unused source
  in a repository is small and legible. The cost of not being able to fall back during an
  acquisition is neither.
