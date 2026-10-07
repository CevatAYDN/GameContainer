using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace Nexus.Core
{
    /// <summary>
    /// Owns the compiled-accessor machinery for Nexus DI: DynamicMethod IL emission
    /// for fast field/property setters and getters, the AOT/IL2CPP reflection fallback,
    /// and the bounded one-time warning tracker for setter-compile failures.
    ///
    /// Extracted from NexusDI so the container reads as pure policy (bind → resolve →
    /// inject) and the IL-emitting plumbing can be reasoned about and changed in
    /// isolation (AOT strategy, new member kinds, warning budget).
    /// </summary>
    internal static class CompiledAccessorEmitter
    {
        /// <summary>Compiles a fast zero-GC setter for an injectable field using DynamicMethod IL generation.</summary>
        internal static Action<object, object> CompileFieldSetter(Type targetType, FieldInfo field)
        {
#if ENABLE_IL2CPP || UNITY_AOT || UNITY_IOS || UNITY_WEBGL
            return null; // AOT safety: bypass IL emitting on AOT platforms
#else
            try
            {
                var dm = new System.Reflection.Emit.DynamicMethod(
                    $"Set_{field.Name}", typeof(void), new[] { typeof(object), typeof(object) }, targetType.Module, true);
                var il = dm.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Castclass, targetType);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                if (field.FieldType.IsValueType)
                    il.Emit(System.Reflection.Emit.OpCodes.Unbox_Any, field.FieldType);
                else if (!field.FieldType.IsInterface && field.FieldType != typeof(object))
                    il.Emit(System.Reflection.Emit.OpCodes.Castclass, field.FieldType);
                il.Emit(System.Reflection.Emit.OpCodes.Stfld, field);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                return (Action<object, object>)dm.CreateDelegate(typeof(Action<object, object>));
            }
            catch (Exception ex)
            {
                LogSetterCompileFailureOnce(targetType, field.Name, ex);
                return null; // AOT/IL2CPP safety: fall back to reflection SetValue.
            }
#endif
        }

        /// <summary>Compiles a fast zero-GC getter for an injectable field using DynamicMethod IL generation.</summary>
        internal static Func<object, object> CompileFieldGetter(Type targetType, FieldInfo field)
        {
#if ENABLE_IL2CPP || UNITY_AOT || UNITY_IOS || UNITY_WEBGL
            return null;
#else
            try
            {
                var dm = new System.Reflection.Emit.DynamicMethod(
                    $"Get_{field.Name}", typeof(object), new[] { typeof(object) }, targetType.Module, true);
                var il = dm.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Castclass, targetType);
                il.Emit(System.Reflection.Emit.OpCodes.Ldfld, field);
                if (field.FieldType.IsValueType)
                    il.Emit(System.Reflection.Emit.OpCodes.Box, field.FieldType);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                return (Func<object, object>)dm.CreateDelegate(typeof(Func<object, object>));
            }
            catch (Exception ex)
            {
                // Log the exception (like other methods in this class).
                // Previously this was a bare `catch { return null; }` with zero logging,
                // making DI field-getter failures impossible to diagnose.
                LogSetterCompileFailureOnce(targetType, field.Name, ex);
                return null;
            }
#endif
        }

        /// <summary>
        /// Compiles required reference-type constructor parameters directly to a newobj call.
        /// AOT targets use shipped source-generated factories or the reflection fallback.
        /// Explicit overrides and optional/value parameters retain their authoritative fallback.
        /// </summary>
        internal static Func<NexusDI, object> CompileConstructor(ConstructorInfo constructor)
        {
#if ENABLE_IL2CPP || UNITY_AOT || UNITY_IOS || UNITY_WEBGL
            return null;
#else
            if (constructor == null) return null;
            // An open generic newobj/parameter resolver has no concrete method context.
            // Unity Mono asserts in its JIT instead of throwing a catchable exception.
            if (constructor.ContainsGenericParameters || constructor.DeclaringType.ContainsGenericParameters
                || constructor.DeclaringType.IsValueType) return null;
            var parameters = constructor.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
                if (parameters[i].ParameterType.IsValueType || parameters[i].ParameterType.IsByRef
                    || parameters[i].ParameterType.IsPointer || parameters[i].HasDefaultValue)
                    return null;
            var type = constructor.DeclaringType;
            try
            {
                var method = new System.Reflection.Emit.DynamicMethod("Construct_" + type.Name,
                    typeof(object), new[] { typeof(NexusDI) }, type.Module, true);
                var il = method.GetILGenerator();
                var resolve = typeof(NexusDI).GetMethod(nameof(NexusDI.ResolveConstructorParameter));
                for (int i = 0; i < parameters.Length; i++)
                {
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, i);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldstr, type.FullName ?? type.Name);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldstr, parameters[i].ParameterType.FullName ?? parameters[i].ParameterType.Name);
                    string name = parameters[i].GetCustomAttribute<InjectAttribute>()?.Name;
                    if (string.IsNullOrEmpty(name)) il.Emit(System.Reflection.Emit.OpCodes.Ldnull);
                    else il.Emit(System.Reflection.Emit.OpCodes.Ldstr, name);
                    il.Emit(System.Reflection.Emit.OpCodes.Callvirt, resolve.MakeGenericMethod(parameters[i].ParameterType));
                }
                il.Emit(System.Reflection.Emit.OpCodes.Newobj, constructor);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                return (Func<NexusDI, object>)method.CreateDelegate(typeof(Func<NexusDI, object>));
            }
            catch (Exception error)
            {
                LogSetterCompileFailureOnce(type, ".ctor", error);
                return null;
            }
#endif
        }

        /// <summary>Compiles a fast zero-GC setter for an injectable property using DynamicMethod IL generation.</summary>
        internal static Action<object, object> CompilePropertySetter(Type targetType, PropertyInfo prop)
        {
#if ENABLE_IL2CPP || UNITY_AOT || UNITY_IOS || UNITY_WEBGL
            return null; // AOT safety: bypass IL emitting on AOT platforms
#else
            try
            {
                var setter = prop.GetSetMethod(true);
                if (setter == null) return null;
                var dm = new System.Reflection.Emit.DynamicMethod(
                    $"Set_{prop.Name}", typeof(void), new[] { typeof(object), typeof(object) }, targetType.Module, true);
                var il = dm.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Castclass, targetType);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                if (prop.PropertyType.IsValueType)
                    il.Emit(System.Reflection.Emit.OpCodes.Unbox_Any, prop.PropertyType);
                else if (!prop.PropertyType.IsInterface && prop.PropertyType != typeof(object))
                    il.Emit(System.Reflection.Emit.OpCodes.Castclass, prop.PropertyType);
                il.Emit(System.Reflection.Emit.OpCodes.Callvirt, setter);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                return (Action<object, object>)dm.CreateDelegate(typeof(Action<object, object>));
            }
            catch (Exception ex)
            {
                LogSetterCompileFailureOnce(targetType, prop.Name, ex);
                return null;
            }
#endif
        }

        // Logged-once-per-member guard so a genuine setter compile failure is surfaced
        // without spamming the log on every injection. AOT/IL2CPP legitimately fails here
        // and falls back to reflection, so this is informational rather than an error.
        // Logging is limited to editor/dev builds: in release players the reflection
        // fallback is the intended behavior, so staying silent avoids startup warning spam.
        /// <summary>
        /// Maximum number of unique (type, member) compile-failure pairs to track.
        /// Beyond this, warnings are silently dropped to prevent unbounded memory growth
        /// in long-running editor sessions with many assemblies.
        /// </summary>
        private const int MaxSetterCompileWarnings = 1024;

        private static readonly ConcurrentDictionary<(Type, string), byte> s_setterCompileWarnings = new(4, 128);

        private static void LogSetterCompileFailureOnce(Type targetType, string memberName, Exception ex)
        {
            // Prevent unbounded growth in long-running editor sessions
            if (s_setterCompileWarnings.Count > MaxSetterCompileWarnings) return;
            if (!s_setterCompileWarnings.TryAdd((targetType, memberName), 0)) return;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            NexusRuntime.Logger?.LogWarning($"[Nexus] Setter compilation failed for {targetType.FullName}.{memberName}: {ex.Message}. Falling back to reflection.");
#endif
        }

        /// <summary>Clears the bounded warning tracker (used when DI caches are reset).</summary>
        internal static void ClearWarnings()
        {
            s_setterCompileWarnings.Clear();
        }
    }
}
