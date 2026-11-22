// Editor tooling (not a test): saves every Test Runner run to Temp/LastTestResults.xml so automation can read PlayMode
// results. A callback registered from outside the project is lost when PlayMode tests reload the scripting domain;
// this one is re-registered on every load ([InitializeOnLoad]).
using System.IO;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace ChibiClash.Tests
{
    /// <summary>Writes the result of each finished test run (EditMode or PlayMode) to Temp/LastTestResults.xml.</summary>
    [InitializeOnLoad]
    static class LastTestResultsWriter
    {
        /// <summary>Where the last run is written (project-relative, inside the git-ignored Temp folder).</summary>
        public const string ResultPath = "Temp/LastTestResults.xml";

        static LastTestResultsWriter()
        {
            ScriptableObject.CreateInstance<TestRunnerApi>().RegisterCallbacks(new Writer());
        }

        sealed class Writer : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }
            public void RunFinished(ITestResultAdaptor result)
            {
                try { TestRunnerApi.SaveResultToFile(result, Path.GetFullPath(ResultPath)); }
                catch (System.Exception e) { Debug.LogWarning("LastTestResultsWriter: could not write " + ResultPath + " (" + e.Message + ")"); }
            }
        }
    }
}
