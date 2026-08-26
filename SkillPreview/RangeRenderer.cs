using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace SkillPreview
{
    /// <summary>
    /// Where a skill's hit box sits relative to the character, per level.
    /// </summary>
    internal sealed class RangeBounds
    {
        internal int LtX;
        internal int LtY;
        internal int RbX;
        internal int RbY;
        internal string Source;

        internal RangeBounds(int ltX, int ltY, int rbX, int rbY, string source)
        {
            LtX = ltX;
            LtY = ltY;
            RbX = rbX;
            RbY = rbY;
            Source = source;
        }
    }

    internal sealed class RangeRenderer
    {
        private static readonly Color MinorGrid = Color.FromRgb(38, 38, 38);
        private static readonly Color MajorGrid = Color.FromRgb(58, 58, 58);
        private static readonly Color AxisGrid = Color.FromRgb(110, 110, 110);
        private static readonly Color LabelColor = Color.FromRgb(150, 150, 150);

        internal SkillContext Skill;
        internal string SelectedSource;
        internal double Zoom = 1.0;
        internal bool ShowCharacter = true;
        internal bool MirrorRange;

        private BitmapSource characterImage;

        internal string InfoText { get; private set; }

        internal RangeRenderer()
        {
            characterImage = PreviewAssets.Load("111.png");
        }

        internal void Draw(Canvas canvas)
        {
            canvas.Children.Clear();
            canvas.Background = new SolidColorBrush(Color.FromRgb(24, 24, 24));

            double unit = PreviewCanvas.BaseUnit * Zoom;
            PreviewCanvas.DrawGrid(canvas, unit, MinorGrid, MajorGrid, AxisGrid, LabelColor);

            RangeBounds bounds;
            if (!string.IsNullOrEmpty(SelectedSource) && TryGetRange(SelectedSource, out bounds))
            {
                DrawRangeRectangle(canvas, bounds, unit);
                DrawCharacter(canvas);
                InfoText = BuildRangeInfo(SelectedSource, bounds);
            }
            else
            {
                DrawCharacter(canvas);
                InfoText = string.IsNullOrEmpty(SelectedSource)
                    ? "這個技能沒有範圍資料\n（找不到 level / common）"
                    : SelectedSource + "\n找不到 lt / rb";
            }
        }

        private void DrawRangeRectangle(Canvas canvas, RangeBounds bounds, double unit)
        {
            int ltX = bounds.LtX;
            int rbX = bounds.RbX;
            if (MirrorRange)
            {
                // A skill's stored range is for the character facing one way; mirroring
                // reflects it across the character so the other facing can be checked.
                int original = ltX;
                ltX = -rbX;
                rbX = -original;
            }

            double left = PreviewCanvas.ToScreenX(Math.Min(ltX, rbX), unit);
            double top = PreviewCanvas.ToScreenY(Math.Min(bounds.LtY, bounds.RbY), unit);
            double right = PreviewCanvas.ToScreenX(Math.Max(ltX, rbX), unit);
            double bottom = PreviewCanvas.ToScreenY(Math.Max(bounds.LtY, bounds.RbY), unit);

            Rectangle rect = new Rectangle
            {
                Width = Math.Max(1.0, right - left),
                Height = Math.Max(1.0, bottom - top),
                Fill = new SolidColorBrush(Color.FromArgb(45, 57, 255, 20)),
                Stroke = new SolidColorBrush(Color.FromRgb(56, 230, 70)),
                StrokeThickness = 2.0
            };
            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, top);
            canvas.Children.Add(rect);
        }

        private void DrawCharacter(Canvas canvas)
        {
            if (!ShowCharacter || characterImage == null)
                return;

            double width = Math.Max(32.0, characterImage.PixelWidth * 1.25 * Zoom);
            double height = Math.Max(32.0, characterImage.PixelHeight * 1.25 * Zoom);
            Image image = new Image
            {
                Source = characterImage,
                Width = width,
                Height = height,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(image, PreviewCanvas.Width / 2.0 - width / 2.0);
            Canvas.SetTop(image, PreviewCanvas.Height / 2.0 - height + 6.0);
            canvas.Children.Add(image);
        }

        /// <summary>
        /// Most skills state their hit box directly as lt/rb vectors. Projectile skills
        /// instead carry a scalar "range", in which case the vertical extent has to be
        /// inferred - preferably from the ball sprite's own bounds, otherwise estimated.
        /// </summary>
        internal bool TryGetRange(string sourceLabel, out RangeBounds bounds)
        {
            bounds = null;

            IPropertyContainer block = null;
            if (Skill != null)
            {
                foreach (SkillRangeSource source in Skill.RangeSources)
                {
                    if (string.Equals(source.Label, sourceLabel, StringComparison.OrdinalIgnoreCase))
                    {
                        block = source.Container;
                        break;
                    }
                }
            }
            if (block == null)
                return false;

            WzVectorProperty lt = WzNav.FindPropertyByName(block, "lt") as WzVectorProperty;
            WzVectorProperty rb = WzNav.FindPropertyByName(block, "rb") as WzVectorProperty;
            if (lt != null && rb != null)
            {
                bounds = new RangeBounds(lt.X.Value, lt.Y.Value, rb.X.Value, rb.Y.Value, "lt / rb");
                return true;
            }

            // Projectile skills state a scalar reach instead. Note that in the newer layout
            // "x"/"y" are level formulas rather than numbers, so the estimate below simply
            // falls through to its own default when they cannot be parsed.
            int range;
            if (WzNav.TryGetInt(WzNav.FindPropertyByName(block, "range"), out range) && range > 0)
            {
                if (TryGetBallRangeBounds(range, out bounds))
                    return true;

                int halfHeight = GetRangeOnlyHalfHeight(block, range);
                bounds = new RangeBounds(0, -halfHeight, range, halfHeight,
                    "range " + range + "（推估的彈道高度）");
                return true;
            }

            return false;
        }

        private bool TryGetBallRangeBounds(int range, out RangeBounds bounds)
        {
            bounds = null;

            // "ball" hangs off the skill node itself, which the context already knows - the
            // parent chain differs between the level and common layouts, so don't walk it.
            IPropertyContainer skillNode = Skill == null ? null : WzNav.Deref(Skill.SkillNode) as IPropertyContainer;
            if (skillNode == null)
                return false;

            IPropertyContainer ball = WzNav.FindPropertyByName(skillNode, "ball", "ball0") as IPropertyContainer;
            if (ball == null)
                return false;

            bool any = false;
            int minX = 0, minY = 0, maxX = 0, maxY = 0;

            foreach (WzCanvasProperty frame in ball.WzProperties.OfType<WzCanvasProperty>())
            {
                WzVectorProperty origin = WzNav.FindPropertyByName(frame, "origin") as WzVectorProperty;
                if (origin == null || frame.PngProperty == null)
                    continue;

                int left = -origin.X.Value;
                int top = -origin.Y.Value;
                int right = frame.PngProperty.Width - origin.X.Value;
                int bottom = frame.PngProperty.Height - origin.Y.Value;

                if (!any)
                {
                    minX = left; minY = top; maxX = right; maxY = bottom;
                    any = true;
                }
                else
                {
                    minX = Math.Min(minX, left);
                    minY = Math.Min(minY, top);
                    maxX = Math.Max(maxX, right);
                    maxY = Math.Max(maxY, bottom);
                }
            }

            if (!any)
                return false;

            bounds = new RangeBounds(0, minY, range, maxY,
                "range " + range + " + ball 高度 (" + minX + "," + minY + ")~(" + maxX + "," + maxY + ")");
            return true;
        }

        private static int GetRangeOnlyHalfHeight(IPropertyContainer level, int range)
        {
            int y;
            if (WzNav.TryGetInt(WzNav.FindPropertyByName(level, "y"), out y) && y != 0)
                return Math.Max(20, Math.Min(100, Math.Abs(y)));
            return Math.Max(30, Math.Min(70, range / 8));
        }

        private static string BuildRangeInfo(string sourceLabel, RangeBounds bounds)
        {
            int width = Math.Abs(bounds.RbX - bounds.LtX);
            int height = Math.Abs(bounds.RbY - bounds.LtY);
            // A purely numeric label is a level number; anything else is a named block.
            string heading = WzNav.ParseFrameIndex(sourceLabel) != int.MaxValue
                ? "lv" + sourceLabel
                : sourceLabel;
            return heading + "\n"
                + "範圍 " + width + " x " + height + "\n"
                + "lt (" + bounds.LtX + ", " + bounds.LtY + ") / rb (" + bounds.RbX + ", " + bounds.RbY + ")\n"
                + bounds.Source;
        }
    }
}
