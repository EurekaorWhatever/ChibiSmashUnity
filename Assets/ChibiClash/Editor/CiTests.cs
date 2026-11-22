using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace ChibiClash.EditorTools
{
    /// <summary>
    /// Runs every EditMode test synchronously in batch mode and exits with 0 (all passed) or 1. Editor-only: it is not
    /// part of the game. Used by .github/workflows/tests.yml through game-ci/unity-builder (buildMethod), the same
    /// action the web build uses successfully. game-ci/unity-test-runner never started Unity on this project's runner
    /// (runs 19-31, "Unity wrote no editor log").
    /// </summary>
    /// <example><code>Unity -batchmode -projectPath . -executeMethod ChibiClash.EditorTools.CiTests.RunEditMode -ciTestsOut artifacts</code></example>
    public static class CiTests
    {
        /// <summary>Entry point for batch mode / game-ci. Writes results.xml-style summary files to <c>-ciTestsOut</c>.</summary>
        public static void RunEditMode()
        {
            string outDir = WebBuild.ArgValue("-ciTestsOut") ?? "artifacts";
            var collector = new Collector();
            int code = 1;
            try
            {
                Directory.CreateDirectory(outDir);
                var api = ScriptableObject.CreateInstance<TestRunnerApi>();
                api.RegisterCallbacks(collector);
                // EditMode only, synchronous: the project has no [UnityTest] coroutine tests in EditMode.
                api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode }) { runSynchronously = true });
                code = collector.Total > 0 && collector.Failed == 0 ? 0 : 1;
                File.WriteAllText(Path.Combine(outDir, "editmode-summary.txt"), collector.Summary());
                File.WriteAllText(Path.Combine(outDir, "editmode-failures.txt"), collector.Failures.ToString());
                Debug.Log("[CiTests] " + collector.Summary());
            }
            catch (Exception e)
            {
                Debug.LogError("[CiTests] failed to run: " + e);
                File.WriteAllText(Path.Combine(outDir, "editmode-failures.txt"), "Runner error: " + e.Message);
            }
            EditorApplication.Exit(code);
        }

        sealed class Collector : ICallbacks
        {
            public int Total, Passed, Failed, Skipped;
            public readonly StringBuilder Failures = new StringBuilder();

            public string Summary() => "EditMode tests: " + Total + " total, " + Passed + " passed, " + Failed + " failed, " + Skipped + " skipped";

            public void RunStarted(ITestAdaptor testsToRun) { }
            public void RunFinished(ITestResultAdaptor result) { }
            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.Test.IsSuite) return;
                Total++;
                switch (result.TestStatus)
                {
                    case TestStatus.Passed: Passed++; break;
                    case TestStatus.Failed:
                        Failed++;
                        // One line per failure, so the workflow can publish each as a public annotation.
                        Failures.Append(result.Test.FullName).Append(" :: ")
                                .Append((result.Message ?? "").Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
                        break;
                    default: Skipped++; break;
                }
            }
        }
    }
}
