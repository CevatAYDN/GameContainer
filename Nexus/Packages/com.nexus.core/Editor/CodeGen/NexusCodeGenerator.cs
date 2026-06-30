using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor
{
    public static class NexusCodeGenerator
    {
        private const string AutoGenKey = "Nexus.AutoGenerateAOTBinder";
        
        public static bool AutoGenerateEnabled
        {
            get => EditorPrefs.GetBool(AutoGenKey, false);
            set => EditorPrefs.SetBool(AutoGenKey, value);
        }

        [MenuItem("Nexus/Auto-Generate AOT on Script Reload")]
        private static void ToggleAutoGenerate()
        {
            AutoGenerateEnabled = !AutoGenerateEnabled;
            Menu.SetChecked("Nexus/Auto-Generate AOT on Script Reload", AutoGenerateEnabled);
            Debug.Log($"[Nexus] Auto-Generate AOT Binder on script reload: {(AutoGenerateEnabled ? "ENABLED" : "DISABLED")}");
        }

        [MenuItem("Nexus/Auto-Generate AOT on Script Reload", true)]
        private static bool ToggleAutoGenerateValidate()
        {
            Menu.SetChecked("Nexus/Auto-Generate AOT on Script Reload", AutoGenerateEnabled);
            return true;
        }

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            if (!AutoGenerateEnabled) return;
            try
            {
                GenerateBinder();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Nexus] Auto-generate AOT binder failed (non-critical): {ex.Message}");
            }
        }

        [MenuItem("Nexus/Generate AOT Binder")]
        public static void GenerateBinder()
        {
            Debug.Log("[Nexus] Generating AOT Binder...");
            var injectTypes = new List<Type>();
            var networkSignalTypes = new List<Type>();
            
            // Gather all types containing [Inject] and all INetworkSignal implementations
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (name.StartsWith("System") || name.StartsWith("Unity") || name.StartsWith("Microsoft") || name.StartsWith("mono") || name.IndexOf("Tests", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf(".Editor", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsValueType && typeof(Nexus.Netcode.INetworkSignal).IsAssignableFrom(type))
                        {
                            networkSignalTypes.Add(type);
                        }

                        if (type.IsClass && !type.IsAbstract)
                        {
                            bool hasInject = false;
                            
                            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            foreach (var f in fields)
                            {
                                if (f.GetCustomAttribute<InjectAttribute>() != null)
                                {
                                    hasInject = true;
                                    break;
                                }
                            }

                            if (!hasInject)
                            {
                                var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                foreach (var p in properties)
                                {
                                    if (p.GetCustomAttribute<InjectAttribute>() != null)
                                    {
                                        hasInject = true;
                                        break;
                                    }
                                }
                            }

                            if (!hasInject)
                            {
                                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                foreach (var m in methods)
                                {
                                    if (m.GetCustomAttribute<InjectAttribute>() != null)
                                    {
                                        hasInject = true;
                                        break;
                                    }
                                }
                            }

                            if (hasInject)
                            {
                                injectTypes.Add(type);
                            }
                        }
                    }
                }
                catch (ReflectionTypeLoadException) { }
            }

            var cacheSb = new StringBuilder();
            var initSb = new StringBuilder();
            var preserveSb = new StringBuilder();

            // Check value types first (Issue 6)
            foreach (var type in injectTypes)
            {
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var f in fields)
                {
                    if (f.GetCustomAttribute<InjectAttribute>() != null && f.FieldType.IsValueType)
                    {
                        throw new InvalidOperationException($"[Nexus CodeGen Error] Field '{f.Name}' in type '{type.FullName}' has [Inject] attribute but is a value type ({f.FieldType.Name}). Injection on value types is not supported because value types are passed by value and injected values will be lost.");
                    }
                }

                var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var p in properties)
                {
                    if (p.GetCustomAttribute<InjectAttribute>() != null && p.PropertyType.IsValueType)
                    {
                        throw new InvalidOperationException($"[Nexus CodeGen Error] Property '{p.Name}' in type '{type.FullName}' has [Inject] attribute but is a value type ({p.PropertyType.Name}). Injection on value types is not supported because value types are passed by value and injected values will be lost.");
                    }
                }
            }

            // Generate NetworkSignalBus CustomDispatcher
            if (networkSignalTypes.Count > 0)
            {
                initSb.AppendLine("            NetworkSignalBus.CustomDispatcher = (bus, signal) =>");
                initSb.AppendLine("            {");
                initSb.AppendLine("                var type = signal.GetType();");
                foreach (var sigType in networkSignalTypes)
                {
                    string fullName = sigType.FullName.Replace("+", ".");
                    initSb.AppendLine($"                if (type == typeof({fullName}))");
                    initSb.AppendLine("                {");
                    initSb.AppendLine($"                    bus.Fire(({fullName})signal);");
                    initSb.AppendLine("                    return;");
                    initSb.AppendLine("                }");
                }
                initSb.AppendLine("            };");
                initSb.AppendLine();
            }

            // Generate Injectors and Cache Definitions (Issue 5 & 7)
            foreach (var type in injectTypes)
            {
                string fullName = type.FullName.Replace("+", "."); // handle nested classes
                string typeSafeName = fullName.Replace(".", "_").Replace("+", "_");

                initSb.AppendLine($"            NexusDI.RegisterInjector<{fullName}>((instance, di) =>");
                initSb.AppendLine("            {");

                // Inject Fields
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var f in fields)
                {
                    if (f.GetCustomAttribute<InjectAttribute>() != null && !f.FieldType.IsValueType)
                    {
                        if (f.IsPublic)
                        {
                            initSb.AppendLine($"                instance.{f.Name} = di.Resolve<{f.FieldType.FullName.Replace("+", ".")}>();");
                        }
                        else
                        {
                            string cacheFieldName = $"s_f_{typeSafeName}_{f.Name}";
                            cacheSb.AppendLine($"        private static readonly System.Reflection.FieldInfo {cacheFieldName} = typeof({fullName}).GetField(\"{f.Name}\", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);");
                            initSb.AppendLine($"                {cacheFieldName}.SetValue(instance, di.Resolve<{f.FieldType.FullName.Replace("+", ".")}>());");
                        }
                    }
                }

                // Inject Properties
                var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var p in properties)
                {
                    if (p.GetCustomAttribute<InjectAttribute>() != null && !p.PropertyType.IsValueType)
                    {
                        var setMethod = p.GetSetMethod(true);
                        if (setMethod != null)
                        {
                            if (setMethod.IsPublic)
                            {
                                initSb.AppendLine($"                instance.{p.Name} = di.Resolve<{p.PropertyType.FullName.Replace("+", ".")}>();");
                            }
                            else
                            {
                                string cachePropName = $"s_p_{typeSafeName}_{p.Name}";
                                cacheSb.AppendLine($"        private static readonly System.Reflection.PropertyInfo {cachePropName} = typeof({fullName}).GetProperty(\"{p.Name}\", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);");
                                initSb.AppendLine($"                {cachePropName}.SetValue(instance, di.Resolve<{p.PropertyType.FullName.Replace("+", ".")}>());");
                            }
                        }
                    }
                }

                // Inject Methods
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var m in methods)
                {
                    if (m.GetCustomAttribute<InjectAttribute>() != null)
                    {
                        bool hasValueTypeParams = false;
                        var paramList = new List<string>();
                        foreach (var param in m.GetParameters())
                        {
                            if (param.ParameterType.IsValueType)
                            {
                                hasValueTypeParams = true;
                                break;
                            }
                            paramList.Add($"di.Resolve<{param.ParameterType.FullName.Replace("+", ".")}>()");
                        }
                        
                        if (hasValueTypeParams) continue;

                        if (m.IsPublic)
                        {
                            initSb.AppendLine($"                instance.{m.Name}({string.Join(", ", paramList)});");
                        }
                        else
                        {
                            string cacheMethodName = $"s_m_{typeSafeName}_{m.Name}";
                            var paramTypesString = string.Join(", ", m.GetParameters().Select(param => $"typeof({param.ParameterType.FullName.Replace("+", ".")})"));
                            cacheSb.AppendLine($"        private static readonly System.Reflection.MethodInfo {cacheMethodName} = typeof({fullName}).GetMethod(\"{m.Name}\", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new System.Type[] {{ {paramTypesString} }}, null);");
                            initSb.AppendLine($"                {cacheMethodName}.Invoke(instance, new object[] {{ {string.Join(", ", paramList)} }});");
                        }
                    }
                }

                initSb.AppendLine("            });");
            }

            // Generate PreserveMembers (Issue 4)
            preserveSb.AppendLine("        // Forces IL2CPP to preserve members that are injected");
            preserveSb.AppendLine("        public static void PreserveMembers()");
            preserveSb.AppendLine("        {");
            preserveSb.AppendLine("            #pragma warning disable 0169, 0414, 0219");
            preserveSb.AppendLine("            if (false)");
            preserveSb.AppendLine("            {");

            foreach (var type in injectTypes)
            {
                string fullName = type.FullName.Replace("+", ".");
                
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var f in fields)
                {
                    if (f.GetCustomAttribute<InjectAttribute>() != null)
                    {
                        if (f.IsPublic)
                            preserveSb.AppendLine($"                var _f_{type.Name}_{f.Name} = default({fullName}).{f.Name};");
                        else
                            preserveSb.AppendLine($"                var _f_{type.Name}_{f.Name} = typeof({fullName}).GetField(\"{f.Name}\", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);");
                    }
                }

                var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var p in properties)
                {
                    if (p.GetCustomAttribute<InjectAttribute>() != null && p.GetMethod != null)
                    {
                        if (p.GetMethod.IsPublic)
                            preserveSb.AppendLine($"                var _p_{type.Name}_{p.Name} = default({fullName}).{p.Name};");
                        else
                            preserveSb.AppendLine($"                var _p_{type.Name}_{p.Name} = typeof({fullName}).GetProperty(\"{p.Name}\", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);");
                    }
                }
            }

            preserveSb.AppendLine("            }");
            preserveSb.AppendLine("            #pragma warning restore 0169, 0414, 0219");
            preserveSb.AppendLine("        }");

            // Assemble the final file
            var sb = new StringBuilder();
            sb.AppendLine("//------------------------------------------------------------------------------");
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//     This code was generated by Nexus AOT Binder Code Generator.");
            sb.AppendLine("//     Changes to this file may cause incorrect behavior and will be lost if");
            sb.AppendLine("//     the code is regenerated.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("//------------------------------------------------------------------------------");
            sb.AppendLine();
            sb.AppendLine("using System;");
            sb.AppendLine("using Nexus.Core;");
            sb.AppendLine("using Nexus.Netcode;");
            sb.AppendLine();
            sb.AppendLine("namespace Nexus.Generated");
            sb.AppendLine("{");
            sb.AppendLine("    public static class NexusGeneratedBinder");
            sb.AppendLine("    {");
            
            // Write static cached fields
            sb.Append(cacheSb.ToString());
            sb.AppendLine();

            sb.AppendLine("        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]");
            sb.AppendLine("        public static void Initialize()");
            sb.AppendLine("        {");
            sb.Append(initSb.ToString());
            sb.AppendLine("        }");
            sb.AppendLine();
            
            // Write PreserveMembers
            sb.Append(preserveSb.ToString());
            sb.AppendLine("    }");
            sb.AppendLine("}");

            // Load output settings (Issue 18)
            var settings = NexusEditorSettings.GetOrCreateSettings();
            string binderFolder = settings.BinderOutputPath;
            string linkXmlFolder = settings.LinkXmlOutputPath;

            // Delete old file if the path has changed
            string defaultFolder = "Assets/Scripts/Nexus";
            string defaultFile = Path.Combine(defaultFolder, "NexusGeneratedBinder.g.cs");
            if (binderFolder != defaultFolder && File.Exists(defaultFile))
            {
                File.Delete(defaultFile);
                if (File.Exists(defaultFile + ".meta"))
                    File.Delete(defaultFile + ".meta");
            }

            // Create target folders
            if (!Directory.Exists(binderFolder))
                Directory.CreateDirectory(binderFolder);
            if (!Directory.Exists(linkXmlFolder))
                Directory.CreateDirectory(linkXmlFolder);

            // Write binder file
            string destBinderFile = Path.Combine(binderFolder, "NexusGeneratedBinder.g.cs");
            File.WriteAllText(destBinderFile, sb.ToString());
            EnsureGitIgnore(binderFolder, "NexusGeneratedBinder.g.cs");

            // Write link.xml (Issue 4)
            var typesByAssembly = new Dictionary<string, List<Type>>();
            foreach (var type in injectTypes)
            {
                var asmName = type.Assembly.GetName().Name;
                if (!typesByAssembly.TryGetValue(asmName, out var list))
                {
                    list = new List<Type>();
                    typesByAssembly[asmName] = list;
                }
                list.Add(type);
            }
            foreach (var type in networkSignalTypes)
            {
                var asmName = type.Assembly.GetName().Name;
                if (!typesByAssembly.TryGetValue(asmName, out var list))
                {
                    list = new List<Type>();
                    typesByAssembly[asmName] = list;
                }
                if (!list.Contains(type))
                    list.Add(type);
            }

            var xmlSb = new StringBuilder();
            xmlSb.AppendLine("<linker>");
            foreach (var kvp in typesByAssembly)
            {
                xmlSb.AppendLine($"  <assembly fullname=\"{kvp.Key}\">");
                foreach (var type in kvp.Value)
                {
                    xmlSb.AppendLine($"    <type fullname=\"{type.FullName}\" preserve=\"all\" />");
                }
                xmlSb.AppendLine("  </assembly>");
            }
            xmlSb.AppendLine("</linker>");

            string destLinkXmlFile = Path.Combine(linkXmlFolder, "link.xml");
            File.WriteAllText(destLinkXmlFile, xmlSb.ToString());
            EnsureGitIgnore(linkXmlFolder, "link.xml");

            AssetDatabase.Refresh();
            Debug.Log($"[Nexus] AOT Binder successfully generated at {destBinderFile}");
            Debug.Log($"[Nexus] AOT link.xml successfully generated at {destLinkXmlFile}");
        }

        private static void EnsureGitIgnore(string folder, string fileNameToIgnore)
        {
            string gitIgnorePath = Path.Combine(folder, ".gitignore");
            if (!File.Exists(gitIgnorePath))
            {
                File.WriteAllText(gitIgnorePath, fileNameToIgnore + "\n");
            }
            else
            {
                string content = File.ReadAllText(gitIgnorePath);
                if (!content.Contains(fileNameToIgnore))
                {
                    File.AppendAllText(gitIgnorePath, "\n" + fileNameToIgnore + "\n");
                }
            }
        }
    }
}
