# Nexus DOTS Bridge (`com.nexus.dots`)

> **Who this is for:** a game whose jobs produce signals (unit commands, combat results) that the
> main thread's `SignalBus` delivers.

`NativeSignalQueue<T>` holds unmanaged signals; jobs write through its parallel writer, and
`DOTSSignalBridge<T>` (a `MonoBehaviour`) drains them into the `SignalBus` on the main thread in
`Update`. The assembly compiles only while `com.unity.collections` 6.6.0 or newer is installed (the version Unity 6000.6 brings); without it the
package adds nothing to the project.

The bridge was part of `com.nexus.core` (assembly `com.nexus.core.dots`) until issue #112 moved it
here. The namespace stayed `Nexus.DOTS`, so game code does not change.

## Install

This package by git URL, pinned to the same commit as `com.nexus.core` (the full 40-character hash):
`https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.dots#<commit>`,
and `com.unity.collections`. Add `com.nexus.dots` to the references of every assembly definition
whose code names the bridge types.

## Use

The producer rules (writer on the main thread, `AddProducerDependency` right after scheduling, who
completes the jobs) are in [Integration paths](../com.nexus.core/docs/INTEGRATION_PATHS.md#scene-components-global-scope-and-jobs);
the strategy-game pattern in [Game patterns](../com.nexus.core/docs/GAME_PATTERNS.md). The package
neither Burst-compiles nor Burst-tests the queue (#109). The package's `link.xml` preserves the whole
assembly when it is compiled.

## Tests

`Tests/DotsOwnershipRegressionTests` (3), compiled only with `com.unity.collections`.
