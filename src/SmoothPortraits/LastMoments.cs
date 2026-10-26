using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Keystone;
using KSP.UI.Screens.Flight;
using UnityEngine;
using UnityEngine.UI;
using Random = UnityEngine.Random;

namespace SmoothPortraits
{
    /// <summary>
    /// (Since 0.3.0.) A kerbal's last moments in their portrait. The game takes a dead kerbal's portrait away the instant they die (Kerbal.die:
    /// the gallery despawns it, and the cabin they sat in, which the portrait's camera looks at, goes with their part); asked for on 2026-10-25
    /// ("Have a cool explosion animation in the Kerbals' portraits when they die and explode"), the portrait now stays a moment longer and shows
    /// what the cabin's camera saw.
    ///
    /// What a camera in a cabin shows of a crash, as in film from cockpit and dashboard cameras: the blow throws the picture about (the camera
    /// on its mount, and the heads on their necks, faster than they can follow); a burst of fire floods the cabin with its light, white at
    /// first and too bright for the camera, then orange and flickering as the flames take hold, and the smoke gathers under the roof and comes
    /// down, darkening it; the camera's link then goes, its picture tearing sideways in bands and rolling as it loses its timing, and the
    /// game's own static takes its place: the feed is lost. A blow with no fire (a cabin crushed, a kerbal outside striking the ground) is the
    /// jolt and the loss alone.
    ///
    /// How. The light on their faces is the cabin's own: their picture is taken by the game's own routine with a lamp put into the cabin and
    /// their head thrown on its neck, four times over (the blow, the flash, the fire's light twice). A picture taken in the middle of a frame
    /// holds the frame up while the scene is made ready for drawing, so while the craft flown could be about to crash these are taken ahead,
    /// at the end of frames, a few times a second (see Prepare); at a death they are only picked up, and taken there and then only if there
    /// were none. When a part with crew in it is about to go (the game's onPartWillDie, before it kills them), a copy of each of their
    /// portraits that is showing (the picture in its mask and the tag with their name, without the portrait's workings) is laid over it on
    /// the same canvas, the game's own is hidden and taken away as ever, and the copy plays the pictures out: about two seconds with fire (a
    /// blast the game queued near the part, FXMonger, through which every mod's explosions go, or propellant in the part), a second and a
    /// quarter without, by the game's own clock (so that it waits while the game is paused, and keeps with the crash when the game slows).
    /// It plays on only once the kerbal is known to be dead (the game's onCrewKilled).
    /// </summary>
    public static class LastMoments
    {
        internal static Keystone.Toggle Setting;

        static readonly List<Ghost> ghosts = new List<Ghost>();
        static readonly List<Blast> blasts = new List<Blast>();
        static readonly HashSet<object> seenThisFrame = new HashSet<object>();
        static int seenFrame = -1;
        static bool looked, hooked;
        static FieldInfo monger, queue, queuePlace, queuePower;
        static Texture2D white;
        internal static string Said = "";
        static Light ivaSun;
        static float sunLooked = -100f;

        /// <summary>The light of the sun inside the craft (the game's IVASun): looked for once a scene, not at a death (a search of every object there is).</summary>
        static Light Sun()
        {
            if (ivaSun != null || Time.unscaledTime - sunLooked < 5f) return ivaSun;
            sunLooked = Time.unscaledTime;
            IVASun sun = UnityEngine.Object.FindObjectOfType<IVASun>();
            return ivaSun = sun != null ? sun.GetComponent<Light>() : null;
        }
        public static bool TestShadows;          // (development build: the fire's lamp casting shadows, to compare what it costs)
        internal static string Split = "", CopySplit = "";

        /// <summary>An explosion the game was asked for lately: where (on its world, so that the scene's shifts do not move it), how hard, when.</summary>
        struct Blast { public CelestialBody body; public double lat, lon, alt, power; public float at; }

        // (the game's events take only a method of an object: a static one throws as it is added, EventData naming its object)
        sealed class Hooks
        {
            public void WillDie(Part p) => LastMoments.WillDie(p);
            public void Killed(EventReport report) => LastMoments.Killed(report);
        }
        static readonly Hooks hooks = new Hooks();

        public static void Hook()
        {
            if (hooked) return;
            hooked = true;
            ivaSun = null; sunLooked = -100f;
            Sun();
            GameEvents.onPartWillDie.Add(hooks.WillDie);
            GameEvents.onCrewKilled.Add(hooks.Killed);
            if (!looked)
            {
                looked = true;
                monger = Reach.Field(typeof(FXMonger), "fetch");
                queue = Reach.Field(typeof(FXMonger), "queuedExplosions");
                Type proto = typeof(FXMonger).GetNestedType("ProtoExplosion", BindingFlags.NonPublic | BindingFlags.Public);
                if (proto != null)
                {
                    queuePlace = proto.GetField("position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    queuePower = proto.GetField("power", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
            }
        }

        public static void Unhook()
        {
            if (!hooked) return;
            hooked = false;
            GameEvents.onPartWillDie.Remove(hooks.WillDie);
            GameEvents.onCrewKilled.Remove(hooks.Killed);
            foreach (Ghost g in ghosts) g.Gone();
            ghosts.Clear();
            blasts.Clear();
            Unready();
        }

        // ================================================================== the explosions the game is asked for

        /// <summary>
        /// At the end of every step of the physics: the explosions queued in it (the game draws them in its LateUpdate and forgets them), kept
        /// two seconds. A part about to go looks among them for the fire that took it.
        /// </summary>
        public static IEnumerator Watch()
        {
            var step = new WaitForFixedUpdate();
            while (true)
            {
                yield return step;
                try { Look(); } catch (Exception) { }
            }
        }

        static void Look()
        {
            if (monger == null || queue == null || queuePlace == null || queuePower == null) return;
            object fx = monger.GetValue(null);
            if (fx == null || !(queue.GetValue(fx) is IList list) || list.Count == 0) return;
            if (seenFrame != Time.frameCount) { seenFrame = Time.frameCount; seenThisFrame.Clear(); }
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body == null) return;
            foreach (object e in list)
            {
                if (e == null || !seenThisFrame.Add(e)) continue;
                Vector3d at = (Vector3d)queuePlace.GetValue(e);
                if (blasts.Count >= 24) blasts.RemoveAt(0);
                blasts.Add(new Blast { body = body, lat = body.GetLatitude(at), lon = body.GetLongitude(at), alt = body.GetAltitude(at), power = (double)queuePower.GetValue(e), at = Time.time });
            }
        }

        /// <summary>The strongest explosion within so far of a place in the last second and a half: where, and how hard (0 if none).</summary>
        static double NearestBlast(Vector3 place, float reach, out Vector3 at)
        {
            at = place;
            double best = 0.0;
            float now = Time.time;
            foreach (Blast b in blasts)
            {
                if (now - b.at > 1.5f || b.body != FlightGlobals.currentMainBody) continue;
                Vector3 where = b.body.GetWorldSurfacePosition(b.lat, b.lon, b.alt);
                if ((where - place).sqrMagnitude > reach * reach || b.power <= best) continue;
                best = b.power; at = where;
            }
            return best;
        }

        /// <summary>Kilograms of what burns in a part (its fuel, oxidiser, monopropellant, solid propellant).</summary>
        static double Burnable(Part p)
        {
            double kg = 0;
            if (p == null || p.Resources == null) return 0;
            foreach (PartResource r in p.Resources)
            {
                if (r == null || r.info == null || r.amount <= 0) continue;
                string n = r.resourceName;
                if (n == "ElectricCharge" || n == "Ablator" || n == "IntakeAir" || n == "Ore" || n == "XenonGas" || n.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                kg += r.amount * r.info.density * 1000.0;
            }
            return kg;
        }

        // ================================================================== the pictures taken ahead

        static readonly Dictionary<Kerbal, Shots> ready = new Dictionary<Kerbal, Shots>();
        static readonly List<Kerbal> readyGone = new List<Kerbal>();
        static float dangerLooked = -100f, dangerUntil = -100f;
        public static bool TestDanger;           // (development build: taken ahead whatever is happening)
        public static bool TestHidden;           // (development build: the copies not shown, to see what is under them)
        internal static long PreparedTicks;
        internal static int Prepared;

        /// <summary>
        /// At the end of every frame, while the craft flown could be about to crash (Danger): one kerbal's last pictures taken again, the one whose
        /// are oldest, at most four times a second each. A picture taken in the middle of a frame, when a kerbal dies, holds the frame up while the
        /// whole scene is made ready for drawing (11 to 17 ms for the first, measured 2026-10-25, the frame of a rehearsal 45 ms against 25); at
        /// the end of a frame it costs a fraction of a millisecond. A fire that is not where it was guessed lights them from the wrong side; that is
        /// all.
        /// </summary>
        public static void Prepare()
        {
            if (Setting == null || !Setting.Value) { Unready(); return; }
            float now = Time.unscaledTime;
            if (now >= dangerLooked) { dangerLooked = now + 0.2f; if (TestDanger || Danger()) dangerUntil = now + 2f; }
            if (now > dangerUntil) { if (ready.Count > 0 && now > dangerUntil + 3f) Unready(); return; }
            KerbalPortraitGallery gallery = KerbalPortraitGallery.Instance;
            if (gallery == null || !gallery.isActiveAndEnabled || !(Portraits.Crew(gallery) is List<KerbalPortrait> portraits)) return;
            Kerbal pick = null;
            float oldest = float.MaxValue;
            foreach (KerbalPortrait portrait in portraits)
            {
                Kerbal k = portrait != null ? portrait.crewMember : null;
                if (k == null || !k.isActiveAndEnabled || k.InPart == null || !Portraits.Showing(portrait)) continue;
                float at = ready.TryGetValue(k, out Shots had) ? had.at : -100f;
                if (at < oldest) { oldest = at; pick = k; }
            }
            if (pick == null || now - oldest < 0.25f) return;
            Part part = pick.InPart;
            Vector3 middle = part.transform.position, up = FlightGlobals.getUpAxis(middle);
            // (a fire from below and to one side: where a crash's fire most often is)
            Vector3 side = Vector3.ProjectOnPlane(Random.onUnitSphere, up);
            Vector3 from = middle - up * 0.6f + (side.sqrMagnitude > 1e-3f ? side.normalized : Vector3.zero) * 0.8f;
            if (!ready.TryGetValue(pick, out Shots shots)) ready[pick] = shots = new Shots();
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            shots.Take(pick, part, true, from, up);
            PreparedTicks += System.Diagnostics.Stopwatch.GetTimestamp() - began;
            Prepared++;
            // (and those no longer showing let go)
            readyGone.Clear();
            foreach (Kerbal k in ready.Keys) if (k == null || !k.isActiveAndEnabled) readyGone.Add(k);
            foreach (Kerbal k in readyGone) { ready[k].Free(); ready.Remove(k); }
        }

        /// <summary>
        /// A few seconds into a flight with crew showing: a last picture made and put away unseen, so that the work done only the first time (the
        /// code made ready, the first pieces of user interface of their kind: 36 ms of the first death's frame, measured, against 4 to 7 after)
        /// is done here and not in a crash.
        /// </summary>
        public static IEnumerator WarmUp()
        {
            float until = Time.unscaledTime + 60f;
            while (Time.unscaledTime < until)
            {
                yield return new WaitForSeconds(1f);
                KerbalPortraitGallery gallery = KerbalPortraitGallery.Instance;
                if (Setting == null || !Setting.Value || gallery == null || !gallery.isActiveAndEnabled || !(Portraits.Crew(gallery) is List<KerbalPortrait> portraits)) continue;
                KerbalPortrait first = portraits.Find(k => k != null && k.crewMember != null && Portraits.Showing(k));
                if (first == null || first.crewMember.InPart == null) continue;
                yield return new WaitForSeconds(2f);
                if (first == null || first.crewMember == null || first.crewMember.InPart == null) continue;
                Ghost g = null;
                try
                {
                    Part part = first.crewMember.InPart;
                    Vector3 up = FlightGlobals.getUpAxis(part.transform.position);
                    var shots = new Shots();
                    shots.Copy(first.crewMember.avatarTexture, 0); shots.Copy(first.crewMember.avatarTexture, 1); shots.Copy(first.crewMember.avatarTexture, 2); shots.Copy(first.crewMember.avatarTexture, 3);
                    g = Ghost.Make(first, "", part, true, part.transform.position - up, up, shots, false);
                    if (g != null) g.Hide();
                }
                catch (Exception) { }
                yield return null;
                if (g != null) { try { g.Step(0.5f); } catch (Exception) { } g.Gone(); }
                yield break;
            }
        }

        /// <summary>A kerbal's pictures taken ahead, if they were taken within the last moment: theirs now, and no longer kept.</summary>
        static Shots Fresh(Kerbal k)
        {
            if (k == null || !ready.TryGetValue(k, out Shots shots)) return null;
            ready.Remove(k);
            if (shots.count >= 4 && Time.unscaledTime - shots.at < 0.8f) return shots;
            shots.Free();
            return null;
        }

        static void Unready()
        {
            foreach (Shots shots in ready.Values) shots.Free();
            ready.Clear();
        }

        /// <summary>
        /// Whether the craft flown could be about to crash: going faster than a hard landing near the ground or the sea (within two and a half
        /// seconds of it), closing fast on another craft near it, or a part of it near the heat it breaks at.
        /// </summary>
        static bool Danger()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.packed || v.GetCrewCount() == 0) return false;
            float speed = (float)v.srfSpeed;
            if (speed > 12f && v.radarAltitude < speed * 2.5f + 40f) return true;
            foreach (Vessel o in FlightGlobals.VesselsLoaded)
            {
                if (o == null || o == v || o.packed) continue;
                float closing = (float)(v.obt_velocity - o.obt_velocity).magnitude;
                if (closing > 8f && (o.CoMD - v.CoMD).magnitude < closing * 2.5f + 40f) return true;
            }
            foreach (Part part in v.parts)
                if (part != null && (part.temperature > 0.85 * part.maxTemp || (part.skinMaxTemp > 0 && part.skinTemperature > 0.85 * part.skinMaxTemp))) return true;
            return false;
        }

        // ================================================================== a part with crew in it about to go

        static void WillDie(Part p)
        {
            try { WillDieNow(p); }
            catch (Exception ex) { Kit.Log("Smooth Portraits", "a last picture could not be taken: " + ex.Message); }
        }

        static void WillDieNow(Part p)
        {
            if (p == null || Setting == null || !Setting.Value || p.protoModuleCrew == null || p.protoModuleCrew.Count == 0) return;
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            KerbalPortraitGallery gallery = KerbalPortraitGallery.Instance;
            if (gallery == null || !gallery.isActiveAndEnabled || !(Portraits.Crew(gallery) is List<KerbalPortrait> portraits)) return;
            Look();
            Vector3 middle = p.transform.position;
            // (fire: a blast queued near the part, or its own propellant, which the game blows up with it)
            double power = NearestBlast(middle, 30f, out Vector3 from);
#if DEV
            Before = ((System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F1") + " ms";
#endif
            bool fire = power >= 0.2 || Burnable(p) >= 5.0;
            Vector3 up = FlightGlobals.getUpAxis(middle);
            if ((from - middle).sqrMagnitude < 0.25f) from = middle - up * 0.6f + Random.onUnitSphere * 0.4f;
            int made = 0;
            foreach (ProtoCrewMember member in p.protoModuleCrew)
            {
                if (member == null) continue;
                foreach (KerbalPortrait portrait in portraits)
                {
                    if (portrait == null || portrait.portrait == null || !portrait.gameObject.activeInHierarchy) continue;
                    bool theirs = portrait.crewMember != null ? portrait.crewMember.protoCrewMember == member : portrait.crewEVAMember != null && portrait.crewEVAMember.part == p;
                    if (!theirs || !Portraits.Showing(portrait)) continue;
                    Ghost g = Ghost.Make(portrait, member.name, p, fire, from, up, Fresh(portrait.crewMember));
                    if (g != null) { ghosts.Add(g); made++; }
                    break;
                }
            }
            if (made == 0) return;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Said = Time.time.ToString("F2") + ": " + made + " last picture" + (made == 1 ? "" : "s") + " for " + (p.partInfo != null ? p.partInfo.title : p.name) + (fire ? ", fire (blast " + power.ToString("F2") + ", " + Burnable(p).ToString("F0") + " kg in it)" : ", no fire") + ", taken in " + ms.ToString("F1") + " ms";
            Kit.Log("Smooth Portraits", Said);
        }

        static void Killed(EventReport report)
        {
            if (report == null) return;
            foreach (Ghost g in ghosts) if (g.name == report.sender) g.dead = true;
        }

        /// <summary>Every frame: each last picture played on, and those done let go.</summary>
        public static void Step()
        {
            for (int i = ghosts.Count - 1; i >= 0; i--)
            {
                Ghost g = ghosts[i];
                bool on;
                try { on = g.Step(Time.deltaTime); }
                catch (Exception ex) { Kit.Log("Smooth Portraits", "a last picture stopped: " + ex.Message); on = false; }
                if (!on) { g.Gone(); ghosts.RemoveAt(i); }
            }
        }

        public static int Showing => ghosts.Count;

        static Texture2D ceiling;

        /// <summary>A smoke layer's shape: solid above, thinning to nothing over the half below it (drawn from the picture's top down).</summary>
        internal static Texture2D Ceiling
        {
            get
            {
                if (ceiling != null) return ceiling;
                ceiling = new Texture2D(4, 128, TextureFormat.RGBA32, false) { name = "SmoothPortraits smoke", wrapMode = TextureWrapMode.Clamp };
                var px = new Color32[4 * 128];
                for (int y = 0; y < 128; y++)
                {
                    float v = y / 127f, a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((v - 0.5f) / 0.5f));
                    for (int x = 0; x < 4; x++) px[y * 4 + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
                ceiling.SetPixels32(px);
                ceiling.Apply();
                return ceiling;
            }
        }

        internal static Texture2D White
        {
            get
            {
                if (white == null) { white = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "SmoothPortraits white" }; white.SetPixels32(new[] { new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255) }); white.Apply(); }
                return white;
            }
        }

#if DEV
        /// <summary>
        /// (Development build.) A part of the craft flown blown up the game's own way (Part.explode, as a crash without Fragmentation does), its
        /// crew with it: "part=Mk1-3" (the first whose title has those letters; none: the first part with crew in it, or a kerbal out on EVA).
        /// </summary>
        public static string Explode(string arg)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return "nothing is flown";
            string want = "";
            foreach (string w in (arg ?? "").Split(' ')) if (w.StartsWith("part=")) want = w.Substring(5).Replace('_', ' ');
            foreach (Part part in v.parts)
            {
                if (part == null || part.partInfo == null) continue;
                if (want.Length > 0 ? part.partInfo.title.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0 : part.protoModuleCrew.Count == 0) continue;
                string crew = part.protoModuleCrew.Count + " aboard";
                part.explode();
                return part.partInfo.title + " blown up (" + crew + ")";
            }
            return "no such part";
        }

        /// <summary>(Development build.) The gallery's first portrait's picture and mask as they are (enabled, maskable, colours, places).</summary>
        public static string Original()
        {
            KerbalPortraitGallery gallery = KerbalPortraitGallery.Instance;
            if (gallery == null || !(Portraits.Crew(gallery) is List<KerbalPortrait> portraits) || portraits.Count == 0 || portraits[0] == null) return "no portrait";
            var text = new System.Text.StringBuilder();
            foreach (Graphic g in portraits[0].GetComponentsInChildren<Graphic>(true))
            {
                if (!(g is Image) && !(g is RawImage)) continue;
                var r = g.rectTransform;
                text.Append(g.name).Append(": ").Append(g.GetType().Name).Append(g.enabled ? "" : " (off)").Append(g.gameObject.activeInHierarchy ? "" : " (not active)").Append(", maskable ").Append(g is MaskableGraphic mg && mg.maskable)
                    .Append(", colour ").Append(g.color.ToString("F2")).Append(", at ").Append(r.anchoredPosition.ToString("F1")).Append(" size ").Append(r.rect.size.ToString("F1"));
                Mask m = g.GetComponent<Mask>();
                if (m != null) text.Append(", mask ").Append(m.enabled ? "on" : "off").Append(m.showMaskGraphic ? " showing its picture" : "");
                if (g is Image im) text.Append(", sprite ").Append(im.sprite != null ? im.sprite.name : "none").Append(", type ").Append(im.type);
                text.Append('\n');
            }
            return text.ToString();
        }

        /// <summary>(Development build.) Each last picture playing: its copy, piece by piece (what draws, in what colour).</summary>
        public static string Copies()
        {
            var text = new System.Text.StringBuilder();
            foreach (Ghost g in ghosts) text.Append(g.Tree());
            return text.Length > 0 ? text.ToString() : "none playing";
        }

        /// <summary>(Development build.) The warm-up run again now (it is unseen: for filming the portraits through it).</summary>
        public static string Warm()
        {
            Portraits host = UnityEngine.Object.FindObjectOfType<Portraits>();
            if (host == null) return "no portraits addon";
            host.StartCoroutine(WarmUp());
            return "warming up in about three seconds";
        }

        /// <summary>(Development build.) What was done last, and how the time of the last pictures made since this was last asked went.</summary>
        public static string Heard() { string said = Said + " | playing " + ghosts.Count + " | " + Split + " | before the pictures " + Before; Split = ""; return said; }
        internal static string Before = "";

        /// <summary>(Development build.) "on": the pictures taken ahead whatever is happening (as if a crash were near); "off": as it comes.</summary>
        public static string Near(string arg) { TestDanger = (arg ?? "").Trim() == "on"; return TestDanger ? "taken ahead all the time" : "taken ahead near a crash"; }

        /// <summary>(Development build.) What taking pictures ahead has cost since this was last asked.</summary>
        public static string Ahead()
        {
            string said = Prepared + " sets taken ahead, " + (PreparedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Mathf.Max(1, Prepared)).ToString("F2") + " ms each";
            Prepared = 0; PreparedTicks = 0;
            return said;
        }

        /// <summary>(Development build.) "hide": the copies made but not shown (what is under them seen); else shown.</summary>
        public static string Unseen(string arg) { TestHidden = (arg ?? "").Trim() == "hide"; return TestHidden ? "copies hidden" : "copies shown"; }

        /// <summary>(Development build.) "shadows" or "none": whether the fire's lamp casts shadows.</summary>
        public static string Shadows(string arg) { TestShadows = (arg ?? "").Trim() == "shadows"; return TestShadows ? "the lamp casts shadows" : "no shadows"; }

        /// <summary>(Development build.) What taking the last pictures costs, without anyone dying: "fire" or "cold", every portrait showing, timed.</summary>
        public static string Cost(string arg)
        {
            Split = "";
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            string said = Rehearse(arg);
            return said + " in " + ((System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F1") + " ms (from a call at " + (Time.inFixedTimeStep ? "a physics step" : "a frame") + "; each: " + Split + ")";
        }

        /// <summary>
        /// (Development build.) The last picture played over every portrait showing, as if each kerbal had just been killed with their part,
        /// without anyone being hurt: "fire" (as by a burst of fire beside them, from "side" degrees round, below by default) or "cold" (a
        /// blow alone). For looking at it again and again. Their portraits are still there under it, and come back when it is over.
        /// </summary>
        public static string Rehearse(string arg)
        {
            KerbalPortraitGallery gallery = KerbalPortraitGallery.Instance;
            if (gallery == null || !(Portraits.Crew(gallery) is List<KerbalPortrait> portraits)) return "no gallery";
            string words = (arg ?? "").Trim().ToLowerInvariant();
            long rehearseBegan = System.Diagnostics.Stopwatch.GetTimestamp();
            bool fire = !words.Contains("cold");
            int made = 0;
            foreach (KerbalPortrait portrait in portraits)
            {
                if (portrait == null || !Portraits.Showing(portrait)) continue;
                Part part = portrait.crewMember != null ? portrait.crewMember.InPart : portrait.crewEVAMember != null ? portrait.crewEVAMember.part : null;
                if (part == null) continue;
                Vector3 middle = part.transform.position, up = FlightGlobals.getUpAxis(middle);
                float side = 0f;
                foreach (string w in words.Split(' ')) if (w.StartsWith("side=")) float.TryParse(w.Substring(5), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out side);
                Vector3 level = Vector3.ProjectOnPlane(part.transform.up, up).sqrMagnitude > 1e-3f ? Vector3.ProjectOnPlane(part.transform.up, up).normalized : Vector3.ProjectOnPlane(part.transform.forward, up).normalized;
                Vector3 from = words.Contains("side=") ? middle + Quaternion.AngleAxis(side, up) * level * 2f : middle - up * 0.6f;
                Split += "{before " + ((System.Diagnostics.Stopwatch.GetTimestamp() - rehearseBegan) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F1") + "} ";
                Ghost g = Ghost.Make(portrait, portrait.crewMemberName, part, fire, from, up, words.Contains("now") ? null : Fresh(portrait.crewMember));
                if (g == null) continue;
                g.dead = true;
                ghosts.Add(g);
                made++;
            }
            return made + " last picture" + (made == 1 ? "" : "s") + (fire ? " with fire" : " without fire");
        }

        /// <summary>(Development build.) The first portrait as the gallery has it: each piece, its components, its size; and the game's static saved to a file ("path").</summary>
        public static string Layout(string arg)
        {
            KerbalPortraitGallery gallery = KerbalPortraitGallery.Instance;
            if (gallery == null || !(Portraits.Crew(gallery) is List<KerbalPortrait> portraits) || portraits.Count == 0 || portraits[0] == null) return "no portrait";
            KerbalPortrait first = portraits[0];
            var text = new System.Text.StringBuilder("canvas " + (first.portrait.canvas != null ? first.portrait.canvas.rootCanvas.name + " (" + first.portrait.canvas.rootCanvas.renderMode + ", scale " + first.portrait.canvas.rootCanvas.scaleFactor + ")" : "none") + "\n");
            foreach (Transform t in first.GetComponentsInChildren<Transform>(true))
            {
                int depth = 0; for (Transform u = t; u != null && u != first.transform; u = u.parent) depth++;
                text.Append(new string(' ', depth * 2)).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (off)");
                if (t is RectTransform r) text.Append(" ").Append(r.rect.size.ToString("F0"));
                foreach (Component c in t.GetComponents<Component>()) if (!(c is Transform)) text.Append(' ').Append(c.GetType().Name);
                text.Append('\n');
            }
            Texture over = first.overlayImg;
            text.Append("static: ").Append(over != null ? over.name + " " + over.width + "x" + over.height : "none");
            string path = (arg ?? "").Trim();
            if (over != null && path.Length > 0)
            {
                RenderTexture rt = RenderTexture.GetTemporary(over.width, over.height, 0, RenderTextureFormat.ARGB32);
                RenderTexture was = RenderTexture.active;
                Graphics.Blit(over, rt);
                RenderTexture.active = rt;
                var read = new Texture2D(over.width, over.height, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0, 0, over.width, over.height), 0, 0);
                read.Apply();
                RenderTexture.active = was;
                RenderTexture.ReleaseTemporary(rt);
                System.IO.File.WriteAllBytes(path, read.EncodeToPNG());
                UnityEngine.Object.Destroy(read);
                text.Append(", saved to ").Append(path);
            }
            return text.ToString();
        }
#endif

        // ================================================================== a kerbal's last pictures

        /// <summary>
        /// A kerbal's cabin camera's pictures for their last moments, taken by the game's own routine with things changed for each: the blow (their
        /// head thrown, the cabin's light as it was); with fire, the flash of it (a hard white light from its side) and two of its light as it burns
        /// (orange, low and from a little aside, the daylight through the windows half blotted out by the smoke). What is changed for them is put
        /// back, and the portrait's own picture with it.
        /// </summary>
        sealed class Shots
        {
            public readonly RenderTexture[] frames = new RenderTexture[4];
            public int count;
            public float at;                     // when they were taken (Time.unscaledTime)

            RenderTexture Frame(int i, Texture like)
            {
                if (frames[i] != null) return frames[i];
                int w = like != null ? like.width : 256, h = like != null ? like.height : 256;
                var rt = new RenderTexture(Mathf.Max(64, w), Mathf.Max(64, h), 0, RenderTextureFormat.ARGB32) { name = "SmoothPortraits last picture " + i, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
                rt.Create();
                frames[i] = rt;
                return rt;
            }

            public void Copy(Texture picture, int i)
            {
                RenderTexture was = RenderTexture.active;
                Graphics.Blit(picture, Frame(i, picture));
                RenderTexture.active = was;
                count = Mathf.Max(count, i + 1);
            }

            public void Take(Kerbal k, Part part, bool fire, Vector3 from, Vector3 up)
            {
                RenderTexture picture = k.avatarTexture;
                if (picture == null || !k.isActiveAndEnabled || part == null) return;
                RenderTexture was = RenderTexture.active;
                // (the portrait shows this texture: what it shows now is kept, and put back after)
                RenderTexture keep = RenderTexture.GetTemporary(picture.width, picture.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(picture, keep);
                // (the cabin is a space of its own, turned as the craft is: a way in the world is turned into it by that turn alone; see Ride)
                Quaternion inside = InternalSpace.Instance != null ? InternalSpace.WorldToInternal(Quaternion.identity) : Quaternion.identity;
                Vector3 toFire = inside * (from - part.transform.position).normalized, down = inside * -up;
                Transform neck = Ride.NeckOf(k);
                Quaternion neckWas = neck != null ? neck.rotation : Quaternion.identity;
                Transform head = k.headTransform != null ? k.headTransform : neck != null ? neck : k.transform;
                Vector3 face = head.position;
                // (thrown towards the blow's side: the cabin is driven away from it faster than a head on its neck can follow. Mostly a tilt to
                // the side, a little forward: on top of the lean the ride already gives them (see Ride), a head thrown 25 degrees towards a fire
                // below was turned crown to the camera, the face in its own shadow)
                Vector3 across = Vector3.ProjectOnPlane(toFire, k.transform.up);
                float leftOrRight = Vector3.Dot(across, k.transform.right) >= 0f ? -1f : 1f;
                Vector3 axis = (0.8f * leftOrRight * k.transform.forward + 0.35f * k.transform.right).normalized;
                foreach (SkinnedMeshRenderer skin in k.GetComponentsInChildren<SkinnedMeshRenderer>(true)) skin.forceMatrixRecalculationPerRender = true;
                Light sunLight = Sun();
                float sunWas = sunLight != null ? sunLight.intensity : 0f;
                Color ambientWas = RenderSettings.ambientLight;
                Camera cam = k.protoCrewMember != null && k.protoCrewMember.seat != null && k.protoCrewMember.seat.portraitCamera != null ? k.protoCrewMember.seat.portraitCamera : k.kerbalCam;
                var lamp = new GameObject("SmoothPortraits fire light").AddComponent<Light>();
                lamp.type = LightType.Point;
                lamp.renderMode = LightRenderMode.ForcePixel;
                lamp.shadows = TestShadows ? LightShadows.Soft : LightShadows.None;
                lamp.shadowStrength = 0.85f;
                lamp.cullingMask = cam != null ? cam.cullingMask | k.kerbalCam.cullingMask : k.kerbalCam.cullingMask;
                lamp.enabled = false;
                try
                {
                    Shoot(k, neck, neckWas, axis, 7f, picture, 0);
                    if (fire)
                    {
                        // (the lamp in the open space between the face and the camera, on the fire's side of it: a lamp behind the seat or under
                        // the floor, where the fire may really be, lights nothing the camera sees)
                        Vector3 eye = cam != null ? cam.transform.position : face + k.transform.forward * 0.6f;
                        Vector3 look = eye - face;
                        float apart = Mathf.Max(0.2f, look.magnitude);
                        look /= apart;
                        Vector3 side = Vector3.ProjectOnPlane(toFire, look);
                        if (side.sqrMagnitude < 1e-3f) side = Vector3.ProjectOnPlane(down, look);
                        side.Normalize();
                        Vector3 below = Vector3.ProjectOnPlane(down, look).normalized;
                        Vector3 near = face + look * Mathf.Min(0.4f, 0.55f * apart);
                        lamp.enabled = true;
                        // the flash: hard and white, too bright for the camera, from the side of the burst
                        lamp.transform.position = near + side * 0.35f;
                        lamp.color = new Color(1f, 0.93f, 0.8f);
                        lamp.intensity = 7.5f; lamp.range = 3f;
                        Shoot(k, neck, neckWas, axis, 10f, picture, 1);
                        // the fire's light as it takes hold: orange, low and to the side of them (so that it models their faces, half in shadow),
                        // the daylight through the windows and off the cabin's walls half lost in the smoke
                        if (sunLight != null) sunLight.intensity = sunWas * 0.35f;
                        RenderSettings.ambientLight = ambientWas * 0.45f;
                        lamp.transform.position = face + side * 0.34f + look * 0.26f + below * 0.3f;
                        lamp.color = new Color(1f, 0.5f, 0.18f);
                        lamp.intensity = 3.3f; lamp.range = 2.2f;
                        Shoot(k, neck, neckWas, axis, 12f, picture, 2);
                        // and a moment later, the flames moved, lower and redder, the smoke thicker (the head as it was: the two are shown by turns,
                        // and only the light is to flicker)
                        if (sunLight != null) sunLight.intensity = sunWas * 0.18f;
                        RenderSettings.ambientLight = ambientWas * 0.3f;
                        lamp.transform.position = face + (Quaternion.AngleAxis(Random.Range(25f, 40f) * (Random.value < 0.5f ? -1f : 1f), look) * side) * 0.36f + look * 0.2f + below * 0.4f;
                        lamp.color = new Color(1f, 0.4f, 0.12f);
                        lamp.intensity = 2.3f; lamp.range = 2.2f;
                        Shoot(k, neck, neckWas, axis, 12f, picture, 3);
                    }
                    else Shoot(k, neck, neckWas, axis, 12f, picture, 1);
                }
                finally
                {
                    if (neck != null) neck.rotation = neckWas;
                    if (sunLight != null) sunLight.intensity = sunWas;
                    RenderSettings.ambientLight = ambientWas;
                    UnityEngine.Object.Destroy(lamp.gameObject);
                    Graphics.Blit(keep, picture);
                    RenderTexture.ReleaseTemporary(keep);
                    RenderTexture.active = was;
                }
                at = Time.unscaledTime;
            }

            void Shoot(Kerbal k, Transform neck, Quaternion neckWas, Vector3 axis, float thrown, RenderTexture picture, int i)
            {
                if (neck != null) neck.rotation = Quaternion.AngleAxis(thrown, axis) * neckWas;
                Portraits.Picture(k);
                Graphics.Blit(picture, Frame(i, picture));
                count = Mathf.Max(count, i + 1);
            }

            public void Free()
            {
                for (int i = 0; i < frames.Length; i++)
                {
                    if (frames[i] == null) continue;
                    frames[i].Release();
                    UnityEngine.Object.Destroy(frames[i]);
                    frames[i] = null;
                }
                count = 0;
            }
        }

        // ================================================================== one kerbal's last picture

        sealed class Ghost
        {
            public string name;
            public bool dead;                    // the game has said they died (onCrewKilled)
            GameObject original;                 // the game's own portrait, hidden under the copy (see Lay)
            ProtoCrewMember member;
            bool fire;
            float age, length;
            Shots shots;
            RenderTexture[] frames => shots.frames;
            int count => shots.count;
            GameObject copy;
            CanvasGroup fade;
            RawImage wholeA, wholeB;             // the picture whole (A: the frame shown; B: the next, laid over by a share), until it tears
            RawImage[] lower, upper;             // and in bands, to tear (made a few a frame after the first: see Step)
            RectTransform area;
            int bands;
            RawImage veil, snow, smoke;
            Texture staticImage;
            float seed, rollFrom;
            int snowFrame = -1;
            Vector2 snowAt;
            Vector2 size;
            const int Bands = 16;

            // ---- the moments, seconds from the blow (fire / without)
            const float FlashFor = 0.07f, FireFor = 1.25f, TearFrom = 0.55f, SnowFrom = 1.15f, FadeFrom = 1.75f, End = 2.2f;
            const float TearFromCold = 0.06f, SnowFromCold = 0.35f, FadeFromCold = 0.85f, EndCold = 1.25f;

            public static Ghost Make(KerbalPortrait portrait, string name, Part part, bool fire, Vector3 from, Vector3 up, Shots ready, bool forReal = true)
            {
                var g = new Ghost { name = name, seed = Random.value * 100f, staticImage = portrait.overlayImg };
                Kerbal inside = portrait.crewMember;
                g.fire = fire;
                g.length = fire ? End : EndCold;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (inside != null && ready != null && ready.count >= 4)
                {
                    // (taken a moment ago, at the end of a frame, where a picture costs a fraction of what it does in the middle of one: see Prepare)
                    g.shots = ready;
                    if (!fire) { ready.Copy(ready.frames[0], 1); ready.Copy(inside.avatarTexture, 0); ready.count = 2; }
                }
                else if (inside != null) { g.shots = new Shots(); g.shots.Take(inside, part, fire, from, up); }
                else if (portrait.portrait.texture is RenderTexture picture) { g.fire = false; g.length = EndCold; g.shots = new Shots(); g.shots.Copy(picture, 0); }
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (g.shots == null || g.count == 0) { if (g.shots != null) g.shots.Free(); return null; }
                if (!g.Lay(portrait, forReal)) { g.Gone(); return null; }
#if DEV
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Split += "[pictures " + ((t1 - t0) * ms).ToString("F1") + " ms, the copy " + ((t2 - t1) * ms).ToString("F1") + " ms (" + CopySplit + ")] ";
#endif
                return g;
            }

            /// <summary>A copy of the portrait (its frame, the picture's place and their name; none of its workings) laid over it where it is, on the same canvas.</summary>
            bool Lay(KerbalPortrait portrait, bool forReal)
            {
                RawImage shown = portrait.portrait;
                Canvas canvas = shown.canvas != null ? shown.canvas.rootCanvas : null;
                if (canvas == null) return false;
                // (only what is drawn of it: the picture in its mask, and the tag with their name, copied under a holder that is not active,
                // so that nothing of theirs runs, and their workings taken out. The whole portrait, its buttons, tooltips and meters, took
                // 8 to 10 ms to copy, in the frame the crash kills them)
                var holder = new GameObject("SmoothPortraits last picture holder");
                holder.SetActive(false);
                try
                {
                    var from = (RectTransform)portrait.transform;
                    var made = new GameObject("SmoothPortraits last picture", typeof(RectTransform)) { layer = portrait.gameObject.layer };
                    var rt = (RectTransform)made.transform;
                    rt.SetParent(holder.transform, false);
                    rt.pivot = from.pivot;
                    rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.sizeDelta = from.rect.size;
                    Transform maskOf = shown.transform.parent != null && shown.transform.parent != from && shown.transform.parent.GetComponent<Mask>() != null ? shown.transform.parent : shown.transform;
                    Transform tag = portrait.nameField != null ? portrait.nameField.transform.parent : null;
                    if (tag == from || tag == null) tag = portrait.nameField != null ? portrait.nameField.transform : null;
                    RawImage image = null;
                    long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    foreach (Transform piece in new[] { maskOf, tag })
                    {
                        if (piece == null) continue;
                        GameObject twin = UnityEngine.Object.Instantiate(piece.gameObject, rt, false);
                        twin.name = piece.name;
                        var trt = (RectTransform)twin.transform;
                        if (piece.parent != from)
                        {
                            // (deeper in the portrait than its own children: put where it is)
                            var prt = (RectTransform)piece;
                            trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 0.5f);
                            trt.sizeDelta = prt.rect.size;
                            trt.pivot = prt.pivot;
                            trt.anchoredPosition = (Vector2)from.InverseTransformPoint(prt.position) + Vector2.Scale(from.pivot - new Vector2(0.5f, 0.5f), from.rect.size);
                        }
                        Strip(twin);
                        if (piece == maskOf) image = maskOf == shown.transform ? twin.GetComponent<RawImage>() : twin.transform.Find(shown.name) != null ? twin.transform.Find(shown.name).GetComponent<RawImage>() : twin.GetComponentInChildren<RawImage>(true);
                        // (the mask's own picture not drawn: it still cuts the corners where it is on, and where it is off, as for a kerbal
                        // outside, it showed as a white edge round the copy and paled it as it faded)
                        if (piece == maskOf && maskOf != shown.transform)
                        {
                            Mask mask = twin.GetComponent<Mask>();
                            if (mask != null && mask.enabled) mask.showMaskGraphic = false;
                            else if (twin.GetComponent<Graphic>() is Graphic own) own.enabled = false;
                            // (and black, should it be drawn after all: in a copy with a canvas of its own it was, white, wherever the shaken
                            // and torn picture left a gap)
                            if (twin.GetComponent<Graphic>() is Graphic under) under.color = Color.black;
                        }
                    }
                    if (image == null) { UnityEngine.Object.Destroy(made); return false; }
                    long c1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    // (on the canvas itself, above everything: the gallery shrinks as it lets the portrait go, and this stays where it was)
                    Vector3 place = from.position, scale = from.lossyScale;
                    Quaternion turn = from.rotation;
                    rt.SetParent(canvas.transform, false);
                    Vector3 parentScale = canvas.transform.lossyScale;
                    rt.localScale = new Vector3(scale.x / Mathf.Max(1e-6f, parentScale.x), scale.y / Mathf.Max(1e-6f, parentScale.y), 1f);
                    rt.position = place;
                    rt.rotation = turn;
                    rt.SetAsLastSibling();
                    made.AddComponent<LayoutElement>().ignoreLayout = true;
                    // (a canvas of its own, so that what changes in it every frame is batched by itself and not with the game's whole interface;
                    // on the interface's layer, which its camera draws: on the default layer a canvas of its own was not drawn at all)
                    made.AddComponent<Canvas>();
                    fade = made.AddComponent<CanvasGroup>();
                    fade.blocksRaycasts = false; fade.interactable = false; fade.alpha = 1f;
                    copy = made;
                    // (the game's own portrait hidden: inside, the game takes it away in this same moment; for a kerbal outside it stays
                    // until their body is gone, its picture lost and drawn white, and showed at the copy's corners and through it as it faded)
                    if (forReal)
                    {
                        original = portrait.gameObject;
                        member = portrait.crewPcm;
                        original.SetActive(false);
                    }
                    else fade.alpha = 0f;
                    long c2 = System.Diagnostics.Stopwatch.GetTimestamp();
                    Feed(image);
#if DEV
                    long c3 = System.Diagnostics.Stopwatch.GetTimestamp();
                    double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    CopySplit = "copied " + ((c1 - c0) * ms).ToString("F2") + ", put on the canvas " + ((c2 - c1) * ms).ToString("F2") + ", the feed " + ((c3 - c2) * ms).ToString("F2");
#endif
                    return true;
                }
                catch (Exception ex) { Kit.Log("Smooth Portraits", "the portrait could not be copied: " + ex.Message); return false; }
                finally { UnityEngine.Object.Destroy(holder); }
            }

            static string PathTo(Transform root, Transform t)
            {
                if (t == root) return "";
                var parts = new List<string>();
                for (Transform u = t; u != null; u = u.parent)
                {
                    if (u == root) { parts.Reverse(); return string.Join("/", parts.ToArray()); }
                    parts.Add(u.name);
                }
                return null;
            }

            /// <summary>Everything of the copy that is not drawing taken out: what is left draws and does nothing.</summary>
            static void Strip(GameObject made)
            {
                for (int pass = 0; pass < 3; pass++)
                    foreach (MonoBehaviour b in made.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (b == null || b is Graphic || b is Mask || b is RectMask2D || b is LayoutElement || b is LayoutGroup || b is ContentSizeFitter || b is AspectRatioFitter || b is CanvasScaler || b is BaseMeshEffect) continue;
                        try { UnityEngine.Object.DestroyImmediate(b); } catch (Exception) { b.enabled = false; }
                    }
                foreach (Graphic g in made.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            }

            /// <summary>The picture's place made into the feed: black under it, the pictures in bands over it (each band its own slice, to tear), a veil for the flash, the game's static.</summary>
            void Feed(RawImage image)
            {
                // (the picture's own place not drawn: it carries the portrait's own material, whose shader takes no colour, so that made
                // black it was drawn white, showed in every gap the shaking and the tearing opened, and did not fade with the rest)
                image.enabled = false;
                var box = new GameObject("feed", typeof(RectTransform)) { layer = image.gameObject.layer };
                area = (RectTransform)box.transform;
                area.SetParent(image.transform, false);
                area.anchorMin = Vector2.zero; area.anchorMax = Vector2.one; area.offsetMin = area.offsetMax = Vector2.zero;
                box.AddComponent<RectMask2D>();
                size = ((RectTransform)image.transform).rect.size;
                // (in the frame they die only what shows at once: the picture whole, and the layers over it. Each piece of the user
                // interface made costs a few tenths of a millisecond, and the 35 of them in that one frame took 5.5 to 7.7 ms)
                lower = new RawImage[Bands];
                upper = new RawImage[Bands];
                Layer(area, "black", White).color = Color.black;
                wholeA = Layer(area, "picture", White);
                wholeB = Layer(area, "picture over", White);
                smoke = Layer(area, "smoke", Ceiling);
                smoke.color = new Color(0.11f, 0.095f, 0.085f, 0f);
                veil = Layer(area, "flash", White);
                veil.color = new Color(1f, 0.95f, 0.86f, 0f);
                snow = Layer(area, "static", staticImage != null ? staticImage : White);
                snow.color = new Color(1f, 1f, 1f, 0f);
                Show(0f);
            }

            static RawImage Band(RectTransform area, int b, string what)
            {
                var go = new GameObject(what + " " + b, typeof(RectTransform)) { layer = area.gameObject.layer };
                var rt = (RectTransform)go.transform;
                rt.SetParent(area, false);
                rt.anchorMin = new Vector2(0f, (float)b / Bands);
                rt.anchorMax = new Vector2(1f, (float)(b + 1) / Bands);
                rt.offsetMin = rt.offsetMax = Vector2.zero;
                var image = go.AddComponent<RawImage>();
                image.raycastTarget = false;
                return image;
            }

            static RawImage Layer(RectTransform area, string what, Texture texture)
            {
                var go = new GameObject(what, typeof(RectTransform)) { layer = area.gameObject.layer };
                var rt = (RectTransform)go.transform;
                rt.SetParent(area, false);
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
                var image = go.AddComponent<RawImage>();
                image.texture = texture;
                image.raycastTarget = false;
                return image;
            }

            /// <summary>On a frame: false once it is over (or the kerbal did not die after all).</summary>
            public bool Step(float dt)
            {
                if (copy == null) return false;
                age += Mathf.Max(0f, dt);
                if (!dead && age > 0.3f) return false;
                // (the bands made a few a frame: all there before the picture tears)
                for (int n = 0; n < 6 && bands < Bands; n++, bands++)
                {
                    lower[bands] = Band(area, bands, "band");
                    upper[bands] = Band(area, bands, "band over");
                    lower[bands].transform.SetSiblingIndex(wholeB.transform.GetSiblingIndex());
                    upper[bands].transform.SetSiblingIndex(wholeB.transform.GetSiblingIndex());
                    lower[bands].enabled = upper[bands].enabled = false;
                }
                // (hidden while the gallery is)
                bool seen = !hidden && !TestHidden && (KerbalPortraitGallery.Instance == null || KerbalPortraitGallery.Instance.gameObject.activeInHierarchy);
                fade.alpha = seen ? Fade() : 0f;
                Show(age);
                return age < length;
            }

            float Fade()
            {
                float from = fire ? FadeFrom : FadeFromCold;
                return age < from ? 1f : Mathf.Clamp01(1f - (age - from) / Mathf.Max(0.05f, length - from));
            }

            /// <summary>
            /// The picture at so many seconds from the blow. Shaken: the camera on its mount thrown about by the blow, a few hundredths of
            /// the picture, dying away within a few tenths, and a little on after while the fire burns. With fire: the flash, too bright,
            /// then the fire's light flickering between two pictures of it, the smoke darkening the cabin. Then the link going: bands of the
            /// picture torn sideways, more and more, the picture rolling, and the game's own static coming up through it until it is all.
            /// </summary>
            void Show(float t)
            {
                if (wholeA == null) return;
                float w = size.x, h = size.y;
                // ---- the shake
                float jolt = 0.075f * Mathf.Exp(-t / 0.12f) + (fire ? 0.012f * Mathf.Exp(-t / 0.9f) : 0f);
                float sx = jolt * w * Wobble(t * 31f, 1f), sy = jolt * h * Wobble(t * 27f, 2f);
                // ---- which pictures, how bright
                Texture a, b = null;
                float over = 0f, bright = 1f, flash = 0f;
                if (!fire || count < 4)
                {
                    a = frames[t < 0.05f || count < 2 ? 0 : 1];
                    bright = 1f;
                }
                else if (t < 0.035f) a = frames[0];
                else if (t < FlashFor) { a = frames[1]; flash = 0.55f; }
                else
                {
                    // (the flash's light going as the fire's comes, the flicker of the flames, and the smoke darkening it)
                    float into = t - FlashFor;
                    float flick = 0.55f * Mathf.PerlinNoise(t * 9f, seed) + 0.45f * Mathf.PerlinNoise(t * 23f, seed + 3f);
                    a = frames[2]; b = frames[3];
                    over = Mathf.Clamp01((flick - 0.3f) / 0.4f);
                    flash = 0.55f * Mathf.Exp(-into / 0.09f);
                    if (into < 0.12f) { b = frames[1]; over = 1f - into / 0.12f; }
                    bright = Mathf.Lerp(1f, 0.55f, Mathf.Clamp01(into / FireFor)) * (0.75f + 0.5f * Mathf.PerlinNoise(t * 13f, seed + 7f));
                }
                // ---- the link going: tearing, rolling, static
                float tearFrom = fire ? TearFrom : TearFromCold, snowFrom = fire ? SnowFrom : SnowFromCold;
                float tear = Mathf.Clamp01((t - tearFrom) / (snowFrom + 0.45f - tearFrom));
                tear *= tear;
                float roll = 0f;
                if (t > snowFrom - 0.2f) { if (rollFrom <= 0f) rollFrom = t; roll = 0.9f * (t - rollFrom) * (t - rollFrom); }
                float snowAmount = Mathf.Clamp01((t - snowFrom) / 0.3f);
                if ((tear > 0f || roll > 0f) && bands == Bands)
                {
                    Place(wholeA, null, 0f, 0f, 0f, 1f, 1f, 1f);
                    Place(wholeB, null, 0f, 0f, 0f, 1f, 1f, 0f);
                    for (int i = 0; i < Bands; i++)
                    {
                        // (a band torn sideways by how far its line lost its timing: neighbouring bands much alike, now and then one far off)
                        float slip = tear * (0.35f * Wobble(t * 18f + i * 0.35f, 5f) + (Mathf.PerlinNoise(i * 0.9f + seed, t * 6f) > 0.72f ? 0.5f * Wobble(t * 40f, i) : 0f));
                        float v = (float)i / Bands + roll;
                        Place(lower[i], a, sx + slip * w, sy, v, 1f / Bands, bright, 1f);
                        Place(upper[i], b, sx + slip * w, sy, v, 1f / Bands, bright, b != null ? over : 0f);
                    }
                }
                else
                {
                    Place(wholeA, a, sx, sy, 0f, 1f, bright, 1f);
                    Place(wholeB, b, sx, sy, 0f, 1f, bright, b != null ? over : 0f);
                }
                veil.color = new Color(1f, 0.95f, 0.86f, Mathf.Clamp01(flash));
                // ---- the smoke: a layer under the cabin's roof, thickening and coming down as the fire burns (hot smoke rises and fills a room
                // from the top), darkening and taking the colour out of what it covers
                if (fire && count >= 4 && t > FlashFor)
                {
                    float thick = Mathf.Clamp01((t - 0.2f) / 1.3f);
                    smoke.uvRect = new Rect(0f, Mathf.Lerp(-0.55f, 0.15f, thick) + 0.03f * Wobble(t * 2f, 9f), 1f, 1f);
                    smoke.color = new Color(0.11f, 0.095f, 0.085f, 0.85f * Mathf.Sqrt(thick));
                }
                else smoke.color = new Color(0.11f, 0.095f, 0.085f, 0f);
                if (snow.texture != null && snowAmount > 0f)
                {
                    // (the game's own static, a window of it taken anywhere, new every frame drawn: snow is new noise at every picture a set
                    // makes. The game's three windows of it in turn, every 0.06 s, beat as regularly as a clock: a pattern, not snow)
                    if (Time.frameCount != snowFrame) { snowFrame = Time.frameCount; snowAt = new Vector2(Random.value * 0.66f, Random.value * 0.66f); }
                    snow.uvRect = staticImage != null ? new Rect(snowAt.x, snowAt.y, 0.3334f, 0.3334f) : new Rect(0f, 0f, 1f, 1f);
                    snow.color = new Color(1f, 1f, 1f, snowAmount);
                }
                else snow.color = new Color(1f, 1f, 1f, 0f);
            }

            static void Place(RawImage band, Texture tex, float x, float y, float v, float tall, float bright, float alpha)
            {
                if (band == null) return;
                if (tex == null || alpha <= 0.002f) { if (band.enabled) band.enabled = false; return; }
                if (!band.enabled) band.enabled = true;
                if (band.texture != tex) band.texture = tex;
                band.uvRect = new Rect(0f, v, 1f, tall);
                band.color = new Color(bright, bright, bright, alpha);
                band.rectTransform.anchoredPosition = new Vector2(x, y);
            }

            /// <summary>A smooth wobble between -1 and 1.</summary>
            float Wobble(float x, float which) => 2f * Mathf.PerlinNoise(x, seed + which * 13.7f) - 1f;

#if DEV
            public string Tree()
            {
                var text = new System.Text.StringBuilder(name + ":\n");
                if (copy == null) return text.Append("  gone\n").ToString();
                foreach (Transform t in copy.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name.StartsWith("band")) continue;
                    int depth = 0; for (Transform u = t; u != null && u != copy.transform; u = u.parent) depth++;
                    text.Append(new string(' ', 2 + depth * 2)).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (off)");
                    if (t is RectTransform r) text.Append(' ').Append(r.rect.size.ToString("F0"));
                    foreach (Component c in t.GetComponents<Component>())
                    {
                        if (c is Transform || c is CanvasRenderer) continue;
                        text.Append(' ').Append(c.GetType().Name);
                        if (c is Graphic gr) text.Append(gr.enabled ? "" : "(off)").Append('[').Append(gr.color.ToString("F2")).Append(gr is RawImage ri && ri.texture != null ? " " + ri.texture.name : gr is Image im && im.sprite != null ? " " + im.sprite.name : "").Append(']');
                        if (c is Mask m) text.Append(m.enabled ? "" : "(off)").Append(m.showMaskGraphic ? "(shows)" : "");
                    }
                    text.Append('\n');
                }
                return text.ToString();
            }
#endif

            /// <summary>(For the warm-up.) Made, but not to be seen.</summary>
            public void Hide() { if (fade != null) fade.alpha = 0f; dead = true; hidden = true; }
            bool hidden;

            public void Gone()
            {
                if (copy != null) UnityEngine.Object.Destroy(copy);
                copy = null;
                // (theirs back, where they turned out not to be dead: a rehearsal, or a part that went without killing them)
                if (original != null && (member == null || (member.rosterStatus != ProtoCrewMember.RosterStatus.Dead && member.rosterStatus != ProtoCrewMember.RosterStatus.Missing))) original.SetActive(true);
                original = null;
                if (shots != null) shots.Free();
            }
        }
    }
}
