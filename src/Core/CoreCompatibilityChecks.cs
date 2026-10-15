using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Escarval.TerrariaWindowKit
{
    // BCL-only checks also run against synthetic dependency types in TestHost.
    // Structural compatibility does not establish the behavior of an untested runtime.
    internal static class CoreCompatibilityChecks
    {
        // Type handles identify the actual loaded CLR types, not just assembly names or versions.
        // The signature is meaningful only among consumers inside the same AppDomain.
        internal static string CreateRuntimeSignature(string runtimeContract, params Type[] dependencyTypes)
        {
            if (string.IsNullOrWhiteSpace(runtimeContract))
                throw new ArgumentException("A runtime contract identifier is required.", nameof(runtimeContract));
            if (dependencyTypes == null) throw new ArgumentNullException(nameof(dependencyTypes));
            if (dependencyTypes.Length == 0)
                throw new ArgumentException("At least one dependency type is required.", nameof(dependencyTypes));

            var signature = new StringBuilder(runtimeContract);
            foreach (Type dependencyType in dependencyTypes)
            {
                if (dependencyType == null)
                    throw new ArgumentException("Dependency types must not be null.", nameof(dependencyTypes));
                signature.Append(';');
                signature.Append(
                    dependencyType.TypeHandle.Value.ToInt64().ToString("X16", CultureInfo.InvariantCulture));
            }

            return signature.ToString();
        }

        internal static void ValidateCoreContracts(Type rendererType, Type widgetInputType, Version loadedVersion)
        {
            if (rendererType == null) throw new ArgumentNullException(nameof(rendererType));
            if (widgetInputType == null) throw new ArgumentNullException(nameof(widgetInputType));
            try
            {
                // Public Core operations used by CoreWindowBackend. No exact assembly version is required.
                RequirePublicMethod(
                    rendererType,
                    "RegisterPanelDraw",
                    true,
                    typeof(void),
                    typeof(string),
                    typeof(Action));
                RequirePublicMethod(rendererType, "UnregisterPanelDraw", true, typeof(void), typeof(string));
                RequirePublicMethod(
                    rendererType,
                    "RegisterPanelBounds",
                    true,
                    typeof(void),
                    typeof(string),
                    typeof(int),
                    typeof(int),
                    typeof(int),
                    typeof(int));
                RequirePublicMethod(rendererType, "UnregisterPanelBounds", true, typeof(void), typeof(string));
                RequirePublicMethod(rendererType, "BringToFront", true, typeof(void), typeof(string));
                RequirePublicMethod(
                    rendererType,
                    "ShouldBlockForHigherPriorityPanel",
                    true,
                    typeof(bool),
                    typeof(string));
                RequirePublicMethod(rendererType, "IsMouseOverPanel", true, typeof(bool), typeof(string));
                RequirePublicMethod(rendererType, "RegisterKeyInputBlock", true, typeof(void), typeof(string));
                RequirePublicMethod(rendererType, "UnregisterKeyInputBlock", true, typeof(void), typeof(string));
                RequirePublicStaticProperty(widgetInputType, "BlockInput", typeof(bool), true);
                RequirePublicStaticProperty(widgetInputType, "MouseLeftClick", typeof(bool), false);
            }
            catch (MissingMemberException error)
            {
                throw new NotSupportedException(
                    "TerrariaWindowKit requires a TerrariaModder Core contract not found in loaded version " +
                    (loadedVersion?.ToString() ?? "unknown") +
                    ".",
                    error);
            }
        }

        internal static void ValidateHarmonyContracts(
            Type harmonyType,
            Type harmonyMethodType,
            Type patchesType,
            Version loadedVersion)
        {
            if (harmonyType == null) throw new ArgumentNullException(nameof(harmonyType));
            if (harmonyMethodType == null) throw new ArgumentNullException(nameof(harmonyMethodType));
            if (patchesType == null) throw new ArgumentNullException(nameof(patchesType));
            try
            {
                RequirePublicConstructor(harmonyType, typeof(string));
                RequirePublicConstructor(harmonyMethodType, typeof(MethodInfo));
                RequirePublicMethod(harmonyType, "GetPatchInfo", true, patchesType, typeof(MethodBase));
                RequirePublicMethod(
                    harmonyType,
                    "Patch",
                    false,
                    typeof(MethodInfo),
                    typeof(MethodBase),
                    harmonyMethodType,
                    harmonyMethodType,
                    harmonyMethodType,
                    harmonyMethodType);
                RequirePublicMethod(
                    harmonyType,
                    "Unpatch",
                    false,
                    typeof(void),
                    typeof(MethodBase),
                    typeof(MethodInfo));
                RequirePublicOwners(patchesType);
            }
            catch (MissingMemberException error)
            {
                throw new NotSupportedException(
                    "TerrariaWindowKit requires a Harmony contract not found in loaded version " +
                    (loadedVersion?.ToString() ?? "unknown") +
                    ".",
                    error);
            }
        }

        // Preserve the exact private metadata checks used by the current Core adapter.
        internal static FieldInfo RequireField(Type declaringType, string name, Type fieldType)
        {
            FieldInfo field = declaringType.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (field == null || field.FieldType != fieldType)
                throw new MissingFieldException(declaringType.FullName, name);
            return field;
        }

        internal static MethodInfo ResolveTarget(
            Type declaringType,
            string name,
            Type returnType,
            Type[] parameterTypes)
        {
            MethodInfo target = declaringType.GetMethod(
                name,
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                parameterTypes,
                null);
            if (target == null || !target.IsStatic || target.ReturnType != returnType)
                throw new MissingMethodException(declaringType.FullName, name);
            return target;
        }

        private static void RequirePublicMethod(
            Type declaringType,
            string name,
            bool isStatic,
            Type returnType,
            params Type[] parameterTypes)
        {
            BindingFlags flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            foreach (MethodInfo method in declaringType.GetMethods(flags))
            {
                // An assignable parameter type (such as object) is not the same CLR method contract.
                if (method.Name != name || method.IsStatic != isStatic || method.ReturnType != returnType) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != parameterTypes.Length) continue;
                var exactParameters = true;
                for (var i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].ParameterType == parameterTypes[i]) continue;
                    exactParameters = false;
                    break;
                }

                if (exactParameters) return;
            }

            throw new MissingMethodException(declaringType.FullName, name);
        }

        private static void RequirePublicConstructor(Type declaringType, params Type[] parameterTypes)
        {
            if (declaringType.GetConstructor(parameterTypes) == null)
                throw new MissingMethodException(declaringType.FullName, ".ctor");
        }

        private static void RequirePublicStaticProperty(Type declaringType, string name, Type valueType, bool writable)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            PropertyInfo property = declaringType.GetProperty(name, flags);
            if (property != null &&
                property.PropertyType == valueType &&
                property.GetIndexParameters().Length == 0 &&
                property.GetGetMethod() is { IsStatic: true } &&
                (!writable || property.GetSetMethod() is { IsStatic: true })) return;
            throw new MissingMemberException(declaringType.FullName, name);
        }

        private static void RequirePublicOwners(Type patchesType)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            PropertyInfo property = patchesType.GetProperty("Owners", flags);
            if (property != null &&
                property.PropertyType == typeof(ReadOnlyCollection<string>) &&
                property.GetIndexParameters().Length == 0 &&
                property.GetGetMethod() is { IsStatic: false }) return;
            throw new MissingMemberException(patchesType.FullName, "Owners");
        }
    }
}