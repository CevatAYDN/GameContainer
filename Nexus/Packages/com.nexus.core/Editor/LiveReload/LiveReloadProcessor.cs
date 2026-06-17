using UnityEditor;
using UnityEngine;
using System;
using System.Reflection;
using System.Collections.Generic;
using Nexus.Core;

namespace Nexus.Editor
{
    public class LiveReloadProcessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (!Application.isPlaying) return;

            foreach (var assetPath in importedAssets)
            {
                if (assetPath.EndsWith(".asset"))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
                    if (asset is ModelData modelData)
                    {
                        TriggerLiveReload(modelData);
                    }
                }
            }
        }

        private static void TriggerLiveReload(ModelData modelData)
        {
            // Create a copy list to avoid modification during iteration
            var contexts = new List<IContext>(NexusRuntime.ActiveContexts);
            foreach (var context in contexts)
            {
                if (context is Context ctx)
                {
                    ProcessContainer(ctx.Container, modelData);
                }
            }
        }

        private static void ProcessContainer(NexusDI container, ModelData modelData)
        {
            if (container == null) return;

            var singletons = container.GetActiveSingletons();
            foreach (var instance in singletons)
            {
                if (instance == null) continue;

                var type = instance.GetType();
                bool hasClassLiveReload = type.GetCustomAttribute<LiveReloadAttribute>() != null;

                var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                bool needsReload = false;

                foreach (var field in fields)
                {
                    if (typeof(ModelData).IsAssignableFrom(field.FieldType))
                    {
                        var value = field.GetValue(instance) as ModelData;
                        if (value != null && value.name == modelData.name)
                        {
                            if (hasClassLiveReload || field.GetCustomAttribute<LiveReloadAttribute>() != null)
                            {
                                needsReload = true;
                            }
                        }
                    }
                }

                var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var prop in properties)
                {
                    if (typeof(ModelData).IsAssignableFrom(prop.PropertyType))
                    {
                        try
                        {
                            var value = prop.GetValue(instance) as ModelData;
                            if (value != null && value.name == modelData.name)
                            {
                                if (hasClassLiveReload || prop.GetCustomAttribute<LiveReloadAttribute>() != null)
                                {
                                    needsReload = true;
                                }
                            }
                        }
                        catch
                        {
                            // Ignore properties that throw on get
                        }
                    }
                }

                if (needsReload)
                {
                    var reloadMethod = type.GetMethod("OnLiveReload", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (reloadMethod != null)
                    {
                        try
                        {
                            reloadMethod.Invoke(instance, null);
                            Debug.Log($"[Nexus] LiveReload: Triggered OnLiveReload() on {type.Name}.");
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[Nexus] LiveReload: Failed to invoke OnLiveReload on {type.Name}: {ex.InnerException?.Message ?? ex.Message}");
                        }
                    }
                }
            }
        }
    }
}
