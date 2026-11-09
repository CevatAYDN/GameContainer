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

A newer transition cancels the one in progress and waits for it to finish; a superseded transition
exits the state it entered, so every entered state is exited exactly once. Between a state's exit and
the next state's enter `CurrentState` is null and `Tick` ticks nothing. `SetErrorState` throws for a
state that is not registered yet, so register the error state first.
The superseded state is also exited when its pending enter finishes with an ordinary
exception, including after `Dispose` (#347). That exit is awaited before the next
transition can enter; a stale failure never starts the configured error state.
An ordinary enter failure also exits its partially entered state before fallback
(#372), and a failing error-state enter is cleaned too. Exit implementations must
tolerate resources that were only partly acquired.

`GameStateMachine` is an `ITickable`: after `tickService.RegisterTickable(fsm)` the current state's
`OnTick` runs every frame. The package's `link.xml` preserves the whole assembly at every stripping level.

## Tests

EditMode, under `Tests/Editor`: `FSMHardeningTests`, `FSMTransitionHistoryTests` and
`GameStateMachineTests` and `FSMFailureCleanupTests`.
