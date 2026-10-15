# Contributing

## Before you commit

- [ ] `dotnet build Resonator.slnx -c Release` succeeds.
- [ ] For a changed target set: a clean `Resonator.ProjectBuilder` build, then `Resonator.PatchGenerator` over it,
      leaves `git status` of the set clean ([Patches](docs/patches.md)).
- [ ] For a Flatweave / ArmAginator target: the build's `syncmembers` check passes ([rules](docs/flatweave.md)).
- [ ] For a weaver change: `Resonator.Deweaver reweave` + `diff` still show
      `weaver(deweave(shipped)) ≡ shipped` ([weaver spec](src/Resonator.Weaver/README.md#verification-done-passing)).
- [ ] No decompiled sources, Resonite binaries, `*.dll`, `*.pdb` or `*.exe` are staged.

## Where changes go

| Change | Where |
|---|---|
| a target, its spec or its patches | `targets/<Set>/<Target>/`, in the set the change belongs to (`--set`) |
| an IL-level fix before decompilation | `src/Resonator.Weaver` (deweave / flatten) |
| a systematic decompiler artifact | `src/Resonator.Pipeline/Fixups/` (an `ISourceFix`) |
| an error only the compiler can locate | `src/Resonator.ProjectBuilder/BuildFixes/` (an `IBuildErrorFix`) |
| a new weaver modification to revert | one `Deweaving/Operations/*.cs`, see the [weaver spec](src/Resonator.Weaver/README.md#code-layout) |

Patches are the last resort: they carry decompiled source. Prefer an IL fix, then a decompiler fix, then a patch.

## Code style

- Settings shared by every project live in [`Directory.Build.props`](Directory.Build.props): `net10.0`,
  nullable enabled, implicit usings off.
- Each file starts with what it is for: a header comment (CLIs) or a `<summary>` on the type.
- Match the surrounding code: file-scoped namespaces, records for data, small static stage classes.

## Docs

User-facing docs are in [`docs/`](docs). Keep the [README](README.md) a front page and put detail in the doc it
belongs to. A changed CLI option goes into the tool's usage text and [`docs/cli.md`](docs/cli.md).
