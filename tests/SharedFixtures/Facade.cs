using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Escarval.TerrariaWindowKit;
using HarmonyLib;

#if CONSUMER_A
namespace UiConsumerA
#else
namespace UiConsumerB
#endif
{
    // This facade is intentionally compiled twice. The host passes only BCL types;
    // no toolkit type from one consumer is ever cast to a type from the other.
    public static class Fixture
    {
        private const string StateKey = "Escarval.TerrariaWindowKit.Tests.Fixture.v1";
        private const string ForeignHarmonyOwner = "tests.foreign-scroll";
        private static readonly Dictionary<string, WindowRecord> _windows = new Dictionary<string, WindowRecord>();
        private static readonly SharedCoordinator _coordinator = SharedCoordinator.Connect();

        public static string ToolkitAssembly() => typeof(WindowHost).Assembly.FullName;

        public static IDictionary<string, object> ToolkitIdentity() =>
            new Dictionary<string, object>
            {
                ["coordinatorSlot"] = SharedCoordinator.Slot,
                ["harmonyOwner"] = HarmonyScrollBoundary.HarmonyOwner,
                ["escapeBlockPrefix"] = KeyboardInputPolicy.EscapeBlockPrefix
            };

        public static IDictionary<string, object> Snapshot() => _coordinator.GetSnapshot();

        public static IDictionary<string, object> CoordinatorApi() =>
            (IDictionary<string, object>)AppDomain.CurrentDomain.GetData(SharedCoordinator.Slot);

        public static string ReconnectProviderId() => SharedCoordinator.Connect().ProviderId;

        public static string Create(
            string modId,
            string windowId,
            int x,
            int y,
            int width,
            int height,
            bool draggable,
            bool resizable,
            bool pinnable)
        {
            return CreateWindow(modId, windowId, x, y, width, height, draggable, resizable, pinnable, false);
        }

        public static string CreatePointAware(
            string modId,
            string windowId,
            int x,
            int y,
            int width,
            int height,
            bool draggable,
            bool resizable,
            bool pinnable)
        {
            return CreateWindow(modId, windowId, x, y, width, height, draggable, resizable, pinnable, true);
        }

        private static string CreateWindow(
            string modId,
            string windowId,
            int x,
            int y,
            int width,
            int height,
            bool draggable,
            bool resizable,
            bool pinnable,
            bool pointAware)
        {
            var token = Guid.NewGuid().ToString("N");
            var record = new WindowRecord();
            var options = new WindowOptions(new WindowBounds(x, y, width, height))
            {
                Draggable = draggable,
                Resizable = resizable,
                Pinnable = pinnable,
                MinimumWidth = 100,
                MinimumHeight = 60
            };
            var callbacks = new WindowCallbacks
            {
                Draw = () => Log("draw:" + record.Host.Id),
                BoundsChanged = _ =>
                {
                    record.BoundsChanged++;
                    Log("geometry:" + (record.Host == null ? token : record.Host.Id));
                },
                HasTextFocus = () => record.TextFocus && (!record.QueryRequiresOpen || record.Host?.IsOpen == true),
                ReleaseTextFocus = () =>
                {
                    if (!record.TextFocus) return;
                    record.TextFocus = false;
                    record.TextReleases++;
                    Log("text:" + record.Host.Id);
                },
                HasPopup = () => record.Popup && (!record.QueryRequiresOpen || record.Host?.IsOpen == true),
                ClosePopup = () =>
                {
                    if (!record.Popup) return;
                    record.Popup = false;
                    record.PopupCloses++;
                    Log("popup:" + record.Host.Id);
                },
                CanReceiveKeyboard = () => record.Keyboard &&
                                           (record.KeyboardPanelOrder == null ||
                                            !KeyboardInputPolicy.IsHigherPriorityPanelBlocking(
                                                record.Host.Id,
                                                record.KeyboardPanelOrder,
                                                record.RegisteredKeyboardPanels,
                                                _coordinator.GetEscapePassThroughIds(),
                                                id => Array.IndexOf(record.MouseOverKeyboardPanels, id) >= 0)),
                WheelAt = (pointerX, pointerY) =>
                {
                    record.WheelDecisions++;
                    record.BoundsAtDecision = BoundsMap().Count;
                    Log("decide:" + record.Host.Id);
                    return record.Host.Bounds.Contains(pointerX, pointerY) ? record.WheelDisposition : 0;
                }
            };
            record.Host = new WindowHost(
                modId,
                windowId,
                options,
                pointAware ? (IWindowBackend)new PointAwareBackend() : new Backend(),
                callbacks);
            _windows.Add(token, record);
            return token;
        }

        public static bool Open(string token) => Get(token).Host.Open();
        public static void Close(string token) => Get(token).Host.Close();
        public static void Dispose(string token) => Get(token).Host.Dispose();
        public static void Activate(string token) => Get(token).Host.Activate();
        public static void Pin(string token, bool pinned) => Get(token).Host.SetPinned(pinned);
        public static void BeginDrag(string token, int x, int y) => Get(token).Host.BeginDrag(x, y);
        public static void BeginResize(string token, int x, int y) => Get(token).Host.BeginResize(x, y);

        public static void Pointer(string token, int x, int y, bool down, bool blocked) =>
            Get(token).Host.UpdatePointer(x, y, down, blocked);

        public static void Refresh(string token) => Get(token).Host.RefreshGeometry();

        public static bool UpdateContent(string token, int x, int y, string operation)
        {
            WindowRecord record = Get(token);
            return record.Host.TryUpdateContent(
                x,
                y,
                () =>
                {
                    record.ContentUpdates++;
                    record.BoundsAtContent = BoundsMap().Count;
                    record.OwnBoundsAtContent = BoundsMap().ContainsKey(record.Host.Id);
                    Log("content:" + record.Host.Id);
                    switch (operation)
                    {
                        case "normal":
                            return;
                        case "throw":
                            throw new InvalidOperationException("Synthetic content update failure");
                        case "close":
                            record.Host.Close();
                            return;
                        case "dispose":
                            record.Host.Dispose();
                            return;
                        case "reopen":
                            record.Host.Close();
                            if (!record.Host.Open()) throw new InvalidOperationException("Synthetic reopen failed");
                            return;
                        case "fit":
                            record.Host.RefreshGeometry();
                            return;
                        case "nested":
                        case "nested-throw":
                            bool shouldThrow = operation == "nested-throw";
                            record.Host.TryUpdateContent(
                                x,
                                y,
                                () =>
                                {
                                    record.NestedUpdates++;
                                    record.BoundsAtNested = BoundsMap().Count;
                                    record.OwnBoundsAtNested = BoundsMap().ContainsKey(record.Host.Id);
                                    Log("nested-content:" + record.Host.Id);
                                    if (shouldThrow) throw new ArgumentException("Synthetic nested content failure");
                                });
                            record.BoundsAfterNested = BoundsMap().Count;
                            return;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(operation));
                    }
                });
        }

        public static bool UpdateContentWithoutCallback(string token, int x, int y) =>
            Get(token).Host.TryUpdateContent(x, y, null);

        public static void PointInputBlocked(string token, bool blocked)
        {
            string id = Get(token).Host.Id;
            var blockedIds = (Dictionary<string, bool>)Shared()["pointBlockedIds"];
            if (blocked) blockedIds[id] = true;
            else blockedIds.Remove(id);
        }

        public static void FailBoundsOperations(string token, int registerCount, int unregisterCount)
        {
            string id = Get(token).Host.Id;
            ((Dictionary<string, int>)Shared()["failRegisterCount"])[id] = registerCount;
            ((Dictionary<string, int>)Shared()["failUnregisterCount"])[id] = unregisterCount;
        }

        public static void ConfigureWindow(
            string token,
            bool textFocus,
            bool popup,
            int wheelDisposition,
            bool keyboard)
        {
            WindowRecord record = Get(token);
            record.TextFocus = textFocus;
            record.Popup = popup;
            record.WheelDisposition = wheelDisposition;
            record.Keyboard = keyboard;
        }

        public static void VisibilityQueries(string token, bool requiresOpen) =>
            Get(token).QueryRequiresOpen = requiresOpen;

        public static void KeyboardPanelPriority(
            string token,
            string[] panelOrder,
            string[] registeredPanels,
            string[] mouseOverPanels)
        {
            WindowRecord record = Get(token);
            record.KeyboardPanelOrder = panelOrder;
            record.RegisteredKeyboardPanels = registeredPanels;
            record.MouseOverKeyboardPanels = mouseOverPanels;
        }

        public static void BlockInput(string token, bool blocked)
        {
            var blockedIds = (Dictionary<string, bool>)Shared()["blockedIds"];
            string id = Get(token).Host.Id;
            if (blocked) blockedIds[id] = true;
            else blockedIds.Remove(id);
        }

        public static IDictionary<string, object> Window(string token)
        {
            WindowRecord record = Get(token);
            WindowHost host = record.Host;
            return new Dictionary<string, object>
            {
                ["id"] = host.Id,
                ["open"] = host.IsOpen,
                ["pinned"] = host.IsPinned,
                ["x"] = host.Bounds.X,
                ["y"] = host.Bounds.Y,
                ["width"] = host.Bounds.Width,
                ["height"] = host.Bounds.Height,
                ["textFocus"] = record.TextFocus,
                ["popup"] = record.Popup,
                ["textReleases"] = record.TextReleases,
                ["popupCloses"] = record.PopupCloses,
                ["boundsChanged"] = record.BoundsChanged,
                ["wheelDecisions"] = record.WheelDecisions,
                ["boundsAtDecision"] = record.BoundsAtDecision,
                ["contentUpdates"] = record.ContentUpdates,
                ["boundsAtContent"] = record.BoundsAtContent,
                ["ownBoundsAtContent"] = record.OwnBoundsAtContent,
                ["nestedUpdates"] = record.NestedUpdates,
                ["boundsAtNested"] = record.BoundsAtNested,
                ["ownBoundsAtNested"] = record.OwnBoundsAtNested,
                ["boundsAfterNested"] = record.BoundsAfterNested
            };
        }

        public static void Frame(long frame, bool down, bool pressed, bool menu)
        {
            Shared()["menu"] = menu;
            _coordinator.BeginFrame(frame, down, pressed, menu);
            _coordinator.CompleteFrame(frame);
        }

        public static IDictionary<string, object> Wheel(int x, int y) => _coordinator.SelectWheel(x, y);
        public static IDisposable SuspendScroll(int x, int y) => _coordinator.SuspendBoundsForScroll(x, y);
        public static void CloseAll(int reason) => _coordinator.CloseAll(reason);

        public static IDisposable Runtime(string family, string signature)
        {
            return _coordinator.AcquireRuntime(
                family,
                signature,
                () =>
                {
                    Increment("installs", 1);
                    return new Cleanup(() => Increment("uninstalls", 1));
                });
        }

        public static IDisposable FailedRuntime(string family, string signature)
        {
            return _coordinator.AcquireRuntime(
                family,
                signature,
                () => throw new InvalidOperationException("Synthetic installation failure"));
        }

        public static IDisposable NullRuntime(string family, string signature) =>
            _coordinator.AcquireRuntime(family, signature, () => null);

        public static IDisposable ReentrantRuntime(string family, string signature) =>
            _coordinator.AcquireRuntime(
                family,
                signature,
                () => _coordinator.AcquireRuntime(family, signature, () => new Cleanup(() => { })));

        public static IDisposable RuntimeWithFailingCleanup(string family, string signature)
        {
            return _coordinator.AcquireRuntime(
                family,
                signature,
                () =>
                {
                    Increment("installs", 1);
                    return new RetryCleanup();
                });
        }

        public static void Viewport(int width, int height)
        {
            Shared()["viewportWidth"] = width;
            Shared()["viewportHeight"] = height;
        }

        public static string[] Events() => EventList().ToArray();
        public static void ResetEvents() => EventList().Clear();
        public static IDictionary<string, object> SharedState() => Shared();

        public static IDisposable BoundaryLease()
        {
            // Resolve targets for every joining consumer, not only the installer.
            MethodInfo[] targets = BoundaryTargets();
            return _coordinator.AcquireRuntime(
                "tests.synthetic-scroll",
                "v1",
                () =>
                {
                    Increment("boundaryInstalls", 1);
                    IDisposable patches = HarmonyScrollBoundary.Patch(
                        targets,
                        () => (bool)Shared()["hasScroll"],
                        () => _coordinator.SuspendBoundsForScroll((int)Shared()["mouseX"], (int)Shared()["mouseY"]));
                    return new Cleanup(() =>
                    {
                        patches.Dispose();
                        Increment("boundaryUninstalls", 1);
                    });
                });
        }

        public static IDisposable DirectBoundary() =>
            HarmonyScrollBoundary.Patch(
                BoundaryTargets(),
                () => (bool)Shared()["hasScroll"],
                () => _coordinator.SuspendBoundsForScroll((int)Shared()["mouseX"], (int)Shared()["mouseY"]));

        public static IDisposable ForeignBoundary()
        {
            MethodInfo target = BoundaryTargets()[0];
            MethodInfo prefix = typeof(Fixture).GetMethod(
                nameof(ForeignPrefix),
                BindingFlags.NonPublic | BindingFlags.Static);
            var harmony = new Harmony(ForeignHarmonyOwner);
            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            }
            catch
            {
                // Harmony may record a patch before its wrapper rebuild fails.
                harmony.Unpatch(target, prefix);
                throw;
            }

            return new Cleanup(() => harmony.Unpatch(target, prefix));
        }

        public static bool HasForeignBoundary()
        {
            Patches info = Harmony.GetPatchInfo(BoundaryTargets()[0]);
            if (info == null) return false;
            foreach (Patch prefix in info.Prefixes)
                if (prefix.owner == ForeignHarmonyOwner)
                    return true;

            return false;
        }

        public static string BoundaryPatchAssembly()
        {
            Patches info = Harmony.GetPatchInfo(BoundaryTargets()[0]);
            if (info == null) return null;
            foreach (Patch prefix in info.Prefixes)
                if (prefix.owner == HarmonyScrollBoundary.HarmonyOwner)
                {
                    Type declaringType = prefix.PatchMethod?.DeclaringType ??
                                         throw new InvalidOperationException(
                                             "The scroll prefix must have a declaring type.");
                    return declaringType.Assembly.FullName;
                }

            return null;
        }

        private static MethodInfo[] BoundaryTargets()
        {
            Type fixture = typeof(Fixture).Assembly.GetName().Name.EndsWith("ConsumerA", StringComparison.Ordinal)
                ? typeof(Fixture)
                : Assembly.Load("Escarval.TerrariaWindowKit.ConsumerA").GetType("UiConsumerA.Fixture", true);
            return
            [
                fixture.GetMethod(nameof(SyntheticOuter), BindingFlags.Public | BindingFlags.Static),
                fixture.GetMethod(nameof(SyntheticInner), BindingFlags.Public | BindingFlags.Static)
            ];
        }

        // Invoked by Harmony rather than through an ordinary C# call.
        // ReSharper disable once UnusedMember.Local
        private static void ForeignPrefix() => Increment("foreignPatchCalls", 1);

        public static void InvokeTarget(bool nested, bool fail)
        {
            Shared()["nested"] = nested;
            Shared()["fail"] = fail;
            Shared()["targetCalls"] = 0;
            Shared()["bodyBounds"] = -1;
            SyntheticOuter();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SyntheticOuter()
        {
            ObserveTarget();
            if ((bool)Shared()["nested"])
                SyntheticInner();
            else if ((bool)Shared()["fail"])
                throw new InvalidOperationException("Synthetic scroll failure");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SyntheticInner()
        {
            ObserveTarget();
            if ((bool)Shared()["fail"])
                throw new InvalidOperationException("Synthetic scroll failure");
        }

        private static void ObserveTarget()
        {
            Increment("targetCalls", 1);
            Shared()["bodyBounds"] = BoundsMap().Count;
            Log("target");
        }

        private static WindowRecord Get(string token) => _windows[token];
        private static Dictionary<string, int[]> BoundsMap() => (Dictionary<string, int[]>)Shared()["bounds"];
        private static List<string> EventList() => (List<string>)Shared()["events"];
        private static void Log(string message) => EventList().Add(message);
        private static void Increment(string key, int delta) => Shared()[key] = (int)Shared()[key] + delta;

        private static bool ConsumeFailure(string key, string id)
        {
            var failures = (Dictionary<string, int>)Shared()[key];
            if (!failures.TryGetValue(id, out int count) || count <= 0) return false;
            failures[id] = count - 1;
            return true;
        }

        private static Dictionary<string, object> Shared()
        {
            lock (AppDomain.CurrentDomain)
            {
                if (AppDomain.CurrentDomain.GetData(StateKey) is Dictionary<string, object> state)
                    return state;
                state = new Dictionary<string, object>
                {
                    ["viewportWidth"] = 1000,
                    ["viewportHeight"] = 800,
                    ["menu"] = false,
                    ["blocked"] = false,
                    ["bounds"] = new Dictionary<string, int[]>(),
                    ["draws"] = new Dictionary<string, Action>(),
                    ["blockedChecks"] = new Dictionary<string, int>(),
                    ["blockedIds"] = new Dictionary<string, bool>(),
                    ["pointBlockedIds"] = new Dictionary<string, bool>(),
                    ["pointChecks"] = new Dictionary<string, int[]>(),
                    ["failRegisterCount"] = new Dictionary<string, int>(),
                    ["failUnregisterCount"] = new Dictionary<string, int>(),
                    ["events"] = new List<string>(),
                    ["escapeBlocks"] = 0,
                    ["installs"] = 0,
                    ["uninstalls"] = 0,
                    ["boundaryInstalls"] = 0,
                    ["boundaryUninstalls"] = 0,
                    ["foreignPatchCalls"] = 0,
                    ["cleanupAttempts"] = 0,
                    ["hasScroll"] = true,
                    ["mouseX"] = 110,
                    ["mouseY"] = 110,
                    ["nested"] = false,
                    ["fail"] = false,
                    ["targetCalls"] = 0,
                    ["bodyBounds"] = -1
                };
                AppDomain.CurrentDomain.SetData(StateKey, state);
                return state;
            }
        }

        private sealed class WindowRecord
        {
            public int BoundsAfterNested = -1;
            public int BoundsAtContent = -1;
            public int BoundsAtDecision;
            public int BoundsAtNested = -1;
            public int BoundsChanged;
            public int ContentUpdates;
            public WindowHost Host;
            public bool Keyboard = true;
            public string[] KeyboardPanelOrder;
            public string[] MouseOverKeyboardPanels;
            public int NestedUpdates;
            public bool OwnBoundsAtContent;
            public bool OwnBoundsAtNested;
            public bool Popup;
            public int PopupCloses;
            public bool QueryRequiresOpen;
            public string[] RegisteredKeyboardPanels;
            public bool TextFocus;
            public int TextReleases;
            public int WheelDecisions;
            public int WheelDisposition;
        }

        private class Backend : IWindowBackend
        {
            public int ViewportWidth => (int)Shared()["viewportWidth"];
            public int ViewportHeight => (int)Shared()["viewportHeight"];
            public bool IsMenuOpen => (bool)Shared()["menu"];

            public bool IsInputBlocked(string id)
            {
                ((Dictionary<string, int>)Shared()["blockedChecks"])[id] = BoundsMap().Count;
                Log("blocked:" + id);
                return (bool)Shared()["blocked"] || ((Dictionary<string, bool>)Shared()["blockedIds"]).ContainsKey(id);
            }

            public void RegisterDraw(string id, Action draw)
            {
                ((Dictionary<string, Action>)Shared()["draws"]).Add(id, draw);
                Log("draw+:" + id);
            }

            public void UnregisterDraw(string id)
            {
                ((Dictionary<string, Action>)Shared()["draws"]).Remove(id);
                Log("draw-:" + id);
            }

            public void RegisterBounds(string id, WindowBounds bounds)
            {
                if (ConsumeFailure("failRegisterCount", id))
                {
                    Log("bounds!+:" + id);
                    throw new InvalidOperationException("Synthetic bounds registration failure");
                }

                BoundsMap()[id] = [bounds.X, bounds.Y, bounds.Width, bounds.Height];
                Log("bounds+:" + id);
            }

            public void UnregisterBounds(string id)
            {
                BoundsMap().Remove(id);
                Log("bounds-:" + id);
                if (ConsumeFailure("failUnregisterCount", id))
                    throw new InvalidOperationException("Synthetic bounds unregistration failure");
            }

            public void BringToFront(string id) => Log("front:" + id);

            public IDisposable AcquireEscapeBlock()
            {
                Increment("escapeBlocks", 1);
                Log("escape+");
                return new Cleanup(() =>
                {
                    Increment("escapeBlocks", -1);
                    Log("escape-");
                });
            }
        }

        private sealed class PointAwareBackend : Backend, IWindowUpdateInput
        {
            public bool IsInputBlockedAt(string id, int x, int y)
            {
                ((Dictionary<string, int>)Shared()["blockedChecks"])[id] = BoundsMap().Count;
                ((Dictionary<string, int[]>)Shared()["pointChecks"])[id] = [x, y];
                Log("blocked-at:" + id);
                return ((Dictionary<string, bool>)Shared()["pointBlockedIds"]).TryGetValue(id, out bool blocked) &&
                       blocked;
            }
        }

        private sealed class Cleanup(Action action) : IDisposable
        {
            private Action _action = action;

            public void Dispose()
            {
                Action action = _action;
                _action = null;
                action?.Invoke();
            }
        }

        private sealed class RetryCleanup : IDisposable
        {
            private bool _attempted;
            private bool _completed;

            public void Dispose()
            {
                if (_completed) return;
                Increment("cleanupAttempts", 1);
                if (!_attempted)
                {
                    _attempted = true;
                    throw new InvalidOperationException("Synthetic runtime cleanup failure");
                }

                Increment("uninstalls", 1);
                _completed = true;
            }
        }
    }
}