using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace GreatHeistAuto
{
    /// <summary>Crosshair, health, ammo, wave and kills, damage direction, hit markers, death screen, Esc pause.</summary>
    public class GameHud : MonoBehaviour
    {
        public PlayerController Player;
        public Texture2D[] BloodScreen;

        Image healthFill, damageFlash, fade, hitMarker;
        Image[] cross;
        Text ammo, weapon, wave, kills, banner, hint, reloadText, prompt, carSpeed;
        Image carHealthBack, carHealthFill;
        Text carHealthLabel;
        RawImage[] screenBlood;
        float[] screenBloodAlpha;
        int nextScreenBlood;
        RectTransform damageArrowPivot;
        Image damageArrow;
        GameObject pausePanel;
        float hitMarkerUntil, flashAlpha, arrowAlpha, bannerUntil;
        Vector3 lastDamageDir;
        bool paused;

        public bool Paused => paused;

        void Start()
        {
            var canvas = UiKit.MakeCanvas("HUD", 5);
            canvas.transform.SetParent(transform, false);
            var root = canvas.transform;
            UiKit.EnsureEventSystem();
            Vector2 C = new(.5f, .5f);

            damageFlash = UiKit.Img(root, "DamageFlash", C, Vector2.zero, Vector2.zero, new Color(.7f, 0, 0, 0));
            UiKit.Fill(damageFlash.rectTransform);

            // Blood on the lens: big splats that run off.
            int splatCount = BloodScreen != null && BloodScreen.Length > 0 ? 8 : 0;
            screenBlood = new RawImage[splatCount];
            screenBloodAlpha = new float[splatCount];
            for (int i = 0; i < splatCount; i++)
            {
                var ri = UiKit.Rect(root, "ScreenBlood" + i, C, Vector2.zero, new Vector2(760, 760)).gameObject.AddComponent<RawImage>();
                ri.rectTransform.pivot = C;
                ri.texture = BloodScreen[i % BloodScreen.Length];
                ri.raycastTarget = false;
                ri.color = new Color(.8f, .8f, .8f, 0f);
                screenBlood[i] = ri;
            }

            cross = new Image[4];
            for (int i = 0; i < 4; i++) cross[i] = UiKit.Img(root, "Cross" + i, C, Vector2.zero, i < 2 ? new Vector2(2, 9) : new Vector2(9, 2), new Color(1, 1, 1, .9f));
            foreach (var c in cross) c.rectTransform.pivot = C;
            hitMarker = UiKit.Img(root, "HitMarker", C, Vector2.zero, new Vector2(20, 20), new Color(1, 1, 1, 0));
            hitMarker.rectTransform.pivot = C;
            hitMarker.rectTransform.localRotation = Quaternion.Euler(0, 0, 45);

            damageArrowPivot = UiKit.Rect(root, "ArrowPivot", C, Vector2.zero, Vector2.zero);
            damageArrowPivot.pivot = C;
            damageArrow = UiKit.Img(damageArrowPivot, "Arrow", C, new Vector2(0, 190), new Vector2(70, 12), new Color(1, .15f, .1f, 0));
            damageArrow.rectTransform.pivot = C;

            // Health, bottom left.
            Vector2 BL = new(0, 0);
            UiKit.Img(root, "HealthBack", BL, new Vector2(40, 40), new Vector2(340, 22), new Color(0, 0, 0, .55f));
            healthFill = UiKit.Img(root, "HealthFill", BL, new Vector2(43, 43), new Vector2(334, 16), new Color(.35f, .85f, .35f, .95f));
            UiKit.Label(root, "HealthLabel", BL, new Vector2(40, 66), new Vector2(200, 30), "HEALTH", 20, TextAnchor.LowerLeft, Color.white);

            // Ammo, bottom right.
            Vector2 BR = new(1, 0);
            ammo = UiKit.Label(root, "Ammo", BR, new Vector2(-40, 34), new Vector2(300, 60), "", 52, TextAnchor.LowerRight, Color.white);
            weapon = UiKit.Label(root, "Weapon", BR, new Vector2(-40, 96), new Vector2(400, 28), "", 22, TextAnchor.LowerRight, new Color(1, .75f, .4f));
            reloadText = UiKit.Label(root, "Reload", C, new Vector2(0, -70), new Vector2(300, 30), "", 22, TextAnchor.MiddleCenter, new Color(1, .8f, .4f));
            reloadText.rectTransform.pivot = C;

            // Wave and kills, top right.
            Vector2 TR = new(1, 1);
            wave = UiKit.Label(root, "Wave", TR, new Vector2(-40, -30), new Vector2(300, 40), "", 34, TextAnchor.UpperRight, Color.white);
            kills = UiKit.Label(root, "Kills", TR, new Vector2(-40, -74), new Vector2(400, 30), "", 22, TextAnchor.UpperRight, Color.white);

            banner = UiKit.Label(root, "Banner", new Vector2(.5f, .72f), Vector2.zero, new Vector2(800, 80), "", 54, TextAnchor.MiddleCenter, Color.white);
            banner.rectTransform.pivot = C;
            hint = UiKit.Label(root, "Hint", new Vector2(.5f, 0), new Vector2(0, 20), new Vector2(1500, 26),
                "WASD move   Shift sprint   Ctrl crouch   Space jump   LMB fire or punch   RMB aim   R reload   1-4 guns   0 no gun   MMB look around   E car   Space brake and drift   Tab horn   L lights   V view   Esc", 18, TextAnchor.LowerCenter, new Color(1, 1, 1, .8f));
            hint.rectTransform.pivot = new Vector2(.5f, 0);

            prompt = UiKit.Label(root, "Prompt", C, new Vector2(0, -130), new Vector2(600, 30), "", 26, TextAnchor.MiddleCenter, Color.white);
            prompt.rectTransform.pivot = C;
            carSpeed = UiKit.Label(root, "CarSpeed", new Vector2(1, 0), new Vector2(-40, 40), new Vector2(300, 60), "", 52, TextAnchor.LowerRight, Color.white);
            // The car's health sits right above the player's.
            carHealthBack = UiKit.Img(root, "CarHealthBack", BL, new Vector2(40, 102), new Vector2(340, 22), new Color(0, 0, 0, .55f));
            carHealthFill = UiKit.Img(root, "CarHealthFill", BL, new Vector2(43, 105), new Vector2(334, 16), new Color(.95f, .6f, .1f, .95f));
            carHealthLabel = UiKit.Label(root, "CarHealthLabel", BL, new Vector2(40, 128), new Vector2(200, 30), "CAR", 20, TextAnchor.LowerLeft, Color.white);

            fade = UiKit.Img(root, "Fade", C, Vector2.zero, Vector2.zero, new Color(0, 0, 0, 0));
            UiKit.Fill(fade.rectTransform);

            BuildPause(root, C);

            Player.Health.Damaged += OnDamaged;
            BulletSystem.BulletLanded += OnLanded;
            CharacterRig.GoreAt += OnGore;
            var gm = GameManager.Instance;
            gm.HostilityChanged += OnHostility;
            gm.EnemyKilled += OnKill;
            gm.PlayerDied += OnDied;
            gm.PlayerRespawned += OnRespawned;
            SetCursor(false);
        }

        void BuildPause(Transform root, Vector2 C)
        {
            var dim = UiKit.Img(root, "Pause", C, Vector2.zero, Vector2.zero, new Color(0, 0, 0, .55f));
            UiKit.Fill(dim.rectTransform);
            dim.raycastTarget = true;
            pausePanel = dim.gameObject;
            var title = UiKit.Label(dim.transform, "Title", C, new Vector2(0, 130), new Vector2(700, 90), "PAUSED", 64, TextAnchor.MiddleCenter, Color.white);
            title.rectTransform.pivot = C;
            var resume = UiKit.MakeButton(dim.transform, "Resume", C, new Vector2(0, 20), new Vector2(340, 70), "Resume", 32);
            resume.GetComponent<RectTransform>().pivot = C;
            resume.onClick.AddListener(() => SetPaused(false));
            var menu = UiKit.MakeButton(dim.transform, "Menu", C, new Vector2(0, -70), new Vector2(340, 70), "Main menu", 32);
            menu.GetComponent<RectTransform>().pivot = C;
            menu.onClick.AddListener(() => { Time.timeScale = 1f; SceneManager.LoadScene("MainMenu"); });
            pausePanel.SetActive(false);
        }

        void OnDestroy()
        {
            BulletSystem.BulletLanded -= OnLanded;
            CharacterRig.GoreAt -= OnGore;
            Time.timeScale = 1f;
        }

        void OnDamaged(HitZone zone, Vector3 point, Vector3 dir, Health shooter)
        {
            flashAlpha = zone == HitZone.Head ? 0.42f : 0.2f;
            if (screenBlood != null && screenBlood.Length > 0) AddScreenBlood(zone == HitZone.Head ? 1f : 0.75f, zone == HitZone.Head ? 3 : 1);
            arrowAlpha = 1f;
            lastDamageDir = -dir;
        }

        void OnLanded(Health victim, HitZone zone, Health shooter)
        {
            if (shooter != Player.Health) return;
            hitMarkerUntil = Time.unscaledTime + (zone == HitZone.Head ? 0.3f : 0.14f);
            hitMarker.color = zone == HitZone.Head ? new Color(1, .25f, .15f, 1) : Color.white;
        }

        void OnGore(Vector3 where, float power)
        {
            if (screenBlood == null || screenBlood.Length == 0 || Player.Cam == null) return;
            Vector3 view = Player.Cam.WorldToViewportPoint(where);
            float d = Vector3.Distance(where, Player.Cam.transform.position);
            if (view.z < 0f || d > 9f * Mathf.Max(1f, power * .4f)) return;
            AddScreenBlood(Mathf.Clamp01(power * 0.5f * (1f - d / 14f)), Mathf.Min(2, Mathf.CeilToInt(power * .5f)));
        }

        void AddScreenBlood(float strength, int count)
        {
            for (int k = 0; k < count; k++)
            {
                int i = nextScreenBlood; nextScreenBlood = (nextScreenBlood + 1) % screenBlood.Length;
                var rt = screenBlood[i].rectTransform;
                rt.anchoredPosition = new Vector2(Random.Range(-720f, 720f), Random.Range(-340f, 340f));
                rt.localRotation = Quaternion.Euler(0, 0, Random.Range(0, 360f));
                rt.localScale = Vector3.one * Random.Range(0.55f, 1.25f);
                screenBloodAlpha[i] = Mathf.Max(screenBloodAlpha[i], strength);
            }
        }

        void OnHostility(bool hostile) { if (hostile) Banner("THEY ARE ALL AFTER YOU", 2.4f, new Color(1f, .35f, .2f)); }

        void OnKill(EnemyAI e, HitZone zone)
        {
            if (zone == HitZone.Head) Banner("HEADSHOT", 1.1f, new Color(1, .4f, .2f));
        }

        void OnDied() => Banner("YOU DIED", 2.8f, new Color(.95f, .15f, .1f));
        void OnRespawned() { }

        void Banner(string text, float seconds, Color c)
        {
            if (banner == null) return;
            banner.text = text; banner.color = c;
            bannerUntil = Time.unscaledTime + seconds;
        }

        void SetCursor(bool free)
        {
            Cursor.lockState = free ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = free;
        }

        void SetPaused(bool p)
        {
            paused = p;
            pausePanel.SetActive(p);
            Time.timeScale = p ? 0f : 1f;
            AudioListener.pause = p;
            Player.InputEnabled = !p;
            SetCursor(p);
        }

        void Update()
        {
            var k = Keyboard.current;
            if (k != null && k.escapeKey.wasPressedThisFrame) SetPaused(!paused);

            var gm = GameManager.Instance;
            var gun = Player.Gun;

            for (int i = 0; screenBlood != null && i < screenBlood.Length; i++)
            {
                screenBloodAlpha[i] = Mathf.MoveTowards(screenBloodAlpha[i], 0f, Time.deltaTime * 0.3f);
                var col = screenBlood[i].color; col.a = screenBloodAlpha[i]; screenBlood[i].color = col;
                if (screenBloodAlpha[i] > 0f) screenBlood[i].rectTransform.anchoredPosition += Vector2.down * (Time.deltaTime * 14f);   // it runs down the lens
            }

            var car = Player.CurrentVehicle;
            bool inCar = car != null;
            prompt.text = !inCar && Player.VehicleInReach != null && !Player.Health.Dead ? "E   drive" : inCar && Mathf.Abs(car.ForwardSpeed) < 7f ? "E   get out" : "";
            carSpeed.text = inCar ? $"{Mathf.RoundToInt(Mathf.Abs(car.ForwardSpeed) * 3.6f)}<size=24> km/h</size>" : "";
            carHealthBack.enabled = inCar; carHealthFill.enabled = inCar; carHealthLabel.enabled = inCar;
            if (inCar)
            {
                float h = Mathf.Clamp01(car.CurrentHealth / car.MaxHealth);
                carHealthFill.rectTransform.sizeDelta = new Vector2(334f * h, 16f);
                carHealthFill.color = car.OnFire ? new Color(1f, .22f, .08f, .95f) : new Color(.95f, .6f, .1f, .95f);
            }
            ammo.enabled = !inCar; weapon.enabled = !inCar;
            float hp = Player.Health.Current / Health.MaxHealth;
            healthFill.rectTransform.sizeDelta = new Vector2(334f * hp, 16f);
            healthFill.color = Color.Lerp(new Color(.9f, .2f, .15f, .95f), new Color(.35f, .85f, .35f, .95f), hp);
            ammo.text = gun == null ? "" : gun.Reloading ? "--" : $"{gun.InMag}<size=26> / {gun.Data.MagazineSize}</size>";
            weapon.text = gun == null ? "Unarmed" : $"[{Player.Slot + 1}] {gun.Data.DisplayName}";
            reloadText.text = inCar || gun == null ? "" : gun.Reloading ? "RELOADING" : gun.InMag == 0 ? "PRESS R" : "";
            wave.text = gm.Hostile ? "HOSTILE" : "CALM";
            wave.color = gm.Hostile ? new Color(1f, .35f, .2f) : Color.white;
            kills.text = $"Kills {gm.Kills}   Headshots {gm.Headshots}";

            // Crosshair opens with the spread.
            float px = 6f + Player.CurrentSpread * 16f;
            cross[0].rectTransform.anchoredPosition = new Vector2(0, px + 4);
            cross[1].rectTransform.anchoredPosition = new Vector2(0, -px - 4);
            cross[2].rectTransform.anchoredPosition = new Vector2(px + 4, 0);
            cross[3].rectTransform.anchoredPosition = new Vector2(-px - 4, 0);
            bool dead = Player.Health.Dead;
            bool hideCross = dead || inCar || gun == null;
            foreach (var c in cross) c.enabled = !hideCross;

            var hm = hitMarker.color;
            hm.a = Time.unscaledTime < hitMarkerUntil ? 1f : 0f;
            hitMarker.color = hm;

            flashAlpha = Mathf.MoveTowards(flashAlpha, 0f, Time.deltaTime * 0.8f);
            damageFlash.color = new Color(.7f, 0, 0, flashAlpha);
            arrowAlpha = Mathf.MoveTowards(arrowAlpha, 0f, Time.deltaTime * 0.9f);
            damageArrow.color = new Color(1, .15f, .1f, arrowAlpha);
            if (arrowAlpha > 0f)
            {
                float a = Vector3.SignedAngle(Player.transform.forward, lastDamageDir.Flat(), Vector3.up);
                damageArrowPivot.localRotation = Quaternion.Euler(0, 0, -a);
            }

            banner.enabled = Time.unscaledTime < bannerUntil;
            if (hint != null && Time.timeSinceLevelLoad > 14f) hint.enabled = false;
            float wantFade = dead ? 0.75f : 0f;
            var f = fade.color; f.a = Mathf.MoveTowards(f.a, wantFade, Time.deltaTime * 0.6f); fade.color = f;
        }
    }
}
