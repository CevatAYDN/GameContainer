using System.Reflection;
using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor.Inspector
{
    [CustomEditor(typeof(View), true)]
    public class ViewEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            if (target == null) return;

            try
            {
                serializedObject.Update();
            }
            catch
            {
                return;
            }

            var view = (View)target;
            var viewType = view.GetType();
            var mediatorAttr = viewType.GetCustomAttribute<MediatorAttribute>();

            string badge = mediatorAttr != null ? mediatorAttr.MediatorType.Name : "Unmediated View";
            StatusType badgeType = mediatorAttr != null ? StatusType.Success : StatusType.Warning;
            NexusInspectorGUI.DrawHeader(viewType.Name, "Nexus MVCS View Component", badge, badgeType);

            // 1. Mediator Binding Card
            NexusInspectorGUI.BeginCard("Mediator Binding");
            if (mediatorAttr != null)
            {
                NexusInspectorGUI.DrawStatusRow("Bound Mediator", mediatorAttr.MediatorType.Name, StatusType.Success);
                NexusInspectorGUI.DrawMessage($"When this GameObject activates, {mediatorAttr.MediatorType.Name} will be instantiated and bound to this view automatically.", StatusType.Info);
            }
            else
            {
                NexusInspectorGUI.DrawMessage($"No [Mediator(typeof(...))] attribute declared on '{viewType.Name}'. " +
                    "To bind a mediator, decorate your class: [Mediator(typeof(MyMediator))] public class " + viewType.Name + " : View { ... }", StatusType.Warning);
            }
            NexusInspectorGUI.EndCard();

            // 2. Component Properties
            NexusInspectorGUI.BeginCard("View Properties");
            DrawDefaultInspector();
            NexusInspectorGUI.EndCard();

            serializedObject.ApplyModifiedProperties();
        }
    }
}
