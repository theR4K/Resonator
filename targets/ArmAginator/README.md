# ArmAginator — the ARM port target set

Consumed by the ArmAgedon repo in `--no-csproj` mode: each target's tree is compiled by the
consumer's own csproj (`generated/` + its `Injected/` code).

| Target | Weave | Contents |
|---|---|---|
| `FrooxEngine` | flatten (extends `Flatweave/FrooxEngine`) | the engine minus the Ext.* split types; ARM/platform patches on top of the Resonator layer |
| `FrooxEngine.Ext.*` | flatten | split feature assemblies (`select_types` from the same FrooxEngine.dll) |
| `RenderiteShared` | none | renderer protocol; `MessagingManager` replaced in-process |
| `Elements.Core`, `Elements.Assets`, `Awwdio` | none (`Elements.Assets` extends `Flatweave/Elements.Assets`; `Elements.Core` and `Awwdio` extend the Resonator targets, Flatweave has no layer for them) | built from source by ArmAgedon so their native-backed code can move into strategies (ArmAgedon `docs/native-separation.md`); data-model assemblies with hashes of their own |
| inherited from `Flatweave` | package / source | the official wrapper libraries ([docs/target-sets.md](../../docs/target-sets.md#official-libraries-flatweave)). Package targets reach the consumers as `PackageReference`s; ArmAgedon builds the source targets (CSCore, SharpFont, FreeImageNET, POpusCodec, OpenLipSync.Inference, RNNoise.NET, ApplicationLoopback.NET) from `--target <Lib> --no-csproj` trees in its `src/ThirdParty` |

Data-model types (`IWorldElement`, `[DataModelType]`, e.g. `AudioZitaReverb`, `ModelImportDialog`,
`LeapMotionDriver`) stay in the core target: the compatibility hash is per assembly, and saved worlds name
the type's assembly. A split target takes only implementation code that needs an optional native
library (Discord, LipSync, uTouch, Vive); the core reaches it through `ExtRegistry`. Plugins that need no
generated code (Reverb, Leap) have no target here.

Patches only cut stock code or call into the consumer's `Injected/` code: a one-line seam
(`ArmAgedonHost.InProcessRenderer`, `ZitaNative.Available`, ...), or `partial` on the class plus an
early return into a method of a partial half in `Injected/` that keeps access to private state
(`CoroutineManager`, `WorldAssetManager`, `PlatformInterface`).

Since 2026-10 the chain is `Resonator → Flatweave → ArmAginator`: the weaver's output is kept as
source, so the consumer does **not** reweave (the shipped weaver also refuses an already-weaved
dll, so an old reweave step is a no-op). Code the consumer adds must follow the [Flatweave rules](../../docs/flatweave.md)
(GetSyncMember override for new Workers, `[SyncMethod]` on sync-delegate targets);
`Resonator.Deweaver syncmembers <dll>` checks a built dll.

The renderer's shader tree and `ShaderMeta.g.cs` (`ArmAgedon.Rendering.ShaderVariants`) come from the
separate `Resonator.ShadersReferenceBuilder` tool ([docs/shaders.md](../../docs/shaders.md)): `all --tree <dir>/Official --out <dir>/ShaderMeta.g.cs --namespace ArmAgedon.Rendering`.
