using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

// ReSharper disable InconsistentNaming

namespace Escarval.TerrariaWindowKit
{
    /// <summary>
    /// Wraps Core's global scroll blockers. Installation is explicit: source-only
    /// copies must enter through the shared runtime lease, never PatchAll.
    /// </summary>
    internal static class HarmonyScrollBoundary
    {
        // Stable patch owner for this runtime family, independent of external dependency versions.
        internal const string HarmonyOwner = "escarval.terrariawindowkit.core.scroll";
        private static readonly object _gate = new object();

        private static readonly MethodInfo _prefixMethod = typeof(HarmonyScrollBoundary).GetMethod(
            nameof(Prefix),
            BindingFlags.Static | BindingFlags.NonPublic);

        private static readonly MethodInfo _finalizerMethod = typeof(HarmonyScrollBoundary).GetMethod(
            nameof(Finalizer),
            BindingFlags.Static | BindingFlags.NonPublic);

        private static Installation _active;

        internal static IDisposable Patch(MethodInfo[] targets, Func<bool> hasScroll, Func<IDisposable> suspend)
        {
            if (targets == null) throw new ArgumentNullException(nameof(targets));
            if (targets.Length == 0)
                throw new ArgumentException("At least one scroll boundary is required.", nameof(targets));
            if (hasScroll == null) throw new ArgumentNullException(nameof(hasScroll));
            if (suspend == null) throw new ArgumentNullException(nameof(suspend));

            lock (_gate)
            {
                if (_active != null)
                    throw new InvalidOperationException(
                        "A scroll boundary is already installed by this source-only copy.");

                var unique = new HashSet<MethodInfo>();
                foreach (MethodInfo target in targets)
                {
                    if (target == null || !unique.Add(target))
                        throw new ArgumentException(
                            "Scroll boundaries must be non-null and distinct.",
                            nameof(targets));
                    Patches existing = Harmony.GetPatchInfo(target);
                    if (existing != null && existing.Owners.Contains(HarmonyOwner))
                        throw new InvalidOperationException(
                            "Another provider already owns the toolkit scroll boundary.");
                }

                var installation = new Installation(hasScroll, suspend);
                _active = installation;
                try
                {
                    foreach (MethodInfo target in targets)
                    {
                        // Harmony can record a patch before rebuilding its wrapper throws.
                        // Include the failing target in rollback as well as earlier targets.
                        installation.AttemptedTargets.Add(target);
                        installation.Harmony.Patch(
                            target,
                            prefix: new HarmonyMethod(_prefixMethod),
                            finalizer: new HarmonyMethod(_finalizerMethod));
                    }

                    return installation;
                }
                catch (Exception installError)
                {
                    try
                    {
                        installation.Dispose();
                    }
                    catch (Exception rollbackError)
                    {
                        throw new AggregateException(
                            "Scroll installation and rollback both failed.",
                            installError,
                            rollbackError);
                    }

                    throw;
                }
            }
        }

        private static void Prefix(out IDisposable __state)
        {
            // Assign state before any callback can mutate bounds or throw. The
            // callback itself must roll back partial work if it cannot return a lease.
            var scope = new DeferredScope();
            __state = scope;
            Installation installation = _active;
            if (installation == null || !installation.HasScroll()) return;
            scope.Set(installation.Suspend());
        }

        private static Exception Finalizer(Exception __exception, IDisposable __state)
        {
            try
            {
                __state?.Dispose();
            }
            catch (Exception restoreError)
            {
                // Preserve the original failure, including its identity and stack.
                // If the target succeeded, restoration failure must remain visible.
                if (__exception == null) return restoreError;
            }

            return __exception;
        }

        private sealed class DeferredScope : IDisposable
        {
            private bool _disposed;
            private IDisposable _inner;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                IDisposable inner = _inner;
                _inner = null;
                inner?.Dispose();
            }

            internal void Set(IDisposable inner)
            {
                if (_disposed)
                {
                    inner?.Dispose();
                    return;
                }

                _inner = inner;
            }
        }

        private sealed class Installation : IDisposable
        {
            internal readonly List<MethodInfo> AttemptedTargets = new List<MethodInfo>();
            internal readonly Harmony Harmony = new Harmony(HarmonyOwner);
            internal readonly Func<bool> HasScroll;
            internal readonly Func<IDisposable> Suspend;
            private bool _disposed;

            internal Installation(Func<bool> hasScroll, Func<IDisposable> suspend)
            {
                HasScroll = hasScroll;
                Suspend = suspend;
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    var failures = new List<Exception>();
                    foreach (MethodInfo target in AttemptedTargets)
                    {
                        TryUnpatch(target, _prefixMethod, failures);
                        TryUnpatch(target, _finalizerMethod, failures);
                    }

                    if (failures.Count != 0)
                        throw new AggregateException("Could not remove all toolkit scroll wrappers.", failures);
                    _disposed = true;
                    AttemptedTargets.Clear();
                    if (ReferenceEquals(_active, this)) _active = null;
                }
            }

            private void TryUnpatch(MethodInfo target, MethodInfo patch, List<Exception> failures)
            {
                try
                {
                    // A shared owner ID is not sufficient to identify our methods.
                    Harmony.Unpatch(target, patch);
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }
        }
    }
}