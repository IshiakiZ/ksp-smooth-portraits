using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Keystone;
using KSP.UI.Screens.Flight;
using UnityEngine;

#if !DEV
[assembly: KSPAssembly("SmoothPortraits", 0, 1)]
[assembly: KSPAssemblyDependency("Keystone", 0, 11)]
#endif

namespace SmoothPortraits
{
    /// <summary>
    /// The crew's portraits at the bottom right of the flight screen, drawn as often as the game itself.
    ///
    /// The game draws each portrait with a camera of its own, pointed at the kerbal in their seat, and
    /// to save work it lets each of those cameras take a picture only every tenth to seventh of a second:
    /// seven to ten pictures a second, however fast the game is running. That is why the faces jerk.
    ///
    /// This mod asks the game for those pictures itself, as often as the screen is redrawn (or as often as
    /// the settings say), and tells the game's own timer to stand back. Nothing about a picture is changed:
    /// each is made by the game's own code, the same camera, the same size.
    ///
    /// A picture is not free (a quarter of a millisecond of the game's own thread for a kerbal in a seat), so
    /// the mod keeps count of what its pictures take and holds itself to a share of every frame, three
    /// hundredths of it as installed. Where that is not enough for every portrait in every frame, they take turns, evenly:
    /// each still gets as many pictures a second as can be afforded, and no frame pays for more than its
    /// share. A kerbal outside the ship is dearer by far (their portrait is the whole scene seen again
    /// from their helmet, five cameras' worth), and gets fewer pictures for the same share.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class PortraitsAtStart : MonoBehaviour
    {
        void Awake() { Portraits.Setup(); Destroy(gameObject); }
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class Portraits : MonoBehaviour
    {
        public const string Version = "0.4.0";

        static Mod mod;
        static Toggle on, outside, sharp, ride, last;
        static Slider most, share;
        // (the page's fine details, in its Advanced view: each as installed does what the mod did before it had them)
        internal static Slider leanBy, buzzBy, shakeBy;
        internal static float Of(Slider slider, float installed) => slider != null ? (float)slider : installed;

        /// <summary>The settings added after Fine(true) are fine details: on the page only in Keystone's Advanced view, until Fine(false).
        /// (Keystone 0.11.0 has the two views; an older one has neither call, and shows the fine details with the rest.)</summary>
        static void Fine(bool fine) { try { if (fine) Advanced(mod); else Simple(mod); } catch (Exception) { } }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] static void Advanced(Mod page) => page.Advanced();
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] static void Simple(Mod page) => page.Simple();

        // The game's own, which it keeps to itself (see Keystone.Reach).
        static bool looked;
        static FieldInfo crewList, seen, seenOutside, timer, timerOutside;
        static MethodInfo cycle, cycleOutside;
        static Action<Kerbal> draw;
        static Action<Kerbal, Camera> drawFromSeat;

        readonly List<Kerbal> inside = new List<Kerbal>();
        readonly List<KerbalEVA> out_ = new List<KerbalEVA>();
        readonly Dictionary<Kerbal, float> held = new Dictionary<Kerbal, float>();                 // each portrait this draws, and when it last drew it
        readonly Dictionary<KerbalEVA, float> heldOutside = new Dictionary<KerbalEVA, float>();
        float credit, creditOutside;
        int turn, turnOutside;
        double costInside = 0.0003, costOutside = 0.003;                   // seconds a picture, as measured (a first guess until then)
        float nextLook;
        int givenBack;                                                     // (times a portrait's own timer was given back to the game while held: for the readout)

        // what it has been doing lately, for the window
        float tallyFrom;
        int tallyFrames, tallyInside, tallyOutside;
        double tallySpent;
        string report = "";

        /// <summary>Say that the mod is here and what can be set about it: once, when the game starts, so that its page is in the window in every scene.</summary>
        public static void Setup()
        {
            if (mod == null || Kit.Find("Smooth Portraits") != mod)
            {
                mod = Kit.Register("Smooth Portraits", Version, "The crew's portraits in flight, drawn as often as the game itself instead of seven to ten times a second. Each picture is the game's own; only how often it is taken changes.");
                on = mod.Toggle("on", "Smooth portraits", true, "Off: the game draws the portraits itself again, seven to ten times a second.");
                most = mod.Slider("most", "At most", 60f, 10f, 144f, " a second", 0, "The most pictures each portrait gets in a second. No more are ever taken than the game draws frames.");
                share = mod.Slider("share", "Share of a frame", 3f, 0.5f, 15f, " %", 1, "How much of each frame's time the portraits may take. Where that is not enough for all of them in every frame, they take turns.");
                outside = mod.Toggle("outside", "Kerbals outside too", true, "The portrait of a kerbal outside the ship is the whole scene drawn again from their helmet: far dearer. Off leaves those to the game.");
                ride = mod.Toggle("ride", "Shaken by the ride", true, "The crew's heads move in their portraits as the ride moves them, as in film from real cockpits: pressed back under thrust and nodding forward when it stops, swaying in turns, buzzing with solid boosters, the air's buffeting near the speed of sound and rough ground. The game moves only their faces. Off: as the game draws them.");
                Fine(true);
                leanBy = mod.Slider("lean", "How far heads lean", 1f, 0f, 3f, "x", 2, "How far the crew's heads lean with the ride's pushes (back into the headrest no further than it gives, forward no further than the straps let).");
                buzzBy = mod.Slider("buzz", "How much heads buzz", 1f, 0f, 3f, "x", 2, "How much the crew's heads buzz as the craft does under power.");
                shakeBy = mod.Slider("mountshake", "The portrait's camera shaken", 1f, 0f, 3f, "x", 2, "How much the camera taking each portrait shakes on its mount with the buzz.");
                Fine(false);
                last = mod.Toggle("last", "Last moments", true, "(0.3.0) When a kerbal is killed with their part, their portrait stays a moment and shows what the cabin's camera saw: the blow throwing the picture about, the flash and the fire's light, then the picture tearing and the feed lost. Off: the portrait goes at once, as the game has it.");
                sharp = mod.Toggle("sharp", "Smooth edges", true, "The portraits drawn with their edges smoothed (four samples a point), and a kerbal outside the ship in full colour (the game draws those in sixteen-bit colour, which bands): a little more of the graphics card's time for each picture. Off: as the game draws them.");
            }
        }

        void Awake()
        {
            Setup();
            mod.Panel = Readout;
            LastMoments.Setting = last;
            if (!looked)
            {
                looked = true;
                crewList = Reach.Field(typeof(KerbalPortraitGallery), "portraits");
                seen = Reach.Field(typeof(Kerbal), "visibleInPortrait");
                timer = Reach.Field(typeof(Kerbal), "updateCoroutine");
                cycle = Reach.Method(typeof(Kerbal), "kerbalAvatarUpdateCycle");
                seenOutside = Reach.Field(typeof(KerbalEVA), "visibleInPortrait");
                timerOutside = Reach.Field(typeof(KerbalEVA), "updateAvatarCoroutine");
                cycleOutside = Reach.Method(typeof(KerbalEVA), "kerbalAvatarUpdateCycle");
                draw = Reach.Call<Action<Kerbal>>(typeof(Kerbal), "kerbalAvatarUpdate");
                drawFromSeat = Reach.Call<Action<Kerbal, Camera>>(typeof(Kerbal), "kerbalSeatCamUpdate", typeof(Camera));
            }
        }

        bool Able => crewList != null && seen != null && timer != null && cycle != null && draw != null && drawFromSeat != null;
        static bool AbleOutside => seenOutside != null && timerOutside != null && cycleOutside != null;

        /// <summary>
        /// (Since 0.2.0.) The game's own timer for a portrait (a coroutine the kerbal keeps, taking a picture every 0.10 to 0.15 s)
        /// stopped, as the game itself stops it while a portrait is hidden: it takes no pictures while this mod draws the portrait.
        /// (Until 0.2.0 it was only told to wait two seconds between pictures, and still took one of each every two seconds, in the
        /// middle of a frame, the same as one the mod had just taken.)
        /// </summary>
        static void Halt(MonoBehaviour kerbal, FieldInfo running)
        {
            if (running.GetValue(kerbal) is Coroutine c) { kerbal.StopCoroutine(c); running.SetValue(kerbal, null); }
        }

        /// <summary>The game's own timer for a portrait going again, as the game starts it when a portrait comes into view: if it is not going, and the portrait is showing.</summary>
        static bool Resume(MonoBehaviour kerbal, FieldInfo running, MethodInfo cycleOf, FieldInfo showing)
        {
            if (kerbal == null || !kerbal.isActiveAndEnabled || running.GetValue(kerbal) != null || !(bool)showing.GetValue(kerbal)) return false;
            if (!(cycleOf.Invoke(kerbal, null) is System.Collections.IEnumerator e)) return false;
            running.SetValue(kerbal, kerbal.StartCoroutine(e));
            return true;
        }

        void OnDestroy()
        {
            LastMoments.Unhook();
            Ride.Upright();
            Ride.Hook(false);
            LetGo();
            if (mod != null && mod.Panel == Readout) mod.Panel = null;
        }

        /// <summary>Hand every portrait back to the game's own timer.</summary>
        void LetGo()
        {
            foreach (Kerbal kerbal in held.Keys) if (kerbal != null) Resume(kerbal, timer, cycle, seen);
            held.Clear();
            if (AbleOutside) foreach (KerbalEVA walker in heldOutside.Keys) if (walker != null) Resume(walker, timerOutside, cycleOutside, seenOutside);
            heldOutside.Clear();
        }

        void LateUpdate()
        {
            if (!Able) return;
            LastMoments.Step();
            // (the heads leant in the game's own pictures of the portraits too, while the ride leans them in this mod's: see Ride.Hook)
#if DEV
            Ride.Hook(on.Value && ride.Value && !testUprightInTheirs);
#else
            Ride.Hook(on.Value && ride.Value);
#endif
            if (!on.Value) { if (held.Count > 0 || heldOutside.Count > 0) LetGo(); report = "off"; return; }
            KerbalPortraitGallery gallery = KerbalPortraitGallery.Instance;
            if (gallery == null || !gallery.isActiveAndEnabled) return;

            // Whose portraits are showing. (Looked up a few times a second; which seats are showing changes rarely.) Each one this
            // draws has the game's own timer stopped, and started again should this not have drawn it for a second (its share
            // spent on the others for long, or anything gone wrong): the game takes its own pictures of it again until this does.
            if (Time.unscaledTime >= nextLook)
            {
                float now = Time.unscaledTime;
                nextLook = now + 0.25f;
                inside.Clear();
                out_.Clear();
                var crew = crewList.GetValue(gallery) as List<KerbalPortrait>;
                if (crew != null)
                    for (int n = 0; n < crew.Count; n++)
                    {
                        if (crew[n] == null) continue;
                        Kerbal kerbal = crew[n].crewMember;
                        if (kerbal != null && kerbal.isActiveAndEnabled && (bool)seen.GetValue(kerbal))
                        {
                            inside.Add(kerbal);
                            if (!held.TryGetValue(kerbal, out float drawn)) held[kerbal] = drawn = now;
#if DEV
                            if (testBoth) { Resume(kerbal, timer, cycle, seen); continue; }
#endif
                            if (now - drawn < 1f) Halt(kerbal, timer);
                            else if (Resume(kerbal, timer, cycle, seen)) givenBack++;
                            continue;
                        }
                        KerbalEVA walker = crew[n].crewEVAMember;
                        if (walker == null || !outside.Value || !AbleOutside) continue;
                        if (!walker.isActiveAndEnabled || !(bool)seenOutside.GetValue(walker) || walker.vessel != FlightGlobals.ActiveVessel || walker.kerbalPortraitCamera == null) continue;
                        out_.Add(walker);
                        if (!heldOutside.TryGetValue(walker, out float drawnOutside)) heldOutside[walker] = drawnOutside = now;
                        if (now - drawnOutside < 1f) Halt(walker, timerOutside);
                        else if (Resume(walker, timerOutside, cycleOutside, seenOutside)) givenBack++;
                    }
                if (!outside.Value && heldOutside.Count > 0)
                {
                    foreach (KerbalEVA walker in heldOutside.Keys) if (walker != null) Resume(walker, timerOutside, cycleOutside, seenOutside);
                    heldOutside.Clear();
                }
                // (gone from view: the game stopped its timer itself, and starts it when the portrait shows again, which this then stops)
                if (held.Count > inside.Count) foreach (Kerbal gone in new List<Kerbal>(held.Keys)) if (gone == null || !inside.Contains(gone)) held.Remove(gone);
                if (heldOutside.Count > out_.Count) foreach (KerbalEVA gone in new List<KerbalEVA>(heldOutside.Keys)) if (gone == null || !out_.Contains(gone)) heldOutside.Remove(gone);
#if DEV
                theirs.Clear();
                foreach (Kerbal k in held.Keys) { Camera c = k.protoCrewMember != null && k.protoCrewMember.seat != null && k.protoCrewMember.seat.portraitCamera != null ? k.protoCrewMember.seat.portraitCamera : k.kerbalCam; if (c != null) theirs.Add(c); }
                foreach (KerbalEVA k in heldOutside.Keys) if (k.kerbalPortraitCamera != null) theirs.Add(k.kerbalPortraitCamera);
                if (!counting) { Camera.onPreRender += Counted; counting = true; }
#endif
            }
            if (inside.Count == 0 && out_.Count == 0) report = "no portraits showing";
            // (the heads leant by the ride here, after the faces' animation and before Unity makes their skin for this frame, and put
            // back at its end: see Ride)
            if (ride.Value && inside.Count > 0) { Ride.Step(inside, Time.deltaTime); Ride.TurnHeads(inside); }
#if DEV
            Watch();
#endif
        }

#if DEV
        // (for the development build: pictures of the portraits this holds taken by anything but this mod, the game's own timer above all)
        static readonly HashSet<Camera> theirs = new HashSet<Camera>();
        static bool counting, ours;
        static int othersTook;
        static void Counted(Camera cam)
        {
            if (ours || cam == null || !theirs.Contains(cam)) return;
            othersTook++;
            Note("theirs " + cam.name + " < " + Caller());
        }

        // (and what happened around them, frame by frame, for Others: each portrait's showing and the game's timer for it going or not, and
        // this mod's own slow pictures and frames without any)
        static readonly List<string> notes = new List<string>();
        readonly Dictionary<Kerbal, int> watched = new Dictionary<Kerbal, int>();
        static void Note(string what) { if (notes.Count < 400) notes.Add("f" + Time.frameCount + " " + Time.unscaledTime.ToString("F3") + " " + what); }

        static string Caller()
        {
            var said = new System.Text.StringBuilder();
            System.Diagnostics.StackFrame[] frames = new StackTrace(2, false).GetFrames() ?? new System.Diagnostics.StackFrame[0];
            int n = 0;
            foreach (System.Diagnostics.StackFrame f in frames)
            {
                System.Reflection.MethodBase m = f.GetMethod();
                if (m == null) continue;
                if (n++ > 0) said.Append(" < ");
                said.Append(m.DeclaringType != null ? m.DeclaringType.Name + "." : "").Append(m.Name);
                if (n >= 9) break;
            }
            return said.ToString();
        }

        void Watch()
        {
            foreach (Kerbal k in held.Keys)
            {
                if (k == null) continue;
                int now = ((bool)seen.GetValue(k) ? 1 : 0) + (timer.GetValue(k) != null ? 2 : 0);
                if (watched.TryGetValue(k, out int was) && was == now) continue;
                watched[k] = now;
                Note(k.crewMemberName + ": showing " + ((now & 1) != 0) + ", the game's timer " + ((now & 2) != 0 ? "going" : "stopped"));
            }
        }

        /// <summary>For the development build: the notes since the last call (pictures of the portraits taken by others, and by whom; each portrait's showing and timer as they change; this mod's slow pictures and frames without any), and cleared.</summary>
        public static string Others()
        {
            string said = othersTook + " taken by others; " + string.Join("\n", notes.ToArray());
            notes.Clear();
            return said;
        }
#endif

        /// <summary>
        /// The pictures are taken when the frame is finished and on the screen: everything in the scene has been
        /// moved and posed for this frame by then, so a picture costs only its own drawing. (Taken earlier in the
        /// frame, as the game's own are, the first camera to draw anything has to wait while the whole scene is
        /// made ready for drawing: measured from here that looked like 3.7 ms a picture, where it is 0.2.)
        /// </summary>
        System.Collections.IEnumerator AfterEachFrame()
        {
            var finished = new WaitForEndOfFrame();
            while (true)
            {
                yield return finished;
#if DEV
                Ride.Check(0);
                Ride.Seen("by the frame's end, before this mod's pictures", null);
#endif
                try { Pictures(); }
                catch (Exception ex) { Kit.Log("Smooth Portraits", "stopped for this frame: " + ex.Message); }
#if DEV
                Ride.Check(1);
                Ride.Seen("after this mod's pictures", null);
#endif
                // (and, while a crash may be near, the crew's last pictures taken ahead: see LastMoments.Prepare)
                try { if (Able) LastMoments.Prepare(); }
                catch (Exception ex) { Kit.Log("Smooth Portraits", "last pictures not taken ahead: " + ex.Message); }
                finally { Ride.Upright(); }
            }
        }

        void Pictures()
        {
            if (!Able || !on.Value || (inside.Count == 0 && out_.Count == 0)) return;
            KerbalPortraitGallery gallery = KerbalPortraitGallery.Instance;
            if (gallery == null || !gallery.isActiveAndEnabled) return;
#if DEV
            if (probing || testNone) return;
#endif
            // How many pictures this frame may take: as many as the rate asks for, and no more than its share of the frame pays for.
            float frame = Mathf.Clamp(Time.unscaledDeltaTime, 0.002f, 0.1f);
            double purse = frame * share.Value * 0.01, each = inside.Count > 0 && out_.Count > 0 ? 0.5 * purse : purse;
            long began = Stopwatch.GetTimestamp();
            // (The game's way of taking a picture leaves the portrait's own texture as the place everything after it is
            // drawn to and read from. In the middle of a frame the next camera puts that right; here nothing would, and
            // whatever reads the screen at the end of a frame, a screenshot say, would get a portrait instead.)
            RenderTexture before = RenderTexture.active;
            int taken = Take(inside.Count, ref credit, ref turn, ref saved, each, frame, drawInside, ref costInside);
#if DEV
            if (taken < inside.Count) Note("ours " + taken + " of " + inside.Count + ": saved " + (saved * 1000.0).ToString("F2") + " ms, a picture costing " + (costInside * 1000.0).ToString("F2"));
#endif
            int takenOutside = Take(out_.Count, ref creditOutside, ref turnOutside, ref savedOutside, each, frame, drawOutside, ref costOutside);
            if (taken + takenOutside > 0) RenderTexture.active = before;

            tallyFrames++; tallyInside += taken; tallyOutside += takenOutside;
            tallySpent += (Stopwatch.GetTimestamp() - began) / (double)Stopwatch.Frequency;
            float since = Time.unscaledTime - tallyFrom;
            if (since >= 1f)
            {
                report = (inside.Count > 0 ? inside.Count + " portrait" + (inside.Count == 1 ? "" : "s") + " inside at " + (tallyInside / since / inside.Count).ToString("F0") + " a second each (" + (costInside * 1000.0).ToString("F2") + " ms a picture)" : "") +
                         (inside.Count > 0 && out_.Count > 0 ? "; " : "") +
                         (out_.Count > 0 ? out_.Count + " outside at " + (tallyOutside / since / out_.Count).ToString("F0") + " a second (" + (costOutside * 1000.0).ToString("F2") + " ms a picture)" : "") +
                         "; " + (tallySpent / tallyFrames * 1000.0).ToString("F2") + " ms a frame, " + (100.0 * tallySpent / since).ToString("F1") + "% of the time, at " + (tallyFrames / since).ToString("F0") + " frames a second";
                tallyFrom = Time.unscaledTime; tallyFrames = 0; tallyInside = 0; tallyOutside = 0; tallySpent = 0.0;
            }
        }

        double saved, savedOutside;                                        // time put by for pictures and not yet spent, seconds
        Action<int> drawInside, drawOutside;

        void Start()
        {
            drawInside = n =>
            {
                Kerbal kerbal = inside[n];
                bool shaken = ride.Value;
                if (shaken) Ride.ShakeCamera(kerbal);
                Ride.Taking(kerbal);
#if DEV
                ours = true;
#endif
                try { Draw(kerbal); }
                finally
                {
                    Ride.Taken();
                    if (shaken) Ride.StillCamera(kerbal);
#if DEV
                    ours = false;
#endif
                }
                if (held.ContainsKey(kerbal)) held[kerbal] = Time.unscaledTime;
            };
            drawOutside = n =>
            {
                KerbalEVA walker = out_[n];
#if DEV
                ours = true;
                try { Draw(walker); } finally { ours = false; }
#else
                Draw(walker);
#endif
                if (heldOutside.ContainsKey(walker)) heldOutside[walker] = Time.unscaledTime;
            };
            StartCoroutine(AfterEachFrame());
            if (Able) { LastMoments.Hook(); StartCoroutine(LastMoments.Watch()); StartCoroutine(LastMoments.WarmUp()); }
        }

        /// <summary>
        /// Take this frame's pictures of one kind of portrait, in turn, and time them.
        ///
        /// Two things are counted. 'credit' is how many pictures are owed: it grows each frame by what the
        /// rate asks for (by no more than one picture for each portrait, which is every portrait in every
        /// frame). 'saved' is the time put by to pay for them: it grows each frame by that frame's share.
        /// A picture is taken for each whole one owed, so long as what is saved covers what a picture has
        /// been costing. So where the share is too small for every portrait in every frame they take turns,
        /// and a picture that costs more than one frame's share (a kerbal outside) is taken once every few
        /// frames, when enough has been put by. Nothing is saved up for long: what is not spent within a
        /// few frames is let go, or it would all be spent in one frame later and hold that frame up.
        /// </summary>
        int Take(int count, ref float credit, ref int turn, ref double saved, double purse, float frame, Action<int> picture, ref double cost)
        {
            if (count == 0) { credit = 0f; saved = 0.0; return 0; }
            credit = Mathf.Min(credit + Mathf.Min(most.Value * frame, 1f) * count, count + 0.999f);       // (a part of a picture owed is carried over; whole frames' worth are not)
            saved = Math.Min(saved + purse, Math.Max(4.0 * purse, 1.2 * cost));
            int taken = 0;
            while (credit >= 1f && taken < count && saved >= cost)
            {
                long began = Stopwatch.GetTimestamp();
                try { picture(turn % count); }
                catch (Exception ex) { Kit.Log("Smooth Portraits", "a portrait could not be drawn: " + ex.Message); }
                double took = (Stopwatch.GetTimestamp() - began) / (double)Stopwatch.Frequency;
#if DEV
                if (took > 0.0015) Note("ours slow: " + (took * 1000.0).ToString("F2") + " ms, the picture before costing " + (cost * 1000.0).ToString("F2"));
#endif
                cost += (took - cost) * 0.05;
                turn = (turn + 1) % count;
                credit -= 1f;
                saved -= took;
                taken++;
            }
            return taken;
        }

        /// <summary>(For LastMoments.) The gallery's portraits, as it keeps them.</summary>
        internal static List<KerbalPortrait> Crew(KerbalPortraitGallery gallery) => gallery != null && crewList != null ? crewList.GetValue(gallery) as List<KerbalPortrait> : null;

        /// <summary>(For LastMoments.) Whether a portrait is showing (not scrolled out of the gallery's view).</summary>
        internal static bool Showing(KerbalPortrait portrait)
        {
            if (portrait == null || portrait.portrait == null || !portrait.portrait.enabled) return false;
            if (portrait.crewMember != null) return seen != null && (bool)seen.GetValue(portrait.crewMember);
            return portrait.crewEVAMember != null && (seenOutside == null || (bool)seenOutside.GetValue(portrait.crewEVAMember));
        }

        /// <summary>(For LastMoments.) A picture of a kerbal inside, the game's own way, into their portrait's texture.</summary>
        internal static void Picture(Kerbal kerbal) { if (drawFromSeat != null && draw != null) Draw(kerbal); }

        /// <summary>One portrait's picture, the way the game's own timer has it taken (see Kerbal.kerbalAvatarUpdateCycle).</summary>
        static void Draw(Kerbal kerbal)
        {
            if (kerbal == null || !kerbal.isActiveAndEnabled) return;
            Sharpen(kerbal.avatarTexture);
            ProtoCrewMember member = kerbal.protoCrewMember;
            if (member != null && member.seat != null && member.seat.portraitCamera != null) drawFromSeat(kerbal, member.seat.portraitCamera);
            else draw(kerbal);
        }

        /// <summary>The same for a kerbal outside (see KerbalEVA.kerbalAvatarUpdateCycle): the sky, the air, the far scene, the near one, then the kerbal's own camera.</summary>
        static void Draw(KerbalEVA kerbal)
        {
            if (kerbal == null || !kerbal.isActiveAndEnabled || kerbal.kerbalPortraitCamera == null) return;
            RenderTexture picture = kerbal.AvatarTexture;
            Sharpen(picture);
            RenderTexture.active = picture;
            Shoot(kerbal.kerbalCamSkyBox, picture);
            Shoot(kerbal.kerbalCamAtmos, picture);
            Shoot(kerbal.kerbalCam01, picture);
            Shoot(kerbal.kerbalCam00, picture);
            Shoot(kerbal.kerbalPortraitCamera, picture);
            RenderTexture.active = null;
        }

        // (the portraits' pictures made over with smoothed edges, once each: the same pictures, which the portraits' panel shows)
        static readonly HashSet<RenderTexture> sharpened = new HashSet<RenderTexture>();

        /// <summary>
        /// A portrait's picture made over, once, with four samples a point for smooth edges, and in full colour where the game made it
        /// sixteen-bit (a kerbal outside: RGB565, which bands). The same picture object is kept, released and made again, so the
        /// portraits' panel, which shows it, goes on showing it.
        /// </summary>
        static void Sharpen(RenderTexture picture)
        {
            if (picture == null || sharp == null || !sharp.Value || sharpened.Contains(picture)) return;
            sharpened.Add(picture);
            if (sharpened.Count > 64) sharpened.RemoveWhere(t => t == null);
            try
            {
                int samples = QualitySettings.antiAliasing > 1 ? Mathf.Max(QualitySettings.antiAliasing, 4) : 4;
                if (picture.antiAliasing >= samples && picture.format != RenderTextureFormat.RGB565) return;
                picture.Release();
                picture.antiAliasing = samples;
                if (picture.format == RenderTextureFormat.RGB565) picture.format = RenderTextureFormat.ARGB32;
                picture.Create();
            }
            catch (Exception) { }
        }

        static void Shoot(Camera camera, RenderTexture onto)
        {
            if (camera == null) return;
            camera.targetTexture = onto;
            camera.Render();
        }

        void Readout()
        {
            GUILayout.Space(4f);
            GUILayout.Label("<size=11><color=#b0d8ff>" + (Able ? report : "This version of the game keeps its portraits somewhere this mod does not know: left to the game.") + "</color></size>", Host.Rich);
        }

#if DEV
        /// <summary>For the development build: what a portrait's picture costs, taken apart: the camera as it is, and with one thing after another left out.</summary>
        public static string Probe()
        {
            foreach (Portraits one in FindObjectsOfType<Portraits>())
            {
                if (one.inside.Count == 0) return "no portrait showing";
                Kerbal kerbal = one.inside[0];
                ProtoCrewMember member = kerbal.protoCrewMember;
                Camera camera = member != null && member.seat != null && member.seat.portraitCamera != null ? member.seat.portraitCamera : kerbal.kerbalCam;
                var text = new System.Text.StringBuilder();
                text.Append(camera.name + " (" + (camera == kerbal.kerbalCam ? "the kerbal's own" : "the seat's") + "): mask " + Layers(camera.cullingMask) + ", path " + camera.actualRenderingPath + ", clear " + camera.clearFlags + ", depth mode " + camera.depthTextureMode +
                            ", hdr " + camera.allowHDR + ", msaa " + camera.allowMSAA + ", near " + camera.nearClipPlane + " far " + camera.farClipPlane + ", fov " + camera.fieldOfView + ", enabled " + camera.enabled + ", parts: ");
                foreach (Component part in camera.GetComponents<Component>()) text.Append(part.GetType().Name + " ");
                text.Append("| buffers: ");
                foreach (UnityEngine.Rendering.CameraEvent when in Enum.GetValues(typeof(UnityEngine.Rendering.CameraEvent)))
                {
                    int n = camera.GetCommandBuffers(when).Length;
                    if (n > 0) text.Append(when + " x" + n + " ");
                }
                text.Append("| lights: ");
                foreach (Light light in FindObjectsOfType<Light>())
                    if (light.isActiveAndEnabled && (light.cullingMask & camera.cullingMask) != 0) text.Append(light.name + " (" + light.type + ", shadows " + light.shadows + ", mask " + Layers(light.cullingMask & camera.cullingMask) + ") ");
                text.Append("| shadows " + QualitySettings.shadows + ", cascades " + QualitySettings.shadowCascades + ", distance " + QualitySettings.shadowDistance + ", pixel lights " + QualitySettings.pixelLightCount);
                int renderers = 0, skinned = 0;
                foreach (Renderer r in FindObjectsOfType<Renderer>())
                    if (r.enabled && r.gameObject.activeInHierarchy && ((1 << r.gameObject.layer) & camera.cullingMask) != 0) { renderers++; if (r is SkinnedMeshRenderer) skinned++; }
                text.Append(" | renderers on its layers: " + renderers + " (" + skinned + " skinned)");

                Func<double> time = () =>
                {
                    Draw(kerbal);
                    long began = Stopwatch.GetTimestamp();
                    for (int n = 0; n < 12; n++) Draw(kerbal);
                    return (Stopwatch.GetTimestamp() - began) * 1000.0 / Stopwatch.Frequency / 12.0;
                };
                text.Append(" || as it is " + time().ToString("F2") + " ms");
                ShadowQuality shadows = QualitySettings.shadows;
                QualitySettings.shadows = ShadowQuality.Disable;
                text.Append("; no shadows " + time().ToString("F2"));
                QualitySettings.shadows = shadows;
                int mask = camera.cullingMask, kerbalMask = kerbal.kerbalCam.cullingMask;
                camera.cullingMask = 0; kerbal.kerbalCam.cullingMask = 0;
                text.Append("; nothing to draw " + time().ToString("F2"));
                camera.cullingMask = mask; kerbal.kerbalCam.cullingMask = kerbalMask;
                for (int layer = 0; layer < 32; layer++)
                {
                    if ((mask & (1 << layer)) == 0) continue;
                    camera.cullingMask = 1 << layer; kerbal.kerbalCam.cullingMask = 1 << layer;
                    text.Append("; only layer " + layer + " (" + LayerMask.LayerToName(layer) + ") " + time().ToString("F2"));
                }
                camera.cullingMask = mask; kerbal.kerbalCam.cullingMask = kerbalMask;
                int lights = QualitySettings.pixelLightCount;
                QualitySettings.pixelLightCount = 0;
                text.Append("; no pixel lights " + time().ToString("F2"));
                QualitySettings.pixelLightCount = lights;
                text.Append("; as it is again " + time().ToString("F2"));
                return text.ToString();
            }
            return "not running";
        }

        /// <summary>For the development build: everyone the gallery knows of, and why each is or is not drawn by the mod.</summary>
        public static string Crew()
        {
            KerbalPortraitGallery gallery = KerbalPortraitGallery.Instance;
            if (gallery == null) return "no gallery";
            var crew = crewList.GetValue(gallery) as List<KerbalPortrait>;
            var text = new System.Text.StringBuilder("gallery " + (gallery.isActiveAndEnabled ? "on" : "off") + ", mode " + gallery.PortraitGalleryMode + ", " + (crew != null ? crew.Count : -1) + " in it: ");
            if (crew != null)
                foreach (KerbalPortrait item in crew)
                {
                    if (item == null) continue;
                    if (item.crewMember != null) text.Append("[inside " + item.crewMember.crewMemberName + ", on " + item.crewMember.isActiveAndEnabled + ", showing " + seen.GetValue(item.crewMember) + "] ");
                    if (item.crewEVAMember != null)
                        text.Append("[outside " + item.crewEVAMember.name + ", on " + item.crewEVAMember.isActiveAndEnabled + ", showing " + (seenOutside != null ? seenOutside.GetValue(item.crewEVAMember) : "?") + ", flown " + (item.crewEVAMember.vessel == FlightGlobals.ActiveVessel) +
                                    ", camera " + (item.crewEVAMember.kerbalPortraitCamera != null) + ", texture " + (item.crewEVAMember.AvatarTexture != null) + ", every " + item.crewEVAMember.KerbalAvatarUpdateInterval + "] ");
                }
            return text.ToString();
        }

        static bool testNone, testBoth, testUprightInTheirs;

        /// <summary>For the development build: the ride's heads (their bones, cameras and skins, and the other pictures they were leant for and not; see Ride.Heads).</summary>
        public static string Heads() => Ride.Heads();

        /// <summary>For the development build: Who("start"), then Who("") a few seconds later: where the turned heads were found turned back (each camera's hooks called one by one; see Ride.Who).</summary>
        public static string Who(string arg) => Ride.Who((arg ?? "").Trim());

        /// <summary>For the development build: the crew's animators animating whether seen or not ("always"), or as the game had them ("").</summary>
        public static string Culling(string arg) => Ride.Culling((arg ?? "").Trim());

        /// <summary>For the development build: "off" leaves a turned head that something turned back as it was left for its picture (as before); anything else turns it again (see Ride.Again).</summary>
        public static string Again(string arg)
        {
            Ride.TestNoAgain = (arg ?? "").Trim() == "off";
            return Ride.TestNoAgain ? "heads turned back left so for their pictures, as before" : "heads turned back turned again before their pictures";
        }

        /// <summary>For the development build: "upright" leaves the heads as the game has them in the game's own pictures of the portraits (as before 2026-10-28); anything else leans them there too.</summary>
        public static string Theirs(string arg)
        {
            testUprightInTheirs = (arg ?? "").Trim() == "upright";
            return testUprightInTheirs ? "the game's own pictures with the heads upright, as before" : "the game's own pictures with the heads leant by the ride";
        }

        /// <summary>For the development build (item 16.3): the cockpit view ("in") or the flight view ("out"), as the game's own keys switch them.</summary>
        public static string Cockpit(string arg)
        {
            if (CameraManager.Instance == null) return "no camera manager";
            if ((arg ?? "").Trim() == "in") return CameraManager.Instance.SetCameraIVA() ? "in the cockpit" : "the cockpit view would not open";
            CameraManager.Instance.SetCameraFlight();
            return "the flight view";
        }

        /// <summary>For the development build (item 16.5): how the portraits are lit and seen: the inside's sun (the game's IVASun) and every lamp lighting the first portrait's layers, and its camera.</summary>
        public static string Lit()
        {
            foreach (Portraits one in FindObjectsOfType<Portraits>())
            {
                if (one.inside.Count == 0) return "no portrait showing";
                Kerbal kerbal = one.inside[0];
                ProtoCrewMember member = kerbal.protoCrewMember;
                Camera camera = member != null && member.seat != null && member.seat.portraitCamera != null ? member.seat.portraitCamera : kerbal.kerbalCam;
                var text = new System.Text.StringBuilder(camera.name + ": fov " + camera.fieldOfView.ToString("F1") + ", near " + camera.nearClipPlane.ToString("F3") + ", far " + camera.farClipPlane.ToString("F1") + ", mask " + Layers(camera.cullingMask) + ", hdr " + camera.allowHDR + ", " + camera.actualRenderingPath + ", effects:");
                foreach (Behaviour b in camera.GetComponents<Behaviour>()) text.Append(' ').Append(b.GetType().Name).Append(b.enabled ? "" : "(off)");
                text.Append(" | ambient ").Append(RenderSettings.ambientMode).Append(' ').Append(RenderSettings.ambientLight.ToString("F2")).Append(", shadows ").Append(QualitySettings.shadows).Append(" to ").Append(QualitySettings.shadowDistance.ToString("F0")).Append(" m");
                foreach (Light light in FindObjectsOfType<Light>())
                    if (light.isActiveAndEnabled && (light.cullingMask & camera.cullingMask) != 0)
                        text.Append(" | ").Append(light.name).Append(light.GetComponent<IVASun>() != null ? " (the inside's sun)" : "").Append(": ").Append(light.type).Append(", intensity ").Append(light.intensity.ToString("F2")).Append(", colour ").Append(light.color.ToString("F2"))
                            .Append(", shadows ").Append(light.shadows).Append(light.shadows != LightShadows.None ? " " + light.shadowStrength.ToString("F2") : "").Append(", ").Append(light.renderMode).Append(", mask ").Append(Layers(light.cullingMask & camera.cullingMask))
                            .Append(light.type != LightType.Directional ? ", range " + light.range.ToString("F1") + " at " + Vector3.Distance(light.transform.position, camera.transform.position).ToString("F2") + " m" : ", from " + (-light.transform.forward).ToString("F2"));
                return text.ToString();
            }
            return "not running";
        }

        /// <summary>For the development build: "none" stops the mod's pictures while it still holds the portraits (the game's own come back after a second, at the game's pace: 52 of two portraits in 3.1 s on the PC, 2026-10-28); "both" lets them go on with the game's own timer kept going beside them, as when it is started again in the middle of this mod's pictures; anything else lets them go on alone.</summary>
        public static string Hold(string arg)
        {
            string how = (arg ?? "").Trim();
            testNone = how == "none";
            testBoth = how == "both";
            return testNone ? "the mod's pictures stopped, the portraits still held" : testBoth ? "the mod's pictures going on, and the game's own beside them" : "the mod's pictures going on";
        }

        static string probed = "not run";
        public static string Probed() => probed;

        /// <summary>For the development build: the cost of one picture a frame (what smooth portraits would pay), with one thing after another changed. Read the answer with Probed a few seconds later.</summary>
        public static string ProbeFrames()
        {
            foreach (Portraits one in FindObjectsOfType<Portraits>())
            {
                if (one.inside.Count == 0) return "no portrait showing";
                one.StartCoroutine(one.FrameProbe(one.inside[0]));
                return "started";
            }
            return "not running";
        }

        System.Collections.IEnumerator FrameProbe(Kerbal kerbal)
        {
            probing = true;
            var text = new System.Text.StringBuilder();
            SkinnedMeshRenderer[] skins = kerbal.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            Animator[] animators = kerbal.GetComponentsInChildren<Animator>(true);
            Animation[] animations = kerbal.GetComponentsInChildren<Animation>(true);
            text.Append(skins.Length + " skinned meshes: ");
            foreach (SkinnedMeshRenderer skin in skins)
                text.Append(skin.name + " (" + (skin.sharedMesh != null ? skin.sharedMesh.vertexCount + " points, " + skin.sharedMesh.blendShapeCount + " shapes" : "no mesh") + ", " + skin.bones.Length + " bones, quality " + skin.quality + ", offscreen " + skin.updateWhenOffscreen +
                            ", motion " + skin.skinnedMotionVectors + ", layer " + skin.gameObject.layer + (skin.enabled && skin.gameObject.activeInHierarchy ? "" : ", off") + ") ");
            text.Append("| " + animators.Length + " animators: ");
            foreach (Animator animator in animators) text.Append(animator.name + " (culling " + animator.cullingMode + ", update " + animator.updateMode + (animator.isActiveAndEnabled ? "" : ", off") + ") ");
            text.Append("| " + animations.Length + " old-style animations | ");

            double result = 0.0;
            Func<int, System.Collections.IEnumerator> run = frames => Frames(kerbal, frames, v => result = v);
            yield return run(40); text.Append("one picture a frame: " + result.ToString("F2") + " ms");
            yield return FramesTwice(kerbal, 40, v => result = v); text.Append("; a second picture in the same frame: " + result.ToString("F2"));
            foreach (SkinnedMeshRenderer skin in skins) skin.skinnedMotionVectors = false;
            yield return run(40); text.Append("; without motion vectors for the skinned meshes: " + result.ToString("F2"));
            var qualities = new SkinQuality[skins.Length];
            for (int n = 0; n < skins.Length; n++) { qualities[n] = skins[n].quality; skins[n].quality = SkinQuality.Bone1; }
            yield return run(40); text.Append("; and one bone a point: " + result.ToString("F2"));
            for (int n = 0; n < skins.Length; n++) skins[n].quality = qualities[n];
            foreach (SkinnedMeshRenderer skin in skins)
            {
                if (!skin.enabled || !skin.gameObject.activeInHierarchy) continue;
                skin.enabled = false;
                yield return run(25); text.Append("; without " + skin.name + ": " + result.ToString("F2"));
                skin.enabled = true;
            }
            foreach (Animator animator in animators) animator.enabled = false;
            yield return run(40); text.Append("; with the animators stopped: " + result.ToString("F2"));
            foreach (Animator animator in animators) animator.enabled = true;
            foreach (SkinnedMeshRenderer skin in skins) skin.enabled = false;
            yield return run(40); text.Append("; with no skinned mesh at all: " + result.ToString("F2"));
            foreach (SkinnedMeshRenderer skin in skins) skin.enabled = true;
            yield return run(40); text.Append("; as it was: " + result.ToString("F2"));
            probed = text.ToString();
            probing = false;
        }

        bool probing;

        System.Collections.IEnumerator Frames(Kerbal kerbal, int frames, Action<double> tell)
        {
            yield return null; yield return null;
            double sum = 0.0;
            for (int n = 0; n < frames; n++)
            {
                yield return new WaitForEndOfFrame();
                long began = Stopwatch.GetTimestamp();
                Draw(kerbal);
                sum += (Stopwatch.GetTimestamp() - began) * 1000.0 / Stopwatch.Frequency;
            }
            tell(sum / frames);
        }

        System.Collections.IEnumerator FramesTwice(Kerbal kerbal, int frames, Action<double> tell)
        {
            yield return null; yield return null;
            double sum = 0.0;
            for (int n = 0; n < frames; n++)
            {
                yield return new WaitForEndOfFrame();
                Draw(kerbal);
                long began = Stopwatch.GetTimestamp();
                Draw(kerbal);
                sum += (Stopwatch.GetTimestamp() - began) * 1000.0 / Stopwatch.Frequency;
            }
            tell(sum / frames);
        }

        static string Layers(int mask)
        {
            string text = "";
            for (int layer = 0; layer < 32; layer++) if ((mask & (1 << layer)) != 0) text += (text.Length > 0 ? "+" : "") + layer + ":" + LayerMask.LayerToName(layer);
            return text;
        }
#endif

        /// <summary>For the development build: what it is doing, in a line.</summary>
        public static string Stats()
        {
            foreach (Portraits one in FindObjectsOfType<Portraits>())
                return one.report + " | on " + on.Value + ", at most " + most.Value + ", share " + share.Value + "%, held " + one.held.Count + " + " + one.heldOutside.Count + ", given back to the game " + one.givenBack + " times"
                       + (ride.Value ? " | ride: " + Ride.Now(one.inside) : "")
#if DEV
                       + ", pictures of them taken by others " + othersTook
#endif
                       ;
            return "not running (the flight scene only)";
        }
    }
}
