# CLI reference

[← README](../README.md)

Executables: `src/<Project>/bin/Release/net10.0/<Project>.exe`. Each prints its usage with `--help` and exits non-zero on failure.

- [Resonator.ProjectBuilder](#resonatorprojectbuilder)
- [Resonator.PatchGenerator](#resonatorpatchgenerator)
- [Resonator.Deweaver](#resonatordeweaver)
- [Resonator.ShadersReferenceBuilder](#resonatorshadersreferencebuilder)

## Shared pipeline options

ProjectBuilder and PatchGenerator take the same target selection:

| Option | Meaning |
|---|---|
| `--target <target\|assembly>` | one target, or every target of an assembly (default: all) |
| `--set <name>` | the target set `targets/<name>` (default: `Resonator`) |
| `--targets <dir>` | an explicit target-set directory (overrides `--set`) |
| `--resonite <dir>` | Resonite install (default: `%RESONITE_DIR%`, else the Steam path) |
| `--no-pdb` | decompile without the PDB (decompiler-generated local names) |

## Resonator.ProjectBuilder

Shipped Resonite assemblies → buildable C# projects. See [Pipeline](pipeline.md).

```
Resonator.ProjectBuilder <output-dir> [pipeline options]
  [--stage all|prepare|decompile|fixup|build]   run one stage only (default: all)
  [--no-csproj]                                 emit only source + references.targets
  [--keep-going]                                carry on after a failing target, report all at the end
  [--publish <dir>]                             drop-in folder of a successful full run (default: <output-dir>/publish)
  [--no-publish]                                skip it
```

A successful full run (all stages, csproj on) ends with **publish**: one folder to copy over a game install (not the
one the pipeline reads). It holds what the run changes: each target's dll + pdb, the set's packages and the extra
assemblies they bring (Concentus), and replaced natives under `runtimes/` - nothing identical to the install, no
differing copy of a game file the run does not own. `publish.txt` lists each file with its target.

A full run (all stages) generates into a staging copy of the output layout under the work dir, then updates the output
with only what changed: a file with the same bytes is not written (it keeps its timestamp, so the consumer's build
recompiles only what a patch or a new revision changed), and a file a previous run generated that this one does not is
deleted. What a run generated is listed in each project dir's `.resonator-generated`; nothing else there is touched
(keep dirs, a csproj, `bin/`, `obj/`). A one-stage run (`--stage`) works in place, as before.

One target in the run → its project goes straight into `<output-dir>`; several → `<output-dir>/<target>/`.
A `--target` run also builds the source targets it references (they are its projects' siblings), so e.g.
`--set Flatweave --target Elements.Assets` builds CSCore, FreeImageNET, RNNoise.NET and SharpFont first and puts
each under `<output-dir>/<target>/`. `Resonator.PatchGenerator` uses the same layout.

```
Resonator.ProjectBuilder calli <dll> <attribute> [remove-type attribute]
```

The post-compile step of a `calli` target. Its `references.targets` runs it; you rarely call it by hand.

## Resonator.PatchGenerator

Edits of a built project → the target set's patches. See [Patches](patches.md).

```
Resonator.PatchGenerator <project-dir> [pipeline options]
  [--no-csproj]                                 the ProjectBuilder run was --no-csproj
```

`<project-dir>` is the output dir of a ProjectBuilder run with the same set and target selection, edited. Pass
`--no-csproj` when that run had it: it leaves out the referenced source targets, so the tree is `<project-dir>` itself. Patches
are written only into the set being worked on: the edits of an inherited target become a new layer there. Package
targets are skipped.

## Resonator.Deweaver

The command line over `Resonator.Weaver`. The spec of what it reverts is in the
[weaver README](../src/Resonator.Weaver/README.md).

```
Resonator.Deweaver [options] <file-or-dir>...    revert the weave
```

| Option | Meaning |
|---|---|
| `--out <dir>` | write reverted copies into `<dir>` (default: in place) |
| `--dry-run` | report what would change, write nothing |
| `--no-blank-aot` | keep the generated `CoderAOT.Dummy()` body (default: blank it; the pipeline blanks it only for a weaver that rewrites it, before Resonite 2026.10.27) |
| `--empty-methods` | empty the weaver-added methods instead of removing them |
| `--keep-version` | keep the weaver's timestamp version |
| `--version <v>` | the version to reset to (default `1.0.0.0`) |
| `--backup` | write `<name>.dll.weaved.bak` before overwriting in place |

| Command | Does |
|---|---|
| `verify <dll>` | residue check: no weaver traces left |
| `known <weaver.dll>` | is this a known `FrooxEngine.Weaver`? |
| `syncmembers <dll>` | generated `GetSyncMember` consistency of a Flatweave build ([rules](flatweave.md)) |
| `reweave <dir> [<dll>...] [--weaver <dll>]` | run the real weaver over `<dir>` (default: the three weaved dlls) |
| `diff <a.dll> <b.dll>` | structural diff: types, fields, methods, attributes, IL |
| `diff --api <a.dll> <b.dll> [--allowlist <file.json>]` | API-surface diff (recompiled vs original): no IL, no compiler noise |
| `pdbcheck <dll>` | PDB ↔ IL consistency (sequence points, locals) |

```powershell
# report only
Resonator.Deweaver --dry-run "C:\Program Files (x86)\Steam\steamapps\common\Resonite"

# revert the three weaved dlls into a folder, then check
Resonator.Deweaver --out work\deweaved FrooxEngine.dll ProtoFluxBindings.dll ProtoFlux.Nodes.FrooxEngine.dll
Resonator.Deweaver verify work\deweaved\FrooxEngine.dll
```

## Resonator.ShadersReferenceBuilder

Official shaders → reference source tree + metadata table. See [Shaders](shaders.md).

```
Resonator.ShadersReferenceBuilder <list|fetch|unpack|meta|all> [options]
```

| Command | Does |
|---|---|
| `list` | print the shader versions listed in the shipped engine |
| `fetch` | download them (the game's AssetGatherer, anonymous, sha256-verified, cached) |
| `unpack` | cached bundles → `<tree>/<version>/<Shader>/` (`metadata.json` + `Sources/**`) |
| `meta` | `<tree>` → generated metadata table (a partial class, `AddGeneratedMeta`) |
| `all` | fetch + unpack + meta |

Options: [Shaders → Usage](shaders.md#usage).
