using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;

namespace GreatHeistAuto
{
    /// <summary>Main menu: a looping LAGP video behind the title "Great Heist Auto" and a Play button.</summary>
    public class MainMenu : MonoBehaviour
    {
        public VideoClip Background;
        public Texture2D BackgroundStill;
        public Texture2D Logo;
        public AudioClip Music, Click;
        public string GameScene = "City";

        AudioSource audioSource;
        RenderTexture rt;

        void Start()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Time.timeScale = 1f;
            AudioListener.pause = false;

            audioSource = gameObject.AddComponent<AudioSource>();
            if (Music != null) { audioSource.clip = Music; audioSource.loop = true; audioSource.volume = 0.7f; audioSource.Play(); }

            var canvas = UiKit.MakeCanvas("Menu");
            UiKit.EnsureEventSystem();
            var root = canvas.transform;
            uiRoot = root;
            Vector2 C = new(.5f, .5f);

            var bg = UiKit.Rect(root, "Background", C, Vector2.zero, Vector2.zero).gameObject.AddComponent<RawImage>();
            UiKit.Fill(bg.rectTransform);
            bg.raycastTarget = false;
            bg.color = Color.white;
            if (BackgroundStill != null) bg.texture = BackgroundStill;
            var fitter = bg.gameObject.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            fitter.aspectRatio = 1280f / 704f;
            if (Background != null)
            {
                rt = new RenderTexture((int)Background.width, (int)Background.height, 0);
                var vp = gameObject.AddComponent<VideoPlayer>();
                vp.clip = Background; vp.isLooping = true; vp.audioOutputMode = VideoAudioOutputMode.None;
                vp.renderMode = VideoRenderMode.RenderTexture; vp.targetTexture = rt;
                vp.prepareCompleted += p => { bg.texture = rt; p.Play(); };
                vp.Prepare();
            }

            var dim = UiKit.Img(root, "Dim", C, Vector2.zero, Vector2.zero, new Color(0, 0, 0, .28f));
            UiKit.Fill(dim.rectTransform);

            // The logo image holds the title; if it is missing the same words are drawn as text.
            if (Logo != null)
            {
                var logo = UiKit.Rect(root, "Title", new Vector2(.5f, .66f), Vector2.zero, new Vector2(1200, 1200f * Logo.height / Logo.width)).gameObject.AddComponent<RawImage>();
                logo.rectTransform.pivot = C;
                logo.texture = Logo;
                logo.raycastTarget = false;
            }
            else
            {
                var t = UiKit.Label(root, "Title", new Vector2(.5f, .66f), Vector2.zero, new Vector2(1200, 200), "Great Heist Auto", 110, TextAnchor.MiddleCenter, Color.white);
                t.rectTransform.pivot = C;
            }

            var play = UiKit.MakeButton(root, "Play", new Vector2(.5f, .3f), Vector2.zero, new Vector2(360, 86), "Play", 44);
            play.GetComponent<RectTransform>().pivot = C;
            play.onClick.AddListener(OnPlay);
            play.Select();

            var lab = UiKit.MakeButton(root, "AnimationLab", new Vector2(1f, 0f), new Vector2(-200, 50), new Vector2(340, 56), "Animation Lab", 26);
            lab.GetComponent<RectTransform>().pivot = C;
            lab.onClick.AddListener(() => SceneManager.LoadScene(AnimLab.SceneName));
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-ghalab") >= 0) StartCoroutine(AnimLab.ClickFromMenu(lab));
        }

        bool loading;
        Transform uiRoot;

        public void OnPlay()
        {
            if (loading) return;
            loading = true;
            if (Click != null) audioSource.PlayOneShot(Click);
            StartCoroutine(LoadGame());
        }

        /// <summary>The city takes a while to load: a screen that says so and shows how far it has got.</summary>
        System.Collections.IEnumerator LoadGame()
        {
            Vector2 C = new(.5f, .5f);
            // On a canvas of its own, which outlives this scene: it stays until the city can be seen.
            var canvas = UiKit.MakeCanvas("LoadingScreen", 1000);
            DontDestroyOnLoad(canvas.gameObject);
            var screen = canvas.transform;
            var group = canvas.gameObject.AddComponent<CanvasGroup>();
            var cover = UiKit.Img(screen, "LoadingCover", C, Vector2.zero, Vector2.zero, new Color(.02f, .02f, .03f, 1f));
            UiKit.Fill(cover.rectTransform);
            cover.raycastTarget = true;
            if (Logo != null)
            {
                var logo = UiKit.Rect(screen, "LoadingLogo", new Vector2(.5f, .62f), Vector2.zero, new Vector2(900, 900f * Logo.height / Logo.width)).gameObject.AddComponent<RawImage>();
                logo.rectTransform.pivot = C;
                logo.texture = Logo;
                logo.raycastTarget = false;
            }
            var spinner = UiKit.Rect(screen, "LoadingSpinner", new Vector2(.5f, .27f), Vector2.zero, new Vector2(84, 84)).gameObject.AddComponent<RawImage>();
            spinner.rectTransform.pivot = C;
            spinner.raycastTarget = false;
            spinner.texture = LoadingSpinner.MakeRing(256);
            var tip = UiKit.Label(screen, "LoadingTip", new Vector2(.5f, .15f), Vector2.zero, new Vector2(1300, 40),
                "WASD move    Shift run    LMB fire or punch    RMB aim    E car    Space brake and drift", 22, TextAnchor.MiddleCenter, new Color(.75f, .78f, .85f));
            tip.rectTransform.pivot = C;
            spinner.gameObject.AddComponent<LoadingSpinner>().Group = group;
            yield return null;
            yield return new WaitForEndOfFrame();   // the screen is up before the work starts

            // Low priority: the loading takes a little longer but leaves time to draw between its pieces, so the ring keeps turning.
            Application.backgroundLoadingPriority = ThreadPriority.Low;
            var job = SceneManager.LoadSceneAsync(GameScene);
            while (!job.isDone)
            {
                LoadingSpinner.Phase = job.progress < 0.9f ? $"reading {job.progress:P0}" : "activating the city";
                audioSource.volume = Mathf.MoveTowards(audioSource.volume, 0.25f, Time.unscaledDeltaTime * 0.3f);
                yield return null;
            }
        }

        void OnDestroy()
        {
            if (rt != null) { rt.Release(); Destroy(rt); }
        }
    }

    /// <summary>The ring on the loading screen. It turns by the clock, so it is always where it should be, and it takes the screen away once the city is ready.</summary>
    public class LoadingSpinner : MonoBehaviour
    {
        public CanvasGroup Group;
        /// <summary>What the loading is doing now, for the stall log: set by the menu and by the city's start-up steps.</summary>
        public static string Phase = "";
        const float TurnsPerSecond = 1.1f, FadeSeconds = 0.35f;
        float started, lastFrame, longestFreeze, fading = -1f;
        int cityFrame0 = -1;
        int readyFrames;

        void Start()
        {
            started = lastFrame = Time.realtimeSinceStartup;
        }

        void LateUpdate()
        {
            float now = Time.realtimeSinceStartup;
            float gap = now - lastFrame;
            longestFreeze = Mathf.Max(longestFreeze, gap);
            // Every stall, with what was going on, so the slow steps of loading can be found (tools/check_loading.py lists them).
            if (SceneManager.GetActiveScene().name == "City")
            {
                if (cityFrame0 < 0) cityFrame0 = Time.frameCount;
                Phase = $"city frame {Time.frameCount - cityFrame0}, segments {SegmentLoader.Progress:P0}";
            }
            if (gap > 0.1f && fading < 0f)
                Debug.Log($"[LOAD] stall {gap:F2} s at {now - started:F1} s, scene {SceneManager.GetActiveScene().name}, phase {Phase}");
            lastFrame = now;
            transform.localRotation = Quaternion.Euler(0f, 0f, -now * TurnsPerSecond * 360f);
            if (fading < 0f)
            {
                bool ready = SceneManager.GetActiveScene().name == "City" && GameManager.Instance != null && GameManager.Instance.Player != null
                             && SegmentLoader.Done;
                readyFrames = ready ? readyFrames + 1 : 0;
                if (readyFrames >= 3)      // past the first heavy frames of the city
                {
                    fading = now;
                    Application.backgroundLoadingPriority = ThreadPriority.BelowNormal;
                    Debug.Log($"[LOAD] done in {now - started:F1} s, longest freeze {longestFreeze:F2} s");
                }
            }
            else
            {
                float k = (now - fading) / FadeSeconds;
                if (Group != null) Group.alpha = 1f - k;
                if (k >= 1f) Destroy(Group != null ? Group.gameObject : gameObject);
            }
        }

        /// <summary>A ring with a tail: solid at its head, fading away behind.</summary>
        public static Texture2D MakeRing(int size)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var pixels = new Color32[size * size];
            float outer = size * .5f - 2f, inner = outer * .74f, c = (size - 1) * .5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x - c, dy = y - c, r = Mathf.Sqrt(dx * dx + dy * dy);
                    float edge = Mathf.Clamp01(outer - r + .5f) * Mathf.Clamp01(r - inner + .5f);
                    float along = Mathf.Repeat(-Mathf.Atan2(dy, dx) / (Mathf.PI * 2f), 1f);    // the ring turns clockwise: the sharp bright end leads, and the tail fades away behind it
                    float a = edge * Mathf.Pow(along, 1.6f);
                    pixels[y * size + x] = new Color32(242, 140, 31, (byte)(a * 255f));
                }
            t.SetPixels32(pixels);
            t.Apply();
            return t;
        }
    }
}
