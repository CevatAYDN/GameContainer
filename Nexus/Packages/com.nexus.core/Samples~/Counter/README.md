# Nexus Counter Example

This sample demonstrates the basic usage of the Nexus Observable Architecture using a 0-GC, strongly-typed MVCS structure.

## Structure

1. **`CounterSignal.cs`**: A simple structure signal carrying the increment amount payload.
2. **`ICounterModel.cs` / `CounterModel.cs`**: The reactive state model tracking the count.
3. **`CounterIncrementCommand.cs`**: A 0-allocation, AOT-compatible command implementing `ICommand<CounterSignal>` to update the model.
4. **`CounterView.cs` / `CounterMediator.cs`**: The UI components displaying count changes and handling click events.
5. **`CounterLifecycle.cs`**: The context lifecycle configuring dependencies and binding signals to commands.

## How to Setup & Run

1. Open your Unity scene.
2. Create a Canvas with a **Button** (assign to `incrementButton`) and a **Text** element (assign to `countText`).
3. Add the `CounterView` component to your canvas or UI hierarchy.
4. Create a new empty GameObject named `CounterRoot` and add the `Root` component to it.
5. In the `Root` component, set up your Context configurations (or use the **Root Wizard** under `Window/Nexus/Root Wizard`).
6. Enter **Play Mode** and click the button to see the count update and trace signals in the **Nexus Inspector**!
