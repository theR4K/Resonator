# Resonator/FrooxEngine — patches

Per-file fixes for **decompiler rendering artifacts** that the fixup stage's global regexes
can't handle: the minimum needed to recompile the engine. Every layer that extends this target
(`Flatweave/FrooxEngine`, `ArmAginator/FrooxEngine`) gets them first.

> Preference: **Cecil/IL-level fixes over .cs changes** where possible (prepare stage,
> `fix_ctor_types`). Patches are only for artifacts that exist only in the *decompiled source*.

| Patch | File | What's wrong in the decompilation | Fix |
|-------|------|-----------------------------------|-----|
| `RadiantDash.cs.patch` | `FrooxEngine/RadiantDash.cs` | `borderRect /= ref resolution;` — invalid `ref` on a division operand (**rendering unstable — see below**) | drop the `ref` |
| `LegacyWorldThumbnailItem.cs.patch` | `FrooxEngine/LegacyWorldThumbnailItem.cs` | `ref colorX reference = ref _borderColor.Value;` — invalid `ref` declaration | plain assignment |
| `MaterialUpdateWriter.cs.patch` | `FrooxEngine/MaterialUpdateWriter.cs` | `AllocateFloatArray`/`AllocateFloat4Array` return a Span derived from a byref arg pointing at `this` fields — **no C# source form compiles under current Roslyn** (CS8170/CS8347) | inline the `AllocateArray<T>` body into the two wrappers (behaviour identical, IL of these 2 private methods differs slightly) |
| `ProtoFluxTool.cs.patch` | `FrooxEngine/ProtoFlux/ProtoFluxTool.cs` | local function `IsNodeGrabbed()` declared inside a `foreach` body — illegal | hoist the local function to method level |
| `WebsocketClient.cs.patch` | `FrooxEngine/WebsocketClient.cs` | closure display class `<>c__DisplayClass29_1` referenced but **never emitted** (async-method display class dropped) | reconstruct the closure as 3 plain locals |
| `QUIC_Manager.cs.patch` | `FrooxEngine/QUIC_Manager.cs` | `new <>z__ReadOnlySingleElementList<IListener>(…)` — compiler-generated type, decompiled with the escaped name `_003C_003Ez__…` | C# 12 collection expression `return [ … ];` — the compiler regenerates the type with the right name |

The decompiled declaration of `<>z__ReadOnlySingleElementList` itself is removed by the spec's
`exclude_types` (no deletion patch needed).

All six apply unchanged to both the deweaved (Resonator) and the flattened (Flatweave)
decompilation; patches are located by context, so the weaver-generated members in Flatweave
files only shift line numbers.

## Non-determinism warning (2026-07, observed)

The decompiler's rendering of *some* operator lines is **not stable across runs** with
identical input: the RadiantDash `borderRect /= ref resolution;` line rendered clean in one
decompile and with the `ref` artifact in the next. So a patch whose context holds such a line
may APPLY on some runs and SKIP on others; the build stage's ref-arg auto-fix is the safety net
for that case. Keep both.

## Changing or adding a patch

1. `Resonator.ProjectBuilder <out> --target FrooxEngine` (any set that contains this layer).
2. Fix the file in `<out>` by hand.
3. `Resonator.PatchGenerator <out> --target FrooxEngine` (same set) — rewrites that set's
   own patch layer as a delta over its ancestors. To change *this* layer, run it with `--set Resonator`.
4. Re-run the ProjectBuilder from scratch; the PatchGenerator right after must leave git clean.

See [docs/patches.md](../../../../docs/patches.md).

Prefer a decompiler fix (`Resonator.Pipeline/Fixups/`) if an artifact is systematic (many files), a patch if it is
local, an IL fix if it can be avoided before decompilation.

## What is NOT patched (handled elsewhere)

| Artifact | Handled by |
|----------|-----------|
| Weaver IL (generated sync methods, etc.) | prepare stage: `Deweaver` (deweave) or `Flattener` (flatten) |
| Open-generic casts `((T<X>)(object)this).M` | fixup: `OpenGenericCastFix` |
| `Unsafe.As<…>(x)` / `new Typearg` / generic member parens | fixup: `UnsafeAsFix`, `NewTypeArgumentFix`, `GenericMemberCallFix` |
| Missing transitive references (CS0012) | build auto-fix loop: `MissingReferenceFix` |
| Spurious `ref` on call args | build auto-fix loop: `RefArgumentFix` |
| Fody marker `.ctor` | `fix_ctor_types` → `FixCtor` (post-build) |
