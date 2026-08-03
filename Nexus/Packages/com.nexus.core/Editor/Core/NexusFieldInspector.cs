using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor.UIElements;

namespace Nexus.Editor
{
    /// <summary>
    /// Deep module owning the "how do we inspect a runtime member in the editor?" decision.
    ///
    /// <c>ExplorerPlugin.CreateSignalFieldUI</c> and <c>HierarchyPlugin.CreateFieldUI</c>
    /// were near-identical type → UI Toolkit switch trees that had already drifted
    /// (read-only support, undo hooks, fallback labels). This module owns the mapping once,
    /// so every inspector agrees on how each type renders and which members are editable.
    /// </summary>
    public static class NexusFieldInspector
    {
        /// <summary>Called with the new value just before it is written (e.g. Undo.RecordObject).</summary>
        public delegate void BeforeWriteHandler(object newValue);

        /// <summary>
        /// Enumerates the editable members of a type: instance fields (excluding
        /// auto-property backing fields) and instance properties (excluding indexers),
        /// in declaration order — the read-loop every inspector was hand-rolling.
        /// </summary>
        public static IEnumerable<(MemberInfo Member, Type MemberType)> EnumerateMembers(Type type)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                // Skip auto-property backing fields ("<Property>k__BackingField").
                if (field.Name.Contains("<") && field.Name.Contains(">")) continue;
                yield return (field, field.FieldType);
            }

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0) continue; // indexer
                yield return (prop, prop.PropertyType);
            }
        }

        /// <summary>
        /// Builds a UI Toolkit field for the given member type. Returns <c>null</c> for
        /// types this module does not map (custom classes, unsupported value types) so the
        /// host can render its own localized fallback.
        /// </summary>
        /// <param name="label">Field label (the member name).</param>
        /// <param name="type">Runtime type of the member.</param>
        /// <param name="getter">Reads the current value.</param>
        /// <param name="setter">Writes a new value; when <c>null</c> the field renders read-only.</param>
        /// <param name="beforeWrite">Optional hook invoked with the new value just before the setter.</param>
        public static VisualElement CreateField(string label, Type type, Func<object> getter,
            Action<object> setter, BeforeWriteHandler beforeWrite = null)
        {
            object initialValue = null;
            try { initialValue = getter(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Nexus] Field inspector failed to read '{label}': {ex.Message}");
            }

            if (type == typeof(int))
                return Wire(new IntegerField(label) { value = (int)(initialValue ?? 0) }, setter, beforeWrite);
            if (type == typeof(float))
                return Wire(new FloatField(label) { value = (float)(initialValue ?? 0f) }, setter, beforeWrite);
            if (type == typeof(double))
                return Wire(new DoubleField(label) { value = (double)(initialValue ?? 0.0) }, setter, beforeWrite);
            if (type == typeof(bool))
                return Wire(new Toggle(label) { value = (bool)(initialValue ?? false) }, setter, beforeWrite);
            if (type == typeof(string))
                return Wire(new TextField(label) { value = (string)initialValue ?? "" }, setter, beforeWrite);
            if (type == typeof(Vector2))
                return Wire(new Vector2Field(label) { value = (Vector2)(initialValue ?? Vector2.zero) }, setter, beforeWrite);
            if (type == typeof(Vector3))
                return Wire(new Vector3Field(label) { value = (Vector3)(initialValue ?? Vector3.zero) }, setter, beforeWrite);
            if (type == typeof(Color))
                return Wire(new ColorField(label) { value = (Color)(initialValue ?? Color.white) }, setter, beforeWrite);
            if (type.IsEnum)
                return Wire(new EnumField(label, (Enum)(initialValue ?? Enum.GetValues(type).GetValue(0))), setter, beforeWrite);

            // Unsupported type: the host supplies the fallback label.
            return null;
        }

        private static VisualElement Wire<T>(BaseField<T> field, Action<object> setter, BeforeWriteHandler beforeWrite)
        {
            if (setter == null)
            {
                field.SetEnabled(false);
            }
            else
            {
                field.RegisterValueChangedCallback(evt =>
                {
                    beforeWrite?.Invoke(evt.newValue);
                    setter(evt.newValue);
                });
            }
            return field;
        }
    }
}
