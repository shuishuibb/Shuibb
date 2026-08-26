using System;
using System.Collections.Generic;
using System.Linq;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace SkillPreview
{
    /// <summary>
    /// One block a hit range can be read from: either a single level under "level", or a
    /// whole-skill block such as "common" / "PVPcommon".
    /// </summary>
    internal sealed class SkillRangeSource
    {
        internal string Label;
        internal IPropertyContainer Container;

        internal SkillRangeSource(string label, IPropertyContainer container)
        {
            Label = label;
            Container = container;
        }
    }

    /// <summary>
    /// The skill the preview is showing.
    ///
    /// Two layouts exist in the wild and both are supported. Older data stores one node per
    /// level under "level"; newer data drops that entirely and keeps a single "common" block
    /// (plus sometimes "PVPcommon") whose stats are formulas of the level, with one lt/rb
    /// shared across all levels.
    /// </summary>
    internal sealed class SkillContext
    {
        internal WzObject SkillNode { get; private set; }

        internal string SkillId { get; private set; }

        /// <summary>Selectable range blocks, in display order. May be empty.</summary>
        internal List<SkillRangeSource> RangeSources { get; private set; }

        /// <summary>True when the skill carries any effect/affected animation container.</summary>
        internal bool HasEffects { get; private set; }

        private SkillContext()
        {
            RangeSources = new List<SkillRangeSource>();
        }

        /// <summary>
        /// Resolves the skill from the selected node, accepting only the selections a user
        /// makes when they mean "show me this skill": the skill node itself, one of its range
        /// blocks ("level", "common", "PVPcommon"), or a level inside "level".
        ///
        /// The search deliberately does NOT climb further. Selecting something deeper - an
        /// effect frame, say - means the user wants that node's own editor, and taking over
        /// the panel there would hide it.
        /// </summary>
        internal static SkillContext Resolve(WzObject selected)
        {
            if (selected == null)
                return null;

            SkillContext context = TryBuild(selected);
            if (context != null)
                return context;

            WzObject parent = selected.Parent;
            if (parent != null && IsRangeBlockName(selected.Name))
            {
                context = TryBuild(parent);
                if (context != null)
                    return context;
            }

            // A level inside the "level" container.
            if (parent != null && IsLevelContainerName(parent.Name) && parent.Parent != null)
                return TryBuild(parent.Parent);

            return null;
        }

        private static bool IsLevelContainerName(string name)
        {
            return string.Equals(name, "level", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>"level", "common", "PVPcommon", and any other *common block.</summary>
        private static bool IsRangeBlockName(string name)
        {
            if (name == null)
                return false;
            return IsLevelContainerName(name)
                || name.EndsWith("common", StringComparison.OrdinalIgnoreCase);
        }

        private static SkillContext TryBuild(WzObject skillNode)
        {
            if (skillNode == null)
                return null;

            SkillContext context = new SkillContext();
            context.SkillNode = skillNode;
            context.SkillId = skillNode.Name;

            foreach (WzObject child in WzNav.GetChildren(skillNode))
            {
                if (IsLevelContainerName(child.Name))
                {
                    // Old layout: one node per level.
                    IPropertyContainer levelRoot = WzNav.Deref(child) as IPropertyContainer;
                    if (levelRoot == null)
                        continue;
                    foreach (WzObject level in WzNav.OrderByFrameIndex(levelRoot.WzProperties.Cast<WzObject>()))
                    {
                        IPropertyContainer levelContainer = WzNav.Deref(level) as IPropertyContainer;
                        if (levelContainer != null)
                            context.RangeSources.Add(new SkillRangeSource(level.Name, levelContainer));
                    }
                }
                else if (IsRangeBlockName(child.Name))
                {
                    // New layout: one shared block for the whole skill.
                    IPropertyContainer block = WzNav.Deref(child) as IPropertyContainer;
                    if (block != null)
                        context.RangeSources.Add(new SkillRangeSource(child.Name, block));
                }
                else if (EffectRenderer.IsEffectContainerName(child.Name))
                {
                    context.HasEffects = true;
                }
            }

            // Worth previewing only if there is something to show.
            return (context.RangeSources.Count > 0 || context.HasEffects) ? context : null;
        }

        /// <summary>Summary line for the panel header.</summary>
        internal string BuildSummary()
        {
            List<string> parts = new List<string>();

            int maxLevel = GetMaxLevel();
            if (maxLevel > 0)
                parts.Add("最高 " + maxLevel + " 級");

            if (RangeSources.Count > 0)
                parts.Add(RangeSources.Count + " 組範圍資料");
            else
                parts.Add("無範圍資料");

            return string.Join("　", parts);
        }

        /// <summary>
        /// Level count, read either from a "maxLevel" in one of the blocks (new layout) or
        /// from the number of level nodes (old layout).
        /// </summary>
        private int GetMaxLevel()
        {
            foreach (SkillRangeSource source in RangeSources)
            {
                int maxLevel;
                if (WzNav.TryGetInt(WzNav.FindPropertyByName(source.Container, "maxLevel"), out maxLevel)
                    && maxLevel > 0)
                {
                    return maxLevel;
                }
            }

            int numeric = 0;
            foreach (SkillRangeSource source in RangeSources)
                if (WzNav.ParseFrameIndex(source.Label) != int.MaxValue)
                    numeric++;
            return numeric;
        }
    }
}
