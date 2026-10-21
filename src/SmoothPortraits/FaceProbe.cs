#if DEV
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace SmoothPortraits
{
    /// <summary>
    /// (Development build only.) The first kerbal's face through a stretch of frames, for the owner's report of 2026-10-20 ("the
    /// Kerbals' eyes are growing and shrinking really fast, especially when it's launching the rocket"): its animators (culling,
    /// update mode, speed), the game's expression system (its rates, and each frame its expression and variances), each layer's
    /// state, the eyes' pieces and the face's blend shapes, frame by frame, sampled after the animators have run. Through dev_call:
    /// Faces("seconds"), then Said() once it is done. Nothing is left in the game afterwards.
    /// </summary>
    public sealed class FaceProbe : MonoBehaviour
    {
        static FaceProbe running;
        static string said = "not run";
        float seconds = 6f;

        public static string Faces(string arg)
        {
            if (running != null) return "already running";
            if (!float.TryParse((arg ?? "").Trim(), out float s) || s <= 0f) s = 6f;
            if (FlightGlobals.ActiveVessel == null) return "no craft";
            running = new GameObject("SmoothPortraits.FaceProbe").AddComponent<FaceProbe>();
            running.seconds = s;
            said = "running for " + s + " s";
            return "started";
        }

        public static string Said() => said;

        /// <summary>The first kerbal's skeleton where its face is: each eye, pupil, driven, neck and head piece with its parents up to the kerbal, and the bones each skinned mesh of the face hangs on.</summary>
        public static string Tree()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            Kerbal k = null;
            if (v != null) foreach (ProtoCrewMember c in v.GetVesselCrew()) if (c != null && c.KerbalRef != null) { k = c.KerbalRef; break; }
            if (k == null) return "no kerbal inside";
            var text = new StringBuilder(k.crewMemberName + ":\n");
            foreach (Transform t in k.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                if (!(n.Contains("eye") || n.Contains("pupil") || n.Contains("jntdrv") || n.Contains("neck") || n.Contains("headpivot") || n.Contains("head"))) continue;
                var path = new StringBuilder(t.name);
                for (Transform u = t.parent; u != null && u != k.transform.parent; u = u.parent) path.Insert(0, u.name + "/");
                text.Append("  ").Append(path).Append(t.GetComponent<SkinnedMeshRenderer>() != null ? " (skinned mesh)" : t.GetComponent<Renderer>() != null ? " (mesh)" : "").Append('\n');
            }
            foreach (SkinnedMeshRenderer r in k.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                string n = r.name.ToLowerInvariant();
                if (!(n.Contains("eye") || n.Contains("pupil") || n.Contains("head"))) continue;
                text.Append("  ").Append(r.name).Append(": root ").Append(r.rootBone != null ? r.rootBone.name : "-").Append(", bones");
                foreach (Transform b in r.bones) if (b != null) text.Append(' ').Append(b.name);
                text.Append('\n');
            }
            return text.ToString();
        }

        string JointsSaid(float t)
        {
            int n = jointTrack.Count;
            string[] kinds = { "scale", "place", "turn" };
            float[] least = { 0.01f, 0.0005f, 0.5f };
            var found = new List<KeyValuePair<float, string>>();
            var series = new Dictionary<string, List<float>>();
            for (int i = 0; i < faceJoints.Count && n > 1; i++)
                for (int k = 0; k < 3; k++)
                    for (int c = 0; c < 3; c++)
                    {
                        float prev = jointTrack[0][3 * i + k][c], acc = prev, lo = acc, hi = acc, lastStep = 0f;
                        int turns = 0;
                        var values = new List<float> { acc };
                        for (int f = 1; f < n; f++)
                        {
                            float v = jointTrack[f][3 * i + k][c];
                            float step = k == 2 ? Mathf.DeltaAngle(prev, v) : v - prev;
                            prev = v;
                            acc += step;
                            values.Add(acc);
                            lo = Mathf.Min(lo, acc); hi = Mathf.Max(hi, acc);
                            if (Mathf.Abs(step) < least[k] * 0.02f) continue;
                            if (lastStep != 0f && Mathf.Sign(step) != Mathf.Sign(lastStep)) turns++;
                            lastStep = step;
                        }
                        if (hi - lo < least[k]) continue;
                        string name = faceJoints[i].name + " " + kinds[k] + " " + "xyz"[c];
                        found.Add(new KeyValuePair<float, string>(turns / t, name + ": range " + (hi - lo).ToString(k == 1 ? "F4" : "F3") + ", turns back " + (turns / t).ToString("F1") + " a second"));
                        series[name] = values;
                    }
            found.Sort((a, b) => b.Key.CompareTo(a.Key));
            var text = new StringBuilder(n + " frames over " + t.ToString("F1") + " s; " + faceJoints.Count + " face joints; " + found.Count + " of their parts move:\n");
            for (int i = 0; i < found.Count; i++) text.Append("  ").Append(found[i].Value).Append('\n');
            text.Append("joints:");
            foreach (Transform j in faceJoints) text.Append(' ').Append(j != null ? j.name : "-");
            text.Append('\n');
            // (the three that turn back most often, frame by frame for the first 90 frames)
            for (int i = 0; i < found.Count && i < 3; i++)
            {
                string name = found[i].Value.Split(':')[0];
                if (!series.TryGetValue(name, out List<float> values)) continue;
                text.Append(name).Append(':');
                for (int f = 0; f < values.Count && f < 90; f++) text.Append(' ').Append(values[f].ToString("F3"));
                text.Append('\n');
            }
            return text.ToString();
        }

        /// <summary>Every joint of the first kerbal's face (eyes, lids, brows, mouth: their names), frame by frame for so many seconds: which move, how far, and how often they turn back. Read with Said().</summary>
        public static string Joints(string arg)
        {
            if (running != null) return "already running";
            string r = Faces(arg);
            if (running != null) running.joints = true;
            return r;
        }

        bool joints;
        readonly List<Transform> faceJoints = new List<Transform>();
        readonly List<Vector3[]> jointTrack = new List<Vector3[]>();     // each frame: per joint (scale, position, euler)

        Kerbal kerbal;
        kerbalExpressionSystem faces;
        Animator[] animators;
        readonly List<Transform> eyes = new List<Transform>();
        readonly List<SkinnedMeshRenderer> shaped = new List<SkinnedMeshRenderer>();
        FieldInfo varianceField, secondField;
        readonly StringBuilder rows = new StringBuilder();
        float[] last;
        int frames, posed, expressionMoved, layerChanges;
        float began, lastExpression = float.NaN;
        int[] lastState;

        void Start()
        {
            began = Time.unscaledTime;
            foreach (ProtoCrewMember c in FlightGlobals.ActiveVessel.GetVesselCrew())
                if (c != null && c.KerbalRef != null) { kerbal = c.KerbalRef; break; }
            if (kerbal == null) { said = "no kerbal inside"; running = null; Destroy(gameObject); return; }
            faces = kerbal.GetComponentInChildren<kerbalExpressionSystem>(true);
            animators = kerbal.GetComponentsInChildren<Animator>(true);
            foreach (Transform t in kerbal.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                if ((n.Contains("eye") || n.Contains("pupil")) && eyes.Count < 4) eyes.Add(t);
            }
            foreach (SkinnedMeshRenderer r in kerbal.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0) shaped.Add(r);
            foreach (Transform t in kerbal.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                if (n.Contains("eye") || n.Contains("lid") || n.Contains("brow") || n.Contains("pupil") || n.Contains("mouth") || n.Contains("jaw") || n.Contains("lip") || n.Contains("head") || n.Contains("neck")) faceJoints.Add(t);
            }
            varianceField = typeof(kerbalExpressionSystem).GetField("variance", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            secondField = typeof(kerbalExpressionSystem).GetField("secondaryVariance", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var head = new StringBuilder(kerbal.crewMemberName + ": " + animators.Length + " animators: ");
            foreach (Animator a in animators)
                head.Append(a.name + " (culling " + a.cullingMode + ", update " + a.updateMode + ", speed " + a.speed.ToString("F2") + ", layers " + a.layerCount + ", parameters " + a.parameterCount + (a.isActiveAndEnabled ? "" : ", off") + ") ");
            if (faces != null)
                head.Append("| expressions: lerp " + faces.expressionLerpRate + ", variance lerp " + faces.varianceLerpRate + " every " + faces.varianceUpdateInterval + " s, second variance lerp " + faces.secondaryVarianceLerpRate + " every " + faces.secondaryVarianceUpdateInterval + " s");
            else head.Append("| no expression system");
            head.Append(" | eye pieces:");
            foreach (Transform t in eyes) head.Append(" " + t.name + (t.GetComponent<Renderer>() != null ? "(drawn)" : ""));
            head.Append(" | blend shapes:");
            foreach (SkinnedMeshRenderer r in shaped)
            {
                head.Append(" " + r.name + "[");
                for (int i = 0; i < r.sharedMesh.blendShapeCount && i < 12; i++) head.Append((i > 0 ? "," : "") + r.sharedMesh.GetBlendShapeName(i));
                head.Append("]");
            }
            rows.Append(head).Append('\n');
            rows.Append("t s | expression panic whee | variance second | eye pieces' scale y | blend weights | each layer's state@time\n");
        }

        void LateUpdate()
        {
            if (kerbal == null) return;
            float t = Time.unscaledTime - began;
            frames++;
            // (the pose as the animators left it this frame: the eyes' pieces and the face's blend weights)
            var now = new List<float>();
            foreach (Transform e in eyes) now.Add(e.localScale.y);
            foreach (SkinnedMeshRenderer r in shaped)
                for (int i = 0; i < r.sharedMesh.blendShapeCount && i < 12; i++) now.Add(r.GetBlendShapeWeight(i));
            if (last != null)
            {
                bool moved = false;
                for (int i = 0; i < now.Count && i < last.Length; i++) if (Mathf.Abs(now[i] - last[i]) > 1e-5f) moved = true;
                if (moved) posed++;
            }
            last = now.ToArray();
            float expression = faces != null ? faces.expression : 0f;
            if (!float.IsNaN(lastExpression) && Mathf.Abs(expression - lastExpression) > 1e-6f) expressionMoved++;
            lastExpression = expression;
            var layers = new StringBuilder();
            Animator main = animators.Length > 0 ? animators[0] : null;
            if (main != null && main.isActiveAndEnabled)
            {
                if (lastState == null) lastState = new int[main.layerCount];
                for (int i = 0; i < main.layerCount && i < lastState.Length; i++)
                {
                    AnimatorStateInfo s = main.GetCurrentAnimatorStateInfo(i);
                    if (frames > 1 && s.fullPathHash != lastState[i]) layerChanges++;
                    lastState[i] = s.fullPathHash;
                    layers.Append(' ').Append(i).Append(':').Append((s.fullPathHash & 0xffff).ToString("x4")).Append('@').Append(s.normalizedTime.ToString("F2")).Append(main.IsInTransition(i) ? "~" : "");
                }
            }
            if (frames <= 600)
            {
                float variance = varianceField != null && faces != null ? (float)varianceField.GetValue(faces) : 0f;
                float second = secondField != null && faces != null ? (float)secondField.GetValue(faces) : 0f;
                rows.Append(t.ToString("F3")).Append(" | ").Append(expression.ToString("F3")).Append(' ').Append(faces != null ? faces.panicLevel.ToString("F2") : "-").Append(' ').Append(faces != null ? faces.wheeLevel.ToString("F2") : "-")
                    .Append(" | ").Append(variance.ToString("F2")).Append(' ').Append(second.ToString("F2")).Append(" |");
                // (what the game's seat camera bob is made of: the craft's acceleration, and the camera's turn as its last picture left it)
                Camera seatCam = kerbal.protoCrewMember != null && kerbal.protoCrewMember.seat != null ? kerbal.protoCrewMember.seat.portraitCamera : null;
                Vector3 e = seatCam != null ? seatCam.transform.localEulerAngles : Vector3.zero;
                rows.Append(' ').Append(((float)FlightGlobals.ship_acceleration.magnitude).ToString("F1")).Append(" m/s2 cam ")
                    .Append(Mathf.DeltaAngle(0f, e.x).ToString("F2")).Append(',').Append(Mathf.DeltaAngle(0f, e.y).ToString("F2")).Append(',').Append(Mathf.DeltaAngle(0f, e.z).ToString("F2"))
                    .Append(seatCam != null ? " fov " + seatCam.fieldOfView.ToString("F0") : " no seat camera");
                rows.Append(" |").Append(layers).Append('\n');
            }
            if (joints)
            {
                var one = new Vector3[faceJoints.Count * 3];
                for (int i = 0; i < faceJoints.Count; i++)
                {
                    Transform j = faceJoints[i];
                    one[3 * i] = j != null ? j.localScale : Vector3.zero;
                    one[3 * i + 1] = j != null ? j.localPosition : Vector3.zero;
                    one[3 * i + 2] = j != null ? j.localEulerAngles : Vector3.zero;
                }
                jointTrack.Add(one);
            }
            if (t < seconds) return;
            if (joints) { said = JointsSaid(t); running = null; Destroy(gameObject); return; }
            said = frames + " frames over " + t.ToString("F1") + " s (" + (frames / t).ToString("F0") + " a second): the pose changed in " + posed + " of them, the expression in " + expressionMoved +
                   ", a layer's state " + layerChanges + " times\n" + rows;
            running = null;
            Destroy(gameObject);
        }
    }
}
#endif
