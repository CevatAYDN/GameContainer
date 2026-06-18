using Nexus.Core;
using UnityEngine;

namespace Nexus
{
    public class GameplayHUDMediator : Mediator<GameplayHUDView>
    {
        protected override void OnBind()
        {
            Debug.Log($"[{nameof(GameplayHUDMediator)}] Binding View to Model...");
        }

        protected override void OnUnbind()
        {
            Debug.Log($"[{nameof(GameplayHUDMediator)}] Unbinding...");
        }
    }
}
