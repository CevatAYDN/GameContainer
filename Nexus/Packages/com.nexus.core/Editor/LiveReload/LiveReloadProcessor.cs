using UnityEditor;
using UnityEngine;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Nexus.Core;

namespace Nexus.Editor
{
    public class LiveReloadProcessor : AssetPostprocessor
    {
        private static readonly ConcurrentDictionary<Type, MemberInfo[]> s_modelDataMembersCache = new();
        private static readonly ConcurrentDictionary<Type, bool> s_classLiveReloadCache = new();
        private static readonly ConcurrentDictionary<Type, MethodInfo> s_reloadMethodCache = new();

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
            var contexts = new List<IContext>(NexusRuntime.ActiveContexts);
            foreach (var context in contexts)
            {
                if (context is Context ctx)
                {
                    ProcessContainer(ctx.Container, modelData);
                }
            }
        }

        private static MemberInfo[] GetModelDataMembers(Type type)
        {
            return s_modelDataMembersCache.GetOrAdd(type, t =>
            {
                var members = new List<MemberInfo>();
                var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var field in fields)
                {
                    if (typeof(ModelData).IsAssignableFrom(field.FieldType))
                        members.Add(field);
                }
                var properties = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var prop in properties)
                {
                    if (typeof(ModelData).IsAssignableFrom(prop.PropertyType))
                        members.Add(prop);
                }
                return members.ToArray();
            });
        }

        private static void ProcessContainer(NexusDI container, ModelData modelData)
        {
            if (container == null) return;

            var singletons = container.GetActiveSingletons();
            foreach (var instance in singletons)
            {
                if (instance == null) continue;

                var type = instance.GetType();
                bool hasClassLiveReload = s_classLiveReloadCache.GetOrAdd(type, t => t.GetCustomAttribute<LiveReloadAttribute>() != null);

                var members = GetModelDataMembers(type);
                bool needsReload = false;

                foreach (var member in members)
                {
                    object rawValue;
                    if (member is FieldInfo field)
                    {
                        rawValue = field.GetValue(instance);
                    }
                    else if (member is PropertyInfo prop)
                    {
                        try { rawValue = prop.GetValue(instance); }
                        catch { continue; }
                    }
                    else continue;

                    if (rawValue is ModelData value && value != null && value.name == modelData.name)
                    {
                        if (hasClassLiveReload || member.GetCustomAttribute<LiveReloadAttribute>() != null)
                        {
                            needsReload = true;
                            break;
                        }
                    }
                }

                if (needsReload)
                {
                    var reloadMethod = s_reloadMethodCache.GetOrAdd(type, t =>
                        t.GetMethod("OnLiveReload", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));

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
