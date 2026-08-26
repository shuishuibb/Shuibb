using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SkillPreview
{
    /// <summary>
    /// The fixed 760x420 drawing surface both views share.
    ///
    /// WZ coordinates are centred on the character's feet, so world (0,0) maps to the
    /// middle of the canvas and one WZ unit is <see cref="BaseUnit"/> screen units before
    /// the user's zoom factor is applied. The canvas itself never resizes - the window
    /// puts it in a Viewbox - so these constants stay fixed.
    /// </summary>
    internal static class PreviewCanvas
    {
        internal const double Width = 760.0;
        internal const double Height = 420.0;
        internal const double BaseUnit = 1.35;

        private const double CentreX = Width / 2.0;
        private const double CentreY = Height / 2.0;

        internal static double ToScreenX(double worldX, double unit)
        {
            return CentreX + worldX * unit;
        }

        internal static double ToScreenY(double worldY, double unit)
        {
            return CentreY + worldY * unit;
        }

        internal static void AddLine(Canvas canvas, double x1, double y1, double x2, double y2, Color color, double thickness)
        {
            canvas.Children.Add(new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = thickness
            });
        }

        internal static void AddAxisLabel(Canvas canvas, string text, int worldX, int worldY, double unit,
            double offsetX, double offsetY, Color color)
        {
            TextBlock label = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(color),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12.0
            };
            Canvas.SetLeft(label, ToScreenX(worldX, unit) + offsetX);
            Canvas.SetTop(label, ToScreenY(worldY, unit) + offsetY);
            canvas.Children.Add(label);
        }

        /// <summary>
        /// Grid every 10 WZ units, emphasised every 100, with the two axes brightest.
        /// </summary>
        internal static void DrawGrid(Canvas canvas, double unit, Color minor, Color major, Color axis, Color labelColor)
        {
            for (int x = -320; x <= 320; x += 10)
            {
                double screenX = ToScreenX(x, unit);
                Color color = x == 0 ? axis : (x % 100 == 0 ? major : minor);
                AddLine(canvas, screenX, 0.0, screenX, Height, color, x == 0 ? 1.2 : 0.55);
            }

            for (int y = -160; y <= 160; y += 10)
            {
                double screenY = ToScreenY(y, unit);
                Color color = y == 0 ? axis : (y % 100 == 0 ? major : minor);
                AddLine(canvas, 0.0, screenY, Width, screenY, color, y == 0 ? 1.2 : 0.55);
            }

            foreach (int x in new[] { -300, -200, -100, 100, 200, 300 })
                AddAxisLabel(canvas, x.ToString(), x, 0, unit, -4.0, 10.0, labelColor);

            foreach (int y in new[] { -100, 100 })
                AddAxisLabel(canvas, y.ToString(), 0, y, unit, 8.0, -8.0, labelColor);
        }
    }
}
