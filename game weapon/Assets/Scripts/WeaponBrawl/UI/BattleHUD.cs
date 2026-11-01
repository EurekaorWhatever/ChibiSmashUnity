using UnityEngine;
using UnityEngine.UI;

namespace WeaponBrawl
{
    /// <summary>Top-corner fighter cards (portrait, health %, ult meter) and match timer, in the same hard-angled "Vs." style as the menu.</summary>
    public class BattleHUD : MonoBehaviour
    {
        class FighterCard
        {
            public Fighter fighter;
            public ComicFigure icon;
            public SkewPanel iconPlate, hpFill, ultFill;
            public Text title, percent, passive, status;
            public Color color;
            public float shownHp = 1f;
        }

        FighterCard left, right;
        Text timer, banner;
        SkewPanel bannerPlate;
        float timeLeft, bannerUntil;
        bool running;

        public static BattleHUD Create(Canvas canvas)
        {
            var rt = UIFactory.Node("HUD", canvas.transform).Stretch();
            var hud = rt.gameObject.AddComponent<BattleHUD>();
            hud.left = BuildCard(rt, true);
            hud.right = BuildCard(rt, false);

            var top = new Vector2(0.5f, 1f);
            var timerPlate = UIFactory.Skew(rt, "TimerPlate", UITheme.Navy);
            timerPlate.BottomInset = 28f;
            timerPlate.rectTransform.Place(top, top, Vector2.zero, new Vector2(240f, 90f));
            hud.timer = UIFactory.Label(timerPlate.transform, "Timer", "3:00", 54, Color.white, TextAnchor.MiddleCenter, UITheme.Heading);
            hud.timer.rectTransform.Stretch();

            var center = new Vector2(0.5f, 0.5f);
            hud.bannerPlate = UIFactory.Skew(rt, "BannerPlate", UITheme.Navy, 70f);
            hud.bannerPlate.rectTransform.Place(center, center, new Vector2(0f, 150f), new Vector2(900f, 170f));
            hud.banner = UIFactory.Label(hud.bannerPlate.transform, "Banner", "", 116, UITheme.P1, TextAnchor.MiddleCenter, UITheme.Heading);
            hud.banner.rectTransform.Stretch();
            return hud;
        }

        static FighterCard BuildCard(RectTransform parent, bool leftSide)
        {
            float sx = leftSide ? 1f : -1f;
            float lean = 26f * sx;
            var anchor = leftSide ? new Vector2(0f, 1f) : new Vector2(1f, 1f);
            var align = leftSide ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
            var card = new FighterCard();

            var root = UIFactory.Skew(parent, leftSide ? "P1Card" : "P2Card", UITheme.Paper, lean);
            root.BorderWidth = 4f;
            root.BorderColor = UITheme.Navy;
            root.rectTransform.Place(anchor, anchor, new Vector2(24f * sx, -20f), new Vector2(610f, 172f));

            card.iconPlate = UIFactory.Skew(root.transform, "Portrait", Color.white, lean * 0.7f);
            card.iconPlate.rectTransform.Place(anchor, anchor, new Vector2(26f * sx, -18f), new Vector2(140f, 136f));
            card.icon = UIFactory.Node("Icon", card.iconPlate.transform).gameObject.AddComponent<ComicFigure>();
            card.icon.rectTransform.Stretch(14f);
            card.icon.raycastTarget = false;

            card.title = Row(root.transform, anchor, sx, -14f, 40f, 28, align, UITheme.Heading);
            card.percent = Row(root.transform, anchor, sx, -8f, 52f, 46, leftSide ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft, UITheme.Heading);
            card.passive = Row(root.transform, anchor, sx, -56f, 28f, 20, align, FontStyle.Normal);
            card.passive.color = UITheme.NavySoft;

            card.hpFill = Bar(root.transform, anchor, sx, -90f, 26f, leftSide);
            card.ultFill = Bar(root.transform, anchor, sx, -122f, 12f, leftSide);
            card.ultFill.color = UITheme.Gold;
            card.status = Row(root.transform, anchor, sx, -136f, 24f, 18, align, FontStyle.Bold);
            return card;
        }

        static Text Row(Transform parent, Vector2 anchor, float sx, float y, float height, int size, TextAnchor align, FontStyle style)
        {
            var t = UIFactory.Label(parent, "Text", "", size, UITheme.Navy, align, style);
            t.rectTransform.Place(anchor, anchor, new Vector2(180f * sx, y), new Vector2(392f, height));
            return t;
        }

        static SkewPanel Bar(Transform parent, Vector2 anchor, float sx, float y, float height, bool leftSide)
        {
            var track = UIFactory.Skew(parent, "BarTrack", UITheme.Navy, 10f * sx);
            track.rectTransform.Place(anchor, anchor, new Vector2(180f * sx, y), new Vector2(392f, height));
            var fill = UIFactory.Skew(track.transform, "Fill", Color.white, 10f * sx);
            fill.rectTransform.Stretch(2f);
            fill.FillMode = leftSide ? SkewPanel.Fill.FromLeft : SkewPanel.Fill.FromRight;
            return fill;
        }

        public void Bind(Fighter player, Fighter opponent, float matchSeconds)
        {
            Assign(left, player, UITheme.P1);
            Assign(right, opponent, UITheme.P2);
            timeLeft = matchSeconds;
            running = true;
            player.Died += OnDied;
            opponent.Died += OnDied;
            ShowBanner("FIGHT!", 1.2f);
        }

        static void Assign(FighterCard card, Fighter fighter, Color color)
        {
            card.fighter = fighter;
            card.color = color;
            card.shownHp = 1f;
            card.iconPlate.color = color;
            card.icon.ShowWeapon(fighter.Definition.type, fighter.Variant, fighter.Definition.color);
            card.title.text = $"{fighter.DisplayName}  {fighter.VariantName.ToUpperInvariant()}";
            card.passive.text = $"{fighter.Definition.playstyle}  |  {fighter.Passive.name}";
            card.hpFill.color = color;
        }

        void OnDied(Fighter f) => ShowBanner("K.O.!", 1.5f);

        void ShowBanner(string text, float seconds)
        {
            banner.text = text;
            bannerUntil = Time.time + seconds;
        }

        void Update()
        {
            Refresh(left);
            Refresh(right);
            bannerPlate.enabled = banner.enabled = Time.time < bannerUntil;

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
            card.hpFill.FillAmount = card.shownHp;
            card.percent.text = $"{Mathf.CeilToInt(hp * 100f)}%";
            card.ultFill.FillAmount = f.UltMeter / Fighter.UltMax;
            card.ultFill.color = f.UltReady ? Color.Lerp(UITheme.Gold, Color.white, 0.5f + 0.5f * Mathf.Sin(Time.time * 8f)) : UITheme.Gold;
            card.status.text = f.StatusText;
        }
    }
}
