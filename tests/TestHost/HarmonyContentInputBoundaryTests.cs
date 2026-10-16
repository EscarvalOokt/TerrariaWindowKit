using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using NUnit.Framework;

// ReSharper disable InconsistentNaming

namespace Escarval.TerrariaWindowKit.TestHost
{
    [TestFixture]
    [NonParallelizable]
    public sealed class HarmonyContentInputBoundaryTests
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            string[] scenarios =
            [
                "local lifetime", "nested scopes", "source copies", "foreign patches",
                "validation and thread", "failed patch and cleanup", "warm native wrapper"
            ];
            foreach (string scenario in scenarios)
            {
                yield return new TestCaseData(scenario, false).SetName("content boundary " + scenario + " [A, B]");
                yield return new TestCaseData(scenario, true).SetName("content boundary " + scenario + " [B, A]");
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void ContentInputBoundary(string scenario, bool reverse)
        {
            string path = typeof(ContentInputBoundaryRunner).Assembly.Location;
            var domain = AppDomain.CreateDomain(
                "content input: " + scenario,
                null,
                new AppDomainSetup { ApplicationBase = Path.GetDirectoryName(path) });
            try
            {
                string runnerTypeName = typeof(ContentInputBoundaryRunner).FullName ??
                                        throw new InvalidOperationException(
                                            "Content input runner has no full type name.");
                if (domain.CreateInstanceFromAndUnwrap(path, runnerTypeName) is not ContentInputBoundaryRunner runner)
                    throw new InvalidOperationException("Could not create the isolated content input runner.");
                string failure = runner.Run(scenario, reverse);
                Assert.That(failure, Is.Null, failure);
            }
            finally
            {
                AppDomain.Unload(domain);
            }
        }
    }

    // Fresh domains isolate Harmony detours and each source copy's installation/static scope state.
    public sealed class ContentInputBoundaryRunner : MarshalByRefObject
    {
        private static readonly bool _coreBlocking = true;
        private static bool _nativeBlocking;
        private static bool _foreignBlocking;
        private static bool _failNextTranspiler;
        private static Action _duringTranspiler;
        private static int _foreignCalls;
        private static int _coreCalls;

        private static readonly MethodInfo _target = Method(nameof(CorePostfix));
        private static readonly MethodInfo _otherTarget = Method(nameof(OtherCorePostfix));
        private Consumer _first;
        private Consumer _second;

        public string Run(string scenario, bool reverse)
        {
            try
            {
                var a = new Consumer("A");
                var b = new Consumer("B");
                _first = reverse ? b : a;
                _second = reverse ? a : b;
                switch (scenario)
                {
                    case "local lifetime": LocalLifetime(); break;
                    case "nested scopes": NestedScopes(); break;
                    case "source copies": SourceCopies(); break;
                    case "foreign patches": ForeignPatches(); break;
                    case "validation and thread": ValidationAndThread(); break;
                    case "failed patch and cleanup": FailedPatchAndCleanup(); break;
                    case "warm native wrapper": WarmNativeWrapper(); break;
                    default: throw new ArgumentException("Unknown scenario.");
                }

                return null;
            }
            catch (Exception failure)
            {
                return failure.ToString();
            }
        }

        private void LocalLifetime()
        {
            Assert(ReadCore(false), "Core blocks before attachment.");
            string first = _first.Patch(_target);
            string second = _first.Patch(_target);
            Equal(1, _first.Count(_target), "Local backend leases share one prefix.");
            Assert(ReadCore(false), "Attachment alone cannot disable Core.");
            string scope = _first.Enter(() => true);
            Assert(!ReadCore(false), "An eligible content scope bypasses Core's panel contribution.");
            Assert(ReadCore(true), "An already true native result remains true.");
            _first.Release(first);
            Assert(!ReadCore(false), "Releasing one backend leaves the other backend operational.");
            _first.Release(second);
            Equal(0, _first.Count(_target), "The last backend removes only its prefix.");
            Assert(ReadCore(false), "Disposing the last backend inside an update restores Core immediately.");
            string replacement = _first.Patch(_target);
            string replacementScope = _first.Enter(() => true);
            _first.Release(scope);
            Assert(!ReadCore(false), "Disposing an old scope cannot remove a new installation's scope.");
            _first.Release(replacementScope);
            _first.Release(replacement);
        }

        private void NestedScopes()
        {
            string patch = _first.Patch(_target);
            var eligible = true;
            // Changing eligibility after Enter is the behavior under test; a captured snapshot would hide it.
            // ReSharper disable once AccessToModifiedClosure
            string outer = _first.Enter(() => eligible);
            Assert(!ReadCore(false), "The outer scope permits native content.");
            eligible = false;
            Assert(ReadCore(false), "Eligibility is reevaluated for every query.");
            eligible = true;
            string inner = _first.Enter(() => false);
            Assert(ReadCore(false), "The innermost local scope controls eligibility.");
            _first.Release(inner);
            Assert(!ReadCore(false), "Inner disposal restores the outer predicate.");
            string throwing = _first.Enter(() => throw new ArgumentException("Synthetic predicate failure."));
            Throws<ArgumentException>(() => ReadCore(false));
            Assert(ReadCore(true), "A native blocker does not require running a throwing predicate.");
            _first.Release(throwing);
            Assert(!ReadCore(false), "A failed query leaves the outer scope usable after cleanup.");
            inner = _first.Enter(() => true);
            _first.Release(outer);
            Assert(!ReadCore(false), "Disposing an outer lease does not remove the nested scope.");
            _first.Release(inner);
            Assert(ReadCore(false), "The final scope removal restores Core.");
            _first.Release(patch);
        }

        private void SourceCopies()
        {
            string firstPatch = _first.Patch(_target);
            string secondPatch = _second.Patch(_target);
            Equal(1, _first.Count(_target), "The first source copy has one prefix.");
            Equal(1, _second.Count(_target), "The second source copy has an independent prefix.");
            var firstEligible = true;
            // The outer host intentionally loses priority while its predicate remains registered.
            // ReSharper disable once AccessToModifiedClosure
            string firstScope = _first.Enter(() => firstEligible);
            Assert(!ReadCore(false), "Either source copy can permit its content update.");
            firstEligible = false;
            string secondScope = _second.Enter(() => true);
            Assert(!ReadCore(false), "A nested eligible source copy works while the outer host loses priority.");
            Assert(ReadCore(true), "Both source copies preserve native blockers.");
            _first.Release(firstScope);
            _first.Release(firstPatch);
            Equal(0, _first.Count(_target), "The first source copy removes its own prefix.");
            Equal(1, _second.Count(_target), "The other source copy's prefix remains installed.");
            Assert(!ReadCore(false), "The surviving consumer still updates content.");
            _second.Release(secondScope);
            Assert(ReadCore(false), "Leaving the remaining scope restores Core.");
            _second.Release(secondPatch);
        }

        private void ForeignPatches()
        {
            var foreign = new Harmony("tests.foreign-content");
            MethodInfo postfix = Method(nameof(ForeignPostfix));
            foreign.Patch(_target, postfix: new HarmonyMethod(postfix));
            string patch = _first.Patch(_target);
            string scope = _first.Enter(() => true);
            _foreignBlocking = true;
            Assert(ReadCore(false), "A foreign postfix may still block input during an eligible scope.");
            Equal(1, _foreignCalls, "Foreign postfixes execute when the Core method is skipped.");
            Equal(0, _coreCalls, "Only Core's own method body is bypassed.");
            _foreignBlocking = false;
            Assert(!ReadCore(false), "Unblocked foreign patches preserve the native false result.");
            _first.Release(scope);
            _first.Release(patch);
            Assert(
                Harmony.GetPatchInfo(_target).Owners.Contains("tests.foreign-content"),
                "Boundary disposal preserves another owner's patches.");
            Assert(ReadCore(false), "Core executes again after boundary disposal.");
            foreign.Unpatch(_target, postfix);
        }

        private void ValidationAndThread()
        {
            Throws<ArgumentNullException>(() => _first.Patch(null));
            Throws<ArgumentException>(() => _first.Patch(Method(nameof(InvalidTarget))));
            Throws<InvalidOperationException>(() => _first.Enter(() => true));
            string patch = _first.Patch(_target);
            Throws<ArgumentNullException>(() => _first.Enter(null));
            Throws<InvalidOperationException>(() => _first.Patch(_otherTarget));
            string scope = _first.Enter(() => true);
            Exception wrongEnter = null;
            Exception wrongRelease = null;
            Exception wrongPatch = null;
            var workerBlocked = false;
            var worker = new Thread(() =>
            {
                try
                {
                    _first.Enter(() => true);
                }
                catch (Exception failure)
                {
                    wrongEnter = failure;
                }

                try
                {
                    _first.Release(scope);
                }
                catch (Exception failure)
                {
                    wrongRelease = failure;
                }

                try
                {
                    _first.Patch(_target);
                }
                catch (Exception failure)
                {
                    wrongPatch = failure;
                }

                workerBlocked = ReadCore(false);
            });
            worker.Start();
            worker.Join();
            Assert(wrongEnter is InvalidOperationException, "Scopes reject another thread.");
            Assert(
                wrongRelease is InvalidOperationException,
                "Scope cleanup rejects another thread without losing its lease.");
            Assert(wrongPatch is InvalidOperationException, "Additional backend leases reject another thread.");
            Assert(workerBlocked, "An unrelated thread retains Core's normal blocking behavior.");
            Assert(!ReadCore(false), "A failed cross-thread cleanup leaves the owning scope active.");
            _first.Release(scope);
            _first.Release(patch);
            patch = _first.Patch(_otherTarget);
            _first.Release(patch);
        }

        private void FailedPatchAndCleanup()
        {
            var foreign = new Harmony("tests.content-install-failure");
            MethodInfo transpiler = Method(nameof(FailingTranspiler));
            foreign.Patch(_target, transpiler: new HarmonyMethod(transpiler));
            _duringTranspiler = () =>
            {
                Throws<InvalidOperationException>(() => _first.Patch(_target));
                Throws<InvalidOperationException>(() => _first.Enter(() => true));
            };
            string initial = _first.Patch(_target);
            _first.Release(initial);
            _failNextTranspiler = true;
            Throws<Exception>(() => _first.Patch(_target));
            Equal(0, _first.Count(_target), "A failed installation rolls back its recorded prefix.");
            string patch = _first.Patch(_target);
            string scope = _first.Enter(() => true);
            Assert(!ReadCore(false), "A successful retry works after installation rollback.");
            _first.Release(scope);
            _failNextTranspiler = true;
            // This action runs synchronously; the token changes only after cleanup has been retried.
            // ReSharper disable once AccessToModifiedClosure
            Throws<Exception>(() => _first.Release(patch));
            Throws<InvalidOperationException>(() => _first.Patch(_target));
            _first.Release(patch);
            Equal(0, _first.Count(_target), "A failed last-lease cleanup can be retried without decrementing twice.");
            patch = _first.Patch(_target);
            _first.Release(patch);
            Assert(
                Harmony.GetPatchInfo(_target).Owners.Contains("tests.content-install-failure"),
                "Failure rollback and retries preserve foreign patches.");
            foreign.Unpatch(_target, transpiler);
        }

        private void WarmNativeWrapper()
        {
            MethodInfo getter = Method(nameof(NativeGetter));
            MethodInfo core = Method(nameof(InlineableCorePostfix));
            var coreOwner = new Harmony("tests.core-native-input");
            coreOwner.Patch(getter, postfix: new HarmonyMethod(core));
            var foreign = new Harmony("tests.native.foreign-content");
            MethodInfo foreignPostfix = Method(nameof(ForeignNativePostfix));
            foreign.Patch(getter, postfix: new HarmonyMethod(foreignPostfix));
            for (var i = 0; i < 100000; i++)
                if (!NativeGetter())
                    throw new InvalidOperationException("Core must block the warmed native wrapper.");
            string patch = _first.Patch(core, getter);
            string scope = _first.Enter(() => true);
            Assert(
                !NativeGetter(),
                "A previously JIT-compiled native Harmony wrapper uses the newly scoped Core postfix.");
            _foreignBlocking = true;
            Assert(NativeGetter(), "Refreshing the native wrapper preserves a foreign input-blocking postfix.");
            _foreignBlocking = false;
            string secondPatch = _second.Patch(core, getter);
            string secondScope = _second.Enter(() => true);
            _first.Release(scope);
            _first.Release(patch);
            Assert(
                !NativeGetter(),
                "Refreshing after one source copy detaches preserves the other source copy's boundary.");
            _nativeBlocking = true;
            Assert(NativeGetter(), "The actual native getter's true result survives the scoped boundary.");
            _nativeBlocking = false;
            _second.Release(secondScope);
            _second.Release(secondPatch);
            Assert(NativeGetter(), "Removing the boundary restores the warmed Core blocker.");
            Assert(
                Harmony.GetPatchInfo(getter).Owners.Contains("tests.native.foreign-content"),
                "All wrapper refreshes preserve the foreign patch registration.");
            foreign.Unpatch(getter, foreignPostfix);
            coreOwner.Unpatch(getter, core);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadCore(bool nativeBlocked)
        {
            CorePostfix(ref nativeBlocked);
            return nativeBlocked;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CorePostfix(ref bool __result)
        {
            _coreCalls++;
            if (!__result && _coreBlocking) __result = true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void OtherCorePostfix(ref bool __result)
        {
            if (!__result && _coreBlocking) __result = true;
        }

        // Intentionally eligible for JIT inlining, matching the real Core postfix.
        private static void InlineableCorePostfix(ref bool __result)
        {
            if (!__result && _coreBlocking) __result = true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool NativeGetter() => _nativeBlocking;

        private static void ForeignPostfix(ref bool __0)
        {
            _foreignCalls++;
            if (_foreignBlocking) __0 = true;
        }

        private static void ForeignNativePostfix(ref bool __result)
        {
            if (_foreignBlocking) __result = true;
        }

        private static IEnumerable<CodeInstruction> FailingTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            Action during = _duringTranspiler;
            _duringTranspiler = null;
            during?.Invoke();
            if (_failNextTranspiler)
            {
                _failNextTranspiler = false;
                throw new InvalidOperationException("Synthetic wrapper compilation failure.");
            }

            return instructions;
        }

        private static bool InvalidTarget() => false;

        private static MethodInfo Method(string name) =>
            typeof(ContentInputBoundaryRunner).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new MissingMethodException(nameof(ContentInputBoundaryRunner), name);

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Equal(int expected, int actual, string message) =>
            Assert(expected == actual, message + " Expected: " + expected + "; actual: " + actual + ".");

        private static void Throws<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }

            throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
        }

        private sealed class Consumer
        {
            private readonly Type _facade;

            internal Consumer(string id)
            {
                string path = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Escarval.TerrariaWindowKit.Consumer" + id + ".dll");
                string facadeName = "UiConsumer" + id + ".ContentInputFixture";
                _facade = Assembly.LoadFrom(path).GetType(facadeName, true) ??
                          throw new TypeLoadException("Could not load content input facade: " + facadeName);
            }

            internal string Patch(MethodInfo target, MethodInfo caller = null) =>
                caller == null ? (string)Call("Patch", target) : (string)Call("PatchWithCaller", target, caller);

            internal string Enter(Func<bool> predicate) => (string)Call("Enter", predicate);
            internal void Release(string token) => Call("Release", token);
            internal int Count(MethodInfo target) => (int)Call("PrefixCount", target);

            private object Call(string method, params object[] arguments)
            {
                try
                {
                    MethodInfo target = _facade.GetMethod(method, BindingFlags.Public | BindingFlags.Static) ??
                                        throw new MissingMethodException(_facade.FullName ?? _facade.Name, method);
                    return target.Invoke(null, arguments);
                }
                catch (TargetInvocationException failure)
                {
                    throw failure.InnerException ?? failure;
                }
            }
        }
    }
}