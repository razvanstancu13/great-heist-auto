using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GreatHeistAuto.EditorTools
{
    /// <summary>Paths, import settings and materials for everything LAGP made.</summary>
    public static class Art
    {
        public const string Root = "Assets/Game/Art";
        public const string Gen = "Assets/Game/Generated";
        public static readonly string[] Characters = { "player", "enemy_a", "enemy_b", "enemy_c", "ped_a", "ped_b" };
        public static readonly string[] Guns = { "pistol_a", "pistol_b", "rifle_a", "rifle_b" };
        public static readonly string[] Props = { "barrier", "dumpster", "trashcan", "bench", "crates", "car", "car_b", "car_lod", "car_b_lod", "dacia", "dacia_lod", "oltcit", "oltcit_lod", "trabant", "trabant_lod", "tree_trunk", "streetlamp", "tree", "tree_lod", "grenade" };
        static readonly HashSet<string> Looping = new() { "Idle", "Walk", "Run", "Jog", "Sprint", "CrouchIdle", "CrouchWalk", "JumpLoop", "PistolIdle", "PistolAimNeutral", "Driving" };

        public static void EnsureFolder(string path)
        {
            var parts = path.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }

        public static void ConfigureImports()
        {
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var n in Characters) Character($"{Root}/Characters/{n}.fbx");
                foreach (var n in Guns.Concat(Props)) Prop($"{Root}/Props/{n}.fbx");
                foreach (var p in Directory.GetFiles($"{Root}/Textures", "*.png")) Tex(p.Replace('\\', '/'));
                foreach (var dir in new[] { "Characters", "Props" })
                    foreach (var p in Directory.GetFiles($"{Root}/{dir}", "*.png")) Tex(p.Replace('\\', '/'));
                foreach (var p in Directory.GetFiles($"{Root}/UI", "*.png")) Ui(p.Replace('\\', '/'));
                foreach (var p in Directory.GetFiles($"{Root}/Audio", "*.ogg")) Sound(p.Replace('\\', '/'));
                foreach (var p in Directory.GetFiles($"{Root}/Audio", "*.wav")) Sound(p.Replace('\\', '/'));
            }
            finally { AssetDatabase.StopAssetEditing(); }
            AssetDatabase.Refresh();
        }

        static void Character(string path)
        {
            var m = AssetImporter.GetAtPath(path) as ModelImporter;
            if (m == null) { Debug.LogError("[BUILD] no model " + path); return; }
            bool changed = m.animationType != ModelImporterAnimationType.Generic || m.materialImportMode != ModelImporterMaterialImportMode.None;
            m.animationType = ModelImporterAnimationType.Generic;
            m.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            m.optimizeGameObjects = false;
            m.importAnimation = true;
            m.materialImportMode = ModelImporterMaterialImportMode.None;
            m.importNormals = ModelImporterNormals.Import;
            m.importTangents = ModelImporterTangents.CalculateMikk;
            m.isReadable = true;
            m.resampleCurves = false;
            var roles = RoleFile(path);
            var clips = RoleClips(path, m.defaultClipAnimations, roles);
            foreach (var c in clips)
            {
                string shortName = c.name;
                var r = roles?.clips.FirstOrDefault(x => x.role == shortName);
                c.loopTime = r != null ? r.loop : Looping.Contains(shortName);
                c.loopPose = false;
                c.lockRootRotation = true; c.lockRootHeightY = false; c.lockRootPositionXZ = false;
                c.keepOriginalOrientation = true; c.keepOriginalPositionXZ = true; c.keepOriginalPositionY = true;
            }
            m.clipAnimations = clips;
            m.SaveAndReimport();
        }

        /// <summary>One game role of a character: which Animation Library clip plays it, whether it loops, whether the builder pins it in place.</summary>
        /// <summary>speed: the clip's own ground speed in m/s on the library's mannequin (0 = unknown).</summary>
        [System.Serializable] public class Role { public string role, clip; public float start = 0f, end = 1f, speed; public bool loop, inplace; }
        [System.Serializable] public class RoleList { public Role[] clips; }

        /// <summary>&lt;model&gt;.clips.json next to the FBX (written by tools/gen_models.py from assets.json), or null.</summary>
        public static RoleList RoleFile(string fbxPath)
        {
            string file = Path.ChangeExtension(fbxPath, null) + ".clips.json";
            return File.Exists(file) ? JsonUtility.FromJson<RoleList>(File.ReadAllText(file)) : null;
        }

        /// <summary>The FBX's clips under the game's role names: each role's library take is imported under the role's name (one take
        /// can serve several roles), and a clip the rig made under a role's name itself (the procedural Idle / Walk) becomes
        /// &lt;role&gt;Old. Without a role file the clips keep their own names.</summary>
        static ModelImporterClipAnimation[] RoleClips(string path, ModelImporterClipAnimation[] defaults, RoleList roles)
        {
            foreach (var c in defaults) c.name = ClipName(c.name);
            if (roles == null) return defaults;
            var byName = defaults.ToDictionary(c => c.name, c => c);
            var used = new HashSet<string>(roles.clips.Select(r => r.clip));
            var outClips = new List<ModelImporterClipAnimation>();
            foreach (var c in defaults)
            {
                if (used.Contains(c.name)) continue;                             // comes back under its role
                if (roles.clips.Any(r => r.role == c.name)) c.name += "Old";       // displaced by a library clip
                outClips.Add(c);
            }
            foreach (var r in roles.clips)
            {
                if (!byName.TryGetValue(r.clip, out var src)) { Debug.LogError($"[BUILD] {path}: clip {r.clip} for {r.role} missing"); continue; }
                // A role can be part of a take (start / end: shares of its length), e.g. the airborne part of a jump.
                float len = src.lastFrame - src.firstFrame;
                float end = r.end > r.start ? r.end : 1f;
                outClips.Add(new ModelImporterClipAnimation { name = r.role, takeName = src.takeName,
                    firstFrame = src.firstFrame + r.start * len, lastFrame = src.firstFrame + end * len });
            }
            return outClips.ToArray();
        }

        public static string ClipName(string raw)
        {
            string s = raw;
            int bar = s.LastIndexOf('|');
            if (bar >= 0) s = s[(bar + 1)..];
            int dot = s.IndexOf('.');
            if (dot > 0) s = s[..dot];
            return s;
        }

        static void Prop(string path)
        {
            var m = AssetImporter.GetAtPath(path) as ModelImporter;
            if (m == null) { Debug.LogError("[BUILD] no model " + path); return; }
            m.animationType = ModelImporterAnimationType.None;
            m.importAnimation = false;
            m.materialImportMode = ModelImporterMaterialImportMode.None;
            m.importNormals = ModelImporterNormals.Import;
            m.importTangents = ModelImporterTangents.CalculateMikk;
            m.isReadable = true;
            m.SaveAndReimport();
        }

        static void Tex(string path)
        {
            var t = AssetImporter.GetAtPath(path) as TextureImporter;
            if (t == null) return;
            bool normal = path.EndsWith("_n.png");
            bool dataMask = path.EndsWith("_glow.png");
            t.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            t.wrapMode = TextureWrapMode.Repeat;
            t.mipmapEnabled = true;
            t.anisoLevel = 8;
            t.maxTextureSize = path.EndsWith("/Props/tree.png") || path.Contains("/Characters/") ? 4096 : 2048;   // people are seen close up
            t.sRGBTexture = !normal && !dataMask;
            t.textureCompression = TextureImporterCompression.Compressed;
            t.SaveAndReimport();
        }

        static void Ui(string path)
        {
            var t = AssetImporter.GetAtPath(path) as TextureImporter;
            if (t == null) return;
            t.textureType = TextureImporterType.Default;
            t.alphaIsTransparency = true;
            t.mipmapEnabled = true;
            t.wrapMode = TextureWrapMode.Clamp;
            t.maxTextureSize = 2048;
            t.SaveAndReimport();
        }

        static void Sound(string path)
        {
            var a = AssetImporter.GetAtPath(path) as AudioImporter;
            if (a == null) return;
            var s = a.defaultSampleSettings;
            bool longOne = path.Contains("music") || path.Contains("amb") || path.Contains("rain") || path.Contains("engine") || path.Contains("fire");
            s.loadType = longOne ? AudioClipLoadType.CompressedInMemory : AudioClipLoadType.DecompressOnLoad;
            s.compressionFormat = path.EndsWith(".wav") ? AudioCompressionFormat.PCM : AudioCompressionFormat.Vorbis;
            s.quality = 0.7f;
            a.defaultSampleSettings = s;
            a.SaveAndReimport();
        }

        // ---- materials ----

        static readonly Dictionary<string, Material> Mats = new();

        public static Texture2D Tex2D(string name) => AssetDatabase.LoadAssetAtPath<Texture2D>($"{Root}/Textures/{name}.png");

        public static Material WorldMat(string texture, float smoothLight = 0.15f, float smoothDark = 0.15f, float metallic = 0f, float normalScale = 1f)
        {
            string key = "w_" + texture;
            if (Mats.TryGetValue(key, out var cached) && cached != null) return cached;
            var m = GetOrCreateMat(key);
            m.SetTexture("_BaseMap", Tex2D(texture));
            var n = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Root}/Textures/{texture}_n.png");
            if (n != null) m.SetTexture("_NormalMap", n);
            m.SetFloat("_NormalScale", normalScale);
            m.SetFloat("_SmoothLight", smoothLight);
            m.SetFloat("_SmoothDark", smoothDark);
            m.SetFloat("_Metallic", metallic);
            EditorUtility.SetDirty(m);
            Mats[key] = m;
            return m;
        }

        public static Material ModelMat(string folder, string name, bool selectiveTint = false, float smoothness = 0.25f, float metallic = 0f)
        {
            string key = "m_" + name;
            if (Mats.TryGetValue(key, out var cached) && cached != null) return cached;
            var m = GetOrCreateMat(key);
            m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{Root}/{folder}/{name}.png"));
            m.SetFloat("_SelectiveTint", selectiveTint ? 1f : 0f);
            m.SetFloat("_SmoothLight", smoothness);
            m.SetFloat("_SmoothDark", smoothness);
            m.SetFloat("_Metallic", metallic);
            EditorUtility.SetDirty(m);
            Mats[key] = m;
            return m;
        }

        /// <summary>Windows that glow at night, and surfaces that get wet in rain, on top of WorldMat.</summary>
        public static void SetGlow(Material m, string texture, Color glow, float share)
        {
            m.SetColor("_GlowColor", glow);
            m.SetTexture("_GlowMask", AssetDatabase.LoadAssetAtPath<Texture2D>($"{Root}/Textures/{texture}_glow.png"));
            m.SetFloat("_GlowShare", share);
            EditorUtility.SetDirty(m);
        }

        public static void SetWet(Material m, float amount)
        {
            m.SetFloat("_WetAmount", amount);
            EditorUtility.SetDirty(m);
        }

        public static Material Plain(string key, Shader shader)
        {
            EnsureFolder($"{Gen}/Materials");
            string path = $"{Gen}/Materials/{key}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
            m.shader = shader;
            return m;
        }

        /// <summary>A tinted copy of a car's material, one per paint colour; keeps all cars SRP-batched.</summary>
        public static Material CarPaint(string model, int index, Color tint, float smoothness = 0.62f, float metallic = 0.3f)
        {
            string key = $"m_{model}_paint{index}";
            var m = GetOrCreateMat(key);
            m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{Root}/Props/{model}.png"));
            m.SetColor("_Color", tint);
            m.SetFloat("_SmoothLight", smoothness);
            m.SetFloat("_SmoothDark", 0.85f);
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_WetAmount", 0.4f);
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>Road paint: white or yellow in the vertex colour, drawn over the asphalt.</summary>
        /// <summary>Leaves on alpha cards: cut out, both sides, mip levels that keep the leaves' coverage so trees do not go thin far away.</summary>
        public static Material FoliageMat()
        {
            const string key = "m_foliage";
            if (Mats.TryGetValue(key, out var cached) && cached != null) return cached;
            string path = $"{Root}/UI/leaves_card.png";
            if (AssetImporter.GetAtPath(path) is TextureImporter ti)
            {
                ti.textureType = TextureImporterType.Default;
                ti.alphaIsTransparency = true;
                ti.mipmapEnabled = true;
                ti.mipMapsPreserveCoverage = true;
                ti.alphaTestReferenceValue = 0.45f;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.sRGBTexture = true;
                ti.SaveAndReimport();
            }
            var m = GetOrCreateMat(key);
            m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(path));
            m.SetFloat("_Cull", 0f);
            m.SetFloat("_SmoothLight", 0.08f);
            m.SetFloat("_SmoothDark", 0.08f);
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_WetAmount", 0.2f);
            m.SetColor("_Color", new Color(FoliageBoost, FoliageBoost, FoliageBoost, 1f));
            m.EnableKeyword("_GHA_CUTOUT");
            EditorUtility.SetDirty(m);
            Mats[key] = m;
            return m;
        }

        /// <summary>The material is this much brighter than its texture, so vertex colours (0 to 1) can lift a channel for autumn tints.</summary>
        public const float FoliageBoost = 1.35f;

        public static Material MarkingMat()
        {
            const string key = "w_marking";
            if (Mats.TryGetValue(key, out var cached) && cached != null) return cached;
            var m = GetOrCreateMat(key);
            m.SetTexture("_BaseMap", Tex2D("concrete"));
            m.SetFloat("_WetAmount", 1f);
            m.SetFloat("_SmoothLight", 0.2f);
            m.SetFloat("_SmoothDark", 0.2f);
            m.SetFloat("_DepthBias", -2f);
            EditorUtility.SetDirty(m);
            Mats[key] = m;
            return m;
        }

        public static Material Decal(string name, Texture tex)
        {
            var m = Plain("decal_" + name, Shader.Find("GreatHeistAuto/BloodDecal"));
            m.SetTexture("_BaseMap", tex);
            EditorUtility.SetDirty(m);
            return m;
        }

        public static Material FxMat(string name, string shaderName, Texture tex, Color color, bool additive)
        {
            EnsureFolder($"{Gen}/Materials");
            string path = $"{Gen}/Materials/fx_{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find(shaderName);
            if (m == null) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
            m.shader = shader;
            if (tex != null) { if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex); if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex); }
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            if (m.HasProperty("_Surface"))
            {
                m.SetFloat("_Surface", 1);
                m.SetFloat("_Blend", additive ? 2 : 0);
                m.SetFloat("_SrcBlend", additive ? 1 : 5);
                m.SetFloat("_DstBlend", additive ? 1 : 10);
                m.SetFloat("_SrcBlendAlpha", 1);
                m.SetFloat("_DstBlendAlpha", additive ? 1 : 10);
                m.SetFloat("_ZWrite", 0);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.SetOverrideTag("RenderType", "Transparent");
                m.renderQueue = additive ? 3100 : 3000;
            }
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material GetOrCreateMat(string key)
        {
            EnsureFolder($"{Gen}/Materials");
            string path = $"{Gen}/Materials/{key}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("GreatHeistAuto/TintLit"));
                AssetDatabase.CreateAsset(m, path);
            }
            return m;
        }

        public static void ClearCache() => Mats.Clear();

        public static AudioClip[] Clips(string group)
        {
            var list = new List<AudioClip>();
            for (int i = 0; ; i++)
            {
                var c = AssetDatabase.LoadAssetAtPath<AudioClip>($"{Root}/Audio/{group}_{i}.ogg");
                if (c == null) break;
                list.Add(c);
            }
            if (list.Count == 0) Debug.LogWarning("[BUILD] no audio for " + group);
            return list.ToArray();
        }
    }
}
