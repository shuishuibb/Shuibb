using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MapleLib;
using MapleLib.Converters;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace SkillPreview
{
    internal sealed class EffectFrame
    {
        internal WzCanvasProperty Canvas;
        internal string Phase;
        internal int Delay;

        internal EffectFrame(WzCanvasProperty canvas, string phase, int delay)
        {
            Canvas = canvas;
            Phase = phase;
            Delay = delay;
        }
    }

    internal sealed class CharacterPart
    {
        internal WzCanvasProperty Canvas;
        internal Point Origin;

        internal CharacterPart(WzCanvasProperty canvas, Point origin)
        {
            Canvas = canvas;
            Origin = origin;
        }
    }

    internal sealed class CharacterFrame
    {
        internal string Label;
        internal List<CharacterPart> Parts;
        internal int Delay;
        internal Point Move;

        internal CharacterFrame(string label, List<CharacterPart> parts, int delay, Point move)
        {
            Label = label;
            Parts = parts;
            Delay = delay;
            Move = move;
        }
    }

    /// <summary>
    /// One animation container found on the skill: "effect", "effect0", "affected", and so on.
    /// Each container keeps its own playback position, because containers are layers that run
    /// concurrently and can differ in both frame count and per-frame delay.
    /// </summary>
    internal sealed class EffectGroup
    {
        internal string Name;
        /// <summary>0 for the effect family, 1 for affected. Families never mix on screen.</summary>
        internal int Family;
        /// <summary>
        /// The top-level container this came from ("effect", "effect0", ...). Groups sharing a
        /// root are random variants of one animation, so only one of them may play at a time.
        /// </summary>
        internal string RootContainer;
        internal List<EffectFrame> Frames = new List<EffectFrame>();
        internal int CurrentIndex;

        internal EffectGroup(string name, int family)
        {
            Name = name;
            Family = family;
        }

        internal EffectFrame CurrentFrame
        {
            get
            {
                if (Frames.Count == 0)
                    return null;
                return Frames[Math.Min(CurrentIndex, Frames.Count - 1)];
            }
        }

        /// <summary>Length of one full loop of this container, in milliseconds.</summary>
        internal int LoopDuration
        {
            get
            {
                int total = 0;
                foreach (EffectFrame frame in Frames)
                    total += Math.Max(EffectRenderer.MinimumFrameDelay, frame.Delay);
                return Math.Max(EffectRenderer.MinimumFrameDelay, total);
            }
        }

        /// <summary>Frame showing at <paramref name="position"/> ms into the loop.</summary>
        internal void SeekTo(int position)
        {
            int accumulated = 0;
            for (int i = 0; i < Frames.Count; i++)
            {
                int delay = Math.Max(EffectRenderer.MinimumFrameDelay, Frames[i].Delay);
                if (position < accumulated + delay)
                {
                    CurrentIndex = i;
                    return;
                }
                accumulated += delay;
            }
            CurrentIndex = Math.Max(0, Frames.Count - 1);
        }
    }

    /// <summary>
    /// Plays a skill's effect/affected animation, optionally composited over the character
    /// body animation the skill's "action" entry points at.
    /// </summary>
    internal sealed class EffectRenderer
    {
        internal const int MinimumFrameDelay = 16;

        /// <summary>
        /// Selection keys for the two synthetic entries that overlay a whole family at once.
        /// "effect" containers are layers of the caster's effect and belong on screen together;
        /// "affected" plays on the target instead, so it is kept as its own separate view.
        /// The leading '*' cannot appear in a WZ node name, so a family key can never collide
        /// with a container that happens to be called "effect".
        /// </summary>
        internal const string EffectFamilyKey = "*effect";
        internal const string AffectedFamilyKey = "*affected";

        private readonly List<EffectGroup> groups = new List<EffectGroup>();
        private readonly List<EffectGroup> activeGroups = new List<EffectGroup>();
        private readonly List<CharacterFrame> characterFrames = new List<CharacterFrame>();

        private readonly BitmapSource fallbackCharacter;
        private readonly BitmapSource characterHead;

        private WzFileManager fileManager;
        private int characterFrameIndex;
        private int elapsedMilliseconds;

        internal double Zoom = 1.0;
        internal bool WhiteBackground;

        internal string InfoText { get; private set; }

        internal bool HasContent
        {
            get { return activeGroups.Count > 0 || characterFrames.Count > 0; }
        }

        internal EffectRenderer()
        {
            fallbackCharacter = PreviewAssets.Load("222.png");
            characterHead = PreviewAssets.Load("333.png");
        }

        /// <summary>
        /// The selectable views, as (key, label) pairs: one combined entry per family that has
        /// more than a single container, then each individual container.
        /// </summary>
        internal List<KeyValuePair<string, string>> GroupSelections
        {
            get
            {
                List<KeyValuePair<string, string>> selections = new List<KeyValuePair<string, string>>();

                if (CountInFamily(0) > 1)
                    selections.Add(new KeyValuePair<string, string>(EffectFamilyKey, "effect 全部"));
                if (CountInFamily(1) > 1)
                    selections.Add(new KeyValuePair<string, string>(AffectedFamilyKey, "affected 全部"));

                foreach (EffectGroup group in groups)
                    selections.Add(new KeyValuePair<string, string>(group.Name, group.Name));

                return selections;
            }
        }

        /// <summary>
        /// How many separate containers the family spans. Random variants of one container
        /// count once, because the combined view can only ever show one of them - offering a
        /// "全部" button for them would promise something it does not do.
        /// </summary>
        private int CountInFamily(int family)
        {
            HashSet<string> containers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (EffectGroup group in groups)
                if (group.Family == family)
                    containers.Add(group.RootContainer ?? group.Name);
            return containers.Count;
        }

        /// <summary>The view selected when a skill is first loaded.</summary>
        private string GetDefaultSelection()
        {
            if (CountInFamily(0) > 1)
                return EffectFamilyKey;
            foreach (EffectGroup group in groups)
                if (group.Family == 0)
                    return group.Name;
            if (CountInFamily(1) > 1)
                return AffectedFamilyKey;
            return groups.Count > 0 ? groups[0].Name : null;
        }

        internal void Load(WzObject skillNode, WzFileManager fileManager)
        {
            this.fileManager = fileManager;
            CanvasResolver.ResetFailedLookups();
            groups.Clear();
            activeGroups.Clear();
            characterFrames.Clear();
            characterFrameIndex = 0;
            elapsedMilliseconds = 0;

            if (skillNode == null)
                return;

            foreach (WzObject container in OrderEffectContainers(WzNav.GetChildren(skillNode)))
            {
                CollectGroups(container, container.Name, container.Name,
                    GetEffectFamilyRank(container.Name), 0);
            }

            SelectGroup(GetDefaultSelection());
            LoadCharacterActionFrames(skillNode, fileManager);
        }

        /// <summary>
        /// Turns one animation container into playable groups.
        ///
        /// Usually the container holds the numbered frames directly. But a skill can also wrap
        /// its animation in variant nodes the client picks between at random:
        ///
        ///     effect/random0/effect/0..n
        ///     effect/random1/effect/0..n
        ///
        /// Those are alternatives, not layers, so each variant becomes its own group instead of
        /// having every frame flattened into a single container - which would interleave the
        /// two variants' identically numbered frames into nonsense.
        /// </summary>
        private void CollectGroups(WzObject node, string rootName, string label, int family, int depth)
        {
            node = WzNav.Deref(node);
            if (node == null || depth > 4)
                return;

            List<WzCanvasProperty> frames = new List<WzCanvasProperty>();
            List<WzObject> subContainers = new List<WzObject>();

            foreach (WzObject child in WzNav.GetChildren(node))
            {
                WzObject target = WzNav.Deref(child);
                WzCanvasProperty canvas = target as WzCanvasProperty;
                if (canvas != null)
                {
                    // Only numbered canvases are animation frames; "icon" and friends are not.
                    if (WzNav.ParseFrameIndex(child.Name) != int.MaxValue)
                        frames.Add(canvas);
                }
                else if (target is IPropertyContainer)
                {
                    subContainers.Add(child);
                }
            }

            if (frames.Count > 0)
            {
                EffectGroup group = new EffectGroup(TrimLabel(label, family), family);
                group.RootContainer = rootName;
                foreach (WzCanvasProperty canvas in frames
                    .OrderBy(c => WzNav.ParseFrameIndex(c.Name))
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                {
                    group.Frames.Add(new EffectFrame(canvas, group.Name, WzNav.GetCanvasDelay(canvas)));
                }
                groups.Add(group);
                return;
            }

            foreach (WzObject child in subContainers)
                CollectGroups(child, rootName, label + "/" + child.Name, family, depth + 1);
        }

        /// <summary>
        /// "effect/random0/effect" reads better as "effect/random0" - the trailing repeat of the
        /// family name carries no information.
        /// </summary>
        private static string TrimLabel(string label, int family)
        {
            string familyName = family == 1 ? "affected" : "effect";
            string suffix = "/" + familyName;
            if (label.Length > suffix.Length && label.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return label.Substring(0, label.Length - suffix.Length);
            return label;
        }

        /// <summary>
        /// Picks out the skill's animation containers. Besides the plain "effect"/"affected"
        /// a skill may carry numbered variants - effect0, effect1, affected0 - so all of them
        /// are collected. Every "effect*" is ordered ahead of every "affected*", and within
        /// each family the unsuffixed node comes first, then the numbered ones in order.
        /// </summary>
        private static IEnumerable<WzObject> OrderEffectContainers(IEnumerable<WzObject> children)
        {
            List<WzObject> matched = new List<WzObject>();
            foreach (WzObject child in children)
            {
                if (GetEffectFamilyRank(child.Name) >= 0)
                    matched.Add(child);
            }
            return matched
                .OrderBy(c => GetEffectFamilyRank(c.Name))
                .ThenBy(c => GetEffectVariantIndex(c.Name))
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Whether a node name is one of the animation containers this plays.</summary>
        internal static bool IsEffectContainerName(string name)
        {
            return GetEffectFamilyRank(name) >= 0;
        }

        /// <summary>0 for the effect family, 1 for affected, -1 when the name is neither.</summary>
        private static int GetEffectFamilyRank(string name)
        {
            if (name == null)
                return -1;
            if (IsFamilyMember(name, "effect"))
                return 0;
            if (IsFamilyMember(name, "affected"))
                return 1;
            return -1;
        }

        private static bool IsFamilyMember(string name, string family)
        {
            if (!name.StartsWith(family, StringComparison.OrdinalIgnoreCase))
                return false;
            // Exactly the family name, or the family name followed only by digits - so
            // "effect0" counts but "effectLt" or "effect_old" do not.
            return name.Length == family.Length
                || name.Skip(family.Length).All(char.IsDigit);
        }

        /// <summary>Unsuffixed containers sort before numbered ones; "effect2" after "effect10" is wrong, so parse.</summary>
        private static int GetEffectVariantIndex(string name)
        {
            int rank = GetEffectFamilyRank(name);
            if (rank < 0)
                return int.MaxValue;
            string family = rank == 0 ? "effect" : "affected";
            if (name.Length == family.Length)
                return -1;
            int index;
            if (int.TryParse(name.Substring(family.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
                return index;
            return int.MaxValue;
        }

        /// <summary>
        /// Switches the view. A family key overlays every container in that family at once -
        /// they are layers of a single visual effect, not animations to be shown in turn - while
        /// a container name isolates just that one.
        /// </summary>
        internal void SelectGroup(string key)
        {
            SelectedGroup = key;
            activeGroups.Clear();
            elapsedMilliseconds = 0;

            if (key == null)
                return;

            int family = -1;
            if (key == EffectFamilyKey) family = 0;
            else if (key == AffectedFamilyKey) family = 1;

            if (family < 0)
            {
                foreach (EffectGroup group in groups)
                {
                    if (string.Equals(group.Name, key, StringComparison.OrdinalIgnoreCase))
                    {
                        group.CurrentIndex = 0;
                        activeGroups.Add(group);
                    }
                }
                return;
            }

            // Overlay one group per top-level container. Separate containers (effect, effect0,
            // effect1) are layers that belong on screen together; several groups sharing a
            // container are random variants, so only the first is shown - stacking alternatives
            // would draw an effect that never appears in game.
            HashSet<string> usedContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (EffectGroup group in groups)
            {
                if (group.Family != family)
                    continue;
                if (!usedContainers.Add(group.RootContainer ?? group.Name))
                    continue;
                group.CurrentIndex = 0;
                activeGroups.Add(group);
            }
        }

        internal string SelectedGroup { get; private set; }

        // ---- timeline ----------------------------------------------------------------

        /// <summary>
        /// Every active container runs off one shared clock but loops on its own duration, so
        /// layers of differing length stay in step the way they do in game instead of being
        /// stretched to a common timeline.
        /// </summary>
        internal void Advance(int deltaMilliseconds)
        {
            elapsedMilliseconds += Math.Max(1, deltaMilliseconds);

            foreach (EffectGroup group in activeGroups)
                group.SeekTo(elapsedMilliseconds % group.LoopDuration);

            if (characterFrames.Count > 0)
            {
                int characterDuration = 0;
                foreach (CharacterFrame frame in characterFrames)
                    characterDuration += Math.Max(MinimumFrameDelay, frame.Delay);
                characterDuration = Math.Max(MinimumFrameDelay, characterDuration);

                int position = elapsedMilliseconds % characterDuration;
                int accumulated = 0;
                characterFrameIndex = characterFrames.Count - 1;
                for (int i = 0; i < characterFrames.Count; i++)
                {
                    int delay = Math.Max(MinimumFrameDelay, characterFrames[i].Delay);
                    if (position < accumulated + delay)
                    {
                        characterFrameIndex = i;
                        break;
                    }
                    accumulated += delay;
                }
            }
        }

        // ---- character body animation -------------------------------------------------

        private void LoadCharacterActionFrames(WzObject skillNode, WzFileManager fileManager)
        {
            if (fileManager == null)
                return;

            WzImage bodyImage = FindBodyActionImage(fileManager);
            if (bodyImage == null)
                return;

            foreach (string actionName in GetSkillActionNames(skillNode))
            {
                WzObject actionRoot = FindCharacterActionRoot(bodyImage, actionName);
                if (actionRoot == null)
                    continue;

                string visualName = NormalizeVisualActionName(actionName);
                WzObject visualRoot = FindCharacterActionRoot(bodyImage, visualName) ?? actionRoot;

                List<CharacterFrame> built = BuildCharacterFrames(bodyImage, actionRoot, actionName, visualRoot, visualName);
                if (built.Count > 0)
                {
                    characterFrames.AddRange(built);
                    break;
                }
            }
        }

        private static WzImage FindBodyActionImage(WzFileManager fileManager)
        {
            WzImage bodyImage = fileManager.FindWzImageByName("character", "00002000.img") as WzImage;
            if (bodyImage == null)
            {
                foreach (WzFile file in fileManager.WzFileList)
                {
                    WzImage candidate = file?.WzDirectory?["00002000.img"] as WzImage;
                    if (candidate != null)
                    {
                        bodyImage = candidate;
                        break;
                    }
                }
            }
            if (bodyImage == null)
                return null;

            try
            {
                if (!bodyImage.Parsed)
                    bodyImage.ParseImage();
            }
            catch
            {
                return null;
            }
            return bodyImage;
        }

        private static IEnumerable<string> GetSkillActionNames(WzObject skillNode)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in WzNav.EnumerateStringValues(WzNav.GetChild(skillNode, "action")))
            {
                string trimmed = value == null ? null : value.Trim();
                if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
                    yield return trimmed;
            }
        }

        /// <summary>
        /// "alert3" and friends are numbered variants that all render as the plain "alert"
        /// pose, so they collapse to the base name when looking for artwork.
        /// </summary>
        private static string NormalizeVisualActionName(string actionName)
        {
            string trimmed = actionName == null ? null : actionName.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return trimmed;

            const string alert = "alert";
            if (trimmed.StartsWith(alert, StringComparison.OrdinalIgnoreCase)
                && trimmed.Length > alert.Length
                && trimmed.Skip(alert.Length).All(char.IsDigit))
            {
                return alert;
            }
            return trimmed;
        }

        private static WzObject FindCharacterActionRoot(WzImage bodyImage, string actionName)
        {
            WzObject direct = bodyImage[actionName];
            if (direct != null)
                return direct;

            WzObject underAction = WzNav.GetChild(bodyImage["action"], actionName);
            if (underAction != null)
                return underAction;

            return FindDescendantByName(bodyImage, actionName, 0);
        }

        private static WzObject FindDescendantByName(WzObject root, string name, int depth)
        {
            if (WzNav.IsIgnored(root) || depth > 8)
                return null;

            root = WzNav.Deref(root);
            if (string.Equals(root.Name, name, StringComparison.OrdinalIgnoreCase))
                return root;

            foreach (WzObject child in WzNav.GetChildren(root))
            {
                WzObject found = FindDescendantByName(child, name, depth + 1);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static List<CharacterFrame> BuildCharacterFrames(WzImage bodyImage, WzObject actionRoot,
            string sourceActionName, WzObject visualRoot, string visualActionName)
        {
            List<CharacterFrame> result = new List<CharacterFrame>();

            List<WzObject> steps = WzNav.OrderByFrameIndex(
                WzNav.GetChildren(actionRoot).Where(c => WzNav.ParseFrameIndex(c.Name) != int.MaxValue)).ToList();

            if (steps.Count == 0)
            {
                List<WzCanvasProperty> canvases = WzNav.EnumerateCanvases(actionRoot).ToList();
                if (canvases.Count > 0)
                {
                    List<CharacterPart> parts = BuildCharacterParts(canvases, false);
                    result.Add(new CharacterFrame(sourceActionName, parts,
                        GetCompositeDelay(actionRoot, parts), WzNav.GetVector(actionRoot, "move")));
                }
                return result;
            }

            // A skill action often only stores frame timings and delegates the artwork to
            // another action, either through an explicit "action" string per frame or via
            // the normalised visual action resolved by the caller.
            bool usesVisualRoot = visualRoot != null && actionRoot != visualRoot;

            foreach (WzObject step in steps)
            {
                string mappedAction = WzNav.GetFirstStringValue(WzNav.GetChild(step, "action"));
                WzObject frameRoot = null;
                string label = sourceActionName;

                if (usesVisualRoot)
                {
                    frameRoot = ResolveVisualFrameRoot(visualRoot, step);
                    label = sourceActionName + "->" + visualActionName + "/" + (frameRoot != null ? frameRoot.Name : step.Name);
                }
                else if (!string.IsNullOrWhiteSpace(mappedAction))
                {
                    WzObject mappedRoot = FindCharacterActionRoot(bodyImage, mappedAction.Trim());
                    string mappedFrame = ResolveMappedFrameName(mappedRoot, step);
                    frameRoot = WzNav.GetChild(mappedRoot, mappedFrame);
                    label = sourceActionName + "->" + mappedAction + "/" + mappedFrame;
                }

                if (frameRoot == null)
                {
                    frameRoot = step;
                    label = sourceActionName + "/" + step.Name;
                }

                List<WzCanvasProperty> canvases = WzNav.EnumerateCanvases(frameRoot)
                    .OrderBy(GetCharacterPartSortOrder)
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (canvases.Count == 0)
                    continue;

                List<CharacterPart> parts = BuildCharacterParts(canvases, !usesVisualRoot);
                result.Add(new CharacterFrame(label, parts,
                    GetCompositeDelay(step, parts), WzNav.GetVector(step, "move")));
            }

            return result;
        }

        private static string GetFrameName(WzObject step)
        {
            WzObject frame = WzNav.GetChild(step, "frame");
            if (frame == null)
                return null;
            try
            {
                return frame.GetInt().ToString();
            }
            catch
            {
                return frame.GetString();
            }
        }

        private static string ResolveMappedFrameName(WzObject mappedRoot, WzObject step)
        {
            string frameName = GetFrameName(step);
            if (!string.IsNullOrWhiteSpace(frameName) && WzNav.GetChild(mappedRoot, frameName) != null)
                return frameName;

            if (step != null && WzNav.GetChild(mappedRoot, step.Name) != null)
                return step.Name;

            int index = WzNav.ParseFrameIndex(step == null ? null : step.Name);
            List<WzObject> mappedFrames = WzNav.OrderByFrameIndex(
                WzNav.GetChildren(mappedRoot).Where(c => WzNav.ParseFrameIndex(c.Name) != int.MaxValue)).ToList();

            if (index >= 0 && index < mappedFrames.Count)
                return mappedFrames[index].Name;

            WzObject first = mappedFrames.FirstOrDefault();
            if (first != null)
                return first.Name;
            return step == null ? null : step.Name;
        }

        private static WzObject ResolveVisualFrameRoot(WzObject visualRoot, WzObject step)
        {
            if (visualRoot == null || step == null)
                return step;

            WzObject byName = WzNav.GetChild(visualRoot, step.Name);
            if (byName != null)
                return byName;

            int index = WzNav.ParseFrameIndex(step.Name);
            List<WzObject> visualFrames = WzNav.OrderByFrameIndex(
                WzNav.GetChildren(visualRoot).Where(c => WzNav.ParseFrameIndex(c.Name) != int.MaxValue)).ToList();

            if (index >= 0 && index < visualFrames.Count)
                return visualFrames[index];

            return visualFrames.FirstOrDefault() ?? step;
        }

        private static List<CharacterPart> BuildCharacterParts(List<WzCanvasProperty> canvases, bool alignArmParts)
        {
            List<CharacterPart> parts = new List<CharacterPart>();
            WzCanvasProperty bodyCanvas =
                canvases.FirstOrDefault(c => string.Equals(c.Name, "body", StringComparison.OrdinalIgnoreCase))
                ?? canvases.FirstOrDefault();
            Point bodyOrigin = WzNav.GetOrigin(bodyCanvas);

            foreach (WzCanvasProperty canvas in canvases)
            {
                Point origin = alignArmParts
                    ? GetAlignedOrigin(canvas, bodyCanvas, bodyOrigin)
                    : WzNav.GetOrigin(canvas);
                parts.Add(new CharacterPart(canvas, origin));
            }
            return parts;
        }

        /// <summary>
        /// Arm sprites are positioned against the body through their shared navel/neck
        /// attachment point rather than their own origin, so without this correction the
        /// arm floats away from the torso.
        /// </summary>
        private static Point GetAlignedOrigin(WzCanvasProperty canvas, WzCanvasProperty bodyCanvas, Point bodyOrigin)
        {
            if (IsArmPart(canvas))
            {
                Point origin = WzNav.GetOrigin(canvas);
                Point? armAttach = WzNav.GetMapPoint(canvas, "navel") ?? WzNav.GetMapPoint(canvas, "hand");
                Point? bodyAttach = WzNav.GetMapPoint(bodyCanvas, "navel") ?? WzNav.GetMapPoint(bodyCanvas, "neck");
                if (armAttach.HasValue && bodyAttach.HasValue)
                {
                    return new Point(
                        origin.X + armAttach.Value.X - bodyAttach.Value.X,
                        origin.Y + armAttach.Value.Y - bodyAttach.Value.Y);
                }
            }
            return WzNav.GetOrigin(canvas);
        }

        private static bool IsArmPart(WzCanvasProperty canvas)
        {
            return canvas != null && canvas.Name != null
                && canvas.Name.IndexOf("arm", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int GetCharacterPartSortOrder(WzCanvasProperty canvas)
        {
            string name = canvas.Name ?? string.Empty;
            if (name.IndexOf("body", StringComparison.OrdinalIgnoreCase) >= 0)
                return 10;
            if (name.IndexOf("arm", StringComparison.OrdinalIgnoreCase) >= 0)
                return 20;
            return 30;
        }

        private static int GetCompositeDelay(WzObject frameRoot, List<CharacterPart> parts)
        {
            WzObject delayNode = WzNav.GetChild(frameRoot, "delay");
            int delay = 0;
            if (delayNode != null)
            {
                try { delay = delayNode.GetInt(); }
                catch { delay = 0; }
            }

            if (delay > 0)
                return delay;
            if (delay < 0)
                return Math.Abs(delay);

            foreach (CharacterPart part in parts)
            {
                delay = WzNav.GetCanvasDelay(part.Canvas);
                if (delay > 0)
                    return delay;
            }
            return 100;
        }

        // ---- drawing -------------------------------------------------------------------

        internal void Draw(Canvas canvas)
        {
            canvas.Children.Clear();

            double unit = PreviewCanvas.BaseUnit * Zoom;
            DrawBackdrop(canvas, unit);
            DrawCharacter(canvas, unit);

            if (activeGroups.Count == 0)
            {
                InfoText = characterFrames.Count > 0
                    ? "角色動作\n" + (characterFrameIndex + 1) + " / " + characterFrames.Count
                    : "沒有 effect / affected";
                return;
            }

            // Containers are layers of one effect, so they are all drawn on the same frame,
            // in discovery order - later containers land on top.
            List<string> lines = new List<string>();
            string missingLink = null;
            int missingCount = 0;

            foreach (EffectGroup group in activeGroups)
            {
                EffectFrame frame = group.CurrentFrame;
                if (frame == null)
                    continue;

                bool drawn = DrawEffectFrame(canvas, frame.Canvas, unit);
                if (!drawn)
                {
                    missingCount++;
                    if (missingLink == null)
                        missingLink = GetLinkPath(frame.Canvas);
                }

                lines.Add(group.Name + "  " + (group.CurrentIndex + 1) + " / " + group.Frames.Count
                    + "  " + Math.Max(MinimumFrameDelay, frame.Delay) + "ms"
                    + (drawn ? "" : "  ← 無圖"));
            }

            lines.Add(characterFrames.Count > 0
                ? "角色 " + characterFrames[characterFrameIndex].Label
                  + "  " + (characterFrameIndex + 1) + " / " + characterFrames.Count
                : "（使用預設角色圖）");

            // The frames exist but their pixels live elsewhere. Say so explicitly - a blank
            // grid with no explanation looks like the preview is broken.
            if (missingCount > 0)
            {
                lines.Add("");
                lines.Add("這格沒有圖片資料，需要另外載入存放圖片的檔案");
                if (missingLink != null)
                    lines.Add(missingLink);
            }

            InfoText = string.Join("\n", lines);
        }

        private void DrawBackdrop(Canvas canvas, double unit)
        {
            Color background = WhiteBackground ? Color.FromRgb(248, 250, 252) : Color.FromRgb(24, 24, 24);
            Color minor = WhiteBackground ? Color.FromRgb(226, 232, 240) : Color.FromRgb(43, 43, 43);
            Color major = WhiteBackground ? Color.FromRgb(203, 213, 225) : Color.FromRgb(58, 58, 58);
            Color axis = WhiteBackground ? Color.FromRgb(100, 116, 139) : Color.FromRgb(90, 90, 90);
            Color label = WhiteBackground ? Color.FromRgb(71, 85, 105) : Color.FromRgb(150, 150, 150);

            canvas.Background = new SolidColorBrush(background);
            PreviewCanvas.DrawGrid(canvas, unit, minor, major, axis, label);
        }

        private void DrawCharacter(Canvas canvas, double unit)
        {
            if (characterFrames.Count > 0)
            {
                DrawCharacterFrame(canvas, characterFrames[characterFrameIndex], unit);
                return;
            }

            if (fallbackCharacter == null)
                return;

            double width = Math.Max(32.0, fallbackCharacter.PixelWidth * 1.25 * Zoom);
            double height = Math.Max(32.0, fallbackCharacter.PixelHeight * 1.25 * Zoom);
            Image image = new Image
            {
                Source = fallbackCharacter,
                Stretch = Stretch.Uniform,
                Width = width,
                Height = height,
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(image, PreviewCanvas.Width / 2.0 - width / 2.0);
            Canvas.SetTop(image, PreviewCanvas.Height / 2.0 - height);
            canvas.Children.Add(image);
        }

        private void DrawCharacterFrame(Canvas canvas, CharacterFrame frame, double unit)
        {
            if (frame == null || frame.Parts == null)
                return;

            foreach (CharacterPart part in frame.Parts)
            {
                if (part == null || part.Canvas == null)
                    continue;
                try
                {
                    System.Drawing.Bitmap partSource = CanvasResolver.GetBitmap(part.Canvas, fileManager);
                    if (partSource == null)
                        continue;
                    DrawBitmap(canvas, partSource.ToWpfBitmap(), part.Origin, unit, frame.Move);
                }
                catch
                {
                    // A single unreadable part should not abort the whole frame.
                }
            }

            DrawCharacterHead(canvas, frame, unit);
        }

        /// <summary>
        /// 00002000.img holds only the body; the head comes from the shipped 333.png and is
        /// pinned to the body's "neck" attachment point.
        /// </summary>
        private void DrawCharacterHead(Canvas canvas, CharacterFrame frame, double unit)
        {
            if (characterHead == null || frame == null || frame.Parts == null)
                return;

            CharacterPart body =
                frame.Parts.FirstOrDefault(p => p != null && p.Canvas != null
                    && string.Equals(p.Canvas.Name, "body", StringComparison.OrdinalIgnoreCase))
                ?? frame.Parts.FirstOrDefault();
            if (body == null || body.Canvas == null)
                return;

            Point? neck = WzNav.GetMapPoint(body.Canvas, "neck");

            double width = characterHead.PixelWidth * unit;
            double height = characterHead.PixelHeight * unit;
            Image image = new Image
            {
                Source = characterHead,
                Width = width,
                Height = height,
                Stretch = Stretch.Fill,
                SnapsToDevicePixels = true
            };

            double x = PreviewCanvas.Width / 2.0 + frame.Move.X * unit;
            double y = PreviewCanvas.Height / 2.0 + frame.Move.Y * unit;
            if (neck.HasValue)
            {
                x += neck.Value.X * unit;
                y += neck.Value.Y * unit;
            }
            else
            {
                x -= 4.0 * unit;
                y -= body.Origin.Y * unit;
            }

            Canvas.SetLeft(image, x - width / 2.0);
            Canvas.SetTop(image, y - height + 4.0 * unit);
            canvas.Children.Add(image);
        }

        /// <summary>
        /// Draws one effect frame. Returns false when the frame carries no usable pixels -
        /// typically an _outlink pointing at a _Canvas image that is not loaded (or not present
        /// in the file at all), which must be reported rather than silently drawing nothing.
        /// </summary>
        private bool DrawEffectFrame(Canvas canvas, WzCanvasProperty effectCanvas, double unit)
        {
            try
            {
                System.Drawing.Bitmap source = CanvasResolver.GetBitmap(effectCanvas, fileManager);
                if (source == null || source.Width <= 1 || source.Height <= 1)
                    return false;

                BitmapSource bitmap = source.ToWpfBitmap();
                DrawBitmap(canvas, bitmap, GetOrigin(effectCanvas, bitmap), unit, default(Point));
                return true;
            }
            catch
            {
                // Unreadable frame - leave the backdrop as-is rather than tearing down playback.
                return false;
            }
        }

        /// <summary>The _outlink / _inlink a frame points at, for the "missing art" message.</summary>
        private static string GetLinkPath(WzCanvasProperty canvas)
        {
            string outlink = CanvasResolver.GetOutlink(canvas);
            if (outlink != null)
                return "_outlink " + outlink;

            WzStringProperty inlink = canvas["_inlink"] as WzStringProperty;
            if (inlink != null && !string.IsNullOrEmpty(inlink.Value))
                return "_inlink " + inlink.Value;

            return null;
        }

        private static Point GetOrigin(WzCanvasProperty canvas, BitmapSource bitmap)
        {
            WzVectorProperty origin = canvas["origin"] as WzVectorProperty;
            if (origin != null)
                return new Point(origin.X.Value, origin.Y.Value);
            return new Point(bitmap.PixelWidth / 2.0, bitmap.PixelHeight / 2.0);
        }

        private static void DrawBitmap(Canvas canvas, BitmapSource bitmap, Point origin, double unit, Point move)
        {
            Image image = new Image
            {
                Source = bitmap,
                Width = bitmap.PixelWidth * unit,
                Height = bitmap.PixelHeight * unit,
                Stretch = Stretch.Fill,
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(image, PreviewCanvas.Width / 2.0 + move.X * unit - origin.X * unit);
            Canvas.SetTop(image, PreviewCanvas.Height / 2.0 + move.Y * unit - origin.Y * unit);
            canvas.Children.Add(image);
        }
    }
}
