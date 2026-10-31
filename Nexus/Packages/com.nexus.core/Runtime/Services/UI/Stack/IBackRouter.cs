using System;

namespace Nexus.Core.Services
{
    /// <summary>What a back press does once nothing else took it.</summary>
    public enum RootBack
    {
        /// <summary>Nothing happens (Unity's default on Android).</summary>
        Nothing,
        /// <summary>The app goes to the background (ChipCrisis).</summary>
        MoveTaskToBack,
        /// <summary>The first press raises <see cref="IBackRouter.PressAgainHintRequested"/>; a second within <see cref="IBackRouter.PressAgainSeconds"/> quits (Gigawatt, Wickling).</summary>
        PressAgainToQuit
    }

    /// <summary>Where a game's own back step runs relative to the screen stack.</summary>
    public enum BackOrder { BeforeScreens, AfterScreens }

    /// <summary>
    /// One back chain per context, so a press has at most one handler (ADR-0003). A press stops at the first of:
    /// the <see cref="BackOrder.BeforeScreens"/> steps (most recently added first); the screen stack's back (popped,
    /// handled by the top screen, or blocked); the <see cref="BackOrder.AfterScreens"/> steps (most recently added
    /// first); the <see cref="AtRoot"/> behaviour.
    /// </summary>
    public interface IBackRouter : INexusService
    {
        /// <summary>What a press does when nothing else took it. Default <see cref="RootBack.Nothing"/>.</summary>
        RootBack AtRoot { get; set; }

        /// <summary>The window for the second press under <see cref="RootBack.PressAgainToQuit"/>, in unscaled seconds. Default 2.</summary>
        float PressAgainSeconds { get; set; }

        /// <summary>The first press at the root under <see cref="RootBack.PressAgainToQuit"/>; the game shows its own hint.</summary>
        event Action PressAgainHintRequested;

        /// <summary>A game step outside the stack; it returns true when it handled the press. Disposing removes it.</summary>
        IDisposable AddStep(Func<bool> step, BackOrder order);

        /// <summary>One press through the chain, for tests and tools; true when something handled it.</summary>
        bool Press();
    }
}
