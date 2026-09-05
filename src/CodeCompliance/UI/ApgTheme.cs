using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodeCompliance.UI
{
    /// <summary>
    /// Shared look and feel for every APG Revit Plugins dialog. Windows call
    /// <see cref="Apply"/> once, add <see cref="CreateHeader"/> as their first row and
    /// build the rest from the brushes/helpers here, so all plugins in the suite look
    /// the same. Code-only WPF (no XAML) so it compiles identically on every target.
    /// </summary>
    internal static class ApgTheme
    {
        // ── Brand palette ───────────────────────────────────────────────────────
        public static readonly Color BlueColor = Color.FromRgb(0x27, 0x26, 0xA9);
        public static readonly Color NavyColor = Color.FromRgb(0x17, 0x16, 0x66);

        public static readonly Brush Blue = Freeze(new SolidColorBrush(BlueColor));
        public static readonly Brush Navy = Freeze(new SolidColorBrush(NavyColor));
        public static readonly Brush WindowBackground = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xFA)));
        public static readonly Brush CardBackground = Brushes.White;
        public static readonly Brush CardBorder = Freeze(new SolidColorBrush(Color.FromRgb(0xDD, 0xDF, 0xE8)));
        public static readonly Brush Text = Freeze(new SolidColorBrush(Color.FromRgb(0x23, 0x28, 0x33)));
        public static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0x6D, 0x74, 0x85)));
        public static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x84, 0x49)));
        public static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)));
        public static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0x24, 0x71, 0xA3)));

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        /// <summary>Base chrome every APG dialog shares: background, font, rendering.</summary>
        public static void Apply(Window window)
        {
            window.Background = WindowBackground;
            window.FontFamily = new FontFamily("Segoe UI");
            window.FontSize = 12.0;
            window.Foreground = Text;
            window.UseLayoutRounding = true;
            window.SnapsToDevicePixels = true;
        }

        /// <summary>
        /// Blue brand band shown at the top of each dialog: plugin title + subtitle on
        /// the left, the white APG wordmark on the right.
        /// </summary>
        public static UIElement CreateHeader(string title, string subtitle)
        {
            var band = new Border
            {
                Background = Freeze(new LinearGradientBrush(BlueColor, NavyColor, 90.0)),
                Padding = new Thickness(16, 12, 16, 12)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = Brushes.White,
                FontSize = 16.0,
                FontWeight = FontWeights.SemiBold
            });
            titles.Children.Add(new TextBlock
            {
                Text = subtitle,
                Foreground = Brushes.White,
                Opacity = 0.75,
                FontSize = 11.5,
                Margin = new Thickness(0, 2, 0, 0)
            });
            grid.Children.Add(titles);

            var wordmark = RibbonIcons.Get("ApgWordmarkWhite");
            if (wordmark != null)
            {
                var logo = new Image
                {
                    Source = wordmark,
                    Height = 30,
                    Margin = new Thickness(16, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(logo, 1);
                grid.Children.Add(logo);
            }

            band.Child = grid;
            return band;
        }

        /// <summary>White card with a light border; groups a section of controls.</summary>
        public static Border Card(UIElement content)
        {
            return new Border
            {
                Background = CardBackground,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 10),
                Margin = new Thickness(0, 0, 0, 10),
                Child = content
            };
        }

        public static TextBlock SectionHeader(string text)
        {
            return new TextBlock
            {
                Text = text.ToUpperInvariant(),
                FontWeight = FontWeights.Bold,
                FontSize = 11.0,
                Foreground = Navy,
                Margin = new Thickness(0, 10, 0, 4)
            };
        }

        public static TextBlock FieldLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = Muted,
                FontSize = 11.0,
                Margin = new Thickness(0, 2, 0, 0)
            };
        }

        /// <summary>Solid APG-blue call-to-action button.</summary>
        public static Button PrimaryButton(string caption)
        {
            return StyleButton(new Button { Content = caption },
                BlueColor, Color.FromRgb(0x32, 0x31, 0xC4), NavyColor, Colors.White);
        }

        /// <summary>Neutral secondary button (Cancel, etc.).</summary>
        public static Button SecondaryButton(string caption)
        {
            return StyleButton(new Button { Content = caption },
                Color.FromRgb(0xE8, 0xE9, 0xF0), Color.FromRgb(0xDD, 0xDF, 0xE8),
                Color.FromRgb(0xCF, 0xD2, 0xDE), Color.FromRgb(0x23, 0x28, 0x33));
        }

        private static Button StyleButton(Button button, Color background, Color hover, Color pressed, Color foreground)
        {
            var border = new FrameworkElementFactory(typeof(Border), "border");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            border.AppendChild(content);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, Freeze(new SolidColorBrush(hover)), "border"));
            template.Triggers.Add(hoverTrigger);

            var pressedTrigger = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
            pressedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, Freeze(new SolidColorBrush(pressed)), "border"));
            template.Triggers.Add(pressedTrigger);

            var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "border"));
            template.Triggers.Add(disabledTrigger);

            button.Background = Freeze(new SolidColorBrush(background));
            button.Foreground = Freeze(new SolidColorBrush(foreground));
            button.FontWeight = FontWeights.SemiBold;
            button.Padding = new Thickness(16, 7, 16, 7);
            button.MinWidth = 96;
            button.Cursor = Cursors.Hand;
            button.Template = template;
            return button;
        }
    }
}
