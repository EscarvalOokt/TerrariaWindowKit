using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Escarval.TerrariaWindowKit.TestHost
{
    internal static class ContractSuite
    {
        private static Consumer _a;
        private static Consumer _b;
        private static Consumer _first;
        private static Consumer _second;
        private static long _frame;

        private static readonly List<KeyValuePair<Consumer, string>> _created =
            new List<KeyValuePair<Consumer, string>>();

        internal static IEnumerable<string> ScenarioNames => Scenarios().Select(test => test.Key);

        internal static string Run(bool reverse, string scenario)
        {
            Exception failure = null;
            var initialized = false;
            try
            {
                Initialize(reverse);
                initialized = true;
                Scenarios().Single(test => test.Key == scenario).Value();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                if (initialized)
                {
                    try
                    {
                        Cleanup();
                    }
                    catch (Exception cleanupFailure)
                    {
                        failure = failure == null
                            ? cleanupFailure
                            : new AggregateException("Scenario and cleanup both failed.", failure, cleanupFailure);
                    }
                }
            }

            // Consumer exceptions can refer to types from source-compiled assemblies.
            // Return only BCL text across the AppDomain boundary to the NUnit host.
            return failure?.ToString();
        }

        private static void Initialize(bool reverse)
        {
            string output = AppDomain.CurrentDomain.BaseDirectory;
            if (reverse)
            {
                _b = new Consumer(
                    Path.Combine(output, "Escarval.TerrariaWindowKit.ConsumerB.dll"),
                    "UiConsumerB.Fixture");
                _b.Call("Snapshot");
                _a = new Consumer(
                    Path.Combine(output, "Escarval.TerrariaWindowKit.ConsumerA.dll"),
                    "UiConsumerA.Fixture");
            }
            else
            {
                _a = new Consumer(
                    Path.Combine(output, "Escarval.TerrariaWindowKit.ConsumerA.dll"),
                    "UiConsumerA.Fixture");
                _a.Call("Snapshot");
                _b = new Consumer(
                    Path.Combine(output, "Escarval.TerrariaWindowKit.ConsumerB.dll"),
                    "UiConsumerB.Fixture");
            }

            _first = reverse ? _b : _a;
            _second = reverse ? _a : _b;
        }

        private static KeyValuePair<string, Action>[] Scenarios() =>
        [
            Test("distinct source assemblies share a BCL coordinator", SharedAbi),
            Test("coordinator rejects incompatible provider metadata without damage", CoordinatorCompatibility),
            Test("runtime is installed once and released by its last lease", RuntimeLeases),
            Test("runtime signatures reject incompatible consumers", RuntimeMismatch),
            Test("failed runtime installation leaves no shared lease", RuntimeFailure),
            Test("null and reentrant installers leave no runtime behind", RuntimeInstallEdgeCases),
            Test("independent runtime families retain separate lifetimes", IndependentRuntimeFamilies),
            Test("failed last-lease cleanup blocks reacquisition until retry succeeds", RuntimeCleanupFailure),
            Test("runtime operations require the provider's owning thread", RuntimeThreadAffinity),
            Test("duplicate window identities are rejected without damage", DuplicateIdentity),
            Test("disposing consumer A leaves consumer B operational", IndependentDisposal),
            Test("Escape performs one action per press across both assemblies", EscapeArbitration),
            Test("text, popup and window use separate Escape presses", EscapePriorities),
            Test("pinned roots pass Escape to an unpinned root regardless of activation", PinnedEscape),
            Test("Escape bypasses pinned Core panels but honors foreign panel priority", EscapePanelPriority),
            Test("game menu closes pinned windows and prevents reopening", MenuClosure),
            Test("world unload closes windows from both assemblies", WorldUnload),
            Test(
                "close and dispose clean focus and popups after visibility queries turn false",
                FocusVisibilityCleanup),
            Test("drag, resize and pin are opt-in and obey geometry limits", Geometry),
            Test("content update suspends only owned bounds after checking priority", ContentUpdateScope),
            Test("content update respects point-aware priority and skip conditions", ContentUpdatePriority),
            Test("content update preserves callback and bounds cleanup failures", ContentUpdateFailures),
            Test("content update handles close, reopen, geometry and disposal", ContentUpdateLifecycle),
            Test("nested content updates restore owned bounds only at the outer boundary", ContentUpdateNested),
            Test("scroll decisions precede any bounds removal", ScrollSnapshot),
            Test("Core priority selects wheel owner when local activation order is stale", StaleActivationWheel),
            Test("scroll scopes restore bounds and tolerate disposal", ScrollScope),
            Test("Harmony nested calls and exceptions restore bounds once", HarmonyBoundary),
            Test("a second source copy cannot install the same Harmony owner", HarmonyOwnerConflict),
            Test("toolkit cleanup preserves patches from another Harmony owner", ForeignHarmonyOwnership)
        ];

        private static KeyValuePair<string, Action> Test(string name, Action action) =>
            new KeyValuePair<string, Action>(name, action);

        private static void SharedAbi()
        {
            Assert(_a.Assembly != _b.Assembly, "Consumers must be physically distinct assemblies.");
            Assert(
                (string)_a.Call("ToolkitAssembly") != (string)_b.Call("ToolkitAssembly"),
                "The toolkit must be source-compiled into each consumer.");
            IDictionary<string, object> a = _a.Map("Snapshot");
            IDictionary<string, object> b = _b.Map("Snapshot");
            Equal(a["providerId"], b["providerId"], "Both source copies must connect to one provider.");
            Equal(0, a["windowCount"], "Initial registration count.");
            Equal(a["protocolMajor"], b["protocolMajor"], "Protocol ABI.");
            Equal(2, a["protocolMajor"], "Escape routing requires the updated coordinator protocol.");

            IDictionary<string, object> aIdentity = _a.Map("ToolkitIdentity");
            IDictionary<string, object> bIdentity = _b.Map("ToolkitIdentity");
            var expectedIdentity = new Dictionary<string, object>
            {
                ["coordinatorSlot"] = "Escarval.TerrariaWindowKit.Coordinator",
                ["harmonyOwner"] = "escarval.terrariawindowkit.core.scroll",
                ["escapeBlockPrefix"] = "escarval.terrariawindowkit.escape."
            };
            foreach (KeyValuePair<string, object> expected in expectedIdentity)
            {
                Equal(expected.Value, aIdentity[expected.Key], "Consumer A uses the renamed " + expected.Key + ".");
                Equal(
                    aIdentity[expected.Key],
                    bIdentity[expected.Key],
                    "Both source copies must use the same " + expected.Key + ".");
            }
        }

        private static void CoordinatorCompatibility()
        {
            IDictionary<string, object> api = _first.Map("CoordinatorApi");
            var originalProvider = (string)_first.Call("ReconnectProviderId");
            var originalWindows = (int)_first.Map("Snapshot")["windowCount"];
            // Both fixture types must initialize while the shared provider is still compatible.
            Equal(
                originalProvider,
                _second.Call("ReconnectProviderId"),
                "Both consumers must connect before provider metadata is mutated.");

            object protocol = api["protocolMajor"];
            try
            {
                api["protocolMajor"] = 1;
                Throws<NotSupportedException>(() => _second.Call("ReconnectProviderId"));
            }
            finally
            {
                api["protocolMajor"] = protocol;
            }

            object operation = api["register"];
            try
            {
                api.Remove("register");
                Throws<NotSupportedException>(() => _second.Call("ReconnectProviderId"));
            }
            finally
            {
                api["register"] = operation;
            }

            object escapePassThrough = api["escapePassThroughIds"];
            try
            {
                api.Remove("escapePassThroughIds");
                Throws<NotSupportedException>(() => _second.Call("ReconnectProviderId"));
            }
            finally
            {
                api["escapePassThroughIds"] = escapePassThrough;
            }

            object wheelPriority = api["wheelPriorityAtVersion"];
            try
            {
                api["wheelPriorityAtVersion"] = 0;
                Throws<NotSupportedException>(() => _second.Call("ReconnectProviderId"));
            }
            finally
            {
                api["wheelPriorityAtVersion"] = wheelPriority;
            }

            Equal(
                originalProvider,
                _second.Call("ReconnectProviderId"),
                "Both consumers must reconnect to the original provider after restoration.");
            Equal(
                originalWindows,
                _first.Map("Snapshot")["windowCount"],
                "Failed connections must leave the existing window registry unchanged.");
        }

        private static void RuntimeLeases()
        {
            IDictionary<string, object> state = State();
            var installs = (int)state["installs"];
            var uninstalls = (int)state["uninstalls"];
            var a = (IDisposable)_first.Call("Runtime", "tests.lease", "v1");
            var b = (IDisposable)_second.Call("Runtime", "tests.lease", "v1");
            try
            {
                Equal(
                    installs + 1,
                    state["installs"],
                    "Only the first consumer in this initialization order installs.");
                Equal(2, _a.Map("Snapshot")["runtimeLeaseCount"], "Both copies own a lease.");
                a.Dispose();
                a.Dispose();
                Equal(
                    uninstalls,
                    state["uninstalls"],
                    "Disposing the first consumer preserves the second consumer's runtime.");
                Equal(1, _b.Map("Snapshot")["runtimeLeaseCount"], "Lease disposal is idempotent.");
                b.Dispose();
                Equal(uninstalls + 1, state["uninstalls"], "Last consumer uninstalls.");
                Equal(0, _a.Map("Snapshot")["runtimeFamilyCount"], "Runtime family cleanup.");
            }
            finally
            {
                a.Dispose();
                b.Dispose();
            }
        }

        private static void RuntimeMismatch()
        {
            const string family = "tests.signature";
            const string contract = "tests.cross-consumer.runtime/1";
            string compatible = CoreCompatibilityChecks.CreateRuntimeSignature(contract, typeof(string), typeof(int));
            string sameCompatible = CoreCompatibilityChecks.CreateRuntimeSignature(
                contract,
                typeof(string),
                typeof(int));
            string firstSpecific = CoreCompatibilityChecks.CreateRuntimeSignature(
                contract,
                _first.FacadeType,
                typeof(string));
            string secondSpecific = CoreCompatibilityChecks.CreateRuntimeSignature(
                contract,
                _second.FacadeType,
                typeof(string));
            Equal(compatible, sameCompatible, "Shared CLR types produce the same runtime signature.");
            Assert(firstSpecific != secondSpecific, "Separate source-compiled consumer types must differ.");

            IDictionary<string, object> state = State();
            var installs = (int)state["installs"];
            var first = (IDisposable)_first.Call("Runtime", family, compatible);
            IDisposable second = null;
            try
            {
                second = (IDisposable)_second.Call("Runtime", family, sameCompatible);
                Equal(installs + 1, state["installs"], "Matching calculated signatures install only once.");
                Equal(2, _second.Map("Snapshot")["runtimeLeaseCount"], "Both consumers hold a lease.");
                first.Dispose();
                Equal(1, _second.Map("Snapshot")["runtimeLeaseCount"], "The second lease remains active.");
                second.Dispose();
                Equal(0, _first.Map("Snapshot")["runtimeFamilyCount"], "The shared family is released.");
            }
            finally
            {
                first.Dispose();
                second?.Dispose();
            }

            using ((IDisposable)_first.Call("Runtime", family, firstSpecific))
            {
                var beforeRejection = (int)state["installs"];
                Throws<NotSupportedException>(() => _second.Call("Runtime", family, secondSpecific));
                Equal(beforeRejection, state["installs"], "A rejected installer must never run.");
                Equal(1, _first.Map("Snapshot")["runtimeLeaseCount"], "Mismatch must not acquire a lease.");
            }

            using ((IDisposable)_second.Call("Runtime", family, secondSpecific))
                Equal(
                    1,
                    _first.Map("Snapshot")["runtimeLeaseCount"],
                    "The other signature may install after the first runtime is released.");
            Equal(0, _first.Map("Snapshot")["runtimeFamilyCount"], "All signatures leave the family clean.");
        }

        private static void DuplicateIdentity()
        {
            string first = Create(_first, "shared-mod", "duplicate");
            _first.Call("Open", first);
            var rejected = false;
            try
            {
                string second = Create(_second, "shared-mod", "duplicate");
                _second.Call("Open", second);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }

            Assert(rejected, "The same process window id must not be registered twice.");
            Assert(Open(_first, first), "The original window must remain open.");
            Equal(1, Bounds().Count, "A rejected registration must not overwrite existing bounds.");
        }

        private static void RuntimeFailure()
        {
            Throws<InvalidOperationException>(() => _first.Call("FailedRuntime", "tests.failed-install", "v1"));
            Equal(0, _second.Map("Snapshot")["runtimeLeaseCount"], "A failed installer does not create a lease.");
            Equal(0, _second.Map("Snapshot")["runtimeFamilyCount"], "A failed installer does not retain a family.");
            using ((IDisposable)_second.Call("Runtime", "tests.failed-install", "v1"))
                Equal(1, _first.Map("Snapshot")["runtimeLeaseCount"], "A second consumer can retry the installation.");
        }

        private static void RuntimeInstallEdgeCases()
        {
            foreach (string installer in new[] { "NullRuntime", "ReentrantRuntime" })
            {
                string family = "tests." + installer;
                Throws<InvalidOperationException>(() => _first.Call(installer, family, "v1"));
                Equal(
                    0,
                    _second.Map("Snapshot")["runtimeLeaseCount"],
                    "A failed " + installer + " must not create a lease.");
                Equal(
                    0,
                    _second.Map("Snapshot")["runtimeFamilyCount"],
                    "A failed " + installer + " must not retain a runtime family.");
                using ((IDisposable)_second.Call("Runtime", family, "v1"))
                    Equal(
                        1,
                        _first.Map("Snapshot")["runtimeLeaseCount"],
                        "A different consumer may recover after " + installer + ".");
                Equal(0, _first.Map("Snapshot")["runtimeFamilyCount"], "Recovered runtime families are released.");
            }
        }

        private static void IndependentRuntimeFamilies()
        {
            IDictionary<string, object> state = State();
            var installs = (int)state["installs"];
            var uninstalls = (int)state["uninstalls"];
            var first = (IDisposable)_first.Call("Runtime", "tests.family-first", "v1");
            IDisposable second = null;
            try
            {
                second = (IDisposable)_second.Call("Runtime", "tests.family-second", "v2");
                Equal(installs + 2, state["installs"], "Both independent runtime families install.");
                Equal(2, _first.Map("Snapshot")["runtimeFamilyCount"], "Both families coexist.");
                Equal(2, _second.Map("Snapshot")["runtimeLeaseCount"], "Each family has a lease.");
                first.Dispose();
                Equal(uninstalls + 1, state["uninstalls"], "Only the first family is uninstalled.");
                Equal(1, _second.Map("Snapshot")["runtimeFamilyCount"], "The second family remains.");
                Equal(1, _first.Map("Snapshot")["runtimeLeaseCount"], "The second lease remains.");
                second.Dispose();
                Equal(uninstalls + 2, state["uninstalls"], "The second family uninstalls independently.");
                Equal(0, _first.Map("Snapshot")["runtimeFamilyCount"], "No runtime family remains.");
            }
            finally
            {
                first.Dispose();
                second?.Dispose();
            }
        }

        private static void RuntimeCleanupFailure()
        {
            var attempts = (int)State()["cleanupAttempts"];
            var lease = (IDisposable)_first.Call("RuntimeWithFailingCleanup", "tests.failed-cleanup", "v1");
            try
            {
                Throws<InvalidOperationException>(lease.Dispose);
                Equal(1, _second.Map("Snapshot")["runtimeFamilyCount"], "Failed cleanup retains its runtime family.");
                Equal(0, _second.Map("Snapshot")["runtimeLeaseCount"], "The final owner has released its lease.");
                Throws<InvalidOperationException>(() => _second.Call("Runtime", "tests.failed-cleanup", "v1"));
                Throws<NotSupportedException>(() => _second.Call("Runtime", "tests.failed-cleanup", "v2"));
                Equal(
                    1,
                    _second.Map("Snapshot")["runtimeFamilyCount"],
                    "Neither a matching nor a mismatched signature can replace pending cleanup.");
                Equal(0, _second.Map("Snapshot")["runtimeLeaseCount"], "A rejected acquisition must leave no lease.");
                lease.Dispose();
                Equal(attempts + 2, State()["cleanupAttempts"], "Disposal retries the original cleanup scope.");
                Equal(0, _first.Map("Snapshot")["runtimeFamilyCount"], "Successful retry removes the runtime family.");
                lease.Dispose();
                Equal(attempts + 2, State()["cleanupAttempts"], "Successful disposal becomes idempotent.");
                using ((IDisposable)_second.Call("Runtime", "tests.failed-cleanup", "v2"))
                    Equal(
                        1,
                        _first.Map("Snapshot")["runtimeLeaseCount"],
                        "The other signature can install after cleanup succeeds.");
            }
            finally
            {
                lease.Dispose();
            }
        }

        private static void RuntimeThreadAffinity()
        {
            // Both source-only fixtures must initialize on the provider's UI thread.
            _second.Call("Snapshot");
            var installs = (int)State()["installs"];
            Exception threadFailure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    using ((IDisposable)_second.Call("Runtime", "tests.thread-affinity", "v1"))
                    {
                    }
                }
                catch (Exception error)
                {
                    threadFailure = error;
                }
            });
            worker.Start();
            worker.Join();

            Assert(
                threadFailure is InvalidOperationException,
                "A different thread must be rejected by the shared provider: " + threadFailure);
            Equal(installs, State()["installs"], "A rejected thread must not run the installer.");
            Equal(0, _first.Map("Snapshot")["runtimeLeaseCount"], "A rejected thread must not acquire a lease.");
            Equal(0, _first.Map("Snapshot")["runtimeFamilyCount"], "A rejected thread must not retain a family.");
            using ((IDisposable)_second.Call("Runtime", "tests.thread-affinity", "v1"))
                Equal(1, _first.Map("Snapshot")["runtimeLeaseCount"], "A normal UI-thread acquisition still succeeds.");
        }

        private static void IndependentDisposal()
        {
            string first = Create(_first, "consumer-first", "dispose");
            string second = Create(_second, "consumer-second", "dispose");
            _first.Call("Open", first);
            _second.Call("Open", second);
            _first.Call("Dispose", first);
            Assert(Open(_second, second), "The second consumer remains open after the first is disposed.");
            Equal(1, Bounds().Count, "Only the second consumer's bounds remain.");
            Equal(1, _second.Map("Snapshot")["windowCount"], "Only the second registration remains.");
            Press();
            Assert(!Open(_second, second), "The second consumer still responds to Escape.");
        }

        private static void EscapeArbitration()
        {
            string a = Create(_a, "consumer-a", "escape");
            string b = Create(_b, "consumer-b", "escape");
            _a.Call("Open", a);
            _b.Call("Open", b);
            _b.Call("Activate", b);
            long frame = ++_frame;
            _a.Call("Frame", frame, true, true, false);
            _b.Call("Frame", frame, true, true, false);
            Assert(Open(_a, a) && !Open(_b, b), "Exactly the active window closes.");
            Equal(1, State()["escapeBlocks"], "Escape remains blocked after its recipient closes.");
            _a.Call("Frame", ++_frame, true, false, false);
            Assert(Open(_a, a), "Holding the key must not close the next window.");
            Release();
            Equal(0, State()["escapeBlocks"], "Physical release clears the block.");
            Press();
            Assert(!Open(_a, a), "The next press can close the next active window.");
        }

        private static void EscapePriorities()
        {
            string a = Create(_a, "consumer-a", "priority");
            _a.Call("Open", a);
            _a.Call("ConfigureWindow", a, true, true, 0, true);
            Press();
            IDictionary<string, object> first = _a.Map("Window", a);
            Assert(
                !(bool)first["textFocus"] && (bool)first["popup"] && (bool)first["open"],
                "The first press releases text focus only.");
            Release();
            Press();
            IDictionary<string, object> second = _a.Map("Window", a);
            Assert(!(bool)second["popup"] && (bool)second["open"], "The second press closes the popup only.");
            Release();
            Press();
            Assert(!Open(_a, a), "The third press closes the window.");
        }

        private static void PinnedEscape()
        {
            string a = Create(_a, "consumer-a", "under-pinned", pin: true);
            string b = Create(_b, "consumer-b", "pinned", true, true, true);
            _a.Call("Open", a);
            _b.Call("Open", b);
            _b.Call("Pin", b, true);
            _b.Call("Activate", b);
            Press();
            Assert(!Open(_a, a) && Open(_b, b), "The unpinned root closes even when the pinned root is active.");
            Equal(1, State()["escapeBlocks"], "The first handled press retains its keyboard lease.");
            _a.Call("Frame", ++_frame, true, false, false);
            Assert(Open(_b, b), "Holding Escape must not close the pinned root.");
            Release();

            _a.Call("Open", a);
            _a.Call("Activate", a);
            Press();
            Assert(!Open(_a, a) && Open(_b, b), "An active unpinned root also closes normally.");
            Release();
            Press();
            Assert(Open(_b, b), "A lone pinned root leaves Escape to Terraria.");
            Equal(0, State()["escapeBlocks"], "An unhandled Escape has no toolkit lease.");
            Release();

            _a.Call("Open", a);
            _a.Call("Pin", a, true);
            _b.Call("Activate", b);
            Press();
            Assert(Open(_a, a) && Open(_b, b), "Two pinned roots must remain open.");
            Equal(0, State()["escapeBlocks"], "Pinned roots without internal actions leave Escape unhandled.");
            Release();

            _b.Call("ConfigureWindow", b, false, true, 0, true);
            Press();
            Assert(
                Open(_a, a) && Open(_b, b) && !(bool)_b.Map("Window", b)["popup"],
                "A pinned active root still closes its own popup first.");
            Release();
            _b.Call("ConfigureWindow", b, true, false, 0, true);
            Press();
            Assert(
                Open(_b, b) && !(bool)_b.Map("Window", b)["textFocus"],
                "A pinned active root still releases its own text focus first.");
            _b.Call("Close", b);
            _b.Call("Open", b);
            Assert(!(bool)_b.Map("Window", b)["pinned"], "Pin resets when a window closes.");
        }

        private static void EscapePanelPriority()
        {
            string a = Create(_a, "consumer-a", "behind-pinned");
            string b = Create(_b, "consumer-b", "front-pinned", true, true, true);
            _a.Call("Open", a);
            _b.Call("Open", b);
            _b.Call("Pin", b, true);
            _b.Call("Activate", b);
            var lowerId = (string)_a.Map("Window", a)["id"];
            var pinnedId = (string)_b.Map("Window", b)["id"];
            const string foreignId = "foreign:panel";
            string[] order = [lowerId, foreignId, pinnedId];
            string[] registered = [lowerId, foreignId, pinnedId];
            _a.Call("KeyboardPanelPriority", a, order, registered, new[] { pinnedId });
            Press();
            Assert(!Open(_a, a) && Open(_b, b), "A hovered pinned toolkit panel does not block a lower root.");
            Release();

            _a.Call("Open", a);
            _b.Call("Activate", b);
            _a.Call("KeyboardPanelPriority", a, order, registered, new[] { pinnedId, foreignId });
            Press();
            Assert(Open(_a, a) && Open(_b, b), "A hovered foreign Core panel must block lower Escape.");
            Equal(0, State()["escapeBlocks"], "Foreign priority must not create a toolkit Escape lease.");
            Release();

            _a.Call("KeyboardPanelPriority", a, order, new[] { lowerId, pinnedId }, new[] { pinnedId, foreignId });
            Press();
            Assert(!Open(_a, a), "Unregistered Core panels must not block Escape.");
            Release();

            _a.Call("Open", a);
            _a.Call("ConfigureWindow", a, false, false, 0, false);
            _a.Call("KeyboardPanelPriority", a, order, registered, new[] { pinnedId });
            Press();
            Assert(Open(_a, a) && Open(_b, b), "A denied keyboard recipient must not bypass its blocker.");
            Release();

            _a.Call("ConfigureWindow", a, false, false, 0, true);
            _b.Call("Pin", b, false);
            _b.Call("ConfigureWindow", b, false, false, 0, false);
            _b.Call("Activate", b);
            Press();
            Assert(Open(_a, a) && Open(_b, b), "A blocked top unpinned root must not transfer Escape lower.");
        }

        private static void MenuClosure()
        {
            string a = Create(_a, "consumer-a", "menu", true, true, true);
            string b = Create(_b, "consumer-b", "menu", true, true, true);
            _a.Call("Open", a);
            _b.Call("Open", b);
            _a.Call("Pin", a, true);
            _b.Call("Pin", b, true);
            _a.Call("Frame", ++_frame, false, false, true);
            Assert(!Open(_a, a) && !Open(_b, b), "The menu closes both pinned windows.");
            Assert(!(bool)_b.Call("Open", b), "Opening is rejected while the menu is open.");
            Equal(0, Bounds().Count, "Menu closure removes input bounds.");
            _a.Call("Frame", ++_frame, false, false, false);
            Assert(!Open(_a, a) && !Open(_b, b), "Menu dismissal does not reopen windows.");
        }

        private static void Geometry()
        {
            _a.Call("Viewport", 1000, 800);
            string fixedWindow = Create(_a, "consumer-a", "fixed");
            _a.Call("Open", fixedWindow);
            _a.Call("BeginResize", fixedWindow, 300, 220);
            _a.Call("Pointer", fixedWindow, 430, 310, true, false);
            Equal(200, _a.Map("Window", fixedWindow)["width"], "Resize is disabled by default.");
            _a.Call("BeginDrag", fixedWindow, 100, 100);
            _a.Call("Pointer", fixedWindow, 150, 160, true, false);
            Equal(100, _a.Map("Window", fixedWindow)["x"], "Drag is disabled by default.");
            try
            {
                _a.Call("Pin", fixedWindow, true);
            }
            catch (InvalidOperationException)
            {
            }

            Assert(!(bool)_a.Map("Window", fixedWindow)["pinned"], "Pin is disabled by default.");

            string resizable = Create(_b, "consumer-b", "resizable", true, true, true);
            _b.Call("Open", resizable);
            _b.Call("Pin", resizable, true);
            _b.Call("BeginResize", resizable, 300, 220);
            _b.Call("Pointer", resizable, 430, 310, true, false);
            Equal(330, _b.Map("Window", resizable)["width"], "Pinned windows resize.");
            Equal(210, _b.Map("Window", resizable)["height"], "Both dimensions change.");
            _b.Call("Pointer", resizable, 430, 310, false, false);
            _b.Call("Pointer", resizable, 600, 500, true, false);
            Equal(330, _b.Map("Window", resizable)["width"], "Release cancels resize.");
            _b.Call("BeginDrag", resizable, 100, 100);
            _b.Call("Pointer", resizable, 900, 900, true, false);
            Equal(670, _b.Map("Window", resizable)["x"], "Dragging clamps to viewport width.");
            Equal(590, _b.Map("Window", resizable)["y"], "Dragging clamps to viewport height.");
            _b.Call("Pointer", resizable, 900, 900, true, true);
            _b.Call("Pointer", resizable, 100, 100, true, false);
            Equal(670, _b.Map("Window", resizable)["x"], "Input blocking cancels drag.");
            _b.Call("BeginResize", resizable, 1000, 800);
            _b.Call("BeginDrag", resizable, 670, 590);
            _b.Call("Pointer", resizable, 660, 580, true, false);
            Equal(660, _b.Map("Window", resizable)["x"], "Beginning drag replaces a pending resize.");
            Equal(330, _b.Map("Window", resizable)["width"], "Replacing resize does not change the width.");
            _b.Call("Viewport", 80, 40);
            _b.Call("Refresh", resizable);
            Equal(80, _b.Map("Window", resizable)["width"], "Small viewport overrides minimum width.");
            Equal(40, _b.Map("Window", resizable)["height"], "Small viewport overrides minimum height.");
            _b.Call("Viewport", 1000, 800);
        }

        private static void WorldUnload()
        {
            string a = Create(_a, "consumer-a", "world-unload", true, true, true);
            string b = Create(_b, "consumer-b", "world-unload", true, true, true);
            _a.Call("Open", a);
            _b.Call("Open", b);
            _a.Call("Pin", a, true);
            _b.Call("Pin", b, true);
            _b.Call("CloseAll", 3);
            Assert(!Open(_a, a) && !Open(_b, b), "World unload closes pinned windows from both copies.");
            Equal(0, Bounds().Count, "World unload removes bounds.");
        }

        private static void FocusVisibilityCleanup()
        {
            string a = Create(_a, "consumer-a", "visibility-close");
            string b = Create(_b, "consumer-b", "visibility-dispose");
            _a.Call("Open", a);
            _b.Call("Open", b);
            _a.Call("VisibilityQueries", a, true);
            _b.Call("VisibilityQueries", b, true);
            _a.Call("ConfigureWindow", a, true, true, 0, true);
            _b.Call("ConfigureWindow", b, true, true, 0, true);
            _a.Call("Close", a);
            IDictionary<string, object> closed = _a.Map("Window", a);
            Assert(
                !(bool)closed["textFocus"] && !(bool)closed["popup"],
                "Close must release focus and popup even when IsOpen-dependent queries now return false.");
            Equal(1, closed["textReleases"], "Close releases text exactly once.");
            Equal(1, closed["popupCloses"], "Close releases popup exactly once.");
            _b.Call("Dispose", b);
            IDictionary<string, object> disposed = _b.Map("Window", b);
            Assert(
                !(bool)disposed["textFocus"] && !(bool)disposed["popup"],
                "Dispose must release focus and popup after changing visibility.");
            Equal(1, disposed["textReleases"], "Dispose releases text exactly once.");
            Equal(1, disposed["popupCloses"], "Dispose releases popup exactly once.");
        }

        private static void ContentUpdateScope()
        {
            string first = Create(_first, "content-a", "root");
            string second = Create(_second, "content-b", "root");
            _first.Call("Open", first);
            _second.Call("Open", second);
            var firstId = (string)_first.Map("Window", first)["id"];
            var secondId = (string)_second.Map("Window", second)["id"];
            Bounds()["foreign-panel"] = [800, 600, 100, 100];
            _first.Call("ResetEvents");

            Assert(
                (bool)_first.Call("UpdateContent", first, 110, 110, "normal"),
                "An eligible root runs its content update.");
            IDictionary<string, object> window = _first.Map("Window", first);
            Equal(1, window["contentUpdates"], "The content callback runs once.");
            Equal(2, window["boundsAtContent"], "Other consumer and foreign bounds stay registered.");
            Equal(false, window["ownBoundsAtContent"], "The content does not see its own blocking bounds.");
            Assert(Bounds().ContainsKey(firstId), "The root restores its bounds after content update.");
            Assert(Bounds().ContainsKey(secondId), "The other consumer's bounds are untouched.");
            Assert(Bounds().ContainsKey("foreign-panel"), "Foreign bounds are never removed.");

            var events = (string[])_first.Call("Events");
            int priorityCheck = Array.FindIndex(events, e => e == "blocked:" + firstId);
            int boundsRemoval = Array.FindIndex(events, e => e == "bounds-:" + firstId);
            int content = Array.FindIndex(events, e => e == "content:" + firstId);
            int restoration = Array.FindIndex(events, e => e == "bounds+:" + firstId);
            Assert(
                priorityCheck >= 0 && priorityCheck < boundsRemoval && boundsRemoval < content && content < restoration,
                "Priority precedes scope acquisition, callback and restoration.");
            Equal(
                3,
                ((Dictionary<string, int>)State()["blockedChecks"])[firstId],
                "Priority sees the original registrations.");

            Assert(
                (bool)_second.Call("UpdateContent", second, 900, 700, "normal"),
                "The second source-only assembly can independently update its content.");
            Equal(
                false,
                _second.Map("Window", second)["ownBoundsAtContent"],
                "The second root also suspends only its own bounds.");
            Equal(3, Bounds().Count, "Both consumers and the foreign registration survive both updates.");
        }

        private static void ContentUpdatePriority()
        {
            string fallback = Create(_first, "content", "fallback");
            _first.Call("Open", fallback);
            var fallbackId = (string)_first.Map("Window", fallback)["id"];
            Assert(
                (bool)_first.Call("UpdateContent", fallback, 110, 110, "normal"),
                "The default backend allows an unblocked update.");

            _first.Call("BlockInput", fallback, true);
            _first.Call("ResetEvents");
            Assert(
                !(bool)_first.Call("UpdateContent", fallback, 110, 110, "normal"),
                "The default backend blocks via IsInputBlocked.");
            Equal(1, _first.Map("Window", fallback)["contentUpdates"], "Blocked input never invokes content.");
            Assert(
                ((string[])_first.Call("Events")).All(e => e != "bounds-:" + fallbackId),
                "A blocked update must not alter registrations.");
            _first.Call("BlockInput", fallback, false);

            string pointAware = CreatePointAware(_second, "content", "point-aware");
            _second.Call("Open", pointAware);
            var id = (string)_second.Map("Window", pointAware)["id"];
            _second.Call("BlockInput", pointAware, true);
            _second.Call("ResetEvents");
            Assert(
                (bool)_second.Call("UpdateContent", pointAware, 321, 654, "normal"),
                "Point-aware priority overrides the draw-phase fallback, including outside the root.");
            var events = (string[])_second.Call("Events");
            Assert(events.Any(e => e == "blocked-at:" + id), "Point-aware policy was queried.");
            Assert(events.All(e => e != "blocked:" + id), "Draw-phase policy was not consulted.");
            int[] checkedPoint = ((Dictionary<string, int[]>)State()["pointChecks"])[id];
            Equal(321, checkedPoint[0], "Update X coordinate was passed through unchanged.");
            Equal(654, checkedPoint[1], "Update Y coordinate was passed through unchanged.");
            _second.Call("PointInputBlocked", pointAware, true);
            Assert(
                !(bool)_second.Call("UpdateContent", pointAware, 110, 110, "normal"),
                "Higher-priority point-aware blocking vetoes content.");
            Equal(
                1,
                _second.Map("Window", pointAware)["contentUpdates"],
                "Point-aware rejection does not invoke content.");
            _second.Call("PointInputBlocked", pointAware, false);

            State()["menu"] = true;
            try
            {
                Assert(
                    !(bool)_first.Call("UpdateContent", fallback, 110, 110, "normal"),
                    "The options menu prevents an open root's content update.");
            }
            finally
            {
                State()["menu"] = false;
            }

            _first.Call("Close", fallback);
            Assert(
                !(bool)_first.Call("UpdateContent", fallback, 110, 110, "normal"),
                "Closed roots skip content updates.");
            Throws<ArgumentNullException>(() => _first.Call("UpdateContentWithoutCallback", fallback, 110, 110));
            _first.Call("Dispose", fallback);
            Throws<ObjectDisposedException>(() => _first.Call("UpdateContent", fallback, 110, 110, "normal"));
            Assert(Bounds().ContainsKey(id), "The remaining point-aware consumer is still registered.");
        }

        private static void ContentUpdateFailures()
        {
            string token = Create(_first, "content", "failures");
            _first.Call("Open", token);
            var id = (string)_first.Map("Window", token)["id"];

            Throws<InvalidOperationException>(() => _first.Call("UpdateContent", token, 110, 110, "throw"));
            Assert(Bounds().ContainsKey(id), "A throwing callback restores the original bounds.");

            _first.Call("FailBoundsOperations", token, 0, 1);
            Throws<InvalidOperationException>(() => _first.Call("UpdateContent", token, 110, 110, "normal"));
            Equal(
                1,
                _first.Map("Window", token)["contentUpdates"],
                "A failed bounds removal never invokes the callback.");
            Assert(Bounds().ContainsKey(id), "Removal rollback restores bounds when registration succeeds.");

            _first.Call("FailBoundsOperations", token, 1, 1);
            AggregateException removalAndRollback =
                CaptureAggregate(() => _first.Call("UpdateContent", token, 110, 110, "normal"));
            Equal(2, removalAndRollback.InnerExceptions.Count, "Both removal and rollback failures are preserved.");
            Assert(
                removalAndRollback.InnerExceptions[0].Message.Contains("bounds unregistration"),
                "The original removal failure is first.");
            Assert(
                removalAndRollback.InnerExceptions[1].Message.Contains("bounds registration"),
                "The rollback failure is second.");
            Equal(
                1,
                _first.Map("Window", token)["contentUpdates"],
                "Neither failing bounds acquisition invokes the callback.");
            _first.Call("Close", token);
            _first.Call("Open", token);

            _first.Call("FailBoundsOperations", token, 1, 0);
            AggregateException callbackAndRestore =
                CaptureAggregate(() => _first.Call("UpdateContent", token, 110, 110, "throw"));
            Equal(2, callbackAndRestore.InnerExceptions.Count, "Both callback and restoration failures are preserved.");
            Assert(
                callbackAndRestore.InnerExceptions[0].Message.Contains("content update"),
                "The original callback failure is first.");
            Assert(
                callbackAndRestore.InnerExceptions[1].Message.Contains("bounds registration"),
                "The restoration failure is second.");
            _first.Call("Close", token);
            _first.Call("Open", token);
            Assert(
                (bool)_first.Call("UpdateContent", token, 110, 110, "normal"),
                "The host remains usable after explicit close/reopen recovery.");
            Assert(
                Bounds().ContainsKey(id),
                "Normal restoration works after the test's injected failures are consumed.");
        }

        private static void ContentUpdateLifecycle()
        {
            string token = Create(_first, "content", "lifecycle");
            _first.Call("Open", token);
            var id = (string)_first.Map("Window", token)["id"];
            _first.Call("Viewport", 220, 150);
            Assert(
                (bool)_first.Call("UpdateContent", token, 110, 110, "fit"),
                "Content may request a viewport fit while own bounds are suspended.");
            IDictionary<string, object> window = _first.Map("Window", token);
            Equal(20, window["x"], "Restoration uses the newly fitted X position.");
            Equal(30, window["y"], "Restoration uses the newly fitted Y position.");
            int[] bounds = Bounds()[id];
            Equal(20, bounds[0], "The backend registration uses the updated position.");
            Equal(30, bounds[1], "The backend registration uses the updated Y position.");

            Assert(
                (bool)_first.Call("UpdateContent", token, 30, 40, "close"),
                "Closing within content still completes the scoped callback.");
            Assert(!Open(_first, token), "Content closure leaves the root closed.");
            Assert(!Bounds().ContainsKey(id), "Closure cannot resurrect its bounds on scope exit.");
            _first.Call("Open", token);
            Assert(
                (bool)_first.Call("UpdateContent", token, 30, 40, "reopen"),
                "A close/open transition within content is supported.");
            Assert(Open(_first, token), "The reopened root stays open.");
            Assert(Bounds().ContainsKey(id), "The current root bounds return at the outer scope boundary.");

            Assert(
                (bool)_first.Call("UpdateContent", token, 30, 40, "dispose"),
                "Disposal within content still completes the scoped callback.");
            Assert(!Bounds().ContainsKey(id), "Disposal does not resurrect bounds.");
            Equal(0, _second.Map("Snapshot")["windowCount"], "Disposal removes the coordinator registration.");
        }

        private static void ContentUpdateNested()
        {
            string token = Create(_first, "content", "nested");
            _first.Call("Open", token);
            var id = (string)_first.Map("Window", token)["id"];
            Assert(
                (bool)_first.Call("UpdateContent", token, 110, 110, "nested"),
                "Nested callbacks are permitted on the same UI thread.");
            IDictionary<string, object> window = _first.Map("Window", token);
            Equal(1, window["nestedUpdates"], "The inner callback runs once.");
            Equal(false, window["ownBoundsAtNested"], "The inner callback observes suspended own bounds.");
            Equal(0, window["boundsAfterNested"], "The inner scope does not restore bounds early.");
            Assert(Bounds().ContainsKey(id), "The outer scope restores owned bounds exactly once.");

            _first.Call("ResetEvents");
            Throws<ArgumentException>(() => _first.Call("UpdateContent", token, 110, 110, "nested-throw"));
            Assert(Bounds().ContainsKey(id), "An exceptional nested update restores bounds at the outer boundary.");
            var events = (string[])_first.Call("Events");
            Equal(1, events.Count(e => e == "bounds-:" + id), "Nested exceptional entry removes bounds once.");
            Equal(1, events.Count(e => e == "bounds+:" + id), "Nested exceptional exit restores bounds once.");
        }

        private static void StaleActivationWheel()
        {
            string a = Create(_a, "consumer-a", "core-top");
            string b = Create(_b, "consumer-b", "locally-top");
            _a.Call("Open", a);
            _b.Call("Open", b);
            _a.Call("ConfigureWindow", a, false, false, 1, true);
            _b.Call("ConfigureWindow", b, false, false, 1, true);
            // B is last in the toolkit activation order, while Core reports A on top.
            _b.Call("BlockInput", b, true);
            IDictionary<string, object> wheel = _b.Map("Wheel", 110, 110);
            Equal(
                _a.Map("Window", a)["id"],
                wheel["ownerId"],
                "Blocked B must not hide the eligible Core top window A.");
            Equal(1, wheel["disposition"], "Core top window A receives the wheel.");
            using ((IDisposable)_a.Call("SuspendScroll", 110, 110))
            {
                Assert(
                    Bounds().ContainsKey((string)_a.Map("Window", a)["id"]),
                    "A retains blocking bounds as wheel owner.");
                Assert(
                    !Bounds().ContainsKey((string)_b.Map("Window", b)["id"]),
                    "B's stale blocking bounds are suspended.");
            }
        }

        private static void ScrollSnapshot()
        {
            string a = Create(_a, "consumer-a", "scroll-a");
            string b = Create(_b, "consumer-b", "scroll-b");
            _a.Call("Open", a);
            _b.Call("Open", b);
            _a.Call("ConfigureWindow", a, false, false, 0, true);
            _b.Call("ConfigureWindow", b, false, false, 0, true);
            _a.Call("ResetEvents");
            var blockedChecks = (Dictionary<string, int>)State()["blockedChecks"];
            blockedChecks.Clear();
            Bounds()["foreign-panel"] = [800, 600, 100, 100];
            using ((IDisposable)_b.Call("SuspendScroll", 110, 110))
            {
                Equal(1, Bounds().Count, "Passthrough scopes preserve foreign bounds.");
                Assert(Bounds().ContainsKey("foreign-panel"), "Another mod's bounds must not be changed.");
                Equal(
                    3,
                    blockedChecks[(string)_a.Map("Window", a)["id"]],
                    "A's priority is checked against original bounds.");
                Equal(
                    3,
                    blockedChecks[(string)_b.Map("Window", b)["id"]],
                    "B's priority is checked against original bounds.");
                var events = (string[])_a.Call("Events");
                int firstRemoval = Array.FindIndex(events, e => e.StartsWith("bounds-:", StringComparison.Ordinal));
                int lastDecision = Array.FindLastIndex(
                    events,
                    e => e.StartsWith("decide:", StringComparison.Ordinal) ||
                         e.StartsWith("blocked:", StringComparison.Ordinal));
                Assert(lastDecision >= 0 && firstRemoval > lastDecision, "All decisions precede the first mutation.");
            }

            Equal(3, Bounds().Count, "Scope restoration returns both windows and preserves the foreign panel.");
            Bounds().Remove("foreign-panel");
            _a.Call("ConfigureWindow", a, false, false, 1, true);
            _a.Call("Activate", a);
            IDictionary<string, object> wheel = _b.Map("Wheel", 110, 110);
            Equal(1, wheel["disposition"], "Scrollable content owns the wheel.");
            Equal(_a.Map("Window", a)["id"], wheel["ownerId"], "Wheel owner may come from the other assembly.");
            using ((IDisposable)_b.Call("SuspendScroll", 110, 110))
            {
                Equal(1, Bounds().Count, "The wheel owner's bounds remain registered.");
                Assert(
                    Bounds().ContainsKey((string)_a.Map("Window", a)["id"]),
                    "Delivering owner A retains its bounds.");
            }

            _a.Call("ConfigureWindow", a, false, false, 2, true);
            Equal(2, _b.Map("Wheel", 110, 110)["disposition"], "Popup barrier absorbs the wheel.");
            Equal(0, _b.Map("Wheel", 900, 700)["disposition"], "Outside every window the wheel passes through.");
        }

        private static void ScrollScope()
        {
            string a = Create(_a, "consumer-a", "scope-a");
            string b = Create(_b, "consumer-b", "scope-b");
            _a.Call("Open", a);
            _b.Call("Open", b);
            var outer = (IDisposable)_a.Call("SuspendScroll", 110, 110);
            var inner = (IDisposable)_b.Call("SuspendScroll", 110, 110);
            try
            {
                inner.Dispose();
                Equal(0, Bounds().Count, "An inner scope must not restore the outer suspension.");
                _a.Call("Dispose", a);
                outer.Dispose();
                outer.Dispose();
                Equal(1, Bounds().Count, "Disposing A while suspended must not restore A's bounds.");
                Assert(Bounds().ContainsKey((string)_b.Map("Window", b)["id"]), "B bounds restore normally.");
            }
            finally
            {
                inner.Dispose();
                outer.Dispose();
            }
        }

        private static void HarmonyBoundary()
        {
            string a = Create(_a, "consumer-a", "harmony-a");
            string b = Create(_b, "consumer-b", "harmony-b");
            _a.Call("Open", a);
            _b.Call("Open", b);
            IDictionary<string, object> state = State();
            state["mouseX"] = 110;
            state["mouseY"] = 110;
            state["hasScroll"] = true;
            var installs = (int)state["boundaryInstalls"];
            var uninstalls = (int)state["boundaryUninstalls"];
            var first = (IDisposable)_first.Call("BoundaryLease");
            var second = (IDisposable)_second.Call("BoundaryLease");
            try
            {
                Equal(installs + 1, state["boundaryInstalls"], "Harmony targets are patched only once.");
                Equal(
                    _first.Call("ToolkitAssembly"),
                    _a.Call("BoundaryPatchAssembly"),
                    "Harmony patch methods must come from the first consumer in this initialization order.");
                _a.Call("ResetEvents");
                _a.Call("InvokeTarget", true, false);
                Equal(2, state["targetCalls"], "Both nested patched targets execute.");
                Equal(0, state["bodyBounds"], "Target sees suspended bounds.");
                Equal(2, Bounds().Count, "Normal nested return restores bounds.");
                var events = (string[])_a.Call("Events");
                Equal(
                    2,
                    events.Count(e => e.StartsWith("bounds-:", StringComparison.Ordinal)),
                    "Nested patches remove bounds once.");
                Equal(
                    2,
                    events.Count(e => e.StartsWith("bounds+:", StringComparison.Ordinal)),
                    "Nested patches restore bounds once.");
                Throws<InvalidOperationException>(() => _a.Call("InvokeTarget", true, true));
                Equal(2, Bounds().Count, "Harmony finalizers restore on an inner exception.");
                Throws<InvalidOperationException>(() => _a.Call("InvokeTarget", false, true));
                Equal(2, Bounds().Count, "Harmony finalizers restore on an outer exception.");
                state["hasScroll"] = false;
                _a.Call("InvokeTarget", false, false);
                Equal(2, state["bodyBounds"], "No scroll means no bounds mutation.");
                state["hasScroll"] = true;
                first.Dispose();
                Equal(uninstalls, state["boundaryUninstalls"], "The second lease preserves the installed patch.");
                _a.Call("InvokeTarget", false, false);
                Equal(0, state["bodyBounds"], "Patch remains usable after the first consumer releases its lease.");
                second.Dispose();
                Equal(uninstalls + 1, state["boundaryUninstalls"], "Last lease removes the patch.");
                _a.Call("InvokeTarget", false, false);
                Equal(2, state["bodyBounds"], "Unpatched target retains ordinary bounds.");
            }
            finally
            {
                first.Dispose();
                second.Dispose();
                state["hasScroll"] = true;
            }
        }

        private static void HarmonyOwnerConflict()
        {
            string firstWindow = Create(_first, "consumer-first", "harmony-owner");
            string secondWindow = Create(_second, "consumer-second", "harmony-owner");
            _first.Call("Open", firstWindow);
            _second.Call("Open", secondWindow);
            IDictionary<string, object> state = State();
            var installs = (int)state["boundaryInstalls"];
            var uninstalls = (int)state["boundaryUninstalls"];
            var first = (IDisposable)_first.Call("BoundaryLease");
            IDisposable second = null;
            try
            {
                Equal(installs + 1, state["boundaryInstalls"], "The first consumer installs the boundary.");
                Equal(
                    _first.Call("ToolkitAssembly"),
                    _a.Call("BoundaryPatchAssembly"),
                    "The first consumer owns the active Harmony patch methods.");
                Throws<InvalidOperationException>(() => _second.Call("DirectBoundary"));
                Equal(
                    _first.Call("ToolkitAssembly"),
                    _a.Call("BoundaryPatchAssembly"),
                    "The rejected direct installation must not replace the first owner.");
                _a.Call("InvokeTarget", false, false);
                Equal(0, state["bodyBounds"], "The original boundary keeps suspending bounds.");
                Equal(2, Bounds().Count, "The original boundary still restores bounds.");

                second = (IDisposable)_second.Call("BoundaryLease");
                Equal(
                    installs + 1,
                    state["boundaryInstalls"],
                    "Joining through the coordinator must not install another patch.");
                first.Dispose();
                Equal(uninstalls, state["boundaryUninstalls"], "The second lease keeps the first installation alive.");
                second.Dispose();
                Equal(uninstalls + 1, state["boundaryUninstalls"], "The last lease removes the boundary.");
                Equal(null, _a.Call("BoundaryPatchAssembly"), "No toolkit patch remains after cleanup.");
                _a.Call("InvokeTarget", false, false);
                Equal(2, state["bodyBounds"], "An unpatched target sees the original bounds.");

                using ((IDisposable)_second.Call("BoundaryLease"))
                {
                    Equal(
                        installs + 2,
                        state["boundaryInstalls"],
                        "The second consumer may install after the first runtime is gone.");
                    Equal(
                        _second.Call("ToolkitAssembly"),
                        _a.Call("BoundaryPatchAssembly"),
                        "The second consumer owns the new Harmony patch methods.");
                    _a.Call("InvokeTarget", false, false);
                    Equal(0, state["bodyBounds"], "The replacement boundary is functional.");
                }

                Equal(null, _a.Call("BoundaryPatchAssembly"), "The replacement installation also cleans up.");
            }
            finally
            {
                second?.Dispose();
                first.Dispose();
            }
        }

        private static void ForeignHarmonyOwnership()
        {
            string firstWindow = Create(_first, "consumer-first", "foreign-owner");
            string secondWindow = Create(_second, "consumer-second", "foreign-owner");
            _first.Call("Open", firstWindow);
            _second.Call("Open", secondWindow);
            IDictionary<string, object> state = State();
            var foreign = (IDisposable)_second.Call("ForeignBoundary");
            try
            {
                Assert((bool)_a.Call("HasForeignBoundary"), "The foreign Harmony owner must be registered.");
                var toolkit = (IDisposable)_first.Call("BoundaryLease");
                try
                {
                    Assert(
                        (bool)_a.Call("HasForeignBoundary"),
                        "Installing the toolkit boundary must preserve the foreign owner.");
                    var before = (int)state["foreignPatchCalls"];
                    _a.Call("InvokeTarget", false, false);
                    Equal(before + 1, state["foreignPatchCalls"], "Both patches execute on the target.");
                    Equal(0, state["bodyBounds"], "The toolkit boundary still suspends the bounds.");

                    toolkit.Dispose();
                    Equal(null, _a.Call("BoundaryPatchAssembly"), "Toolkit cleanup removes only its own patch.");
                    Assert((bool)_a.Call("HasForeignBoundary"), "The foreign owner's patch survives cleanup.");
                    before = (int)state["foreignPatchCalls"];
                    _a.Call("InvokeTarget", false, false);
                    Equal(before + 1, state["foreignPatchCalls"], "The remaining foreign patch still executes.");
                    Equal(2, state["bodyBounds"], "Toolkit bounds suppression has been removed.");
                }
                finally
                {
                    toolkit.Dispose();
                }
            }
            finally
            {
                foreign.Dispose();
            }

            Assert(!(bool)_a.Call("HasForeignBoundary"), "The test-owned foreign patch is also removed.");
        }

        private static string Create(
            Consumer consumer,
            string mod,
            string id,
            bool drag = false,
            bool resize = false,
            bool pin = false)
        {
            var token = (string)consumer.Call("Create", mod, id, 100, 100, 200, 120, drag, resize, pin);
            _created.Add(new KeyValuePair<Consumer, string>(consumer, token));
            return token;
        }

        private static string CreatePointAware(Consumer consumer, string mod, string id)
        {
            var token = (string)consumer.Call("CreatePointAware", mod, id, 100, 100, 200, 120, false, false, false);
            _created.Add(new KeyValuePair<Consumer, string>(consumer, token));
            return token;
        }

        private static bool Open(Consumer consumer, string token) => (bool)consumer.Map("Window", token)["open"];
        private static IDictionary<string, object> State() => _a.Map("SharedState");
        private static Dictionary<string, int[]> Bounds() => (Dictionary<string, int[]>)State()["bounds"];
        private static void Press() => _a.Call("Frame", ++_frame, true, true, false);
        private static void Release() => _a.Call("Frame", ++_frame, false, false, false);

        private static void Cleanup()
        {
            foreach (KeyValuePair<Consumer, string> window in _created)
                window.Key.Call("Dispose", window.Value);
            _created.Clear();
            Bounds().Remove("foreign-panel");
            ((Dictionary<string, bool>)State()["blockedIds"]).Clear();
            _a.Call("Viewport", 1000, 800);
            Release();
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Equal(object expected, object actual, string message)
        {
            if (!Equals(expected, actual))
                throw new InvalidOperationException(message + " Expected: " + expected + "; actual: " + actual + ".");
        }

        private static AggregateException CaptureAggregate(Action action)
        {
            try
            {
                action();
            }
            catch (AggregateException exception)
            {
                return exception;
            }

            throw new InvalidOperationException("Expected an AggregateException.");
        }

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

            public Consumer(string path, string facade)
            {
                Assembly = Assembly.LoadFrom(path);
                _facade = Assembly.GetType(facade, true);
            }

            public Assembly Assembly { get; }

            public Type FacadeType => _facade;

            public object Call(string method, params object[] args)
            {
                try
                {
                    MethodInfo target = _facade.GetMethod(method, BindingFlags.Public | BindingFlags.Static) ??
                                        throw new MissingMethodException(_facade.FullName, method);
                    return target.Invoke(null, args);
                }
                catch (TargetInvocationException exception)
                {
                    throw exception.InnerException ?? exception;
                }
            }

            public IDictionary<string, object> Map(string method, params object[] args) =>
                (IDictionary<string, object>)Call(method, args);
        }
    }
}