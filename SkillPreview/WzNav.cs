using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace SkillPreview
{
    /// <summary>
    /// Navigation helpers shared by the range and effect views.
    ///
    /// Everything here treats a UOL as transparent (it follows LinkValue) and skips any
    /// node literally named "bobo", which some WZ edits use as a scratch/backup container
    /// that would otherwise get picked up as real animation frames.
    /// </summary>
    internal static class WzNav
    {
        private const string IgnoredNodeName = "bobo";

        internal static bool IsIgnored(WzObject obj)
        {
            return obj == null || string.Equals(obj.Name, IgnoredNodeName, StringComparison.OrdinalIgnoreCase);
        }

        internal static WzObject Deref(WzObject obj)
        {
            WzUOLProperty uol = obj as WzUOLProperty;
            if (uol != null && uol.LinkValue != null)
                return Deref(uol.LinkValue);
            return obj;
        }

        internal static WzObject GetChild(WzObject root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name))
                return null;

            root = Deref(root);

            WzImage img = root as WzImage;
            if (img != null)
                return img[name];

            WzImageProperty prop = root as WzImageProperty;
            if (prop != null)
                return prop[name];

            return null;
        }

        internal static IEnumerable<WzObject> GetChildren(WzObject root)
        {
            if (IsIgnored(root))
                yield break;

            root = Deref(root);

            IEnumerable<WzImageProperty> children = null;
            WzImage img = root as WzImage;
            if (img != null)
                children = img.WzProperties;
            else
            {
                IPropertyContainer container = root as IPropertyContainer;
                if (container != null)
                    children = container.WzProperties;
            }

            if (children == null)
                yield break;

            foreach (WzImageProperty child in children)
            {
                if (!IsIgnored(child))
                    yield return child;
            }
        }

        internal static IEnumerable<WzCanvasProperty> EnumerateCanvases(WzObject root)
        {
            if (IsIgnored(root))
                yield break;

            root = Deref(root);

            WzCanvasProperty canvas = root as WzCanvasProperty;
            if (canvas != null)
            {
                yield return canvas;
                yield break;
            }

            foreach (WzObject child in GetChildren(root))
            {
                foreach (WzCanvasProperty found in EnumerateCanvases(child))
                    yield return found;
            }
        }

        internal static IEnumerable<string> EnumerateStringValues(WzObject root)
        {
            if (IsIgnored(root))
                yield break;

            root = Deref(root);

            WzStringProperty str = root as WzStringProperty;
            if (str != null)
            {
                yield return str.Value;
                yield break;
            }

            foreach (WzObject child in OrderByFrameIndex(GetChildren(root)))
            {
                foreach (string found in EnumerateStringValues(child))
                    yield return found;
            }
        }

        internal static string GetFirstStringValue(WzObject root)
        {
            return EnumerateStringValues(root).FirstOrDefault();
        }

        /// <summary>
        /// WZ frame nodes are named "0", "1", "2"... so they must be ordered numerically;
        /// plain string ordering would put "10" before "2". Non-numeric names sort last and
        /// fall back to a case-insensitive name comparison.
        /// </summary>
        internal static IEnumerable<WzObject> OrderByFrameIndex(IEnumerable<WzObject> nodes)
        {
            return nodes
                .OrderBy(n => ParseFrameIndex(n.Name))
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
        }

        internal static int ParseFrameIndex(string name)
        {
            int result;
            if (name != null && int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                return result;
            return int.MaxValue;
        }

        internal static Point GetOrigin(WzCanvasProperty canvas)
        {
            WzVectorProperty origin = canvas == null ? null : canvas["origin"] as WzVectorProperty;
            if (origin != null)
                return new Point(origin.X.Value, origin.Y.Value);
            return default(Point);
        }

        internal static Point? GetMapPoint(WzCanvasProperty canvas, string name)
        {
            IPropertyContainer map = canvas == null ? null : canvas["map"] as IPropertyContainer;
            if (map != null)
            {
                WzVectorProperty vector = map[name] as WzVectorProperty;
                if (vector != null)
                    return new Point(vector.X.Value, vector.Y.Value);
            }
            return null;
        }

        internal static Point GetVector(WzObject parent, string name)
        {
            WzVectorProperty vector = GetChild(parent, name) as WzVectorProperty;
            if (vector != null)
                return new Point(vector.X.Value, vector.Y.Value);
            return default(Point);
        }

        internal static int GetCanvasDelay(WzCanvasProperty canvas)
        {
            WzImageProperty delay = canvas == null ? null : canvas["delay"];
            if (delay == null)
                return 100;
            try
            {
                return delay.GetInt();
            }
            catch
            {
                return 100;
            }
        }

        internal static bool TryGetInt(WzObject property, out int value)
        {
            value = 0;

            WzIntProperty intProp = property as WzIntProperty;
            if (intProp != null) { value = intProp.Value; return true; }

            WzShortProperty shortProp = property as WzShortProperty;
            if (shortProp != null) { value = shortProp.Value; return true; }

            WzLongProperty longProp = property as WzLongProperty;
            if (longProp != null)
            {
                value = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, longProp.Value));
                return true;
            }

            WzStringProperty strProp = property as WzStringProperty;
            if (strProp != null)
                return int.TryParse(strProp.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

            return false;
        }

        internal static WzImageProperty FindPropertyByName(IPropertyContainer container, params string[] names)
        {
            if (container == null)
                return null;
            foreach (WzImageProperty prop in container.WzProperties)
            {
                foreach (string name in names)
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                        return prop;
                }
            }
            return null;
        }
    }
}
