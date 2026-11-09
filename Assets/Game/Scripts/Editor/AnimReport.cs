using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GreatHeistAuto.EditorTools
{
    /// <summary>
    /// Numbers for every character clip, from the imported data: true ground pace, length, and tremor (fast wobble of the bones that a
    /// smooth curve through the motion does not explain). Run by tools/clip_report.py.
    /// </summary>
    public static class AnimReport
    {
        static readonly string[] Bones = { "Hips", "Spine2", "Head", "LeftHand", "RightHand", "LeftFoot", "RightFoot" };

        static void Line(StringBuilder sb, string model, GameObject fbx, float scale, AnimationClip clip, string label)
        {
            var inst = Object.Instantiate(fbx);
            var map = inst.GetComponentsInChildren<Transform>().GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            var tracks = Bones.ToDictionary(b => b, b => new List<Vector3>());
            int steps = Mathf.Max(30, Mathf.CeilToInt(clip.length * 120f));
            float low = float.MaxValue, high = float.MinValue;
            for (int i = 0; i <= steps; i++)
            {
                clip.SampleAnimation(inst, clip.length * i / steps);
                foreach (var b in Bones) tracks[b].Add(map[b].position * scale);
                low = Mathf.Min(low, map["LeftFoot"].position.y * scale, map["RightFoot"].position.y * scale);
                high = Mathf.Max(high, map["LeftFoot"].position.y * scale, map["RightFoot"].position.y * scale);
            }
            Object.DestroyImmediate(inst);
            string tremor = string.Join(" ", Bones.Select(b => $"{b}={AnimLab.Tremor(tracks[b]):F1}"));
            sb.AppendLine($"[CLIP] {model} {label} len={clip.length:F2}s fps={clip.frameRate:F0} footmin={low:F3} footmax={high:F3} pace={Actors.ClipPace(fbx, clip, scale):F2} tremor_mm {tremor}");
        }

        public static void Run()
        {
            var sb = new StringBuilder();
            foreach (var model in new[] { "player", "enemy_a", "enemy_b", "enemy_c", "ped_a", "ped_b" })
            {
                string path = $"{Art.Root}/Characters/{model}.fbx";
                var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                float scale = Actors.BodyScale(model, fbx);
                var raw = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview")).OrderBy(c => c.name).ToArray();
                foreach (var clip in raw)
                {
                    var clean = AssetDatabase.LoadAssetAtPath<AnimationClip>($"{Art.Gen}/Clips/{model}/{clip.name}.anim");
                    Line(sb, model, fbx, scale, clip, clean != null ? clip.name + "_raw" : clip.name);
                    if (clean != null) Line(sb, model, fbx, scale, clean, clip.name);
                }
            }
            File.WriteAllText("Logs/clip_report.txt", sb.ToString());
            Debug.Log("[REPORT] done");
        }
    }
}
