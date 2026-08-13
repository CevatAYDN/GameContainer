using System;
using System.Collections.Generic;
using System.Reflection;

namespace Nexus.Core
{
    /// <summary>
    /// Encapsulates member-set reflection scanning for injection attributes across runtime, editor,
    /// and codegen. Consolidates member inspection loops and base-class walks into a single, clean
    /// inspection utility.
    /// </summary>
    public static class InjectScanner
    {
        public readonly struct MemberSet
        {
            public readonly FieldInfo[] Fields;
            public readonly PropertyInfo[] Properties;
            public readonly MethodInfo[] Methods;

            public MemberSet(FieldInfo[] fields, PropertyInfo[] properties, MethodInfo[] methods)
            {
                Fields = fields;
                Properties = properties;
                Methods = methods;
            }
        }

        public static MemberSet ScanMembers(Type type)
        {
            var fieldsList = new List<FieldInfo>();
            var propsList = new List<PropertyInfo>();
            var methodsList = new List<MethodInfo>();

            var current = type;
            while (current != null && current != typeof(object))
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

                var fields = current.GetFields(flags);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (fields[i].IsDefined(typeof(InjectAttribute), false) || fields[i].IsDefined(typeof(OptionalInjectAttribute), false))
                        fieldsList.Add(fields[i]);
                }

                var props = current.GetProperties(flags);
                for (int i = 0; i < props.Length; i++)
                {
                    if (props[i].IsDefined(typeof(InjectAttribute), false) || props[i].IsDefined(typeof(OptionalInjectAttribute), false))
                        propsList.Add(props[i]);
                }

                var methods = current.GetMethods(flags);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].IsDefined(typeof(InjectAttribute), false))
                        methodsList.Add(methods[i]);
                }

                current = current.BaseType;
            }

            return new MemberSet(fieldsList.ToArray(), propsList.ToArray(), methodsList.ToArray());
        }

        public static bool HasInjectableMembers(Type type)
        {
            if (type == null || !type.IsClass || type.IsAbstract) return false;
            var members = ScanMembers(type);
            return members.Fields.Length > 0 || members.Properties.Length > 0 || members.Methods.Length > 0;
        }

        public static ConstructorInfo FindInjectionConstructor(Type type, out int markedCtorCount)
        {
            markedCtorCount = 0;
            if (type == null || !type.IsClass || type.IsAbstract) return null;

            var publicCtors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            if (publicCtors.Length == 0) return null;

            ConstructorInfo markedCtor = null;
            for (int i = 0; i < publicCtors.Length; i++)
            {
                if (publicCtors[i].IsDefined(typeof(InjectAttribute), false) || publicCtors[i].IsDefined(typeof(ConstructAttribute), false))
                {
                    markedCtorCount++;
                    markedCtor = publicCtors[i];
                }
            }

            if (markedCtorCount == 1) return markedCtor;
            if (markedCtorCount == 0 && publicCtors.Length == 1) return publicCtors[0];
            return null;
        }
    }
}
