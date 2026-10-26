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
            public Quaternion was, camWas;
            public bool turned, shaken;
            public Camera cam;
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
                float nodTo = Mathf.Clamp(Lean * forward, -MostBack, MostForward), sideTo = Mathf.Clamp(Side * across, -MostSide, MostSide);
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
            float t = clock, b = buzz * Buzz;
            foreach (Kerbal k in crew)
            {
                if (k == null || !heads.TryGetValue(k, out Head h) || h.bone == null || h.turned) continue;
                float nod = h.nod + b * (0.5f * Mathf.Sin(h.f1 * 6.283f * t + h.p1) + 0.3f * Mathf.Sin(h.f2 * 6.283f * t + h.p2) + 0.2f * Mathf.Sin(h.f3 * 6.283f * t + h.p3));
                float side = h.side + 0.7f * b * (0.5f * Mathf.Sin(h.f1 * 5.9f * t + h.q1) + 0.3f * Mathf.Sin(h.f2 * 6.7f * t + h.q2) + 0.2f * Mathf.Sin(h.f3 * 6.1f * t + h.q3));
                Transform body = k.transform;
                h.was = h.bone.rotation;
                h.bone.rotation = Quaternion.AngleAxis(nod, body.right) * Quaternion.AngleAxis(-side, body.forward) * h.was;
                h.turned = true;
                leaning.Add(h);
            }
        }

        /// <summary>Every head turned this frame back as the game has it (at the frame's end, after its portraits).</summary>
        public static void Upright()
        {
            for (int i = leaning.Count - 1; i >= 0; i--)
            {
                Head h = leaning[i];
                if (h.bone != null && h.turned) h.bone.rotation = h.was;
                h.turned = false;
            }
            leaning.Clear();
        }

        /// <summary>One kerbal's portrait camera shaken on its mount with the buzz, for its picture.</summary>
        public static void ShakeCamera(Kerbal k)
        {
            if (k == null || !heads.TryGetValue(k, out Head h) || h.cam == null || buzz <= 0.01f) return;
            float t = clock, s = buzz * Shake;
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
