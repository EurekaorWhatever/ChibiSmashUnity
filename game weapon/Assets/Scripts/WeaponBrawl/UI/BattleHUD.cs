using UnityEngine;
using UnityEngine.UI;

namespace WeaponBrawl
{
    /// <summary>Smash-style HUD from the design doc: fighter cards in the top corners (portrait, health %, ult meter) and a match timer.</summary>
    public class BattleHUD : MonoBehaviour
    {
        static readonly Color UltColor = new Color(1f, 0.78f, 0.25f);

        class FighterCard
        {
            public Fighter fighter;
            public Image portrait, hpFill, ultFill;
            public Text initial, title, percent, passive, status;
            public float shownHp = 1f;
        }

        FighterCard left, right;
        Text timer, banner;
        float timeLeft, bannerUntil;
        bool running;

        public static BattleHUD Create(Canvas canvas)
        {
            var rt = UIFactory.Node("HUD", canvas.transform).Stretch();
            var hud = rt.gameObject.AddComponent<BattleHUD>();
            hud.left = BuildCard(rt, true);
            hud.right = BuildCard(rt, false);

            var timerBg = UIFactory.Panel(rt, "TimerBg", new Color(0f, 0f, 0f, 0.55f));
            timerBg.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -30f), new Vector2(220f, 80f));
            hud.timer = UIFactory.Label(timerBg.transform, "Timer", "3:00", 52, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            hud.timer.rectTransform.Stretch();

            hud.banner = UIFactory.Label(rt, "Banner", "", 110, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            hud.banner.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 150f), new Vector2(1400f, 160f));
            hud.banner.gameObject.AddComponent<Outline>().effectDistance = new Vector2(4f, -4f);
            return hud;
        }

        static FighterCard BuildCard(RectTransform parent, bool leftSide)
        {
            float sx = leftSide ? 1f : -1f;
            var anchor = leftSide ? new Vector2(0f, 1f) : new Vector2(1f, 1f);
            var align = leftSide ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
            var card = new FighterCard();

            var root = UIFactory.Panel(parent, leftSide ? "P1Card" : "P2Card", new Color(0f, 0f, 0f, 0.55f));
            root.rectTransform.Place(anchor, anchor, new Vector2(30f * sx, -30f), new Vector2(580f, 170f));

            var side = leftSide ? new Vector2(0f, 0.5f) : new Vector2(1f, 0.5f);
            card.portrait = UIFactory.Panel(root.transform, "Portrait", Color.white);
            card.portrait.rectTransform.Place(side, side, new Vector2(20f * sx, 0f), new Vector2(130f, 130f));
            card.initial = UIFactory.Label(card.portrait.transform, "Initial", "", 70, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            card.initial.rectTransform.Stretch();
            card.initial.gameObject.AddComponent<Shadow>().effectDistance = new Vector2(3f, -3f);

            card.title = Row(root.transform, anchor, sx, -14f, 40f, 28, align, FontStyle.Bold);
            card.percent = Row(root.transform, anchor, sx, -8f, 52f, 46, leftSide ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft, FontStyle.Bold);
            card.passive = Row(root.transform, anchor, sx, -56f, 28f, 20, align, FontStyle.Normal);
            card.passive.color = new Color(0.75f, 0.78f, 0.85f);

            card.hpFill = Bar(root.transform, anchor, sx, -92f, 24f);
            card.ultFill = Bar(root.transform, anchor, sx, -124f, 12f);
            card.ultFill.color = UltColor;
            card.status = Row(root.transform, anchor, sx, -140f, 24f, 18, align, FontStyle.Bold);
            card.status.color = new Color(1f, 0.85f, 0.5f);

            if (!leftSide)
            {
                card.hpFill.fillOrigin = (int)Image.OriginHorizontal.Right;
                card.ultFill.fillOrigin = (int)Image.OriginHorizontal.Right;
            }
            return card;
        }

        static Text Row(Transform parent, Vector2 anchor, float sx, float y, float height, int size, TextAnchor align, FontStyle style)
        {
            var t = UIFactory.Label(parent, "Text", "", size, Color.white, align, style);
            t.rectTransform.Place(anchor, anchor, new Vector2(170f * sx, y), new Vector2(390f, height));
            return t;
        }

        static Image Bar(Transform parent, Vector2 anchor, float sx, float y, float height)
        {
            var bg = UIFactory.Panel(parent, "BarBg", new Color(1f, 1f, 1f, 0.12f), SpriteFactory.Square);
            bg.rectTransform.Place(anchor, anchor, new Vector2(170f * sx, y), new Vector2(390f, height));
            var fill = UIFactory.Panel(bg.transform, "Fill", Color.white, SpriteFactory.Square);
            fill.rectTransform.Stretch();
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            return fill;
        }

        public void Bind(Fighter player, Fighter opponent, float matchSeconds)
        {
            Assign(left, player);
            Assign(right, opponent);
            timeLeft = matchSeconds;
            running = true;
            player.Died += OnDied;
            opponent.Died += OnDied;
            ShowBanner("FIGHT!", 1.2f);
        }

        void Assign(FighterCard card, Fighter fighter)
        {
            card.fighter = fighter;
            card.shownHp = 1f;
            card.portrait.color = fighter.Definition.color;
            card.initial.text = fighter.Definition.displayName.Substring(0, 1);
            card.title.text = $"{fighter.DisplayName}  {fighter.Definition.displayName}";
            card.passive.text = $"Passive: {fighter.Passive.name}";
        }

        void OnDied(Fighter f) => ShowBanner(f == left.fighter ? "K.O.  -  CPU SCORES" : "K.O.!", 1.5f);

        void ShowBanner(string text, float seconds)
        {
            banner.text = text;
            bannerUntil = Time.time + seconds;
        }

        void Update()
        {
            Refresh(left);
            Refresh(right);
            banner.enabled = Time.time < bannerUntil;

            if (running)
            {
                timeLeft = Mathf.Max(0f, timeLeft - Time.deltaTime);
                if (timeLeft <= 0f)
                {
                    running = false;
                    ShowBanner("TIME!", 3f);
                }
            }
            int seconds = Mathf.CeilToInt(timeLeft);
            timer.text = $"{seconds / 60}:{seconds % 60:00}";
        }

        static void Refresh(FighterCard card)
        {
            var f = card.fighter;
            if (f == null) return;
            float hp = f.Health / f.MaxHealth;
            card.shownHp = Mathf.MoveTowards(card.shownHp, hp, Time.deltaTime * 1.5f);
            card.hpFill.fillAmount = card.shownHp;
            card.hpFill.color = Color.Lerp(new Color(0.95f, 0.25f, 0.25f), new Color(0.3f, 0.9f, 0.4f), hp);
            card.percent.text = $"{Mathf.CeilToInt(hp * 100f)}%";
            card.ultFill.fillAmount = f.UltMeter / Fighter.UltMax;
            card.ultFill.color = f.UltReady ? Color.Lerp(UltColor, Color.white, 0.5f + 0.5f * Mathf.Sin(Time.time * 8f)) : UltColor;
            card.status.text = f.StatusText;
        }
    }
}
