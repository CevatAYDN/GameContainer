# Nexus FSM (`com.nexus.fsm`)

> **Who this is for:** a game that drives its flow (boot, menu, level, game over) through async states.

`GameStateMachine` runs one `IGameState` at a time: `ChangeStateAsync` exits the current state and
enters the next, a newer transition supersedes one still in progress, a state whose `OnEnterAsync`
throws falls back to the error state set with `SetErrorState`, and the last 32 transitions are kept
(`GetRecentTransitions`, `OnStateChanged`). With the package installed, the Nexus window shows a
State Machine tab for every `IGameStateMachine` the active contexts resolve.

The package was part of `com.nexus.core` until issue #112 moved it here. The namespace stayed
`Nexus.Core.FSM`, so game code does not change.

## Install

This package by git URL, pinned to the same commit as `com.nexus.core` (the full 40-character hash):
`https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.fsm#<commit>`.
Add `com.nexus.fsm` to the references of every assembly definition whose code names the FSM types.

## Use

```csharp
using Nexus.Core.FSM;

builder.BindInstance<IGameStateMachine>(fsm);            // var fsm = new GameStateMachine();
fsm.RegisterState(new MenuState());
fsm.RegisterState(new ErrorState());
fsm.SetErrorState<ErrorState>();                          // receives the exception as its args
await fsm.ChangeStateAsync<MenuState>();
```

`GameStateMachine` is an `ITickable`: after `tickService.RegisterTickable(fsm)` the current state's
`OnTick` runs every frame. The package's `link.xml` preserves the whole assembly at every stripping level.

## Tests

EditMode, under `Tests/Editor`: `FSMHardeningTests`, `FSMTransitionHistoryTests` and
`GameStateMachineTests`.
