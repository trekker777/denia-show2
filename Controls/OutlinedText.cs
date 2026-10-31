using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;
using System.Globalization;

namespace libvlc_media.Controls
{
    public sealed class OutlinedText : Control
    {
        public static readonly StyledProperty<string> TextProperty =
            AvaloniaProperty.Register<OutlinedText, string>(nameof(Text), string.Empty);

        public static readonly StyledProperty<double> FontSizeProperty =
            AvaloniaProperty.Register<OutlinedText, double>(nameof(FontSize), 96);

        public static readonly StyledProperty<FontFamily> FontFamilyProperty =
            AvaloniaProperty.Register<OutlinedText, FontFamily>(nameof(FontFamily), new FontFamily("Segoe UI, Inter, sans-serif"));

        public static readonly StyledProperty<FontWeight> FontWeightProperty =
            AvaloniaProperty.Register<OutlinedText, FontWeight>(nameof(FontWeight), FontWeight.Bold);

        public static readonly StyledProperty<IBrush> FillProperty =
            AvaloniaProperty.Register<OutlinedText, IBrush>(nameof(Fill), Brushes.White);

        public static readonly StyledProperty<IBrush> StrokeProperty =
            AvaloniaProperty.Register<OutlinedText, IBrush>(nameof(Stroke), Brushes.Black);

        static OutlinedText()
        {
            AffectsMeasure<OutlinedText>(TextProperty, FontSizeProperty, FontFamilyProperty, FontWeightProperty);
            AffectsRender<OutlinedText>(TextProperty, FontSizeProperty, FontFamilyProperty, FontWeightProperty, FillProperty, StrokeProperty);
        }

        public string Text
        {
            get => GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public double FontSize
        {
            get => GetValue(FontSizeProperty);
            set => SetValue(FontSizeProperty, value);
        }

        public FontFamily FontFamily
        {
            get => GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        public FontWeight FontWeight
        {
            get => GetValue(FontWeightProperty);
            set => SetValue(FontWeightProperty, value);
        }

        public IBrush Fill
        {
            get => GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        public IBrush Stroke
        {
            get => GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            var formatted = CreateFormatted();
            if (formatted is null)
                return;

            var stroke = StrokeThickness(FontSize);
            var geometry = formatted.BuildGeometry(new Point(stroke, stroke));
            if (geometry is null)
                return;

            var pen = new Pen(Stroke, stroke)
            {
                LineJoin = PenLineJoin.Round,
                LineCap = PenLineCap.Round
            };
            context.DrawGeometry(Fill, pen, geometry);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var formatted = CreateFormatted();
            if (formatted is null)
                return default;

            var stroke = StrokeThickness(FontSize);
            return new Size(
                Math.Ceiling(formatted.Width + stroke * 2),
                Math.Ceiling(formatted.Height + stroke * 2));
        }

        private FormattedText? CreateFormatted()
        {
            if (string.IsNullOrEmpty(Text) || FontSize <= 0)
                return null;

            return new FormattedText(
                Text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyle.Normal, FontWeight),
                FontSize,
                Fill);
        }

        private static double StrokeThickness(double fontSize) =>
            Math.Max(1.5, fontSize * 0.07);
    }
}
