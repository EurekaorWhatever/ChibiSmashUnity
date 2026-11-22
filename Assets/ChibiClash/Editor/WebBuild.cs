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
            var info = new System.Text.StringBuilder();
            info.AppendLine("built: " + DateTime.UtcNow.ToString("u") + ", Unity " + Application.unityVersion);
            info.AppendLine("crunch flag: " + OnCiRunner());   // never write the raw command line: this file is public
            try
            {
                if (OnCiRunner()) info.AppendLine(CrunchGameTexturesForWeb());   // CI copy only, see CrunchGameTexturesForWeb
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
                // Public build diagnostics next to index.html (the Actions log needs a GitHub sign-in to read).
                info.AppendLine("result: " + report.summary.result + ", " + (report.summary.totalSize / (1024 * 1024)) + " MB");
                if (ok) File.WriteAllText(Path.Combine(output, "build-info.txt"), info.ToString());
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
        /// Web download size: every game texture gets mipmaps off, its full resolution and a WebGL override with Crunch
        /// compression (DXT5 Crunched with alpha, DXT1 Crunched without). Probe 2026-11-22: the web build shipped all
        /// 367 textures as RGBA32 (1.1 GB unpacked, 267 MB download), because mipmapped non-power-of-two textures are not
        /// block-compressed. Without mipmaps, crunched: e.g. a 3840x768 sheet 15.7 MB -> 217 KB, a 3840x2160 stage
        /// layer 44 MB -> 326-574 KB.
        /// It writes .meta importer settings, so it runs ONLY on the CI runner (<see cref="OnCiRunner"/>). The repo,
        /// the PC build and the editor on any PC keep the original settings.
        /// </summary>
        static string CrunchGameTexturesForWeb()
        {
            var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { GameTextureRoot });
            int changed = 0;
            string sample = "";
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!(AssetImporter.GetAtPath(path) is TextureImporter ti)) continue;
                    var web = ti.GetPlatformTextureSettings("WebGL");
                    web.overridden = true;
                    // Mipmaps off: Unity does not block-compress these non-power-of-two textures with mipmaps, so
                    // with mipmaps on every one shipped as RGBA32 (1.1 GB, 2026-11-22 probe). Full resolution: a
                    // downscale (e.g. 768 -> 410 px) breaks the multiple-of-4 rule and also falls back to RGBA32.
                    ti.mipmapEnabled = false;
                    web.maxTextureSize = ti.maxTextureSize;
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
            // Read one importer back after the reimport, to prove the override stuck.
            if (guids.Length > 0 && AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guids[0])) is TextureImporter check)
            {
                var s = check.GetPlatformTextureSettings("WebGL");
                sample = AssetDatabase.GUIDToAssetPath(guids[0]) + " -> overridden=" + s.overridden + " format=" + s.format +
                         " crunch=" + s.crunchedCompression + " max=" + s.maxTextureSize;
            }
            string msg = "crunch: " + guids.Length + " texture(s) found, " + changed + " changed under " + GameTextureRoot +
                         "\nsample: " + sample;
            Debug.Log("[WebBuild] " + msg);
            return msg;
        }
    }
}
