# Target sets

Data only: what to build and the patches on top. Nothing here is code.

```
<Set>/
  set.json                 {"extends": "<Set below>", "description": ...}
  <Target>/
    target.json            the spec
    patches/*.patch        this target's own layer
```

| Set | Extends | |
|---|---|---|
| [`Resonator`](Resonator) | | the pre-weave original (deweave) and the native-wrapper closure |
| [`Flatweave`](Flatweave) | `Resonator` | the weave kept as source (flatten) + official libraries |
| [`ArmAginator`](ArmAginator) | `Flatweave` | the ARM port ([README](ArmAginator/README.md)) |

Docs: [Target sets](../docs/target-sets.md) (specs, `target.json` keys) and [Patches](../docs/patches.md)
(layers, editing workflow).
