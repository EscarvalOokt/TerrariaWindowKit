using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace Escarval.TerrariaWindowKit
{
    /// <summary>Scopes Core's panel contribution to native pointer blocking during an owned content update.</summary>
    /// <remarks>
    /// Each source-only copy owns its prefix and local leases. Skipping Core's postfix leaves the
    /// native result and unrelated Harmony patches intact; panel registrations are never changed.
    /// </remarks>
    internal static class HarmonyContentInputBoundary
    {
        private static readonly object _gate = new object();

        private static readonly MethodInfo _prefix =
            typeof(HarmonyContentInputBoundary).GetMethod(
                nameof(Prefix),
                BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new MissingMethodException(nameof(HarmonyContentInputBoundary), nameof(Prefix));

        private static Installation _active;

        /// <summary>Attaches this source copy's prefix, sharing it between local backend leases.</summary>
        internal static IDisposable Patch(MethodInfo target, MethodInfo callerToRefresh = null)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            ParameterInfo[] parameters = target.GetParameters();
            if (!target.IsStatic ||
                target.IsAbstract ||
                target.ContainsGenericParameters ||
                target.ReturnType != typeof(void) ||
                parameters.Length != 1 ||
                parameters[0].ParameterType != typeof(bool).MakeByRefType())
                throw new ArgumentException(
                    "A static void target with one ref bool parameter is required.",
                    nameof(target));
            if (callerToRefresh != null &&
                (!callerToRefresh.IsStatic ||
                 callerToRefresh.IsAbstract ||
                 callerToRefresh.ContainsGenericParameters ||
                 callerToRefresh.ReturnType != typeof(bool) ||
                 callerToRefresh.GetParameters().Length != 0))
                throw new ArgumentException(
                    "A static bool getter without parameters is required.",
                    nameof(callerToRefresh));

            lock (_gate)
            {
                Installation installation = _active;
                if (installation != null)
                {
                    installation.CheckThread();
                    if (installation.Target != target || installation.CallerToRefresh != callerToRefresh)
                        throw new InvalidOperationException(
                            "This source copy is attached to another content input target.");
                    if (installation.Installing || installation.Cleaning)
                        throw new InvalidOperationException(
                            "Content input installation or cleanup is already in progress.");
                    if (installation.Closing)
                    {
                        // A failed installation rollback has no caller-owned lease to retry it.
                        // A subsequent attachment can finish that rollback before installing again.
                        if (!installation.FailedInstallation)
                            throw new InvalidOperationException(
                                "Content input cleanup must finish before attaching again.");
                        installation.Unpatch();
                        installation = null;
                    }
                }

                if (installation == null)
                {
                    installation = new Installation(target, callerToRefresh);
                    _active = installation;
                    try
                    {
                        installation.Harmony.Patch(target, prefix: new HarmonyMethod(_prefix));
                        installation.RefreshCaller();
                        installation.Installing = false;
                    }
                    catch (Exception failure)
                    {
                        installation.Installing = false;
                        installation.Closing = true;
                        installation.FailedInstallation = true;
                        try
                        {
                            installation.Unpatch();
                        }
                        catch (Exception rollback)
                        {
                            throw new AggregateException(
                                "Content input installation and rollback both failed.",
                                failure,
                                rollback);
                        }

                        throw;
                    }
                }

                installation.References++;
                var released = false;
                return new ActionLease(() =>
                {
                    lock (_gate)
                    {
                        installation.CheckThread();
                        if (!released)
                        {
                            installation.References--;
                            released = true;
                        }

                        if (installation.References != 0) return;
                        installation.Closing = true;
                        installation.Unpatch();
                    }
                });
            }
        }

        /// <summary>Uses the innermost local update's current eligibility until its scope is disposed.</summary>
        internal static IDisposable Enter(Func<bool> canReceiveInput)
        {
            if (canReceiveInput == null) throw new ArgumentNullException(nameof(canReceiveInput));
            lock (_gate)
            {
                Installation installation = _active;
                if (installation == null || installation.Installing || installation.Closing)
                    throw new InvalidOperationException(
                        "The content input boundary must be attached before entering it.");
                installation.CheckThread();
                var scope = new ContentScope(canReceiveInput);
                installation.Scopes.Add(scope);
                return new ActionLease(() =>
                {
                    lock (_gate)
                    {
                        installation.CheckThread();
                        // Removing a particular scope also tolerates disposal during a nested update.
                        installation.Scopes.Remove(scope);
                    }
                });
            }
        }

        // This is the argument of Core's void postfix, not Harmony's return-value __result.
        // Never clear an existing native/foreign blocker or invoke a consumer predicate for it.
        private static bool Prefix(ref bool __0)
        {
            if (__0) return true;
            // Lifecycle writes hold _gate. Native queries only inspect mutable state on its UI thread.
            // ReSharper disable once InconsistentlySynchronizedField
            Installation installation = _active;
            if (installation == null || installation.Thread != Thread.CurrentThread.ManagedThreadId)
                return true;
            if (installation.Installing || installation.Closing || installation.Scopes.Count == 0)
                return true;
            ContentScope scope = installation.Scopes[installation.Scopes.Count - 1];
            return !scope.CanReceiveInput();
        }

        private sealed class ContentScope
        {
            internal readonly Func<bool> CanReceiveInput;

            internal ContentScope(Func<bool> canReceiveInput)
            {
                CanReceiveInput = canReceiveInput;
            }
        }

        private sealed class Installation
        {
            internal readonly MethodInfo CallerToRefresh;

            internal readonly Harmony Harmony = new Harmony(
                "escarval.terrariawindowkit.core.content." + Guid.NewGuid().ToString("N"));

            internal readonly List<ContentScope> Scopes = new List<ContentScope>();
            internal readonly MethodInfo Target;
            internal readonly int Thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            internal bool Cleaning;
            internal bool Closing;
            internal bool FailedInstallation;
            internal bool Installing = true;
            internal int References;

            internal Installation(MethodInfo target, MethodInfo callerToRefresh)
            {
                Target = target;
                CallerToRefresh = callerToRefresh;
            }

            internal void CheckThread()
            {
                if (Thread != System.Threading.Thread.CurrentThread.ManagedThreadId)
                    throw new InvalidOperationException("Content input operations must run on the owning UI thread.");
            }

            internal void Unpatch()
            {
                if (Cleaning) throw new InvalidOperationException("Content input cleanup is already in progress.");
                Cleaning = true;
                try
                {
                    // The same target can contain another source copy's prefix and foreign patches.
                    Harmony.Unpatch(Target, _prefix);
                    RefreshCaller();
                    // Patch and backend-lease cleanup call this method while holding _gate.
                    // ReSharper disable InconsistentlySynchronizedField
                    if (ReferenceEquals(_active, this)) _active = null;
                    // ReSharper restore InconsistentlySynchronizedField
                }
                finally
                {
                    Cleaning = false;
                }
            }

            internal void RefreshCaller()
            {
                // Core's small postfix may already be inlined into the native getter's Harmony
                // wrapper. Rebuild that wrapper after changing its callee, retaining every patch.
                if (CallerToRefresh != null) Harmony.Patch(CallerToRefresh);
            }
        }
    }
}