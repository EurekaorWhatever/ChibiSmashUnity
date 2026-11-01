using System.Collections;
using UnityEngine;

namespace WeaponBrawl
{
    /// <summary>Fire-and-forget visual effects: bursts, rings, damage popups, lightning and camera shake.</summary>
    public class Fx : MonoBehaviour
    {
        public static bool ShakeEnabled = true;

        static Fx instance;

        Camera cam;
        Vector3 camRest;
        float shakeTime, shakeStrength;
        bool shaking;

        static Fx I
        {
            get
            {
                if (instance == null) instance = new GameObject("[Fx]").AddComponent<Fx>();
                return instance;
            }
        }

        public static void Burst(Vector2 pos, Color color, float size = 1f, float duration = 0.25f) =>
            I.StartCoroutine(I.Expand(SpriteFactory.Circle, pos, color, size * 0.3f, size, duration));

        public static void Ring(Vector2 pos, Color color, float size = 2f, float duration = 0.35f) =>
            I.StartCoroutine(I.Expand(SpriteFactory.Ring, pos, color, size * 0.2f, size, duration));

        public static void Popup(Vector2 pos, string text, Color color, float scale = 1f) =>
            I.StartCoroutine(I.PopupRoutine(pos, text, color, scale));

        public static void Bolt(Vector2 from, Vector2 to, Color color, float duration = 0.2f) =>
            I.StartCoroutine(I.BoltRoutine(from, to, color, duration));

        public static void Shake(float strength, float duration)
        {
            if (!ShakeEnabled) return;
            var fx = I;
            fx.shakeStrength = Mathf.Max(fx.shakeStrength, strength);
            fx.shakeTime = Mathf.Max(fx.shakeTime, duration);
        }

        IEnumerator Expand(Sprite sprite, Vector2 pos, Color color, float from, float to, float duration)
        {
            var sr = SpriteFactory.Part(null, "fx", sprite, color, pos, Vector2.one * from, 50);
            for (float t = 0f; t < 1f; t += Time.deltaTime / duration)
            {
                sr.transform.localScale = Vector3.one * Mathf.Lerp(from, to, Ease.OutCubic(t));
                var c = color;
                c.a = color.a * (1f - t);
                sr.color = c;
                yield return null;
            }
            Destroy(sr.gameObject);
        }

        IEnumerator PopupRoutine(Vector2 pos, string text, Color color, float scale)
        {
            var go = new GameObject("popup");
            Vector3 start = pos + new Vector2(Random.Range(-0.25f, 0.25f), 0f);
            go.transform.position = start;
            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.font = UIFactory.Font;
            tm.fontSize = 64;
            tm.fontStyle = FontStyle.Bold;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = color;
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = UIFactory.Font.material;
            mr.sortingOrder = 100;

            const float duration = 0.75f;
            for (float t = 0f; t < 1f; t += Time.deltaTime / duration)
            {
                go.transform.position = start + Vector3.up * Ease.OutCubic(t) * 0.8f;
                float pop = t < 0.15f ? Mathf.Lerp(1.5f, 1f, t / 0.15f) : 1f;
                tm.characterSize = 0.06f * scale * pop;
                var c = color;
                c.a = t < 0.6f ? 1f : 1f - (t - 0.6f) / 0.4f;
                tm.color = c;
                yield return null;
            }
            Destroy(go);
        }

        IEnumerator BoltRoutine(Vector2 from, Vector2 to, Color color, float duration)
        {
            var go = new GameObject("bolt");
            var lr = go.AddComponent<LineRenderer>();
            lr.material = SpriteFactory.LineMaterial;
            lr.sortingOrder = 60;
            lr.positionCount = 8;
            lr.startWidth = 0.2f;
            lr.endWidth = 0.08f;
            for (int i = 0; i < 8; i++)
            {
                var p = Vector2.Lerp(from, to, i / 7f);
                if (i > 0 && i < 7) p.x += Random.Range(-0.3f, 0.3f);
                lr.SetPosition(i, p);
            }
            for (float t = 0f; t < 1f; t += Time.deltaTime / duration)
            {
                var c = Color.Lerp(Color.white, color, t);
                c.a = 1f - t;
                lr.startColor = lr.endColor = c;
                yield return null;
            }
            Destroy(go);
        }

        void LateUpdate()
        {
            if (cam == null) cam = Camera.main;
            if (cam == null) return;
            if (shakeTime > 0f)
            {
                if (!shaking)
                {
                    camRest = cam.transform.position;
                    shaking = true;
                }
                shakeTime -= Time.unscaledDeltaTime;
                cam.transform.position = camRest + (Vector3)(Random.insideUnitCircle * shakeStrength);
            }
            else if (shaking)
            {
                cam.transform.position = camRest;
                shaking = false;
                shakeStrength = 0f;
            }
        }
    }
}
