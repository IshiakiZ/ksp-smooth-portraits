using System.Collections.Generic;
using UnityEngine;

namespace SmoothPortraits
{
    /// <summary>
    /// (Since 0.2.0.) The crew's heads moved by the ride in their portraits, as in film from real cockpits and capsules: the game moves
    /// only their faces (its expressions, from the g they feel), never their heads.
    ///
    /// What the film shows (crews' cabin cameras through launches, staging and landings): under thrust a crew lying back in their
    /// seats are pressed into them and their heads held against the headrests; when the engines stop, the heads swing forward and
    /// bob back, a fraction of a second; at staging, a jolt and a bob; through the air's roughest stretch (near the speed of sound,
    /// and the most pressure of the air) and above all under solid boosters, everything buzzes, heads and the camera on its mount
    /// alike; liquid engines alone run smoothly. A head on its neck swings at some two to three times a second and settles within
    /// one or two swings (the head and neck's own frequency, as measured in seats and in crash tests).
    ///
    /// Here: once a frame, how the craft flown is being pushed (its felt acceleration: what it does less the pull of gravity, as the
    /// crew feel it) and how hard it is buzzing (from its engines, solid boosters most, by their thrust against its weight; from the
    /// air's pressure on it, most near the speed of sound; from rough ground under it at speed). Each kerbal's head is a weight on a
    /// spring and damper, two swings a second and a third of the way to still in each: pushed one way, it leans against the push,
    /// back into the headrest (by no more than it gives) or forward (as far as its straps let), and swings when the push changes; and
    /// it buzzes as the craft does. The portrait's camera shakes on its mount a little with the buzz.
    ///
    /// When the head is turned (2026-10-20, the owner, of the crew's portraits at a launch: "eyes are not attached. They're trying to
    /// be set back into the position they're originally at, but they keep falling out of place and going back"). Until then the
    /// neck was turned only for the picture, at the end of the frame. By then Unity had already made the frame's skin of the face (a
    /// mesh on many bones, made once a frame, after the scripts' last updates), so the face was drawn unturned, while the eyes and
    /// pupils (each on one bone, placed as it is drawn) were drawn turned: the eyes slipped out of a still face by as much as the head
    /// leant and buzzed, and came back. Measured on Bob's launch, every frame recorded: the eye's place in the face moved 0.88 px a
    /// frame (the middle of them) with the ride, 0.21 without it. Now each head is turned once a frame after the face's animation and
    /// before its skin is made (LateUpdate, TurnHeads), so face and eyes go together, and is put back once the frame's portraits are taken.
    /// (And, since 2026-10-28, turned again just before each portrait's picture, where the kerbal's animator has written its pose since:
    /// see Again.)
    /// </summary>
    static class Ride
    {
        // (a head's lean, in degrees for each g of push; how far it can go back into the headrest and forward against its straps, and
        // to the side; the head and neck's swing, two a second, damped a third of the way to still in each)
        const float Lean = 5f, Side = 4f, MostBack = 2.5f, MostForward = 15f, MostSide = 10f, Swing = 2f * Mathf.PI * 2f, Damp = 0.3f;
        const float Buzz = 1f, Shake = 0.25f;                              // degrees of head buzz and of the camera's shake at full buzz

        sealed class Head
        {
            public Transform bone;
            public float nod, side, nodSpeed, sideSpeed;
            public float f1, f2, f3, p1, p2, p3, q1, q2, q3, c1, c2;          // its own buzz: frequencies and phases
            public Quaternion was, camWas, lean, to;                       // (as the game had it, the turn given, and where that left it: see Again)
            public bool turned, shaken;
            public Camera cam;
#if DEV
            public string who;
            public Quaternion seen, seenLocal;                             // (as it was last looked at, to find where it is turned back: see Seen)
            public float movedAfter, movedBefore;                          // (the most it was turned after the ride's turn, and between being put back and turned again)
            public bool putBack;
#endif
        }

        static readonly Dictionary<Kerbal, Head> heads = new Dictionary<Kerbal, Head>();
        static readonly List<Head> leaning = new List<Head>();             // the heads turned this frame, to be put back at its end
        static int steppedFrame = -1;
        static Vector3 push;                                               // the push the crew feel, in the world, in g
        static float buzz, clock;
        internal static string Said = "";

        /// <summary>Once a frame: the ride as the craft flown has it, and every head showing swung on by it.</summary>
        public static void Step(List<Kerbal> crew, float dt)
        {
            if (steppedFrame == Time.frameCount) return;
            steppedFrame = Time.frameCount;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            dt = Mathf.Clamp(dt, 0f, 0.1f);
            clock += dt;
            float g = (float)PhysicsGlobals.GravitationalAcceleration;
            push = -(Vector3)v.perturbation / g;                              // (what pushes a head about, as the craft's frame has it)
            buzz = Buzzing(v, g);
            // (the craft's inside is a space of its own, turned as the craft is: a way in the world is turned into it by the craft's
            // turn alone. The game's InternalSpace.WorldToInternal(Vector3) is for places: it takes the craft's place off first, and
            // the first heads leant by that, pinned against their headrests and tipped to one side on the pad)
            Quaternion inside = InternalSpace.Instance != null ? InternalSpace.WorldToInternal(Quaternion.identity) : Quaternion.Inverse(v.transform.rotation);
            Vector3 p = inside * push;
            foreach (Kerbal k in crew)
            {
                if (k == null) continue;
                Head h = HeadOf(k);
                if (h == null || h.bone == null) continue;
                // (the push in the kerbal's own frame: forward where its face looks, to its right, along its neck)
                Transform body = k.transform;
                float forward = Vector3.Dot(p, body.forward), across = Vector3.Dot(p, body.right);
                float leans = Portraits.Of(Portraits.leanBy, 1f);
                float nodTo = Mathf.Clamp(Lean * leans * forward, -MostBack, MostForward), sideTo = Mathf.Clamp(Side * leans * across, -MostSide, MostSide);
                // (the spring and damper, in small steps)
                for (float left = dt; left > 1e-5f;)
                {
                    float s = Mathf.Min(left, 0.005f);
                    h.nodSpeed += (Swing * Swing * (nodTo - h.nod) - 2f * Damp * Swing * h.nodSpeed) * s;
                    h.sideSpeed += (Swing * Swing * (sideTo - h.side) - 2f * Damp * Swing * h.sideSpeed) * s;
                    h.nod += h.nodSpeed * s;
                    h.side += h.sideSpeed * s;
                    left -= s;
                }
            }
            if (heads.Count > 16) foreach (Kerbal gone in new List<Kerbal>(heads.Keys)) if (gone == null) heads.Remove(gone);
        }

        /// <summary>How hard the craft is buzzing, nought to one and a half: its engines (solid boosters rumble most, jets least) by their thrust against its weight, the air's buffeting, rough ground at speed.</summary>
        static float Buzzing(Vessel v, float g)
        {
            float engines = 0f;
            foreach (Part part in v.parts)
            {
                if (part == null) continue;
                foreach (PartModule m in part.Modules)
                {
                    if (!(m is ModuleEngines e) || !e.EngineIgnited || e.finalThrust <= 0f) continue;
                    bool solid = false, jet = false;
                    foreach (Propellant prop in e.propellants) { if (prop.name == "SolidFuel") solid = true; else if (prop.name == "IntakeAir") jet = true; }
                    engines += (solid ? 1f : jet ? 0.15f : 0.35f) * e.finalThrust;
                }
            }
            float weight = Mathf.Max(0.1f, (float)v.totalMass * g);
            float fromEngines = Mathf.Clamp(engines / (1.5f * weight), 0f, 1.5f);
            float mach = (float)v.mach, nearSound = Mathf.Exp(-((mach - 1f) / 0.2f) * ((mach - 1f) / 0.2f));
            float fromAir = Mathf.Clamp01((float)v.dynamicPressurekPa / 40f) * (0.5f + nearSound);
            float fromGround = v.Landed || v.Splashed ? Mathf.Clamp01((float)v.srfSpeed / 30f) * 0.5f : 0f;
            return Mathf.Min(1.5f, Mathf.Sqrt(fromEngines * fromEngines + fromAir * fromAir + fromGround * fromGround));
        }

        static Head HeadOf(Kerbal k)
        {
            if (heads.TryGetValue(k, out Head h)) return h;
            h = new Head { bone = Neck(k.transform) };
            var luck = new System.Random(k.GetInstanceID());
            float R(float lo, float hi) => lo + (hi - lo) * (float)luck.NextDouble();
            h.f1 = R(3.5f, 5f); h.f2 = R(6f, 8f); h.f3 = R(9f, 12f);
            h.p1 = R(0f, 6.3f); h.p2 = R(0f, 6.3f); h.p3 = R(0f, 6.3f); h.q1 = R(0f, 6.3f); h.q2 = R(0f, 6.3f); h.q3 = R(0f, 6.3f);
            h.c1 = R(11f, 15f); h.c2 = R(16f, 21f);
            ProtoCrewMember member = k.protoCrewMember;
            h.cam = member != null && member.seat != null && member.seat.portraitCamera != null ? member.seat.portraitCamera : k.kerbalCam;
            // (the face's skin made from its bones as they are at each picture, not once a frame: else it can be drawn unturned while
            // the eyes, each on one bone, are drawn turned)
            foreach (SkinnedMeshRenderer skin in k.GetComponentsInChildren<SkinnedMeshRenderer>(true)) skin.forceMatrixRecalculationPerRender = true;
#if DEV
            h.who = k.crewMemberName;
#endif
            heads[k] = h;
            return h;
        }

        /// <summary>(Since 0.3.0, for LastMoments.) The bone a kerbal's head turns on.</summary>
        internal static Transform NeckOf(Kerbal k) => k == null ? null : heads.TryGetValue(k, out Head h) ? h.bone : Neck(k.transform);

        /// <summary>The bone the head turns on: the neck's, by its name in the kerbal's model (the highest one so named), or the head's.</summary>
        static Transform Neck(Transform root)
        {
            Transform best = null; int bestDepth = int.MaxValue; bool bestNeck = false;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                bool neck = n.Contains("neck"), head = n.Contains("headpivot") || n == "bn_head" || n.EndsWith("_head");
                if (!neck && !head) continue;
                int depth = 0; for (Transform u = t; u != null && u != root; u = u.parent) depth++;
                if (best == null || (neck && !bestNeck) || (neck == bestNeck && depth < bestDepth)) { best = t; bestDepth = depth; bestNeck = neck; }
            }
            return best;
        }

        /// <summary>
        /// Once a frame, after the faces' animation and before Unity makes their skin (from LateUpdate): every head showing turned as
        /// the ride has it, so that face and eyes are drawn turned together. Put back by Upright once the frame's portraits are taken.
        /// </summary>
        public static void TurnHeads(List<Kerbal> crew)
        {
            SingleBack();
            foreach (Kerbal k in crew)
            {
                if (k == null || !heads.TryGetValue(k, out Head h) || h.bone == null || h.turned) continue;
                Turn(k, h);
                leaning.Add(h);
            }
        }

        static void Turn(Kerbal k, Head h)
        {
            float t = clock, b = buzz * Buzz * Portraits.Of(Portraits.buzzBy, 1f);
            float nod = h.nod + b * (0.5f * Mathf.Sin(h.f1 * 6.283f * t + h.p1) + 0.3f * Mathf.Sin(h.f2 * 6.283f * t + h.p2) + 0.2f * Mathf.Sin(h.f3 * 6.283f * t + h.p3));
            float side = h.side + 0.7f * b * (0.5f * Mathf.Sin(h.f1 * 5.9f * t + h.q1) + 0.3f * Mathf.Sin(h.f2 * 6.7f * t + h.q2) + 0.2f * Mathf.Sin(h.f3 * 6.1f * t + h.q3));
            Transform body = k.transform;
            h.was = h.bone.rotation;
            h.lean = Quaternion.AngleAxis(nod, body.right) * Quaternion.AngleAxis(-side, body.forward);
            h.bone.rotation = h.lean * h.was;
            h.to = h.bone.rotation;
            h.turned = true;
#if DEV
            h.seen = h.to; h.seenLocal = h.bone.localRotation;
#endif
        }

        // (a head turned for one picture taken outside the frame's turning, and its camera: see OtherPicture)
        static Head single;
        static Camera singleFor;
        static bool hooked;

        /// <summary>A head turned for another's picture put back, should that picture not have ended (from TurnHeads and Upright).</summary>
        static void SingleBack()
        {
            if (single == null) return;
            if (single.bone != null && single.turned) single.bone.rotation = single.was;
            single.turned = false;
            single = null; singleFor = null;
        }

        /// <summary>
        /// (2026-10-28) The heads turned for every other picture of a portrait as well: those taken outside the time from LateUpdate to the
        /// frame's end, while the heads stand as the game has them. The game's own timer takes those, in the middle of a frame, when this
        /// gives a portrait back to it (Portraits: none drawn for a second) or when it starts again as a portrait comes into view. The
        /// owner, of the clouds chat's films: "the heads still glitch back and forth. They jitter": one frame in every two seconds or so
        /// was the game's, the heads upright in it, two to three pixels from where the ride leant them (as far as they leant), and back.
        /// </summary>
        public static void Hook(bool on)
        {
            if (on == hooked) return;
            hooked = on;
            if (on) { Camera.onPreCull += OtherPicture; Camera.onPreRender += Again; Camera.onPostRender += OtherPictureTaken; }
            else { Camera.onPreCull -= OtherPicture; Camera.onPreRender -= Again; Camera.onPostRender -= OtherPictureTaken; SingleBack(); }
        }

        // (the camera of the portrait this mod is taking a picture of, from Portraits' drawInside: see Again)
        static Camera taking;

        /// <summary>This mod's picture of a kerbal inside begun (from Portraits' drawInside), and ended.</summary>
        public static void Taking(Kerbal k)
        {
            taking = k != null && heads.TryGetValue(k, out Head h) ? h.cam : null;
#if DEV
            Drawing = taking != null ? k.crewMemberName : "";
#endif
        }

        public static void Taken()
        {
            taking = null;
#if DEV
            Drawing = "";
#endif
        }

        /// <summary>
        /// (2026-10-28) Just before a picture of a portrait is drawn, this mod's or the game's that OtherPicture leant: any turned head
        /// that something turned back after the ride turned it, turned again from wherever it was left. The owner, of the films after
        /// the game's own pictures were leant: "jitter is still there, especially on the girl kerbal".
        ///
        /// What turns them back is the kerbal's own animator. The game has it cull its writes (CullUpdateTransforms): when its frame's
        /// animation is worked out, the kerbal counts as unseen and its bones are left as they were; the first camera to see the
        /// kerbal after that, as it culls, has the animator write them all, the neck as the animation has it among them. That camera
        /// is a portrait's, after LateUpdate: this mod's own pictures, or the game's. Measured on the pad (Who, every camera's hooks
        /// called one by one): both heads turned back by the whole lean in the culling of the first portrait to show either kerbal,
        /// some 20 times a second each; with the animators set to animate always, never. So a picture showed the head upright where
        /// its camera was the first to see that kerbal in the frame, and leant where another had been first: with the game's timer
        /// drawing beside this mod's pictures, Valentina's head stepped by 0.9 px between the two. In Melsby's picture the lean moves
        /// his head less than 0.2 px.
        /// </summary>
        static void Again(Camera cam)
        {
            if (cam == null) return;
            if (cam == taking) foreach (Head h in leaning) Again(h);
            if (single != null && cam == singleFor) Again(single);
        }

        static void Again(Head h)
        {
            if (!h.turned || h.bone == null || Mathf.Abs(Quaternion.Dot(h.bone.rotation, h.to)) >= 0.999999f) return;
#if DEV
            againFound++;
            if (TestNoAgain) return;
#endif
            h.was = h.bone.rotation;
            h.bone.rotation = h.lean * h.was;
            h.to = h.bone.rotation;
#if DEV
            h.seen = h.to; h.seenLocal = h.bone.localRotation;
#endif
        }

        static void OtherPicture(Camera cam)
        {
#if DEV
            Missed(cam);
#endif
            if (cam == null || single != null || heads.Count == 0) return;
            foreach (KeyValuePair<Kerbal, Head> kv in heads)
            {
                Head h = kv.Value;
                if (h.cam != cam || kv.Key == null || h.bone == null || h.turned) continue;
#if DEV
                if (h.putBack) h.movedBefore = Mathf.Max(h.movedBefore, Quaternion.Angle(h.bone.rotation, h.was));
#endif
                Turn(kv.Key, h);
                single = h; singleFor = cam;
#if DEV
                leantFor[h] = (leantFor.TryGetValue(h, out int n) ? n : 0) + 1;
#endif
                return;
            }
        }

#if DEV
        // (for the development build: how many other pictures each head was leant for, and the pictures of a head's portrait it was not
        // leant for, with why; see Heads)
        static readonly Dictionary<Head, int> leantFor = new Dictionary<Head, int>();
        static readonly Dictionary<string, int> missed = new Dictionary<string, int>();
        static void Missed(Camera cam)
        {
            if (cam == null) return;
            foreach (KeyValuePair<Kerbal, Head> kv in heads)
            {
                if (kv.Key == null || CamOf(kv.Key) != cam) continue;
                Head h = kv.Value;
                string why = h.turned ? "turned already (this mod's own, or the frame's turning)" : single != null ? "another head turned for its picture" : h.cam != cam ? "known by " + (h.cam != null ? h.cam.name : "no camera") : h.bone == null ? "no bone" : null;
                if (why == null) return;
                string key = kv.Key.crewMemberName + " by " + cam.name + ": " + why;
                missed[key] = (missed.TryGetValue(key, out int n) ? n : 0) + 1;
                return;
            }
        }

        static Camera CamOf(Kerbal k)
        {
            ProtoCrewMember member = k.protoCrewMember;
            return member != null && member.seat != null && member.seat.portraitCamera != null ? member.seat.portraitCamera : k.kerbalCam;
        }

        static string PathOf(Transform t, Transform root)
        {
            string path = t.name;
            for (Transform u = t.parent; u != null && u != root; u = u.parent) path = u.name + "/" + path;
            return path;
        }

        /// <summary>For the development build: each head the ride has: its kerbal, its bone, the camera it is known by and the one its portrait is drawn with now, every skin under the kerbal (made at each picture or not, and on the bone or not), and how many other pictures it was leant for and not, since the last call.</summary>
        internal static string Heads()
        {
            var said = new System.Text.StringBuilder();
            foreach (KeyValuePair<Kerbal, Head> kv in heads)
            {
                Kerbal k = kv.Key; Head h = kv.Value;
                if (k == null) { said.Append("(a kerbal gone)\n"); continue; }
                Camera now = CamOf(k);
                said.Append(k.crewMemberName).Append(": bone ").Append(h.bone != null ? PathOf(h.bone, k.transform) : "none")
                    .Append("; known by ").Append(h.cam != null ? h.cam.name + "#" + h.cam.GetInstanceID() : "none")
                    .Append(", drawn by ").Append(now != null ? now.name + "#" + now.GetInstanceID() : "none")
                    .Append("; nod ").Append(h.nod.ToString("F2")).Append(", side ").Append(h.side.ToString("F2"))
                    .Append("; leant for ").Append(leantFor.TryGetValue(h, out int n) ? n : 0).Append(" other pictures")
                    .Append("; turned after the ride's turn by at most ").Append(h.movedAfter.ToString("F3")).Append("°, between put back and turned again by at most ").Append(h.movedBefore.ToString("F3")).Append("°\n");
                h.movedAfter = h.movedBefore = 0f;
                foreach (SkinnedMeshRenderer skin in k.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    bool onBone = false;
                    if (h.bone != null && skin.bones != null) foreach (Transform b in skin.bones) if (b != null && b.IsChildOf(h.bone)) { onBone = true; break; }
                    said.Append("  skin ").Append(skin.name).Append(skin.enabled && skin.gameObject.activeInHierarchy ? "" : " (hidden)")
                        .Append(skin.forceMatrixRecalculationPerRender ? ", made at each picture" : ", made once a frame")
                        .Append(onBone ? ", on the bone" : ", not on the bone").Append('\n');
                }
            }
            said.Append("turned after the ride's turn, at most: by the frame's end ").Append(movedBy[0].ToString("F3")).Append("°, after its pictures ").Append(movedBy[1].ToString("F3")).Append("°, at the put back ").Append(movedBy[2].ToString("F3")).Append("°\n");
            movedBy[0] = movedBy[1] = movedBy[2] = 0f;
            foreach (KeyValuePair<string, int> m in missed) said.Append("not leant: ").Append(m.Key).Append(" x").Append(m.Value).Append('\n');
            leantFor.Clear(); missed.Clear();
            return said.Length > 0 ? said.ToString() : "no heads";
        }
#endif

        static void OtherPictureTaken(Camera cam) { if (single != null && cam == singleFor) SingleBack(); }

#if DEV
        // (the most any head was turned after the ride's turn: by the frame's end before its pictures, after them, and at the put back)
        static readonly float[] movedBy = new float[3];
        internal static void Check(int at)
        {
            foreach (Head h in leaning) if (h.bone != null && h.turned) movedBy[at] = Mathf.Max(movedBy[at], Quaternion.Angle(h.bone.rotation, h.to));
        }

        // (where a turned head was found turned back, and in whose picture, with the most it was turned by; see Seen and Who)
        internal static string Drawing = "";
        internal static bool TestNoAgain;                                  // (the heads not turned again before their pictures, to compare)
        static int againFound;
        static readonly Dictionary<string, int> undone = new Dictionary<string, int>();
        static readonly Dictionary<string, float> undoneMost = new Dictionary<string, float>();

        /// <summary>Every turned head looked at: any turned since it was last looked at is counted against where it was found and whose picture this mod was taking.</summary>
        internal static void Seen(string at, Camera cam)
        {
            foreach (Head h in leaning) Seen(h, at, cam);
            if (single != null) Seen(single, at, cam);
        }

        static void Seen(Head h, string at, Camera cam)
        {
            if (h.bone == null || !h.turned) return;
            float world = Quaternion.Angle(h.bone.rotation, h.seen), own = Quaternion.Angle(h.bone.localRotation, h.seenLocal);
            if (world < 0.05f && own < 0.05f) return;
            string key = h.who + " turned " + (own >= 0.05f ? "on its own bone" : "with what it hangs on") + ", " + at + (cam != null ? " for " + cam.name : "") + (Drawing.Length > 0 ? ", in this mod's picture of " + Drawing : ", outside this mod's pictures");
            undone[key] = (undone.TryGetValue(key, out int n) ? n : 0) + 1;
            undoneMost[key] = Mathf.Max(undoneMost.TryGetValue(key, out float m) ? m : 0f, world);
            h.seen = h.bone.rotation; h.seenLocal = h.bone.localRotation;
        }

        // (every camera's hooks called one by one, each followed by a look at the heads; see Who)
        static Camera.CameraCallback[] preCull, preRender, postRender;
        static Camera.CameraCallback wrapCull, wrapRender, wrapPost;

        static Camera.CameraCallback[] Each(Camera.CameraCallback all) => all == null ? new Camera.CameraCallback[0] : System.Array.ConvertAll(all.GetInvocationList(), d => (Camera.CameraCallback)d);

        static Camera.CameraCallback OneByOne(Camera.CameraCallback[] each, string stage, string before)
        {
            return cam =>
            {
                Seen(before, cam);
                foreach (Camera.CameraCallback one in each)
                {
                    try { one(cam); }
                    catch (System.Exception ex) { Debug.LogException(ex); }
                    System.Reflection.MethodInfo m = one.Method;
                    Seen(stage + ", by " + (m.DeclaringType != null ? m.DeclaringType.FullName + " (" + m.DeclaringType.Assembly.GetName().Name + ")" : "?") + "." + m.Name, cam);
                }
            };
        }

        static Camera.CameraCallback Back(Camera.CameraCallback now, Camera.CameraCallback wrap, Camera.CameraCallback[] was)
        {
            Camera.CameraCallback all = null;
            foreach (Camera.CameraCallback one in was) all += one;
            // (anything hooked on while they were called one by one comes after them; anything let go meanwhile is back: keep it short)
            return all + (Camera.CameraCallback)System.Delegate.Remove(now, wrap);
        }

        /// <summary>For the development build: Who("start") calls every camera's hooks one by one, looking at the turned heads after each; Who("") stops, and says where they were found turned back, and what else might have done it (another copy of this mod still running, what the portraits' cameras carry, the crew's animators).</summary>
        internal static string Who(string what)
        {
            if (what == "start")
            {
                undone.Clear(); undoneMost.Clear(); againFound = 0;
                if (preCull == null)
                {
                    preCull = Each(Camera.onPreCull); preRender = Each(Camera.onPreRender); postRender = Each(Camera.onPostRender);
                    Camera.onPreCull = wrapCull = OneByOne(preCull, "before culling", "before culling, before its first hook");
                    Camera.onPreRender = wrapRender = OneByOne(preRender, "after culling", "in culling");
                    Camera.onPostRender = wrapPost = OneByOne(postRender, "after drawing", "in drawing");
                }
                return "calling " + preCull.Length + " + " + preRender.Length + " + " + postRender.Length + " camera hooks one by one";
            }
            if (preCull != null)
            {
                Camera.onPreCull = Back(Camera.onPreCull, wrapCull, preCull);
                Camera.onPreRender = Back(Camera.onPreRender, wrapRender, preRender);
                Camera.onPostRender = Back(Camera.onPostRender, wrapPost, postRender);
                preCull = preRender = postRender = null;
            }
            var said = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, int> u in undone) said.Append(u.Key).Append(": x").Append(u.Value).Append(", at most ").Append(undoneMost[u.Key].ToString("F3")).Append("°\n");
            if (undone.Count == 0) said.Append("no turned head found turned back\n");
            said.Append("turned again before their pictures: ").Append(againFound).Append(TestNoAgain ? " (found only: switched off)" : "").Append('\n');
            // (another copy of this mod running in the game: the release one a quickload brings back, or an earlier development build)
            foreach (System.Reflection.Assembly a in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type t;
                try { t = a.GetType("SmoothPortraits.Ride", false); } catch (System.Exception) { continue; }
                if (t == null || t == typeof(Ride)) continue;
                object hooked = t.GetField("hooked", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null);
                object those = t.GetField("heads", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null);
                said.Append("another copy of the ride: ").Append(a.GetName().Name).Append(", hooked ").Append(hooked ?? "?").Append(", heads ").Append(those is System.Collections.ICollection c ? c.Count.ToString() : "?").Append('\n');
            }
            foreach (MonoBehaviour b in Object.FindObjectsOfType<MonoBehaviour>())
                if (b.GetType().FullName == "SmoothPortraits.Portraits") said.Append("Portraits running: ").Append(b.GetType().Assembly.GetName().Name).Append(b.GetType() == typeof(Portraits) ? " (this one)" : "").Append(b.isActiveAndEnabled ? "" : " (off)").Append('\n');
            foreach (KeyValuePair<Kerbal, Head> kv in heads)
            {
                if (kv.Key == null) continue;
                said.Append(kv.Value.who).Append(": ");
                if (kv.Value.cam != null) { said.Append(kv.Value.cam.name).Append(" carries"); foreach (Behaviour b in kv.Value.cam.GetComponents<Behaviour>()) said.Append(' ').Append(b.GetType().Name).Append(b.enabled ? "" : " (off)"); }
                foreach (Animator a in kv.Key.GetComponentsInChildren<Animator>(true)) said.Append("; animator ").Append(a.name).Append(" culling ").Append(a.cullingMode).Append(", update ").Append(a.updateMode);
                foreach (SkinnedMeshRenderer s in kv.Key.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (s.isVisible) { said.Append("; seen now"); break; }
                said.Append('\n');
            }
            undone.Clear(); undoneMost.Clear(); againFound = 0;
            return said.ToString();
        }

        // (the crew's animators set to animate always, and as the game had them)
        static readonly Dictionary<Animator, AnimatorCullingMode> culling = new Dictionary<Animator, AnimatorCullingMode>();

        /// <summary>For the development build: the crew's animators animating whether seen or not ("always"), or as the game had them ("").</summary>
        internal static string Culling(string how)
        {
            if (how == "always")
            {
                foreach (Kerbal k in heads.Keys)
                    if (k != null) foreach (Animator a in k.GetComponentsInChildren<Animator>(true)) { if (!culling.ContainsKey(a)) culling[a] = a.cullingMode; a.cullingMode = AnimatorCullingMode.AlwaysAnimate; }
                return culling.Count + " animators animating always";
            }
            int n = 0;
            foreach (KeyValuePair<Animator, AnimatorCullingMode> c in culling) if (c.Key != null) { c.Key.cullingMode = c.Value; n++; }
            culling.Clear();
            return n + " animators as the game had them";
        }
#endif

        /// <summary>Every head turned this frame back as the game has it (at the frame's end, after its portraits).</summary>
        public static void Upright()
        {
#if DEV
            Check(2);
#endif
            SingleBack();
            for (int i = leaning.Count - 1; i >= 0; i--)
            {
                Head h = leaning[i];
#if DEV
                if (h.bone != null && h.turned) { h.movedAfter = Mathf.Max(h.movedAfter, Quaternion.Angle(h.bone.rotation, h.to)); h.putBack = true; }
#endif
                if (h.bone != null && h.turned) h.bone.rotation = h.was;
                h.turned = false;
            }
            leaning.Clear();
        }

        /// <summary>One kerbal's portrait camera shaken on its mount with the buzz, for its picture.</summary>
        public static void ShakeCamera(Kerbal k)
        {
            if (k == null || !heads.TryGetValue(k, out Head h) || h.cam == null || buzz <= 0.01f) return;
            float t = clock, s = buzz * Shake * Portraits.Of(Portraits.shakeBy, 1f);
            Transform c = h.cam.transform;
            h.camWas = c.localRotation;
            c.localRotation = h.camWas * Quaternion.Euler(s * Mathf.Sin(h.c1 * 6.283f * t + h.p2), s * Mathf.Sin(h.c2 * 6.283f * t + h.q1), 0f);
            h.shaken = true;
        }

        /// <summary>And its camera back, after the picture.</summary>
        public static void StillCamera(Kerbal k)
        {
            if (k == null || !heads.TryGetValue(k, out Head h) || !h.shaken) return;
            h.shaken = false;
            if (h.cam != null) h.cam.transform.localRotation = h.camWas;
        }

        /// <summary>(For the readout.) The push felt and the buzz, and the first heads' lean.</summary>
        public static string Now(List<Kerbal> crew)
        {
            var say = new System.Text.StringBuilder("push " + push.magnitude.ToString("F2") + " g, buzz " + buzz.ToString("F2"));
            foreach (Kerbal k in crew)
                if (k != null && heads.TryGetValue(k, out Head h))
                    say.Append("; ").Append(k.crewMemberName).Append(h.bone != null ? " (" + h.bone.name + ")" : " (no neck)").Append(" nod ").Append(h.nod.ToString("F1")).Append("°, side ").Append(h.side.ToString("F1")).Append('°');
            return say.ToString();
        }
    }
}
