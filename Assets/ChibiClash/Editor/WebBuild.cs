using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace ChibiClash.EditorTools
{
    /// <summary>
    /// Builds the browser (WebGL) version of the game for GitHub Pages. Editor-only: it is not part of the game.
    /// Used by .github/workflows/web-build.yml through game-ci (buildMethod), and from the menu for a local build.
    /// </summary>
    /// <remarks>
    /// GitHub Pages cannot send the Content-Encoding headers that Unity's default Brotli build needs. So this build uses
    /// Gzip with the decompression fallback, which the loader unpacks itself in any browser. These two settings are
    /// applied only while building and restored afterwards, so ProjectSettings/ProjectSettings.asset is left untouched.
    /// Scenes are the enabled entries of the Build Settings (EditorBuildSettings), the same list a normal build uses.
    /// </remarks>
    public static class WebBuild
    {
        /// <summary>Output folder of a local build (the folder that holds index.html).</summary>
        public const string DefaultOutput = "build/WebGL/WebGL";

        const string MenuPath = "Chibi Clash/Build Web Version (GitHub Pages)";

        /// <summary>Menu entry: local WebGL build into <see cref="DefaultOutput"/>.</summary>
        [MenuItem(MenuPath, false, 200)]
        public static void BuildFromMenu() => Build(DefaultOutput, exitOnFinish: false);

        /// <summary>
        /// Entry point for batch mode and game-ci. Reads the output folder from <c>-customBuildPath</c>, as game-ci
        /// passes it, and falls back to <see cref="DefaultOutput"/>. Exits with code 0 on success and 1 on failure.
        /// </summary>
        /// <example><code>Unity -batchmode -projectPath . -buildTarget WebGL -executeMethod ChibiClash.EditorTools.WebBuild.BuildFromCommandLine -customBuildPath build/WebGL/WebGL</code></example>
        public static void BuildFromCommandLine() => Build(ArgValue("-customBuildPath") ?? DefaultOutput, exitOnFinish: true);

        /// <summary>Value after <paramref name="name"/> on the command line, or null when absent.</summary>
        public static string ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        static void Build(string output, bool exitOnFinish)
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            var oldCompression = PlayerSettings.WebGL.compressionFormat;
            bool oldFallback = PlayerSettings.WebGL.decompressionFallback;
            bool ok = false;
            try
            {
                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
                PlayerSettings.WebGL.decompressionFallback = true;
                Directory.CreateDirectory(output);
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = output,
                    target = BuildTarget.WebGL,
                    options = BuildOptions.None,
                });
                ok = report.summary.result == BuildResult.Succeeded;
                Debug.Log("[WebBuild] " + report.summary.result + ": " + scenes.Length + " scene(s), " +
                          (report.summary.totalSize / (1024 * 1024)) + " MB -> " + output);
                if (ok) File.WriteAllText(Path.Combine(output, ".nojekyll"), "");   // serve files starting with '_' as-is
            }
            catch (Exception e)
            {
                Debug.LogError("[WebBuild] failed: " + e);
            }
            finally
            {
                PlayerSettings.WebGL.compressionFormat = oldCompression;      // leave the project settings as they were
                PlayerSettings.WebGL.decompressionFallback = oldFallback;
            }
            if (exitOnFinish) EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
