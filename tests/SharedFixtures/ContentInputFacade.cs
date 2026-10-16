using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Escarval.TerrariaWindowKit;
using HarmonyLib;

#if CONSUMER_A
namespace UiConsumerA
#else
namespace UiConsumerB
#endif
{
    // Compiled independently into both consumers; only BCL objects cross the assembly boundary.
    public static class ContentInputFixture
    {
        private static readonly Dictionary<string, IDisposable> _leases = new Dictionary<string, IDisposable>();

        public static string Patch(MethodInfo target) => Retain(HarmonyContentInputBoundary.Patch(target));

        public static string PatchWithCaller(MethodInfo target, MethodInfo caller) =>
            Retain(HarmonyContentInputBoundary.Patch(target, caller));

        public static string Enter(Func<bool> canReceiveInput) =>
            Retain(HarmonyContentInputBoundary.Enter(canReceiveInput));

        public static void Release(string token)
        {
            _leases[token].Dispose();
            _leases.Remove(token);
        }

        public static int PrefixCount(MethodInfo target) =>
            Harmony.GetPatchInfo(target)
                ?.Prefixes.Count(patch => patch.PatchMethod.DeclaringType == typeof(HarmonyContentInputBoundary)) ??
            0;

        private static string Retain(IDisposable lease)
        {
            var token = Guid.NewGuid().ToString("N");
            _leases.Add(token, lease);
            return token;
        }
    }
}