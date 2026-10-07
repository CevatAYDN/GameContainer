using System;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Core
{
    // Registration-time analysis only. Resolution and validation share injection metadata
    // and binding ownership; validation never executes a constructor, factory or lifecycle.
    internal static class DiBindingValidator
    {
        internal static List<DiValidationIssue> Validate(NexusDI container, int maxParameters)
        {
            var issues = new List<DiValidationIssue>();
            var bindings = container.GetValidationBindings();
            var edges = new Dictionary<object, List<NexusDI.ValidationBinding>>();
            foreach (var binding in bindings)
            {
                if (!binding.IsOpaque && binding.Concrete != null && !binding.Key.IsAssignableFrom(binding.Concrete))
                    issues.Add(new DiValidationIssue(binding.Concrete, binding.Key, DiValidationIssueType.InvalidBinding,
                        $"Implementation '{binding.Concrete.FullName}' is not assignable to binding '{Label(binding)}'."));
                if (binding.IsOpaque || edges.ContainsKey(binding.Identity)) continue;
                var dependencies = new List<NexusDI.ValidationBinding>();
                edges.Add(binding.Identity, dependencies);
                NexusDI.InjectableMetadata meta;
                try
                {
                    if (binding.Concrete == null || binding.Concrete.IsAbstract || binding.Concrete.IsInterface)
                        throw new InvalidOperationException("An implementation, instance or factory is required.");
                    if (binding.Concrete.ContainsGenericParameters)
                        throw new InvalidOperationException("Close the generic implementation type before registering it.");
                    meta = NexusDI.GetOrCreateInjectMetadata(binding.Concrete);
                    if (!binding.Concrete.IsValueType && meta.Constructor == null && binding.Concrete.GetConstructor(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                        null, Type.EmptyTypes, null) == null)
                        throw new InvalidOperationException("No usable constructor exists. Register an explicit factory or instance.");
                }
                catch (Exception error)
                {
                    issues.Add(new DiValidationIssue(binding.Concrete ?? binding.Key, binding.Key,
                        DiValidationIssueType.InvalidBinding, $"Invalid binding '{Label(binding)}': {error.Message}"));
                    continue;
                }

                void Check(Type dependency, string name, bool optional, bool lazy,
                    DiValidationIssueType kind, string member)
                {
                    if (lazy) return; // LazyInjection<T> resolves only when its Value is read.
                    if (!binding.Owner.IsRegistered(dependency, name))
                    {
                        if (!optional)
                            issues.Add(new DiValidationIssue(binding.Concrete, dependency, kind,
                                $"'{Label(binding)}' {member} requires '{dependency.FullName}'" +
                                (string.IsNullOrEmpty(name) ? "" : $" named '{name}'") + "; register this dependency in its owning scope."));
                        return;
                    }
                    var target = binding.Owner.FindValidationBinding(dependency, name);
                    if (target == null) return; // Built-in container or opaque external adapter.
                    if (!target.IsOpaque) dependencies.Add(target);
                    if (binding.Lifetime != Lifetime.Transient && target.Lifetime == Lifetime.Transient && !target.IsOpaque)
                        issues.Add(new DiValidationIssue(binding.Concrete, dependency, DiValidationIssueType.CaptiveDependency,
                            $"[CaptiveDependency] Cached '{Label(binding)}' captures transient '{Label(target)}' via {member}. " +
                            "Use a cached binding or an explicit factory to manage its lifetime."));
                }

                var parameters = meta.ConstructorParameterTypes ?? Array.Empty<Type>();
                if (maxParameters > 0 && parameters.Length > maxParameters)
                    issues.Add(new DiValidationIssue(binding.Concrete, binding.Concrete,
                        DiValidationIssueType.MissingConstructorDependency,
                        $"[ConstructorExplosion] '{Label(binding)}' has {parameters.Length} constructor parameters (> {maxParameters})."));
                for (int i = 0; i < parameters.Length; i++)
                {
                    bool overridden = false;
                    if (binding.Overrides != null)
                        for (int o = 0; o < binding.Overrides.Length; o++)
                            if (binding.Overrides[o].Type == parameters[i])
                            {
                                overridden = true;
                                var value = binding.Overrides[o].Value;
                                if ((value == null && parameters[i].IsValueType && Nullable.GetUnderlyingType(parameters[i]) == null)
                                    || (value != null && !parameters[i].IsInstanceOfType(value)))
                                    issues.Add(new DiValidationIssue(binding.Concrete, parameters[i], DiValidationIssueType.InvalidBinding,
                                        $"'{Label(binding)}' constructor parameter {i} has an incompatible explicit value."));
                                break; // First override wins, matching CreateInstance.
                            }
                    if (!overridden)
                        Check(parameters[i], meta.ConstructorParameterNames?[i],
                            meta.ConstructorParameterHasDefault?[i] == true, false,
                            DiValidationIssueType.MissingConstructorDependency, $"constructor parameter {i}");
                }
                foreach (var field in meta.Fields)
                    Check(field.Type, field.Name, field.IsOptional, field.IsLazy,
                        DiValidationIssueType.MissingFieldDependency, $"[Inject] field '{field.Field.Name}'");
                foreach (var property in meta.Properties)
                    Check(property.Type, property.Name, property.IsOptional, property.IsLazy,
                        DiValidationIssueType.MissingPropertyDependency, $"[Inject] property '{property.Property.Name}'");
                foreach (var method in meta.Methods)
                    for (int i = 0; i < method.ParameterTypes.Length; i++)
                        Check(method.ParameterTypes[i], method.ParameterNames?[i], method.OptionalParameterMask[i], false,
                            DiValidationIssueType.MissingMethodDependency, $"[Inject] method '{method.Method.Name}' parameter {i}");
            }

            var states = new Dictionary<object, int>();
            var path = new List<NexusDI.ValidationBinding>();
            void Visit(NexusDI.ValidationBinding binding)
            {
                if (states.TryGetValue(binding.Identity, out int state))
                {
                    if (state != 1) return;
                    int start = path.FindIndex(item => ReferenceEquals(item.Identity, binding.Identity));
                    var chain = new StringBuilder();
                    for (int i = start; i < path.Count; i++) chain.Append(Label(path[i])).Append(" -> ");
                    chain.Append(Label(binding));
                    issues.Add(new DiValidationIssue(binding.Concrete, binding.Key, DiValidationIssueType.CircularDependency,
                        "Circular dependency: " + chain + ". Break the eager dependency chain."));
                    return;
                }
                states[binding.Identity] = 1;
                path.Add(binding);
                if (edges.TryGetValue(binding.Identity, out var next))
                    foreach (var dependency in next) Visit(dependency);
                path.RemoveAt(path.Count - 1);
                states[binding.Identity] = 2;
            }
            foreach (var binding in bindings)
                if (!binding.IsOpaque) Visit(binding);
            return issues;
        }

        private static string Label(NexusDI.ValidationBinding binding)
            => binding.Key.FullName + (string.IsNullOrEmpty(binding.Name) ? "" : $" named '{binding.Name}'");
    }
}
