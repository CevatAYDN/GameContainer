using Nexus.Core;
using UnityEngine;

namespace Nexus
{
    [Mediator(typeof(GameplayHUDMediator))]
    public class GameplayHUDView : View
    {
        // Define your view events, fields and UI elements here

        protected override void OnBind(IContext context)
        {
            Debug.Log($"[{nameof(GameplayHUDView)}] Bound to context Test1");
        }

        protected override void OnUnbind()
        {
            Debug.Log($"[{nameof(GameplayHUDView)}] Unbound");
        }
    }
}
