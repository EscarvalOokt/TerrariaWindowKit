using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Escarval.TerrariaWindowKit.TestHost
{
    [TestFixture]
    [NonParallelizable]
    public sealed class SharedContractTests
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (string scenario in ContractSuite.ScenarioNames)
            {
                yield return new TestCaseData(scenario, false).SetName(scenario + " [A, B]");
                yield return new TestCaseData(scenario, true).SetName(scenario + " [B, A]");
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void SharedContracts(string scenario, bool reverse)
        {
            string assemblyPath = typeof(ScenarioRunner).Assembly.Location;
            var setup = new AppDomainSetup
            {
                ApplicationBase = Path.GetDirectoryName(assemblyPath)
            };
            var domain = AppDomain.CreateDomain("TerrariaWindowKit contract: " + scenario, null, setup);
            try
            {
                string runnerTypeName = typeof(ScenarioRunner).FullName ??
                                        throw new InvalidOperationException("Scenario runner type has no full name.");
                if (domain.CreateInstanceFromAndUnwrap(assemblyPath, runnerTypeName) is not ScenarioRunner runner)
                    throw new InvalidOperationException("Could not create the isolated scenario runner.");
                string failure = runner.Run(reverse, scenario);
                Assert.That(failure, Is.Null, failure);
            }
            finally
            {
                AppDomain.Unload(domain);
            }
        }
    }

    // Each case needs fresh static coordinator and Harmony state for its provider order.
    // Synchronous remoting keeps initialization, the scenario, and cleanup on one UI thread.
    // MarshalByRefObject prevents the runner itself from being copied into the NUnit domain.
    // ReSharper disable once ClassNeverInstantiated.Global
    public sealed class ScenarioRunner : MarshalByRefObject
    {
        public string Run(bool reverse, string scenario) => ContractSuite.Run(reverse, scenario);
    }
}