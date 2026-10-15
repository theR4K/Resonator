# Resonator.Weaver — the weaver↔deweaver spec

[← README](../../README.md) · [CLI reference](../../docs/cli.md#resonatordeweaver)

The exact inverse of **`FrooxEngine.Weaver.dll`** (a Fody weaver, decompiled and
analyzed): reverts every weaver modification so a weaved .NET
assembly is restored to its original, pre-weave state.

This document is the spec of the weaver↔deweaver pair: each revert op below
mirrors one weaver op. The decompiled weaver source is the source of truth for
what the weaver does. The library also holds `Flattener` (the Flatweave
prepass: keep the weave, fix only what C# cannot express) and `Reweaver`
(drive the real weaver, for round-trip tests).

## What the weaver does (from its decompiled source, `AssemblyPostProcessor.Process`)

1. Adds `[Description("FROOXENGINE_WEAVED")]` to the module (and refuses to
   process an assembly that already has it).
2. Stamps the version: the **first** of `AssemblyFileVersion`/`AssemblyVersion`
   attribute → `y.M.d.hhmm` (UTC), and `AssemblyName.Version` → same.
   (For `FrooxEngine.dll` it also rewrites the first line of `ReleaseNotes.txt` —
   a text file, not the DLL.)
3. For every type that **inherits** `FrooxEngine.Worker` (the base `Worker`
   class itself is excluded — the weaver's `InheritsFrom` check has
   `includeSelf=false`), it **adds**:
   - `protected virtual void InitializeSyncMembers()`
   - `public virtual ISyncMember GetSyncMember(int)` (non-abstract types only)
   - `public static T __New()` (non-abstract types, except types named `Slot`)
   and fills their bodies (field init / switch-based lookup / ctor call).
   ⚠ The base `FrooxEngine.Worker` already declares its own **original**
   `InitializeSyncMembers()` and `GetSyncMember(int)` in source (a
   reflection-based fallback via `WorkerInitInfo.syncMemberFields`) — those are
   not weaver additions and are preserved.
4. Adds `[SyncMethod(typeof(Delegate), null)]` to methods that are assigned as
   sync delegates inside worker methods (detected via `ldftn`/`ldvirtftn`
   patterns). Methods already carrying a `SyncMethod`-derived attribute are
   skipped, so original `SyncMethod` attributes are never touched.
5. Flips `private` → `protected` on the `readonly` sync-member fields of worker
   types (`&= ~Private; |= Family`; public fields are a no-op).
6. Rewrites the body of the **last** top-level type whose name contains
   `CoderAOT` (the non-generic `FrooxEngine.CoderAOT` static class) — its
   `Dummy()` gets the generated AOT registration calls. The original body was
   empty.
7. The weaver adds **no fields at all** (verified in its source).

## What this tool reverts

| Weaver modification | Revert action |
|---|---|
| `[Description("FROOXENGINE_WEAVED")]` module attr | **Removed** |
| Weaver-added `InitializeSyncMembers` / `GetSyncMember(int)` / `__New` on worker **subclasses** | **Removed** (base `Worker`'s originals preserved) |
| `[SyncMethod(typeof(Delegate), null)]` attrs | **Removed** (original `SyncMethod` attrs preserved) |
| Sync field visibility `protected` | **Reverted to `private`** (weaver criteria only) |
| `CoderAOT.Dummy` generated body | **Emptied** (`ret`) — original was empty (on by default) |
| Version timestamp | **Reset** (default `1.0.0.0`, configurable) |

## Verification (done, passing)

The revert is proven exact by a round trip (`Resonator.Deweaver reweave` + `diff`):
the reverted DLLs were re-processed with the **actual shipped weaver** and the
result was structurally diffed (every type, field, method signature, attribute
and IL body) against the shipped weaved DLLs:

- **FrooxEngine.dll** — identical except the runtime version timestamp and a
  pure reordering of the AOT calls inside `CoderAOT.Dummy` (HashSet iteration
  order; instruction multiset verified identical).
- **ProtoFluxBindings.dll**, **ProtoFlux.Nodes.FrooxEngine.dll** — identical
  except the version timestamp (and the `FrooxEngine` reference version that
  derives from it).

`weaver(revert(shipped)) ≡ shipped` ⇒ `revert(shipped)` is the original state.

## Usage (`Resonator.Deweaver`)

```
Resonator.Deweaver [options] <file-or-dir>...

  --out <dir>       write reverted copies into <dir> (default: in place)
  --dry-run         report what would change, write nothing
  --no-blank-aot    keep the generated CoderAOT.Dummy body (default: blank it)
  --empty-methods   empty weaver-added method bodies instead of removing them
  --keep-version    do NOT reset the weaver's timestamp version
  --version <v>     version to reset to (default 1.0.0.0)
  --backup          write <name>.dll.weaved.bak before overwriting in place

Resonator.Deweaver verify <dll>           residue check: no weaver traces left
Resonator.Deweaver known <weaver.dll>     is this a KNOWN FrooxEngine.Weaver?
Resonator.Deweaver syncmembers <dll>      generated GetSyncMember consistency (Flatweave builds)
Resonator.Deweaver reweave <dir> [<dll>...] [--weaver <dll>]
Resonator.Deweaver diff [--api] <a.dll> <b.dll> [--allowlist <file.json>]
Resonator.Deweaver pdbcheck <dll>
```

### Examples

```
# Report only
Resonator.Deweaver --dry-run "C:/Program Files (x86)/Steam/steamapps/common/Resonite"

# Revert the three weaved DLLs into a folder
Resonator.Deweaver --out work/deweaved FrooxEngine.dll ProtoFluxBindings.dll ProtoFlux.Nodes.FrooxEngine.dll

# Revert in place, keeping backups
Resonator.Deweaver --backup FrooxEngine.dll

# Verify
Resonator.Deweaver verify work/deweaved/FrooxEngine.dll
```

## Notes / limitations

1. **Version.** The original `AssemblyFileVersion` is overwritten by the weaver
   and not recoverable from the DLL; the tool resets it to `1.0.0.0`
   (`--version` to choose, `--keep-version` to leave it).
2. **Protected sync fields.** If the original source ever declared a sync-member
   field as `protected` (rather than `private`), the weaver's flip is a no-op
   and the tool would still revert it to `private` — indistinguishable from the
   weaved DLL alone. In practice Resonite uses `private`/`public` only
   (verified: shipped DLL had only `0x1`/`0x4`/`0x6` access on those fields).
3. **Locked / native DLLs.** Non-.NET DLLs and DLLs in use by another process
   are skipped and reported separately (e.g. `ProtoFlux.Core.dll` while the
   game is running).
4. `--empty-methods` is a fallback for exotic reference situations; the default
   removal path succeeded on all three shipped DLLs.

## Code layout

| Path | Role |
|------|------|
| `WeaverSpec.cs` | the shared facts: the modification table, worker / method / field predicates, version heuristic (mirrors the weaver's own checks) |
| `Deweaving/Deweaver.cs` | orchestration only: read → detect weaved → run the operations → write (PDB carried over) |
| `Deweaving/IRevertOperation.cs` | the operation interface + the `RevertContext` every operation works on |
| `Deweaving/Operations/*.cs` | one inverse per weaver modification |
| `Deweaving/DeweaveOptions.cs` | options and report |
| `Flattening/Flattener.cs` | the Flatweave prepass (InitOnly / CS0052 visibility / CoderAOT) |
| `Checks/ResidueCheck.cs` | `verify`: is anything of the weaver left? |
| `Checks/KnownWeaverCheck.cs` | refuses unknown weaver versions |
| `Checks/SyncMemberCheck.cs` | generated `GetSyncMember` consistency for non-reweaved builds |
| `Reweaver.cs` | runs the real weaver (round-trip tests) |
| `Cecil/` | the Mono.Cecil helpers every Resonator tool shares |

If a future weaver update adds a 7th modification: write one `Deweaving/Operations/*.cs`,
add it to `Operations` in `Deweaver.cs`, add a row to the tables above and a check to
`ResidueCheck.cs`. Nothing else changes.
