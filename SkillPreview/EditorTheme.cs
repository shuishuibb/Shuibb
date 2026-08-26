using System;
using System.Windows.Media;

namespace SkillPreview
{
    /// <summary>
    /// One palette, two presets. Every colour the inline panels use comes from here so a theme
    /// switch is a matter of swapping the instance and repainting - no restart, and no colour
    /// literals scattered through the panels to fall out of step.
    /// </summary>
    public sealed class EditorTheme
    {
        public bool IsDark { get; private set; }

        public Color PanelBackground { get; private set; }
        public Color CardBackground { get; private set; }
        public Color CardBorder { get; private set; }
        public Color FieldBackground { get; private set; }
        public Color FieldBorder { get; private set; }
        public Color EmptyFieldBorder { get; private set; }
        public Color TextColor { get; private set; }
        public Color StrongTextColor { get; private set; }
        public Color MutedTextColor { get; private set; }
        public Color AccentBackground { get; private set; }
        public Color AccentBorder { get; private set; }
        public Color RowSeparator { get; private set; }
        public Color WarningColor { get; private set; }

        public static EditorTheme Dark()
        {
            return new EditorTheme
            {
                IsDark = true,
                PanelBackground = Color.FromRgb(26, 26, 28),
                CardBackground = Color.FromRgb(33, 33, 36),
                CardBorder = Color.FromRgb(56, 56, 62),
                FieldBackground = Color.FromRgb(43, 43, 47),
                FieldBorder = Color.FromRgb(67, 67, 74),
                EmptyFieldBorder = Color.FromRgb(52, 52, 58),
                TextColor = Color.FromRgb(233, 233, 236),
                StrongTextColor = Colors.White,
                MutedTextColor = Color.FromRgb(151, 151, 160),
                AccentBackground = Color.FromRgb(37, 99, 235),
                AccentBorder = Color.FromRgb(59, 130, 246),
                RowSeparator = Color.FromRgb(44, 44, 49),
                WarningColor = Color.FromRgb(248, 180, 90)
            };
        }

        public static EditorTheme Light()
        {
            return new EditorTheme
            {
                IsDark = false,
                PanelBackground = Color.FromRgb(246, 247, 249),
                CardBackground = Colors.White,
                CardBorder = Color.FromRgb(220, 223, 228),
                FieldBackground = Colors.White,
                FieldBorder = Color.FromRgb(203, 208, 215),
                EmptyFieldBorder = Color.FromRgb(228, 231, 236),
                TextColor = Color.FromRgb(28, 30, 34),
                StrongTextColor = Color.FromRgb(10, 12, 16),
                MutedTextColor = Color.FromRgb(108, 114, 124),
                AccentBackground = Color.FromRgb(37, 99, 235),
                AccentBorder = Color.FromRgb(29, 78, 216),
                RowSeparator = Color.FromRgb(235, 237, 241),
                WarningColor = Color.FromRgb(176, 106, 8)
            };
        }

        public SolidColorBrush Panel => new SolidColorBrush(PanelBackground);
        public SolidColorBrush Card => new SolidColorBrush(CardBackground);
        public SolidColorBrush Border => new SolidColorBrush(CardBorder);
        public SolidColorBrush Field => new SolidColorBrush(FieldBackground);
        public SolidColorBrush FieldEdge => new SolidColorBrush(FieldBorder);
        public SolidColorBrush EmptyEdge => new SolidColorBrush(EmptyFieldBorder);
        public SolidColorBrush Text => new SolidColorBrush(TextColor);
        public SolidColorBrush Strong => new SolidColorBrush(StrongTextColor);
        public SolidColorBrush Muted => new SolidColorBrush(MutedTextColor);
        public SolidColorBrush Accent => new SolidColorBrush(AccentBackground);
        public SolidColorBrush AccentEdge => new SolidColorBrush(AccentBorder);
        public SolidColorBrush Separator => new SolidColorBrush(RowSeparator);
        public SolidColorBrush Warning => new SolidColorBrush(WarningColor);

        /// <summary>
        /// The theme every inline panel reads, plus a change notification so a switch repaints
        /// everything that is already on screen instead of only the panel that was clicked.
        /// </summary>
        public static EditorTheme Current { get; private set; } = Light();

        public static event EventHandler CurrentChanged;

        public static void SetDark(bool dark)
        {
            if (Current != null && Current.IsDark == dark)
                return;
            Current = dark ? Dark() : Light();
            CurrentChanged?.Invoke(null, EventArgs.Empty);
        }

        public static void Toggle()
        {
            SetDark(!Current.IsDark);
        }
    }
}
