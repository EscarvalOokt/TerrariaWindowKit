using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using HarmonyLib;
using NUnit.Framework;

namespace Escarval.TerrariaWindowKit.TestHost
{
    [TestFixture]
    public sealed class CoreCompatibilityChecksTests
    {
        [Test]
        public void RuntimeSignatureIsStableForIdenticalLoadedTypes()
        {
            const string runtimeContract = "escarval.terrariawindowkit.core.runtime/2;scroll=1;input=3";
            string first = CoreCompatibilityChecks.CreateRuntimeSignature(
                runtimeContract,
                typeof(CompatibleRenderer),
                typeof(CompatibleHarmony),
                typeof(SyntheticPlayer));
            string second = CoreCompatibilityChecks.CreateRuntimeSignature(
                runtimeContract,
                typeof(CompatibleRenderer),
                typeof(CompatibleHarmony),
                typeof(SyntheticPlayer));

            Assert.That(second, Is.EqualTo(first));
            Assert.That(first, Does.StartWith(runtimeContract + ";"));
        }

        [Test]
        public void RuntimeContractRevisionChangesTheSignature()
        {
            Type[] dependencies = [typeof(CompatibleRenderer), typeof(CompatibleHarmony)];
            string otherRevision = CoreCompatibilityChecks.CreateRuntimeSignature(
                "escarval.terrariawindowkit.core.runtime/3;scroll=1;input=3",
                dependencies);
            string currentRevision = CoreCompatibilityChecks.CreateRuntimeSignature(
                "escarval.terrariawindowkit.core.runtime/2;scroll=1;input=3",
                dependencies);

            Assert.That(currentRevision, Is.Not.EqualTo(otherRevision));
        }

        [Test]
        public void RuntimeInputRevisionChangesTheSignature()
        {
            string current = CoreCompatibilityChecks.CreateRuntimeSignature(
                "escarval.terrariawindowkit.core.runtime/2;scroll=1;input=3",
                typeof(CompatibleRenderer));
            string older = CoreCompatibilityChecks.CreateRuntimeSignature(
                "escarval.terrariawindowkit.core.runtime/2;scroll=1;input=2",
                typeof(CompatibleRenderer));
            Assert.That(current, Is.Not.EqualTo(older));
        }

        [Test]
        public void DifferentLoadedTypesInSameAssemblyDoNotShareRuntimeSignature()
        {
            const string runtimeContract = "escarval.terrariawindowkit.core.runtime/2;scroll=1;input=3";
            Assert.That(typeof(CompatibleHarmony).Assembly, Is.SameAs(typeof(IncompatibleHarmony).Assembly));
            string first = CoreCompatibilityChecks.CreateRuntimeSignature(
                runtimeContract,
                typeof(CompatibleRenderer),
                typeof(CompatibleHarmony));
            string second = CoreCompatibilityChecks.CreateRuntimeSignature(
                runtimeContract,
                typeof(CompatibleRenderer),
                typeof(IncompatibleHarmony));

            Assert.That(second, Is.Not.EqualTo(first));
        }

        [Test]
        public void DependencyTypeOrderContributesToRuntimeSignature()
        {
            const string runtimeContract = "escarval.terrariawindowkit.core.runtime/2;scroll=1;input=3";
            string first = CoreCompatibilityChecks.CreateRuntimeSignature(
                runtimeContract,
                typeof(CompatibleRenderer),
                typeof(CompatibleHarmony));
            string reversed = CoreCompatibilityChecks.CreateRuntimeSignature(
                runtimeContract,
                typeof(CompatibleHarmony),
                typeof(CompatibleRenderer));

            Assert.That(reversed, Is.Not.EqualTo(first));
        }

        [TestCase("0.4.1.0")]
        [TestCase("0.4.2.0")]
        [TestCase("0.4.3.0")]
        [TestCase("0.4.2.1")]
        [TestCase("1.0.0.0")]
        public void CoreVersionDoesNotRejectMatchingContracts(string version)
        {
            CoreCompatibilityChecks.ValidateCoreContracts(
                typeof(CompatibleRenderer),
                typeof(CompatibleWidgetInput),
                new Version(version));
        }

        [TestCase("2.3.2.0")]
        [TestCase("2.3.3.0")]
        [TestCase("2.3.4.0")]
        [TestCase("2.3.3.1")]
        [TestCase("3.0.0.0")]
        public void HarmonyVersionDoesNotRejectMatchingContracts(string version)
        {
            CoreCompatibilityChecks.ValidateHarmonyContracts(
                typeof(CompatibleHarmony),
                typeof(CompatibleHarmonyMethod),
                typeof(CompatiblePatches),
                new Version(version));
        }

        [Test]
        public void AdditionalCoreMethodsAndOverloadsDoNotRejectRequiredContracts()
        {
            CoreCompatibilityChecks.ValidateCoreContracts(
                typeof(ExtendedRenderer),
                typeof(CompatibleWidgetInput),
                new Version(1, 0, 0, 0));
        }

        [Test]
        public void LoadedHarmonyAssemblySatisfiesRequiredContracts()
        {
            CoreCompatibilityChecks.ValidateHarmonyContracts(
                typeof(Harmony),
                typeof(HarmonyMethod),
                typeof(Patches),
                typeof(Harmony).Assembly.GetName().Version);
        }

        [TestCase("0.4.2.0")]
        [TestCase("0.4.3.0")]
        public void MissingCoreOperationIsRejectedRegardlessOfVersion(string version)
        {
            NotSupportedException error =
                Assert.Throws<NotSupportedException>(
                    (Action)(() => CoreCompatibilityChecks.ValidateCoreContracts(
                        typeof(IncompatibleRenderer),
                        typeof(CompatibleWidgetInput),
                        new Version(version)))) ??
                throw new AssertionException("Expected NotSupportedException.");
            Assert.That(error.InnerException, Is.TypeOf<MissingMethodException>());
            Assert.That(error.Message, Does.Contain(version));
        }

        [Test]
        public void WrongRegistrationReturnTypeIsRejected()
        {
            NotSupportedException error = Assert.Throws<NotSupportedException>(
                                              (Action)(() => CoreCompatibilityChecks.ValidateCoreContracts(
                                                  typeof(RendererWithWrongRegistrationReturn),
                                                  typeof(CompatibleWidgetInput),
                                                  new Version(0, 4, 2, 0)))) ??
                                          throw new AssertionException("Expected NotSupportedException.");
            Assert.That(error.InnerException, Is.TypeOf<MissingMethodException>());
            Assert.That(error.InnerException?.Message, Does.Contain("RegisterPanelDraw"));
        }

        [Test]
        public void CoreInputBlockFieldIsRejected()
        {
            NotSupportedException error =
                Assert.Throws<NotSupportedException>(
                    (Action)(() => CoreCompatibilityChecks.ValidateCoreContracts(
                        typeof(CompatibleRenderer),
                        typeof(WidgetInputWithFieldBlockInput),
                        new Version(0, 4, 2, 0)))) ??
                throw new AssertionException("Expected NotSupportedException.");
            Assert.That(error.InnerException, Is.TypeOf<MissingMemberException>());
            Assert.That(error.InnerException?.Message, Does.Contain("BlockInput"));
        }

        [Test]
        public void CoreMouseClickFieldIsRejected()
        {
            NotSupportedException error =
                Assert.Throws<NotSupportedException>(
                    (Action)(() => CoreCompatibilityChecks.ValidateCoreContracts(
                        typeof(CompatibleRenderer),
                        typeof(WidgetInputWithFieldMouseClick),
                        new Version(0, 4, 2, 0)))) ??
                throw new AssertionException("Expected NotSupportedException.");
            Assert.That(error.InnerException, Is.TypeOf<MissingMemberException>());
            Assert.That(error.InnerException?.Message, Does.Contain("MouseLeftClick"));
        }

        [Test]
        public void CoreInputBlockMustBeWritable()
        {
            NotSupportedException error =
                Assert.Throws<NotSupportedException>(
                    (Action)(() => CoreCompatibilityChecks.ValidateCoreContracts(
                        typeof(CompatibleRenderer),
                        typeof(IncompatibleWidgetInput),
                        new Version(0, 4, 2, 0)))) ??
                throw new AssertionException("Expected NotSupportedException.");
            Assert.That(error.InnerException, Is.TypeOf<MissingMemberException>());
        }

        [Test]
        public void WrongCorePriorityReturnTypeIsRejected()
        {
            NotSupportedException error = Assert.Throws<NotSupportedException>(
                                              (Action)(() => CoreCompatibilityChecks.ValidateCoreContracts(
                                                  typeof(RendererWithWrongPriorityReturn),
                                                  typeof(CompatibleWidgetInput),
                                                  new Version(0, 4, 2, 0)))) ??
                                          throw new AssertionException("Expected NotSupportedException.");
            Assert.That(error.InnerException, Is.TypeOf<MissingMethodException>());
            Assert.That(error.InnerException?.Message, Does.Contain("ShouldBlockForHigherPriorityPanel"));
        }

        [Test]
        public void ReadonlyCoreBlockInputFieldIsRejected()
        {
            NotSupportedException error =
                Assert.Throws<NotSupportedException>(
                    (Action)(() => CoreCompatibilityChecks.ValidateCoreContracts(
                        typeof(CompatibleRenderer),
                        typeof(ReadonlyWidgetInput),
                        new Version(0, 4, 2, 0)))) ??
                throw new AssertionException("Expected NotSupportedException.");
            Assert.That(error.InnerException, Is.TypeOf<MissingMemberException>());
            Assert.That(error.InnerException?.Message, Does.Contain("BlockInput"));
        }

        [Test]
        public void WrongCoreInputValueTypeIsRejected()
        {
            NotSupportedException error = Assert.Throws<NotSupportedException>(
                                              (Action)(() => CoreCompatibilityChecks.ValidateCoreContracts(
                                                  typeof(CompatibleRenderer),
                                                  typeof(WidgetInputWithWrongMouseClickType),
                                                  new Version(0, 4, 2, 0)))) ??
                                          throw new AssertionException("Expected NotSupportedException.");
            Assert.That(error.InnerException, Is.TypeOf<MissingMemberException>());
            Assert.That(error.InnerException?.Message, Does.Contain("MouseLeftClick"));
        }

        [TestCase("2.3.3.0")]
        [TestCase("2.3.4.0")]
        public void MissingHarmonyUnpatchIsRejectedRegardlessOfVersion(string version)
        {
            NotSupportedException error = Assert.Throws<NotSupportedException>(
                                              (Action)(() => CoreCompatibilityChecks.ValidateHarmonyContracts(
                                                  typeof(IncompatibleHarmony),
                                                  typeof(CompatibleHarmonyMethod),
                                                  typeof(CompatiblePatches),
                                                  new Version(version)))) ??
                                          throw new AssertionException("Expected NotSupportedException.");
            Assert.That(error.InnerException, Is.TypeOf<MissingMethodException>());
            Assert.That(error.Message, Does.Contain(version));
        }

        [Test]
        public void HarmonyPatchOwnerCollectionIsRequired()
        {
            NotSupportedException error = Assert.Throws<NotSupportedException>(
                                              (Action)(() => CoreCompatibilityChecks.ValidateHarmonyContracts(
                                                  typeof(HarmonyWithMissingOwners),
                                                  typeof(CompatibleHarmonyMethod),
                                                  typeof(NoOwnersPatches),
                                                  new Version(2, 3, 3, 0)))) ??
                                          throw new AssertionException("Expected NotSupportedException.");
            Assert.That(error.InnerException, Is.TypeOf<MissingMemberException>());
        }

        [Test]
        public void MissingHarmonyMethodConstructorIsRejected()
        {
            NotSupportedException error = Assert.Throws<NotSupportedException>(
                                              (Action)(() => CoreCompatibilityChecks.ValidateHarmonyContracts(
                                                  typeof(HarmonyWithDifferentMethodConstructor),
                                                  typeof(HarmonyMethodWithoutMethodInfoConstructor),
                                                  typeof(CompatiblePatches),
                                                  new Version(2, 3, 3, 0)))) ??
                                          throw new AssertionException("Expected NotSupportedException.");
            Assert.That(error.InnerException, Is.TypeOf<MissingMethodException>());
            Assert.That(error.InnerException?.Message, Does.Contain(".ctor"));
        }

        [Test]
        public void WrongHarmonyFinalizerParameterTypeIsRejected()
        {
            NotSupportedException error = Assert.Throws<NotSupportedException>(
                                              (Action)(() => CoreCompatibilityChecks.ValidateHarmonyContracts(
                                                  typeof(HarmonyWithWrongFinalizerParameter),
                                                  typeof(CompatibleHarmonyMethod),
                                                  typeof(CompatiblePatches),
                                                  new Version(2, 3, 3, 0)))) ??
                                          throw new AssertionException("Expected NotSupportedException.");
            Assert.That(error.InnerException, Is.TypeOf<MissingMethodException>());
            Assert.That(error.InnerException?.Message, Does.Contain("Patch"));
        }

        [Test]
        public void HarmonyOwnersFieldIsRejected()
        {
            NotSupportedException error = Assert.Throws<NotSupportedException>(
                                              (Action)(() => CoreCompatibilityChecks.ValidateHarmonyContracts(
                                                  typeof(HarmonyWithOwnersField),
                                                  typeof(CompatibleHarmonyMethod),
                                                  typeof(OwnersFieldPatches),
                                                  new Version(2, 3, 3, 0)))) ??
                                          throw new AssertionException("Expected NotSupportedException.");
            Assert.That(error.InnerException, Is.TypeOf<MissingMemberException>());
            Assert.That(error.InnerException?.Message, Does.Contain("Owners"));
        }

        [Test]
        public void HarmonyOwnersWrongGetterTypeIsRejected()
        {
            NotSupportedException error = Assert.Throws<NotSupportedException>(
                                              (Action)(() => CoreCompatibilityChecks.ValidateHarmonyContracts(
                                                  typeof(HarmonyWithOwnersList),
                                                  typeof(CompatibleHarmonyMethod),
                                                  typeof(OwnersListPatches),
                                                  new Version(2, 3, 3, 0)))) ??
                                          throw new AssertionException("Expected NotSupportedException.");
            Assert.That(error.InnerException, Is.TypeOf<MissingMemberException>());
            Assert.That(error.InnerException?.Message, Does.Contain("Owners"));
        }

        private static IEnumerable<TestCaseData> RequiredFields()
        {
            yield return new TestCaseData("_keyInputBlockers", typeof(HashSet<string>));
            yield return new TestCaseData("_textInputActive", typeof(bool));
            yield return new TestCaseData("_textInputReleasePending", typeof(bool));
            yield return new TestCaseData("_panelZOrder", typeof(List<string>));
            yield return new TestCaseData(
                "_registeredPanels",
                typeof(Dictionary<string, ValueTuple<int, int, int, int>>));
        }

        [TestCaseSource(nameof(RequiredFields))]
        public void MatchingPrivateStaticFieldIsResolved(string name, Type fieldType)
        {
            FieldInfo field = CoreCompatibilityChecks.RequireField(typeof(CompatibleFields), name, fieldType);
            Assert.That(field.Name, Is.EqualTo(name));
            Assert.That(field.FieldType, Is.EqualTo(fieldType));
        }

        [Test]
        public void MissingFieldIsRejected()
        {
            Assert.That(
                (Action)(() =>
                {
                    CoreCompatibilityChecks.RequireField(typeof(CompatibleFields), "_absent", typeof(bool));
                }),
                Throws.TypeOf<MissingFieldException>());
        }

        [Test]
        public void DifferentFieldTypeIsRejected()
        {
            Assert.That(
                (Action)(() =>
                {
                    CoreCompatibilityChecks.RequireField(
                        typeof(CompatibleFields),
                        "_keyInputBlockers",
                        typeof(HashSet<object>));
                }),
                Throws.TypeOf<MissingFieldException>());
        }

        [Test]
        public void InstanceFieldIsRejected()
        {
            Assert.That(
                (Action)(() =>
                {
                    CoreCompatibilityChecks.RequireField(typeof(IncompatibleFields), "_instance", typeof(bool));
                }),
                Throws.TypeOf<MissingFieldException>());
        }

        [Test]
        public void PublicFieldIsRejected()
        {
            Assert.That(
                (Action)(() =>
                {
                    CoreCompatibilityChecks.RequireField(typeof(IncompatibleFields), "Public", typeof(bool));
                }),
                Throws.TypeOf<MissingFieldException>());
        }

        private static IEnumerable<TestCaseData> RequiredMethods()
        {
            yield return new TestCaseData("OnPreUpdateConsumeScroll", typeof(void), Type.EmptyTypes);
            yield return new TestCaseData(
                "PlayerUpdateScrollBlockPrefix",
                typeof(void),
                new[] { typeof(SyntheticPlayer) });
            yield return new TestCaseData("DoScrollingInInventoryPrefix", typeof(bool), Type.EmptyTypes);
        }

        [TestCaseSource(nameof(RequiredMethods))]
        public void MatchingPrivateStaticMethodIsResolved(string name, Type returnType, Type[] arguments)
        {
            MethodInfo method = CoreCompatibilityChecks.ResolveTarget(
                typeof(CompatibleMethods),
                name,
                returnType,
                arguments);
            Assert.That(method.Name, Is.EqualTo(name));
            Assert.That(method.ReturnType, Is.EqualTo(returnType));
        }

        [Test]
        public void MissingMethodIsRejected()
        {
            Assert.That(
                (Action)(() =>
                {
                    CoreCompatibilityChecks.ResolveTarget(
                        typeof(CompatibleMethods),
                        "Absent",
                        typeof(void),
                        Type.EmptyTypes);
                }),
                Throws.TypeOf<MissingMethodException>());
        }

        [Test]
        public void DifferentParametersAreRejected()
        {
            Assert.That(
                (Action)(() =>
                {
                    CoreCompatibilityChecks.ResolveTarget(
                        typeof(CompatibleMethods),
                        "PlayerUpdateScrollBlockPrefix",
                        typeof(void),
                        [typeof(object)]);
                }),
                Throws.TypeOf<MissingMethodException>());
        }

        [Test]
        public void DifferentReturnTypeIsRejected()
        {
            Assert.That(
                (Action)(() =>
                {
                    CoreCompatibilityChecks.ResolveTarget(
                        typeof(CompatibleMethods),
                        "DoScrollingInInventoryPrefix",
                        typeof(void),
                        Type.EmptyTypes);
                }),
                Throws.TypeOf<MissingMethodException>());
        }

        [Test]
        public void InstanceMethodIsRejected()
        {
            Assert.That(
                (Action)(() =>
                {
                    CoreCompatibilityChecks.ResolveTarget(
                        typeof(IncompatibleMethods),
                        "Instance",
                        typeof(void),
                        Type.EmptyTypes);
                }),
                Throws.TypeOf<MissingMethodException>());
        }

        [Test]
        public void PublicMethodIsRejected()
        {
            Assert.That(
                (Action)(() =>
                {
                    CoreCompatibilityChecks.ResolveTarget(
                        typeof(IncompatibleMethods),
                        "Public",
                        typeof(void),
                        Type.EmptyTypes);
                }),
                Throws.TypeOf<MissingMethodException>());
        }

        // Minimal public dependency facades. They exercise structural checks without loading Terraria.
        // Their methods intentionally exist only for reflection and are never called directly.
        // ReSharper disable UnusedMember.Local
        // ReSharper disable UnusedParameter.Local
        private static class CompatibleRenderer
        {
            public static void RegisterPanelDraw(string id, Action draw)
            {
            }

            public static void UnregisterPanelDraw(string id)
            {
            }

            public static void RegisterPanelBounds(string id, int x, int y, int width, int height)
            {
            }

            public static void UnregisterPanelBounds(string id)
            {
            }

            public static void BringToFront(string id)
            {
            }

            public static bool ShouldBlockForHigherPriorityPanel(string id) => false;
            public static bool IsMouseOverPanel(string id) => false;

            public static void RegisterKeyInputBlock(string id)
            {
            }

            public static void UnregisterKeyInputBlock(string id)
            {
            }
        }

        private static class ExtendedRenderer
        {
            public static void RegisterPanelDraw(string id, Action draw)
            {
            }

            // New overloads and unrelated operations must not invalidate the used signature.
            public static void RegisterPanelDraw(int id, Action draw)
            {
            }

            public static bool SupportsAdditionalFeature(string id) => true;

            public static void UnregisterPanelDraw(string id)
            {
            }

            public static void RegisterPanelBounds(string id, int x, int y, int width, int height)
            {
            }

            public static void UnregisterPanelBounds(string id)
            {
            }

            public static void BringToFront(string id)
            {
            }

            public static bool ShouldBlockForHigherPriorityPanel(string id) => false;
            public static bool IsMouseOverPanel(string id) => false;

            public static void RegisterKeyInputBlock(string id)
            {
            }

            public static void UnregisterKeyInputBlock(string id)
            {
            }
        }

        private static class RendererWithWrongPriorityReturn
        {
            public static void RegisterPanelDraw(string id, Action draw)
            {
            }

            public static void UnregisterPanelDraw(string id)
            {
            }

            public static void RegisterPanelBounds(string id, int x, int y, int width, int height)
            {
            }

            public static void UnregisterPanelBounds(string id)
            {
            }

            public static void BringToFront(string id)
            {
            }

            public static int ShouldBlockForHigherPriorityPanel(string id) => 0;
            public static bool IsMouseOverPanel(string id) => false;

            public static void RegisterKeyInputBlock(string id)
            {
            }

            public static void UnregisterKeyInputBlock(string id)
            {
            }
        }

        private static class RendererWithWrongRegistrationReturn
        {
            public static int RegisterPanelDraw(string id, Action draw) => 0;
        }

        private static class IncompatibleRenderer
        {
            // The draw registration required by the consumer is absent.
        }

        private static class CompatibleWidgetInput
        {
            public static bool BlockInput { get; set; }
            public static bool MouseLeftClick => false;
        }

        private static class WidgetInputWithFieldBlockInput
        {
            // Reflection-only field: the binary contract requires a property.
#pragma warning disable CS0649
            public static bool BlockInput;
#pragma warning restore CS0649
            public static bool MouseLeftClick => false;
        }

        private static class WidgetInputWithFieldMouseClick
        {
#pragma warning disable CS0649
            public static bool MouseLeftClick;
#pragma warning restore CS0649
            public static bool BlockInput { get; set; }
        }

        private static class IncompatibleWidgetInput
        {
            public static bool BlockInput => false;
            public static bool MouseLeftClick => false;
        }

        private static class ReadonlyWidgetInput
        {
            // Intentionally reflection-only: the validator must reject a readonly field.
#pragma warning disable CS0414 // Field is assigned but its value is never used
            public static readonly bool BlockInput = false;
#pragma warning restore CS0414 // Field is assigned but its value is never used
            public static bool MouseLeftClick => false;
        }

        private static class WidgetInputWithWrongMouseClickType
        {
            public static bool BlockInput { get; set; }
            public static int MouseLeftClick => 0;
        }

        private sealed class CompatibleHarmony
        {
            public CompatibleHarmony(string id)
            {
            }

            public static CompatiblePatches GetPatchInfo(MethodBase method) => null;

            public MethodInfo Patch(
                MethodBase original,
                CompatibleHarmonyMethod prefix = null,
                CompatibleHarmonyMethod postfix = null,
                CompatibleHarmonyMethod transpiler = null,
                CompatibleHarmonyMethod finalizer = null) =>
                null;

            public void Unpatch(MethodBase original, MethodInfo patch)
            {
            }
        }

        private sealed class IncompatibleHarmony
        {
            public IncompatibleHarmony(string id)
            {
            }

            public static CompatiblePatches GetPatchInfo(MethodBase method) => null;

            public MethodInfo Patch(
                MethodBase original,
                CompatibleHarmonyMethod prefix = null,
                CompatibleHarmonyMethod postfix = null,
                CompatibleHarmonyMethod transpiler = null,
                CompatibleHarmonyMethod finalizer = null) =>
                null;
            // The instance Unpatch(MethodBase, MethodInfo) operation is absent.
        }

        private sealed class HarmonyWithOwnersField
        {
            public HarmonyWithOwnersField(string id)
            {
            }

            public static OwnersFieldPatches GetPatchInfo(MethodBase method) => null;

            public MethodInfo Patch(
                MethodBase original,
                CompatibleHarmonyMethod prefix = null,
                CompatibleHarmonyMethod postfix = null,
                CompatibleHarmonyMethod transpiler = null,
                CompatibleHarmonyMethod finalizer = null) =>
                null;

            public void Unpatch(MethodBase original, MethodInfo patch)
            {
            }
        }

        private sealed class HarmonyWithOwnersList
        {
            public HarmonyWithOwnersList(string id)
            {
            }

            public static OwnersListPatches GetPatchInfo(MethodBase method) => null;

            public MethodInfo Patch(
                MethodBase original,
                CompatibleHarmonyMethod prefix = null,
                CompatibleHarmonyMethod postfix = null,
                CompatibleHarmonyMethod transpiler = null,
                CompatibleHarmonyMethod finalizer = null) =>
                null;

            public void Unpatch(MethodBase original, MethodInfo patch)
            {
            }
        }

        private sealed class HarmonyWithMissingOwners
        {
            public HarmonyWithMissingOwners(string id)
            {
            }

            public static NoOwnersPatches GetPatchInfo(MethodBase method) => null;

            public MethodInfo Patch(
                MethodBase original,
                CompatibleHarmonyMethod prefix = null,
                CompatibleHarmonyMethod postfix = null,
                CompatibleHarmonyMethod transpiler = null,
                CompatibleHarmonyMethod finalizer = null) =>
                null;

            public void Unpatch(MethodBase original, MethodInfo patch)
            {
            }
        }

        private sealed class HarmonyWithDifferentMethodConstructor
        {
            public HarmonyWithDifferentMethodConstructor(string id)
            {
            }

            public static CompatiblePatches GetPatchInfo(MethodBase method) => null;

            public MethodInfo Patch(
                MethodBase original,
                HarmonyMethodWithoutMethodInfoConstructor prefix = null,
                HarmonyMethodWithoutMethodInfoConstructor postfix = null,
                HarmonyMethodWithoutMethodInfoConstructor transpiler = null,
                HarmonyMethodWithoutMethodInfoConstructor finalizer = null) =>
                null;

            public void Unpatch(MethodBase original, MethodInfo patch)
            {
            }
        }

        private sealed class HarmonyWithWrongFinalizerParameter
        {
            public HarmonyWithWrongFinalizerParameter(string id)
            {
            }

            public static CompatiblePatches GetPatchInfo(MethodBase method) => null;

            public MethodInfo Patch(
                MethodBase original,
                CompatibleHarmonyMethod prefix = null,
                CompatibleHarmonyMethod postfix = null,
                CompatibleHarmonyMethod transpiler = null,
                object finalizer = null) =>
                null;

            public void Unpatch(MethodBase original, MethodInfo patch)
            {
            }
        }

        private sealed class HarmonyMethodWithoutMethodInfoConstructor
        {
            public HarmonyMethodWithoutMethodInfoConstructor(string name)
            {
            }
        }

        private sealed class CompatibleHarmonyMethod
        {
            public CompatibleHarmonyMethod(MethodInfo method)
            {
            }
        }

        private sealed class CompatiblePatches
        {
            public ReadOnlyCollection<string> Owners { get; } = new List<string>().AsReadOnly();
        }

        private sealed class OwnersFieldPatches
        {
            // Reflection-only fixture: a field does not provide Harmony's get_Owners method.
#pragma warning disable CS0414
            public readonly ReadOnlyCollection<string> Owners = new List<string>().AsReadOnly();
#pragma warning restore CS0414
        }

        private sealed class OwnersListPatches
        {
            public List<string> Owners { get; } = new List<string>();
        }

        private sealed class NoOwnersPatches
        {
        }

        // These fixture members are intentionally resolved by reflection, not by direct calls.
        // ReSharper disable NotAccessedField.Local
#pragma warning disable CS0414
        private sealed class CompatibleFields
        {
            private static readonly HashSet<string> _keyInputBlockers = new HashSet<string>();
            private static readonly bool _textInputActive = false;
            private static readonly bool _textInputReleasePending = false;
            private static readonly List<string> _panelZOrder = new List<string>();

            private static readonly Dictionary<string, ValueTuple<int, int, int, int>> _registeredPanels =
                new Dictionary<string, ValueTuple<int, int, int, int>>();
        }

        private sealed class IncompatibleFields
        {
            public static readonly bool Public = false;
            private readonly bool _instance = false;
        }

        private sealed class SyntheticPlayer
        {
        }

        private sealed class CompatibleMethods
        {
            private static void OnPreUpdateConsumeScroll()
            {
            }

            private static void PlayerUpdateScrollBlockPrefix(SyntheticPlayer player)
            {
            }

            private static bool DoScrollingInInventoryPrefix() => false;
        }

        private sealed class IncompatibleMethods
        {
            private void Instance()
            {
            }

            public static void Public()
            {
            }
        }
#pragma warning restore CS0414
        // ReSharper restore NotAccessedField.Local
        // ReSharper restore UnusedParameter.Local
        // ReSharper restore UnusedMember.Local
    }
}