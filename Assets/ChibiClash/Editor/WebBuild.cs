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

        /// <summary>
        /// Largest texture side in the web build only (Build Settings "Asset Import Overrides > Max Texture Size").
        /// At full size the download was ~530 MB (2026-11-18), about 80% character animation sheets 3840 px wide, and
        /// every byte must arrive before a Unity web build can start. Capping at 2048 cuts it to roughly a third, at the
        /// cost of softer art in the browser. The PC build and the texture files/.meta are not affected.
        /// </summary>
        public const int WebMaxTextureSize = 2048;

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
            int oldMaxTexture = EditorUserBuildSettings.overrideMaxTextureSize;
            bool ok = false;
            try
            {
                if (OnCiRunner()) CrunchGameTexturesForWeb();   // CI copy only, see CrunchGameTexturesForWeb
                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
                PlayerSettings.WebGL.decompressionFallback = true;
                EditorUserBuildSettings.overrideMaxTextureSize = WebMaxTextureSize;   // web download size, see above
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
                EditorUserBuildSettings.overrideMaxTextureSize = oldMaxTexture;
            }
            if (exitOnFinish) EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>Folder whose textures are crunched for the web build (everything the game loads at runtime).</summary>
        const string GameTextureRoot = "Assets/ChibiClash/Resources";

        /// <summary>Crunch quality 0..100 for the web build: 50 is Unity's default, artifacts are hard to spot on sprites.</summary>
        const int WebCrunchQuality = 50;

        /// <summary>Command-line flag passed only by .github/workflows/web-build.yml (game-ci customParameters).</summary>
        public const string CiCrunchFlag = "-chibiWebCrunch";

        /// <summary>
        /// True only when the build was started with <see cref="CiCrunchFlag"/>, which only the GitHub workflow passes.
        /// Its checkout is thrown away after the build. The editor menu and a plain batch build never pass it, so
        /// <see cref="CrunchGameTexturesForWeb"/> cannot change files in a working copy.
        /// </summary>
        public static bool OnCiRunner() =>
            Environment.GetCommandLineArgs().Any(a => string.Equals(a, CiCrunchFlag, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Web download size: gives every game texture a WebGL override with Crunch compression (DXT5 Crunched with alpha,
        /// DXT1 Crunched without). The download was still 268 MB after the 2048 px cap (2026-11-22), almost all of it
        /// textures. Crunched DXT is typically 3-5x smaller on disk and decompresses on the GPU format at load.
        /// It writes .meta importer settings, so it runs ONLY on the CI runner (<see cref="OnCiRunner"/>). The repo,
        /// the PC build and the editor on any PC keep the original settings.
        /// </summary>
        static void CrunchGameTexturesForWeb()
        {
            var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { GameTextureRoot });
            int changed = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!(AssetImporter.GetAtPath(path) is TextureImporter ti)) continue;
                    var web = ti.GetPlatformTextureSettings("WebGL");
                    web.overridden = true;
                    web.maxTextureSize = Mathf.Min(ti.maxTextureSize, WebMaxTextureSize);
                    web.format = ti.DoesSourceTextureHaveAlpha() ? TextureImporterFormat.DXT5Crunched : TextureImporterFormat.DXT1Crunched;
                    web.crunchedCompression = true;
                    web.compressionQuality = WebCrunchQuality;
                    ti.SetPlatformTextureSettings(web);
                    ti.SaveAndReimport();
                    changed++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();   // runs the queued reimports in one pass
            }
            Debug.Log("[WebBuild] crunched " + changed + " texture(s) under " + GameTextureRoot + " for WebGL (CI copy only)");
        }
    }
}
