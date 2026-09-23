using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VoiceHubComponent.Models;

namespace VoiceHubComponent.Controls
{
    /// <summary>
    /// 单行歌词控件。整行统一前景色显示，可附加背景声行与翻译/罗马音后缀。
    /// </summary>
    public sealed class LyricsLineText : Control
    {
        private LyricLineItem? _line;
        private LyricLineItem? _backgroundLine;
        private double _lineFontSize = 15;
        private IBrush _foreground = Brushes.White;
        private TextAlignment _textAlignment = TextAlignment.Center;
        private bool _showTranslation = true;
        private bool _showRomanization;
        private FormattedText? _mainText;
        private FormattedText? _backgroundPrefixText;
        private FormattedText? _backgroundText;
        private FormattedText? _suffixText;

        public LyricLineItem? Line
        {
            get => _line;
            set
            {
                if (ReferenceEquals(_line, value)) return;
                _line = value;
                InvalidateMetrics();
            }
        }

        public LyricLineItem? BackgroundLine
        {
            get => _backgroundLine;
            set
            {
                if (ReferenceEquals(_backgroundLine, value)) return;
                _backgroundLine = value;
                InvalidateMetrics();
            }
        }

        public double LineFontSize
        {
            get => _lineFontSize;
            set
            {
                if (Math.Abs(_lineFontSize - value) < 0.01) return;
                _lineFontSize = value;
                InvalidateMetrics();
            }
        }

        public IBrush Foreground
        {
            get => _foreground;
            set
            {
                if (ReferenceEquals(_foreground, value)) return;
                _foreground = value;
                InvalidateMetrics();
            }
        }

        public TextAlignment TextAlignment
        {
            get => _textAlignment;
            set
            {
                if (_textAlignment == value) return;
                _textAlignment = value;
                InvalidateVisual();
            }
        }

        public bool ShowTranslation
        {
            get => _showTranslation;
            set
            {
                if (_showTranslation == value) return;
                _showTranslation = value;
                InvalidateMetrics();
            }
        }

        public bool ShowRomanization
        {
            get => _showRomanization;
            set
            {
                if (_showRomanization == value) return;
                _showRomanization = value;
                InvalidateMetrics();
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            EnsureMetrics();
            var width = (_mainText?.WidthIncludingTrailingWhitespace ?? 0) +
                        (_backgroundPrefixText?.WidthIncludingTrailingWhitespace ?? 0) +
                        (_backgroundText?.WidthIncludingTrailingWhitespace ?? 0) +
                        (_suffixText?.WidthIncludingTrailingWhitespace ?? 0);
            var height = new[]
            {
                _mainText?.Height ?? 0,
                _backgroundText?.Height ?? 0,
                _suffixText?.Height ?? 0
            }.Max();
            return new Size(Math.Min(width, availableSize.Width), height);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            EnsureMetrics();
            if (_mainText == null) return;

            var mainWidth = _mainText.WidthIncludingTrailingWhitespace;
            var prefixWidth = _backgroundPrefixText?.WidthIncludingTrailingWhitespace ?? 0;
            var backgroundWidth = _backgroundText?.WidthIncludingTrailingWhitespace ?? 0;
            var originX = GetOrigin(mainWidth + prefixWidth + backgroundWidth +
                                    (_suffixText?.WidthIncludingTrailingWhitespace ?? 0));

            context.DrawText(_mainText, new Point(originX, 0));

            if (_backgroundPrefixText != null)
            {
                context.DrawText(_backgroundPrefixText, new Point(originX + mainWidth, 0));
            }

            if (_backgroundText != null)
            {
                context.DrawText(_backgroundText, new Point(originX + mainWidth + prefixWidth, 0));
            }

            if (_suffixText != null)
            {
                context.DrawText(
                    _suffixText,
                    new Point(originX + mainWidth + prefixWidth + backgroundWidth, 0));
            }
        }

        private void EnsureMetrics()
        {
            if (_mainText != null) return;
            var line = Line;
            _mainText = CreateFormatted(line?.Text ?? string.Empty, Foreground, LineFontSize);

            var background = BackgroundLine;
            if (background != null && !string.IsNullOrWhiteSpace(background.Text))
            {
                var backgroundFontSize = Math.Max(9, LineFontSize * 0.76);
                _backgroundPrefixText = CreateFormatted(
                    " / ",
                    CreateOpacityBrush(Foreground, 0.42),
                    backgroundFontSize);
                _backgroundText = CreateFormatted(
                    background.Text,
                    CreateOpacityBrush(Foreground, 0.76),
                    backgroundFontSize);
            }

            var suffixParts = new List<string>();
            if (ShowTranslation && !string.IsNullOrWhiteSpace(line?.Translation))
                suffixParts.Add(line.Translation.Trim());
            if (ShowRomanization && !string.IsNullOrWhiteSpace(line?.Romanization))
                suffixParts.Add(line.Romanization.Trim());
            _suffixText = suffixParts.Count == 0
                ? null
                : CreateFormatted(
                    $" / {string.Join(" / ", suffixParts)}",
                    CreateOpacityBrush(Foreground, 0.52),
                    LineFontSize);
        }

        private double GetOrigin(double width) => TextAlignment switch
        {
            TextAlignment.Left or TextAlignment.Start => 0,
            TextAlignment.Right or TextAlignment.End => Math.Max(0, Bounds.Width - width),
            _ => Math.Max(0, (Bounds.Width - width) / 2)
        };

        private static FormattedText CreateFormatted(string text, IBrush brush, double fontSize) => new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default),
            fontSize,
            brush);

        private static IBrush CreateOpacityBrush(IBrush foreground, double opacity) =>
            foreground is ISolidColorBrush solid
                ? new SolidColorBrush(solid.Color, opacity)
                : foreground;

        private void InvalidateMetrics()
        {
            _mainText = null;
            _backgroundPrefixText = null;
            _backgroundText = null;
            _suffixText = null;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }
}
