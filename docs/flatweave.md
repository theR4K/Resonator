# Writing code for Flatweave

[← README](../README.md) · [Target sets](target-sets.md)

A flattened engine (`Flatweave`, `ArmAginator`) is **not reweaved**: the weaver's output is final source. The
weaver's implicit duties therefore fall to you when you add code.

## The three rules

**1. A new `Worker` subclass with sync fields overrides `GetSyncMember`.**
The generated one is a flat switch over the parent's members, so it does not know yours:

```csharp
public override ISyncMember GetSyncMember(int index) =>
    GetSyncMemberFieldInfo(index).GetValue(this) as ISyncMember;
```

**2. A method assigned as a sync delegate gets `[SyncMethod]`.**
That is any method assigned to `SyncDelegate.Target`, used as a `ButtonEventHandler`, or passed to
`SyncDelegateList.Add`:

```csharp
[SyncMethod(typeof(Delegate), null)]
void OnPressed(IButton button, ButtonEventData data) { ... }
```

**3. A patch that adds a sync field to an existing `Worker` extends that type's `InitializeSyncMembers` and
`GetSyncMember`.**

## Check it

```powershell
Resonator.Deweaver syncmembers <built.dll>
```

reports every violation of the three rules, with the fix to add. Every `flatten` build runs it after compiling.
References resolve from the dll's own directory and `%RESONITE_DIR%`.

## Why it works

Flattening clears `readonly` from sync fields (C# cannot assign them otherwise) and marks each one with
`[Resonator.FlattenedInitOnly]`. The `Flatweave/FrooxEngine` patches make the engine's reflection checks read
"readonly or marked", so the compatibility hash of the rebuilt engine equals the shipped one. See
[Weave modes](target-sets.md#weave-modes).
