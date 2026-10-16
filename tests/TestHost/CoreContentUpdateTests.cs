using System;
using System.Collections.Generic;
using NUnit.Framework;
using Terraria.GameInput;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.UI.Widgets;
using Main = Terraria.Main;

namespace Escarval.TerrariaWindowKit.TestHost
{
    [TestFixture]
    [NonParallelizable]
    public sealed class CoreContentUpdateTests
    {
        [SetUp]
        public void SetUp()
        {
            _previousCoordinator = AppDomain.CurrentDomain.GetData(SharedCoordinator.Slot);
            AppDomain.CurrentDomain.SetData(SharedCoordinator.Slot, null);
            SharedCoordinator.Connect();
            _ownedCoordinator = AppDomain.CurrentDomain.GetData(SharedCoordinator.Slot);
            UIRenderer.ResetPanels();
            Main.screenWidth = PlayerInput.RealScreenWidth = 1200;
            Main.screenHeight = PlayerInput.RealScreenHeight = 900;
            Main.UIScale = 1f;
            Main.mouseX = Main.mouseY = 110;
            Main.ingameOptionsWindow = false;
            Main.CurrentInputTextTakerOverride = null;
            Main.drawingPlayerChat = false;
            PlayerInput.NativeIgnoreMouseInterface = false;
            PlayerInput.WritingText = false;
            WidgetInput.BlockInput = false;
            WidgetInput.MouseLeftClick = false;
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                var cleanup = new List<Action>();
                foreach (WindowHost host in _hosts) cleanup.Add(host.Dispose);
                foreach (CoreWindowBackend backend in _backends) cleanup.Add(backend.Dispose);
                SharedCoordinator.RunAll(cleanup);
            }
            finally
            {
                _hosts.Clear();
                _backends.Clear();
                UIRenderer.ResetPanels();
                PlayerInput.NativeIgnoreMouseInterface = false;
                if (ReferenceEquals(AppDomain.CurrentDomain.GetData(SharedCoordinator.Slot), _ownedCoordinator))
                    AppDomain.CurrentDomain.SetData(SharedCoordinator.Slot, _previousCoordinator);
            }
        }

        private readonly List<WindowHost> _hosts = [];
        private readonly List<CoreWindowBackend> _backends = [];
        private object _previousCoordinator;
        private object _ownedCoordinator;

        [Test]
        public void TopNativeContentUpdatesOverLowerToolkitAndForeignPanelsWithoutRemovingBounds()
        {
            WindowHost lower = OpenWindow("ledger");
            RegisterForeignPanel("foreign-lower");
            WindowHost top = OpenWindow("compendium");
            var originalBounds = new Dictionary<string, ValueTuple<int, int, int, int>>(UIRenderer.RegisteredPanels);
            var originalBlockers = new HashSet<string>(UIRenderer.BlockingPanels);
            Assert.That(
                PlayerInput.IgnoreMouseInterface,
                Is.True,
                "Core normally shields native UI behind its panels.");

            bool updated = top.TryUpdateContent(
                110,
                110,
                () =>
                {
                    Assert.That(
                        PlayerInput.IgnoreMouseInterface,
                        Is.False,
                        "The native UI getter must ignore the Core panel barrier during the eligible update.");
                    Assert.That(UIRenderer.RegisteredPanels, Is.EquivalentTo(originalBounds));
                    Assert.That(UIRenderer.BlockingPanels, Is.EquivalentTo(originalBlockers));
                    Assert.That(UIRenderer.RegisteredPanels.ContainsKey(lower.Id), Is.True);
                });

            Assert.That(updated, Is.True);
            Assert.That(UIRenderer.RegisteredPanels, Is.EquivalentTo(originalBounds));
            Assert.That(UIRenderer.BlockingPanels, Is.EquivalentTo(originalBlockers));
            Assert.That(PlayerInput.IgnoreMouseInterface, Is.True, "The native barrier resumes outside the callback.");
        }

        [Test]
        public void GetterWarmedByALowerConsumerUsesTheLateInstalledContentBoundary()
        {
            WindowHost lower = OpenWindow("ledger");
            var blockedReads = 0;
            // The older consumer installs Core's getter patch and runs it before the
            // native-content consumer exists. Its optimized wrapper may inline Core's postfix.
            for (var i = 0; i < 100000; i++)
                if (PlayerInput.IgnoreMouseInterface)
                    blockedReads++;
            Assert.That(blockedReads, Is.EqualTo(100000));

            WindowHost top = OpenWindow("compendium");
            Assert.That(
                top.TryUpdateContent(
                    110,
                    110,
                    () =>
                    {
                        Assert.That(
                            PlayerInput.IgnoreMouseInterface,
                            Is.False,
                            "The production adapter must refresh Core's previously compiled getter wrapper.");
                        Assert.That(UIRenderer.RegisteredPanels.ContainsKey(lower.Id), Is.True);
                        Assert.That(UIRenderer.RegisteredPanels.ContainsKey(top.Id), Is.True);
                    }),
                Is.True);

            Assert.That(PlayerInput.IgnoreMouseInterface, Is.True);
            Assert.That(UIRenderer.RegisteredPanels, Has.Count.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HigherCorePanelRejectsTheUpdateBeforeStartingNativeSuppression(bool toolkitPanel)
        {
            WindowHost lower = OpenWindow("compendium");
            if (toolkitPanel) OpenWindow("higher-toolkit");
            else RegisterForeignPanel("higher-foreign");
            var calls = 0;

            bool updated = lower.TryUpdateContent(110, 110, () => calls++);

            Assert.That(updated, Is.False);
            Assert.That(calls, Is.Zero);
            Assert.That(PlayerInput.IgnoreMouseInterface, Is.True);
            Assert.That(UIRenderer.RegisteredPanels, Has.Count.EqualTo(2));
        }

        [Test]
        public void CoreOrderWinsOverStaleCoordinatorActivationAndDrawInput()
        {
            WindowHost coreTop = OpenWindow("core-top");
            WindowHost coordinatorTop = OpenWindow("coordinator-top");
            // Leave the coordinator's last activation at coordinatorTop, changing only Core order.
            UIRenderer.BringToFront(coreTop.Id);
            // Point-aware update input must not use draw-phase coordinates or the transient draw block.
            Main.mouseX = Main.mouseY = 800;
            WidgetInput.BlockInput = true;

            Assert.That(coordinatorTop.TryUpdateContent(110, 110, () => Assert.Fail("Covered content ran.")), Is.False);
            bool updated = coreTop.TryUpdateContent(
                110,
                110,
                () =>
                {
                    Main.mouseX = Main.mouseY = 110;
                    Assert.That(PlayerInput.IgnoreMouseInterface, Is.False);
                });

            Assert.That(updated, Is.True);
            Assert.That(PlayerInput.IgnoreMouseInterface, Is.True);
        }

        [Test]
        public void NativeIgnoreConditionIsPreservedDuringAnEligibleUpdate()
        {
            WindowHost top = OpenWindow("compendium");
            PlayerInput.NativeIgnoreMouseInterface = true;

            Assert.That(
                top.TryUpdateContent(110, 110, () => Assert.That(PlayerInput.IgnoreMouseInterface, Is.True)),
                Is.True);

            Assert.That(PlayerInput.IgnoreMouseInterface, Is.True);
        }

        [Test]
        public void ScopeRechecksCorePriorityAndDoesNotAdmitANestedCoveredWindow()
        {
            WindowHost lower = OpenWindow("ledger");
            WindowHost top = OpenWindow("compendium");

            Assert.That(
                top.TryUpdateContent(
                    110,
                    110,
                    () =>
                    {
                        Assert.That(PlayerInput.IgnoreMouseInterface, Is.False);
                        Assert.That(
                            lower.TryUpdateContent(
                                110,
                                110,
                                () => Assert.Fail(
                                    "The upper root's bounds must remain visible to nested priority checks.")),
                            Is.False);
                        lower.Activate();
                        Assert.That(
                            PlayerInput.IgnoreMouseInterface,
                            Is.True,
                            "A higher panel created during the callback must regain its native input barrier.");
                        top.Activate();
                        Assert.That(PlayerInput.IgnoreMouseInterface, Is.False);
                    }),
                Is.True);

            Assert.That(PlayerInput.IgnoreMouseInterface, Is.True);
            Assert.That(UIRenderer.RegisteredPanels, Has.Count.EqualTo(2));
        }

        [Test]
        public void NestedUpdatesAndAnExceptionRestoreTheCoreBarrier()
        {
            OpenWindow("ledger");
            WindowHost top = OpenWindow("compendium");

            Assert.Throws<InvalidOperationException>(
                (Action)(() => top.TryUpdateContent(
                    110,
                    110,
                    () =>
                    {
                        Assert.That(PlayerInput.IgnoreMouseInterface, Is.False);
                        Assert.That(
                            top.TryUpdateContent(
                                110,
                                110,
                                () => Assert.That(PlayerInput.IgnoreMouseInterface, Is.False)),
                            Is.True);
                        Assert.That(
                            PlayerInput.IgnoreMouseInterface,
                            Is.False,
                            "The inner exit preserves the outer scope.");
                        throw new InvalidOperationException("Synthetic native content failure.");
                    })));

            Assert.That(PlayerInput.IgnoreMouseInterface, Is.True);
            Assert.That(UIRenderer.RegisteredPanels, Has.Count.EqualTo(2));
            Assert.That(
                top.TryUpdateContent(110, 110, () => Assert.That(PlayerInput.IgnoreMouseInterface, Is.False)),
                Is.True);
            Assert.That(PlayerInput.IgnoreMouseInterface, Is.True);
        }

        [Test]
        public void ClosingTheHostStopsSuppressionWithinTheCallback()
        {
            RegisterForeignPanel("foreign-lower");
            WindowHost top = OpenWindow("compendium");

            Assert.That(
                top.TryUpdateContent(
                    110,
                    110,
                    () =>
                    {
                        Assert.That(PlayerInput.IgnoreMouseInterface, Is.False);
                        top.Close();
                        Assert.That(
                            PlayerInput.IgnoreMouseInterface,
                            Is.True,
                            "A closed host cannot keep suppressing the lower panel's native barrier.");
                    }),
                Is.True);

            Assert.That(UIRenderer.RegisteredPanels.Keys, Is.EquivalentTo(["foreign-lower"]));
        }

        [Test]
        public void BackendDisposalStopsSuppressionAndPreservesForeignBounds()
        {
            RegisterForeignPanel("foreign-lower");
            WindowHost top = OpenWindow("compendium");
            CoreWindowBackend backend = _backends[_backends.Count - 1];

            Assert.That(
                top.TryUpdateContent(
                    110,
                    110,
                    () =>
                    {
                        Assert.That(PlayerInput.IgnoreMouseInterface, Is.False);
                        top.Dispose();
                        backend.Dispose();
                        Assert.That(PlayerInput.IgnoreMouseInterface, Is.True);
                    }),
                Is.True);

            Assert.That(UIRenderer.RegisteredPanels.Keys, Is.EquivalentTo(["foreign-lower"]));
            Assert.That(UIRenderer.BlockingPanels, Is.EquivalentTo(["foreign-lower"]));
            Assert.That(PlayerInput.IgnoreMouseInterface, Is.True);
        }

        private WindowHost OpenWindow(string modId)
        {
            var backend = new CoreWindowBackend(SharedCoordinator.Connect());
            _backends.Add(backend);
            var host = new WindowHost(modId, "root", new WindowOptions(new WindowBounds(100, 100, 300, 200)), backend);
            _hosts.Add(host);
            Assert.That(host.Open(), Is.True);
            return host;
        }

        private static void RegisterForeignPanel(string id)
        {
            UIRenderer.RegisterPanelDraw(id, () => { });
            UIRenderer.RegisterPanelBounds(id, 100, 100, 300, 200);
        }
    }
}