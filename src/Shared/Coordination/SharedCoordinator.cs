using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Escarval.TerrariaWindowKit
{
    /// <summary>Connects source-only copies through a shared AppDomain contract.</summary>
    /// <remarks>
    /// Create the connection on the UI thread before constructing a backend.
    /// The cross-assembly ABI contains only BCL values, delegates and IDisposable;
    /// a consumer must never cast another assembly's source-only toolkit types.
    /// Frame dispatch and runtime leases are managed by CoreWindowBackend.
    /// </remarks>
    internal sealed class SharedCoordinator
    {
        internal const string Slot = "Escarval.TerrariaWindowKit.Coordinator";
        private const int ProtocolMajor = 2;
        private readonly IDictionary<string, object> _api;

        private SharedCoordinator(IDictionary<string, object> api)
        {
            _api = api;
        }

        /// <summary>Identifies the provider selected by the first compatible connection.</summary>
        internal string ProviderId => (string)_api["providerId"];

        /// <summary>Creates or joins the compatible provider in the current AppDomain.</summary>
        /// <returns>A local connection whose callbacks use the shared BCL contract.</returns>
        /// <exception cref="NotSupportedException">An existing provider uses an incompatible protocol or lacks required capabilities.</exception>
        internal static SharedCoordinator Connect()
        {
            lock (AppDomain.CurrentDomain)
            {
                object existing = AppDomain.CurrentDomain.GetData(Slot);
                if (existing == null)
                {
                    existing = new Provider().CreateApi();
                    AppDomain.CurrentDomain.SetData(Slot, existing);
                }

                var api = existing as IDictionary<string, object>;
                if (api == null ||
                    !api.TryGetValue("protocolMajor", out object version) ||
                    !(version is int value) ||
                    value != ProtocolMajor)
                    throw new NotSupportedException(
                        "Incompatible TerrariaWindowKit coordinator protocol. Update consumers together.");
                string[] required =
                [
                    "register", "activate", "beginFrame", "completeFrame", "nextFrame", "selectWheel", "suspendScroll",
                    "closeAll", "acquireRuntime", "snapshot", "escapePassThroughIds"
                ];
                foreach (string name in required)
                    if (!api.ContainsKey(name))
                        throw new NotSupportedException("Coordinator capability missing: " + name);
                if (!api.TryGetValue("wheelPriorityAtVersion", out object wheelPriority) ||
                    !(wheelPriority is int priority) ||
                    priority < 1)
                    throw new NotSupportedException(
                        "Coordinator does not support update-coordinate wheel priority. Update consumers together.");
                return new SharedCoordinator(api);
            }
        }

        internal IDisposable Register(IDictionary<string, object> window) =>
            ((Func<IDictionary<string, object>, IDisposable>)_api["register"])(window);

        internal void Activate(string id) => ((Action<string>)_api["activate"])(id);
        internal long NextFrame() => ((Func<long>)_api["nextFrame"])();

        internal void BeginFrame(long frame, bool down, bool pressed, bool menu) =>
            ((Action<long, bool, bool, bool>)_api["beginFrame"])(frame, down, pressed, menu);

        internal void CompleteFrame(long frame) => ((Action<long>)_api["completeFrame"])(frame);

        internal IDictionary<string, object> SelectWheel(int x, int y) =>
            ((Func<int, int, IDictionary<string, object>>)_api["selectWheel"])(x, y);

        internal IDisposable SuspendBoundsForScroll(int x, int y) =>
            ((Func<int, int, IDisposable>)_api["suspendScroll"])(x, y);

        internal void CloseAll(int reason) => ((Action<int>)_api["closeAll"])(reason);

        internal string[] GetEscapePassThroughIds() => ((Func<string[]>)_api["escapePassThroughIds"])();

        internal IDisposable AcquireRuntime(string family, string signature, Func<IDisposable> install) =>
            ((Func<string, string, Func<IDisposable>, IDisposable>)_api["acquireRuntime"])(family, signature, install);

        internal IDictionary<string, object> GetSnapshot() => ((Func<IDictionary<string, object>>)_api["snapshot"])();

        internal static void RunAll(IEnumerable<Action> actions)
        {
            List<Exception> failures = null;
            foreach (Action action in actions)
                try
                {
                    action();
                }
                catch (Exception failure)
                {
                    (failures ??= new List<Exception>()).Add(failure);
                }

            if (failures != null) throw new AggregateException("One or more cleanup operations failed.", failures);
        }

        private sealed class Provider
        {
            private readonly string _id = Guid.NewGuid().ToString("N");

            private readonly Dictionary<string, RuntimeEntry> _runtimes =
                new Dictionary<string, RuntimeEntry>(StringComparer.Ordinal);

            private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
            private readonly List<IDictionary<string, object>> _windows = new List<IDictionary<string, object>>();
            private IDisposable _escapeBlock;
            private bool _escapeDown;
            private long _lastFrame = long.MinValue;
            private IDictionary<string, object> _pending;
            private int _pendingAction;
            private int _scrollDepth;
            private List<IDisposable> _scrollScopes;
            private long _serial;

            internal IDictionary<string, object> CreateApi() =>
                new Dictionary<string, object>
                {
                    ["protocolMajor"] = ProtocolMajor, ["providerId"] = _id, ["providerVersion"] = "1.0.0",
                    ["wheelPriorityAtVersion"] = 1,
                    ["register"] = new Func<IDictionary<string, object>, IDisposable>(Register),
                    ["activate"] = new Action<string>(Activate),
                    ["nextFrame"] = new Func<long>(() =>
                    {
                        CheckThread();
                        return ++_serial;
                    }),
                    ["beginFrame"] = new Action<long, bool, bool, bool>(BeginFrame),
                    ["completeFrame"] = new Action<long>(CompleteFrame),
                    ["selectWheel"] = new Func<int, int, IDictionary<string, object>>(SelectWheel),
                    ["suspendScroll"] = new Func<int, int, IDisposable>(SuspendScroll),
                    ["closeAll"] = new Action<int>(CloseAll),
                    ["escapePassThroughIds"] = new Func<string[]>(GetEscapePassThroughIds),
                    ["acquireRuntime"] = new Func<string, string, Func<IDisposable>, IDisposable>(AcquireRuntime),
                    ["snapshot"] = new Func<IDictionary<string, object>>(Snapshot)
                };

            private void CheckThread()
            {
                if (Thread.CurrentThread.ManagedThreadId != _thread)
                    throw new InvalidOperationException(
                        "TerrariaWindowKit operations must run on the owning UI thread.");
            }

            private IDisposable Register(IDictionary<string, object> descriptor)
            {
                CheckThread();
                if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
                var window = new Dictionary<string, object>(descriptor, StringComparer.Ordinal);
                var id = (string)window["id"];
                if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Window ID is required.");
                if (_windows.Any(w => (string)w["id"] == id))
                    throw new InvalidOperationException("Window ID already registered: " + id);
                // Validate ABI delegates before this registration becomes observable.
                _ = (Func<bool>)window["isOpen"];
                _ = (Func<bool>)window["canKeyboard"];
                _ = (Func<int>)window["escapeAction"];
                _ = (Action<int>)window["escape"];
                _ = (Func<IDisposable>)window["escapeBlock"];
                _ = (Func<int, int, bool>)window["contains"];
                _ = (Func<int, int, int>)window["wheelAt"];
                _ = (Func<bool>)window["inputBlocked"];
                _ = (Func<IDisposable>)window["suspend"];
                _ = (Action<int>)window["close"];
                _ = (Func<bool>)window["hasTextFocus"];
                _ = (Action)window["releaseTextFocus"];
                _windows.Add(window);
                return new ActionLease(() =>
                {
                    CheckThread();
                    _windows.Remove(window);
                    if (ReferenceEquals(_pending, window)) _pending = null;
                    if (_windows.Count == 0) ReleaseEscapeBlock();
                });
            }

            private void Activate(string id)
            {
                CheckThread();
                IDictionary<string, object> window = _windows.FirstOrDefault(w => (string)w["id"] == id);
                if (window == null) throw new InvalidOperationException("Window is not registered: " + id);
                _windows.Remove(window);
                _windows.Add(window);
                // Focus transfer is local to participating windows; callbacks run without a registry lock.
                RunAll(
                    _windows.ToArray()
                        .Where(w => !ReferenceEquals(w, window) && ((Func<bool>)w["hasTextFocus"])())
                        .Select(w => (Action)w["releaseTextFocus"]));
            }

            private string[] GetEscapePassThroughIds()
            {
                CheckThread();
                return _windows.ToArray()
                    .Where(w => ((Func<bool>)w["isOpen"])() && ((Func<int>)w["escapeAction"])() == 0)
                    .Select(w => (string)w["id"])
                    .ToArray();
            }

            private void BeginFrame(long frame, bool down, bool pressed, bool menu)
            {
                CheckThread();
                if (menu) CloseAll((int)CloseReason.GameMenu);
                if (_lastFrame == frame) return;
                _lastFrame = frame;
                _pending = null;
                bool wasDown = _escapeDown;
                _escapeDown = down;
                if (!down) ReleaseEscapeBlock();
                if (menu || !down || !pressed || wasDown || _escapeBlock != null) return;
                foreach (IDictionary<string, object> window in _windows.ToArray().Reverse())
                {
                    if (!((Func<bool>)window["isOpen"])()) continue;
                    int action = ((Func<int>)window["escapeAction"])();
                    // Pinned roots without a focus or popup action do not block lower roots.
                    if (action == 0) continue;
                    // A real keyboard blocker must not transfer Escape to a lower window.
                    if (!((Func<bool>)window["canKeyboard"])()) return;
                    _escapeBlock = ((Func<IDisposable>)window["escapeBlock"])() ??
                                   throw new InvalidOperationException("Escape block scope is required.");
                    _pending = window;
                    _pendingAction = action;
                    return;
                }
            }

            private void CompleteFrame(long frame)
            {
                CheckThread();
                if (frame != _lastFrame || _pending == null) return;
                IDictionary<string, object> window = _pending;
                int action = _pendingAction;
                _pending = null;
                if (_windows.Contains(window) &&
                    ((Func<bool>)window["isOpen"])() &&
                    ((Func<bool>)window["canKeyboard"])())
                    ((Action<int>)window["escape"])(action);
            }

            private void ReleaseEscapeBlock()
            {
                IDisposable scope = _escapeBlock;
                _escapeBlock = null;
                _pending = null;
                scope?.Dispose();
            }

            private IDictionary<string, object> SelectWheel(int x, int y)
            {
                CheckThread();
                // Evaluate callbacks against the full registered state, before any bounds mutations.
                var decisions = _windows.ToArray()
                    .Select(w => new
                    {
                        Window = w,
                        Open = ((Func<bool>)w["isOpen"])(),
                        Contains = ((Func<int, int, bool>)w["contains"])(x, y),
                        Blocked = w.TryGetValue("inputBlockedAt", out object priorityAt)
                            ? ((Func<int, int, bool>)priorityAt)(x, y)
                            : ((Func<bool>)w["inputBlocked"])(),
                        Route = ((Func<int, int, int>)w["wheelAt"])(x, y)
                    })
                    .ToArray();
                foreach (var decision in decisions.Reverse())
                {
                    if (!decision.Open || !decision.Contains) continue;
                    if (decision.Blocked) continue;
                    if (decision.Route == 0) break;
                    if (decision.Route != 1 && decision.Route != 2)
                        throw new InvalidOperationException("Unknown wheel disposition.");
                    return new Dictionary<string, object>
                        { ["ownerId"] = (string)decision.Window["id"], ["disposition"] = decision.Route };
                }

                return new Dictionary<string, object> { ["ownerId"] = null, ["disposition"] = 0 };
            }

            private IDisposable SuspendScroll(int x, int y)
            {
                CheckThread();
                if (_scrollDepth > 0)
                {
                    _scrollDepth++;
                    return new ActionLease(ReleaseScroll);
                }

                var owner = (string)SelectWheel(x, y)["ownerId"];
                IDictionary<string, object>[] windows = _windows.ToArray()
                    .Where(w => ((Func<bool>)w["isOpen"])() && (string)w["id"] != owner)
                    .ToArray();
                _scrollDepth = 1;
                _scrollScopes = new List<IDisposable>();
                try
                {
                    foreach (IDictionary<string, object> window in windows)
                    {
                        IDisposable scope = ((Func<IDisposable>)window["suspend"])();
                        if (scope != null) _scrollScopes.Add(scope);
                    }

                    return new ActionLease(ReleaseScroll);
                }
                catch (Exception failure)
                {
                    try
                    {
                        ReleaseScroll();
                    }
                    catch (Exception cleanup)
                    {
                        throw new AggregateException(failure, cleanup);
                    }

                    throw;
                }
            }

            private void ReleaseScroll()
            {
                CheckThread();
                if (--_scrollDepth != 0) return;
                List<IDisposable> scopes = _scrollScopes ??
                                           throw new InvalidOperationException(
                                               "Active scroll scopes are required for restoration.");
                _scrollScopes = null;
                RunAll(Enumerable.Reverse(scopes).Select<IDisposable, Action>(scope => scope.Dispose));
            }

            private void CloseAll(int reason)
            {
                CheckThread();
                if (!Enum.IsDefined(typeof(CloseReason), reason)) throw new ArgumentOutOfRangeException(nameof(reason));
                RunAll(
                    _windows.ToArray()
                        .Select<IDictionary<string, object>, Action>(w => () => ((Action<int>)w["close"])(reason))
                        .Concat(
                            reason == (int)CloseReason.GameMenu || reason == (int)CloseReason.WorldUnload
                                ? new[] { ReleaseEscapeBlock }
                                : Array.Empty<Action>()));
            }

            private IDisposable AcquireRuntime(string family, string signature, Func<IDisposable> install)
            {
                CheckThread();
                if (string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(signature) || install == null)
                    throw new ArgumentException("Runtime family, signature and installer are required.");
                if (!_runtimes.TryGetValue(family, out RuntimeEntry entry))
                {
                    entry = new RuntimeEntry(signature);
                    _runtimes.Add(family, entry);
                    try
                    {
                        entry.Scope = install() ??
                                      throw new InvalidOperationException(
                                          "Runtime installer returned no lifetime scope.");
                    }
                    catch
                    {
                        _runtimes.Remove(family);
                        throw;
                    }
                }
                else if (entry.Signature != signature)
                    throw new NotSupportedException("Incompatible runtime implementation for " + family);
                else if (entry.Closing)
                    throw new InvalidOperationException(
                        "Runtime cleanup must finish before another consumer can attach: " + family);
                else if (entry.Scope == null)
                    throw new InvalidOperationException("Reentrant runtime installation: " + family);

                entry.Count++;
                var released = false;
                return new ActionLease(() =>
                {
                    CheckThread();
                    if (!released)
                    {
                        entry.Count--;
                        released = true;
                    }

                    if (entry.Count != 0) return;
                    entry.Closing = true;
                    entry.Scope.Dispose();
                    entry.Scope = null;
                    _runtimes.Remove(family);
                });
            }

            private IDictionary<string, object> Snapshot()
            {
                CheckThread();
                return new Dictionary<string, object>
                {
                    ["providerId"] = _id, ["protocolMajor"] = ProtocolMajor,
                    ["windowCount"] = _windows.Count, ["runtimeFamilyCount"] = _runtimes.Count,
                    ["runtimeLeaseCount"] = _runtimes.Values.Sum(r => r.Count), ["escapeBlocked"] = _escapeBlock != null
                };
            }

            private sealed class RuntimeEntry
            {
                internal readonly string Signature;
                internal bool Closing;
                internal int Count;
                internal IDisposable Scope;

                internal RuntimeEntry(string signature)
                {
                    Signature = signature;
                }
            }
        }
    }
}