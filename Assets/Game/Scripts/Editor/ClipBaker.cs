using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GreatHeistAuto.EditorTools
{
    /// <summary>Clips and clip facts the builder works out from the imported models.</summary>
    public static class ClipBaker
    {
        const int OutputFps = 60;

        /// <summary>
        /// The rise of a jump, from the library's jump clip, timed to the game's real jump. In that clip the legs push off in the first
        /// tenth of a second, then a second passes on the way up. Here the push-off keeps its speed, the rest is squeezed into the time the
        /// rise takes, and the hips hold steady, because the character itself is what rises.
        /// </summary>
        public static AnimationClip BakeJumpUp(GameObject fbx, AnimationClip src, float seconds, string path)
        {
            var pose = Object.Instantiate(fbx);
            var hipsBone = pose.GetComponentsInChildren<Transform>().First(t => t.name == "Hips");
            // The push-off, read from the clip: from the lowest crouch up to the first top of the legs' stretch. The rise starts well into it,
            // since the player is already leaving the ground at the first frame.
            float lowT = 0f, lowY = float.MaxValue, topT = src.length, topY = float.MinValue;
            const int scan = 80;
            for (int i = 0; i <= scan; i++)
            {
                src.SampleAnimation(pose, src.length * i / scan);
                float y = hipsBone.position.y;
                if (i < scan / 2 && y < lowY) { lowY = y; lowT = src.length * i / scan; }
            }
            for (int i = 0; i <= scan; i++)
            {
                float at = src.length * i / scan;
                if (at < lowT) continue;
                src.SampleAnimation(pose, at);
                float y = hipsBone.position.y;
                if (y > topY) { topY = y; topT = at; }
                else if (y < topY - 0.01f) break;
            }
            float pushEnd = topT, pushStart = Mathf.Lerp(lowT, topT, 0.6f);
            const float pushOut = 0.05f;
            Debug.Log($"[BUILD] jump clip {src.name}: crouch lowest at {lowT:F2} s, legs stretched at {topT:F2} s of {src.length:F2} s");
            src.SampleAnimation(pose, pushEnd);
            float hipsUp = hipsBone.position.y;
            src.SampleAnimation(pose, src.length);
            float hipsTop = hipsBone.position.y;
            var clip = Bake(pose, src, "JumpUp", seconds, path, t =>
            {
                float tIn = t < pushOut ? Mathf.Lerp(pushStart, pushEnd, t / pushOut)
                                        : Mathf.Lerp(pushEnd, src.length, Mathf.SmoothStep(0f, 1f, (t - pushOut) / (seconds - pushOut)));
                src.SampleAnimation(pose, tIn);
                if (tIn > pushEnd)
                {
                    float want = Mathf.Lerp(hipsUp, hipsTop, (tIn - pushEnd) / (src.length - pushEnd));
                    hipsBone.position += Vector3.up * (want - hipsBone.position.y);
                }
            });
            Object.DestroyImmediate(pose);
            return clip;
        }

        /// <summary>
        /// A new clip, <paramref name="seconds"/> long at 60 fps, made by posing <paramref name="pose"/> with <paramref name="poseAt"/> at
        /// each frame's time and recording every bone the source clip animates.
        /// </summary>
        static AnimationClip Bake(GameObject pose, AnimationClip src, string name, float seconds, string path, System.Action<float> poseAt)
        {
            Art.EnsureFolder(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));
            AssetDatabase.DeleteAsset(path);
            var paths = AnimationUtility.GetCurveBindings(src).Select(b => b.path).Distinct().ToArray();
            var bones = paths.Select(p => p.Length == 0 ? pose.transform : pose.transform.Find(p)).ToArray();
            int frames = Mathf.Max(4, Mathf.RoundToInt(seconds * OutputFps));
            float dt = seconds / frames;
            var clip = new AnimationClip { name = name, frameRate = OutputFps };
            var values = new Dictionary<EditorCurveBinding, float[]>();
            EditorCurveBinding Bind(string p, string property) => EditorCurveBinding.FloatCurve(p, typeof(Transform), property);
            string[] axes = { "x", "y", "z", "w" };
            void Put(EditorCurveBinding b, int f, float v)
            {
                if (!values.TryGetValue(b, out var arr)) values[b] = arr = new float[frames + 1];
                arr[f] = v;
            }
            for (int f = 0; f <= frames; f++)
            {
                poseAt(f * dt);
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == null) continue;
                    Vector3 m = bones[i].localPosition, sc = bones[i].localScale;
                    Quaternion q = bones[i].localRotation;
                    float[] parts = { q.x, q.y, q.z, q.w };
                    for (int k = 0; k < 4; k++) Put(Bind(paths[i], "m_LocalRotation." + axes[k]), f, parts[k]);
                    for (int k = 0; k < 3; k++)
                    {
                        Put(Bind(paths[i], "m_LocalPosition." + axes[k]), f, m[k]);
                        Put(Bind(paths[i], "m_LocalScale." + axes[k]), f, sc[k]);
                    }
                }
            }
            foreach (var kv in values)
            {
                var keys = new Keyframe[frames + 1];
                for (int f = 0; f <= frames; f++)
                {
                    float slope = (kv.Value[Mathf.Min(frames, f + 1)] - kv.Value[Mathf.Max(0, f - 1)]) / ((Mathf.Min(frames, f + 1) - Mathf.Max(0, f - 1)) * dt);
                    keys[f] = new Keyframe(f * dt, kv.Value[f], slope, slope);
                }
                AnimationUtility.SetEditorCurve(clip, kv.Key, new AnimationCurve(keys));
            }
            AssetDatabase.CreateAsset(clip, path);
            return clip;
        }

        /// <summary>
        /// The clip with its travel taken out: the hips' sideways drift from the first frame to the last is removed (spread evenly
        /// over the clip), height, rotations and the natural sway stay. The character's collider does not move with a clip, so a body
        /// that travelled off it (a get-up shuffling back, a walk exported with its root motion) would snap back when the clip starts again.
        /// </summary>
        public static AnimationClip InPlace(GameObject fbx, AnimationClip src, string path)
        {
            var pose = Object.Instantiate(fbx);
            var hipsBone = pose.GetComponentsInChildren<Transform>().First(t => t.name == "Hips");
            src.SampleAnimation(pose, 0f);
            Vector3 start = hipsBone.position;
            src.SampleAnimation(pose, src.length);
            Vector3 travel = hipsBone.position - start; travel.y = 0f;
            var clip = Bake(pose, src, src.name, src.length, path, t =>
            {
                src.SampleAnimation(pose, t);
                hipsBone.position -= travel * (t / Mathf.Max(1e-4f, src.length));
            });
            clip.wrapMode = src.isLooping ? WrapMode.Loop : WrapMode.Default;
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = src.isLooping;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            Object.DestroyImmediate(pose);
            return clip;
        }

        /// <summary>The share of a throwing clip at which the hand moves forward fastest: where the object leaves it.</summary>
        public static float ReleasePoint(GameObject fbx, AnimationClip clip, string hand = "RightHand") => ReleasePoint(fbx, clip, hand, out _);

        /// <summary>A strike (punch): which hand moves forward fastest (1 right, -1 left) and the share of the clip when it does.</summary>
        /// <remarks>Speed relative to the chest, in any direction: an uppercut rises, a hook swings sideways, and the body's own
        /// lunge is not the fist's. Judged by forward speed alone, the guard hand of an uppercut won.</remarks>
        public static float StrikePoint(GameObject fbx, AnimationClip clip, out int side)
        {
            var inst = Object.Instantiate(fbx);
            var map = inst.GetComponentsInChildren<Transform>().GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            const int steps = 90;
            float bestR = 0f, bestL = 0f, atR = 0.5f, atL = 0.5f;
            Vector3 prevR = Vector3.zero, prevL = Vector3.zero;
            for (int i = 0; i <= steps; i++)
            {
                clip.SampleAnimation(inst, clip.length * i / steps);
                Vector3 chest = map["Spine2"].position;
                Vector3 r = map["RightHand"].position - chest, l = map["LeftHand"].position - chest;
                if (i > 0)
                {
                    float vr = (r - prevR).magnitude, vl = (l - prevL).magnitude;
                    if (vr > bestR) { bestR = vr; atR = (i - 0.5f) / steps; }
                    if (vl > bestL) { bestL = vl; atL = (i - 0.5f) / steps; }
                }
                prevR = r; prevL = l;
            }
            Object.DestroyImmediate(inst);
            side = bestR >= bestL ? 1 : -1;
            return side > 0 ? atR : atL;
        }

        public static float ReleasePoint(GameObject fbx, AnimationClip clip, string hand, out float peakSpeed)
        {
            var inst = Object.Instantiate(fbx);
            var map = inst.GetComponentsInChildren<Transform>().GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            clip.SampleAnimation(inst, 0f);
            Vector3 facing = map["LeftToeBase"].position - map["LeftFoot"].position + map["RightToeBase"].position - map["RightFoot"].position;
            facing.y = 0f; facing.Normalize();
            const int steps = 90;
            float best = float.MinValue, at = 0.5f;
            Vector3 prev = map[hand].position;
            for (int i = 1; i <= steps; i++)
            {
                clip.SampleAnimation(inst, clip.length * i / steps);
                Vector3 p = map[hand].position;
                float v = Vector3.Dot(p - prev, facing);
                if (v > best) { best = v; at = (i - 0.5f) / steps; }
                prev = p;
            }
            Object.DestroyImmediate(inst);
            peakSpeed = best;
            return at;
        }

        /// <summary>The share of the clip at which the left foot is furthest forward: lines up every gait, so blends do not cross the legs.</summary>
        public static float LeftFootForwardPhase(GameObject fbx, AnimationClip clip)
        {
            var inst = Object.Instantiate(fbx);
            var map = inst.GetComponentsInChildren<Transform>().GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            clip.SampleAnimation(inst, 0f);
            Vector3 facing = map["LeftToeBase"].position - map["LeftFoot"].position + map["RightToeBase"].position - map["RightFoot"].position;
            facing.y = 0f; facing.Normalize();
            const int steps = 60;
            float best = float.MinValue, phase = 0f;
            for (int i = 0; i < steps; i++)
            {
                clip.SampleAnimation(inst, clip.length * i / steps);
                float d = Vector3.Dot(map["LeftFoot"].position, facing);
                if (d > best) { best = d; phase = (float)i / steps; }
            }
            Object.DestroyImmediate(inst);
            return phase;
        }
    }
}
