using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace ChibiClash.EditorTools
{
    /// <summary>
    /// Deploys the name server (Cloud Code JS script <c>Assets/ChibiClash/CloudCode/Chibi_Clash.js</c>, dashboard name
    /// "Chibi_Clash", which must match <see cref="Netcode.ChibiNameRegistry.ScriptName"/>) to the environment selected in
    /// the Deployment window (Window > Services > Deployment; normally "production"), with the editor's Unity sign-in.
    /// Editor-only; not part of the game. Same as selecting the file in the Deployment window and pressing Deploy.
    /// </summary>
    /// <remarks>
    /// The Deployment API assembly (Unity.Services.DeploymentApi, com.unity.services.deployment.api) is not
    /// auto-referenced, so it is reached by reflection: Deployments.Instance.DeploymentWindow.GetFromFiles / Deploy.
    /// </remarks>
    public static class CloudCodeDeploy
    {
        /// <summary>The script asset; its file name is the Cloud Code script name.</summary>
        public const string ScriptPath = "Assets/ChibiClash/CloudCode/Chibi_Clash.js";

        const string DeploymentsType = "Unity.Services.DeploymentApi.Editor.Deployments, Unity.Services.DeploymentApi";

        static object Prop(object o, string name) =>
            o?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)?.GetValue(o);

        /// <summary>One line describing the deploy target (project id, environment, whether the Deployment window API is ready).</summary>
        public static string Target()
        {
            var t = Type.GetType(DeploymentsType);
            if (t == null) return "Deployment API not installed";
            var d = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            return "project=" + (Prop(Prop(d, "ProjectIdProvider"), "ProjectId") ?? "?") +
                   " environment=" + (Prop(Prop(d, "EnvironmentProvider"), "Current") ?? "?") +
                   " api=" + (Prop(d, "DeploymentWindow") != null ? "ready" : "not ready");
        }

        /// <summary>Deploy the name server script; the outcome is logged ("[CloudCodeDeploy] ...").</summary>
        [MenuItem("Chibi Clash/Deploy Name Server (Cloud Code)")]
        public static async void DeployNames()
        {
            try
            {
                Debug.Log("[CloudCodeDeploy] " + Target());
                var t = Type.GetType(DeploymentsType);
                var d = t?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var w = Prop(d, "DeploymentWindow");
                if (w == null) { Debug.LogWarning("[CloudCodeDeploy] Deployment API not ready. Open Window > Services > Deployment once, then retry."); return; }

                // List<IDeploymentItem> GetFromFiles(IReadOnlyList<string> filePaths)
                var getFromFiles = FindMethod(w, "GetFromFiles", 1);
                var found = getFromFiles.Invoke(w, new object[] { new List<string> { ScriptPath } }) as IList;
                if (found == null || found.Count == 0 || found[0] == null) { Debug.LogWarning("[CloudCodeDeploy] No deployment item for " + ScriptPath); return; }

                // Task<DeploymentResult<IDeploymentItem>> Deploy(IReadOnlyList<IDeploymentItem> items, CancellationToken token)
                var deploy = FindMethod(w, "Deploy", 2);
                var task = (Task)deploy.Invoke(w, new object[] { found, default(System.Threading.CancellationToken) });
                await task;
                foreach (var it in found)
                {
                    var status = Prop(it, "Status");
                    Debug.Log("[CloudCodeDeploy] " + Prop(it, "Name") + ": " + Prop(status, "Message") + " " + Prop(status, "MessageDetail"));
                }
                var res = task.GetType().GetProperty("Result")?.GetValue(task);
                var deployed = Prop(res, "Deployed") as IList;
                Debug.Log("[CloudCodeDeploy] deployed=" + (deployed != null ? deployed.Count : 0) + " of " + found.Count);
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                Debug.LogWarning("[CloudCodeDeploy] failed: " + inner.GetType().Name + ": " + inner.Message);
            }
        }

        static MethodInfo FindMethod(object target, string name, int paramCount)
        {
            foreach (var t in new[] { target.GetType() }.Concat(target.GetType().GetInterfaces()))
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (m.Name == name && m.GetParameters().Length == paramCount) return m;
            throw new MissingMethodException(target.GetType().Name, name);
        }

        static IEnumerable<Type> Concat(this Type[] a, Type[] b)
        {
            foreach (var x in a) yield return x;
            foreach (var x in b) yield return x;
        }
    }
}
