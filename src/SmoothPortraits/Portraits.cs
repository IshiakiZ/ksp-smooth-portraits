using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Keystone;
using KSP.UI.Screens.Flight;
using UnityEngine;

#if !DEV
[assembly: KSPAssembly("SmoothPortraits", 0, 1)]
[assembly: KSPAssemblyDependency("Keystone", 0, 1)]
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
        public const string Version = "0.1.0";

        static Mod mod;
        static Toggle on, outside;
        static Slider most, share;

        // The game's own, which it keeps to itself (see Keystone.Reach).
        static bool looked;
        static FieldInfo crewList, seen, seenOutside, yields, yieldsOutside;
        static Action<Kerbal> draw;
        static Action<Kerbal, Camera> drawFromSeat;

        readonly WaitForSeconds standBack = new WaitForSeconds(2f);      // what the game's own timers are given to wait on while this draws for them
        readonly List<Kerbal> inside = new List<Kerbal>();
        readonly List<KerbalEVA> out_ = new List<KerbalEVA>();
        readonly HashSet<Kerbal> held = new HashSet<Kerbal>();
        readonly HashSet<KerbalEVA> heldOutside = new HashSet<KerbalEVA>();
        float credit, creditOutside;
        int turn, turnOutside;
        double costInside = 0.0003, costOutside = 0.003;                   // seconds a picture, as measured (a first guess until then)
        float nextLook;

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
            }
        }

        void Awake()
        {
            Setup();
            mod.Panel = Readout;
            if (!looked)
            {
                looked = true;
                crewList = Reach.Field(typeof(KerbalPortraitGallery), "portraits");
                seen = Reach.Field(typeof(Kerbal), "visibleInPortrait");
                yields = Reach.Field(typeof(Kerbal), "updIntervalYield");
                seenOutside = Reach.Field(typeof(KerbalEVA), "visibleInPortrait");
                yieldsOutside = Reach.Field(typeof(KerbalEVA), "updIntervalYield");
                draw = Reach.Call<Action<Kerbal>>(typeof(Kerbal), "kerbalAvatarUpdate");
                drawFromSeat = Reach.Call<Action<Kerbal, Camera>>(typeof(Kerbal), "kerbalSeatCamUpdate", typeof(Camera));
            }
        }

        bool Able => crewList != null && seen != null && yields != null && draw != null && drawFromSeat != null;

        void OnDestroy()
        {
            LetGo();
            if (mod != null && mod.Panel == Readout) mod.Panel = null;
        }

        /// <summary>Hand every portrait back to the game's own timer.</summary>
        void LetGo()
        {
            foreach (Kerbal kerbal in held)
                if (kerbal != null) yields.SetValue(kerbal, new WaitForSeconds(kerbal.updateInterval > 0f ? kerbal.updateInterval : 0.12f));
            held.Clear();
            foreach (KerbalEVA kerbal in heldOutside)
                if (kerbal != null) yieldsOutside.SetValue(kerbal, new WaitForSeconds(kerbal.KerbalAvatarUpdateInterval > 0f ? kerbal.KerbalAvatarUpdateInterval : 0.12f));
            heldOutside.Clear();
        }

        void LateUpdate()
        {
            if (!Able) return;
            if (!on.Value) { if (held.Count > 0 || heldOutside.Count > 0) LetGo(); report = "off"; return; }
            KerbalPortraitGallery gallery = KerbalPortraitGallery.Instance;
            if (gallery == null || !gallery.isActiveAndEnabled) return;

            // Whose portraits are showing. (Looked up a few times a second; which seats are showing changes rarely.)
            if (Time.unscaledTime >= nextLook)
            {
                nextLook = Time.unscaledTime + 0.25f;
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
                            if (held.Add(kerbal)) yields.SetValue(kerbal, standBack);
                            continue;
                        }
                        KerbalEVA walker = crew[n].crewEVAMember;
                        if (walker == null || !outside.Value || seenOutside == null || yieldsOutside == null) continue;
                        if (!walker.isActiveAndEnabled || !(bool)seenOutside.GetValue(walker) || walker.vessel != FlightGlobals.ActiveVessel || walker.kerbalPortraitCamera == null) continue;
                        out_.Add(walker);
                        if (heldOutside.Add(walker)) yieldsOutside.SetValue(walker, standBack);
                    }
                if (!outside.Value && heldOutside.Count > 0)
                {
                    foreach (KerbalEVA walker in heldOutside)
                        if (walker != null) yieldsOutside.SetValue(walker, new WaitForSeconds(walker.KerbalAvatarUpdateInterval > 0f ? walker.KerbalAvatarUpdateInterval : 0.12f));
                    heldOutside.Clear();
                }
                held.RemoveWhere(k => k == null);
                heldOutside.RemoveWhere(k => k == null);
            }
            if (inside.Count == 0 && out_.Count == 0) report = "no portraits showing";
        }

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
                try { Pictures(); }
                catch (Exception ex) { Kit.Log("Smooth Portraits", "stopped for this frame: " + ex.Message); }
            }
        }

        void Pictures()
        {
            if (!Able || !on.Value || (inside.Count == 0 && out_.Count == 0)) return;
            KerbalPortraitGallery gallery = KerbalPortraitGallery.Instance;
            if (gallery == null || !gallery.isActiveAndEnabled) return;
#if DEV
            if (probing) return;
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
            drawInside = n => Draw(inside[n]);
            drawOutside = n => Draw(out_[n]);
            StartCoroutine(AfterEachFrame());
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
                cost += (took - cost) * 0.05;
                turn = (turn + 1) % count;
                credit -= 1f;
                saved -= took;
                taken++;
            }
            return taken;
        }

        /// <summary>One portrait's picture, the way the game's own timer has it taken (see Kerbal.kerbalAvatarUpdateCycle).</summary>
        static void Draw(Kerbal kerbal)
        {
            if (kerbal == null || !kerbal.isActiveAndEnabled) return;
            ProtoCrewMember member = kerbal.protoCrewMember;
            if (member != null && member.seat != null && member.seat.portraitCamera != null) drawFromSeat(kerbal, member.seat.portraitCamera);
            else draw(kerbal);
        }

        /// <summary>The same for a kerbal outside (see KerbalEVA.kerbalAvatarUpdateCycle): the sky, the air, the far scene, the near one, then the kerbal's own camera.</summary>
        static void Draw(KerbalEVA kerbal)
        {
            if (kerbal == null || !kerbal.isActiveAndEnabled || kerbal.kerbalPortraitCamera == null) return;
            RenderTexture picture = kerbal.AvatarTexture;
            RenderTexture.active = picture;
            Shoot(kerbal.kerbalCamSkyBox, picture);
            Shoot(kerbal.kerbalCamAtmos, picture);
            Shoot(kerbal.kerbalCam01, picture);
            Shoot(kerbal.kerbalCam00, picture);
            Shoot(kerbal.kerbalPortraitCamera, picture);
            RenderTexture.active = null;
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
            foreach (Portraits one in FindObjectsOfType<Portraits>()) return one.report + " | on " + on.Value + ", at most " + most.Value + ", share " + share.Value + "%, held " + one.held.Count + " + " + one.heldOutside.Count;
            return "not running (the flight scene only)";
        }
    }
}
