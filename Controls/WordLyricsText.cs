using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VoiceHubComponent.Models;
using VoiceHubComponent.Services;

namespace VoiceHubComponent.Controls
{
    /// <summary>
    /// 逐字扫光歌词行控件。
    /// 有逐字时间轴时按单词进度扫光；无逐字时间轴时按行进度整行扫光。
    /// </summary>
    public sealed class WordLyricsText : Control
    {
        private const double SweepFeatherWidth = 4;
        private LyricLineItem? _line;
        private LyricLineItem? _backgroundLine;
        private double _positionMs;
        private double _lineFontSize = 15;
        private IBrush _foreground = Brushes.White;
        private TextAlignment _textAlignment = TextAlignment.Center;
        private bool _wordByWord = true;
        private bool _showTranslation = true;
        private bool _showRomanization;
        private FormattedText? _mainText;
        private FormattedText? _mutedMainText;
        private FormattedText? _backgroundPrefixText;
        private FormattedText? _backgroundText;
        private FormattedText? _mutedBackgroundText;
        private FormattedText? _suffixText;
        private double[] _wordStarts = Array.Empty<double>();
        private double[] _wordWidths = Array.Empty<double>();
        private double[] _backgroundWordStarts = Array.Empty<double>();
        private double[] _backgroundWordWidths = Array.Empty<double>();
        private readonly LinearGradientBrush _mainSweepMask = CreateSweepMask();
        private readonly LinearGradientBrush _backgroundSweepMask = CreateSweepMask();

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

        public double PositionMs
        {
            get => _positionMs;
            set
            {
                if (Math.Abs(_positionMs - value) < 0.5) return;
                _positionMs = value;
                InvalidateVisual();
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

        public bool WordByWord
        {
            get => _wordByWord;
            set
            {
                if (_wordByWord == value) return;
                _wordByWord = value;
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
            var suffixWidth = _suffixText?.WidthIncludingTrailingWhitespace ?? 0;
            var originX = GetOrigin(mainWidth + prefixWidth + backgroundWidth + suffixWidth);
            var mainOrigin = new Point(originX, 0);

            var mainAnimate = WordByWord && HasWordTiming(Line);
            var mainFilled = ComputeFilledWidth(Line, _wordStarts, _wordWidths);
            if (mainAnimate && mainFilled <= 0 && IsWellIntoLine(Line))
            {
                // 已进入行内但扫光进度未推进（时间轴异常或估算偏差），回退整行全亮，避免一直灰显
                mainAnimate = false;
            }

            DrawSweptText(
                context,
                _mainText,
                _mutedMainText ?? _mainText,
                mainOrigin,
                mainAnimate,
                mainFilled,
                _mainSweepMask);

            if (_backgroundText != null)
            {
                var prefixOrigin = new Point(originX + mainWidth, 0);
                if (_backgroundPrefixText != null) context.DrawText(_backgroundPrefixText, prefixOrigin);
                var backgroundOrigin = new Point(prefixOrigin.X + prefixWidth, 0);

                var backgroundAnimate = WordByWord && HasWordTiming(BackgroundLine);
                var backgroundFilled = ComputeFilledWidth(BackgroundLine, _backgroundWordStarts, _backgroundWordWidths);
                if (backgroundAnimate && backgroundFilled <= 0 && IsWellIntoLine(BackgroundLine))
                {
                    backgroundAnimate = false;
                }

                DrawSweptText(
                    context,
                    _backgroundText,
                    _mutedBackgroundText ?? _backgroundText,
                    backgroundOrigin,
                    backgroundAnimate,
                    backgroundFilled,
                    _backgroundSweepMask);
            }

            if (_suffixText != null)
                context.DrawText(_suffixText, new Point(originX + mainWidth + prefixWidth + backgroundWidth, 0));
        }

        private void EnsureMetrics()
        {
            if (_mainText != null) return;
            var line = Line;
            var text = line?.Text ?? string.Empty;
            _mainText = CreateFormatted(text, Foreground, LineFontSize);
            _mutedMainText = CreateFormatted(text, CreateOpacityBrush(Foreground, 0.52), LineFontSize);
            (_wordStarts, _wordWidths) = BuildWordMetrics(line, _mainText, LineFontSize);

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
                _mutedBackgroundText = CreateFormatted(
                    background.Text,
                    CreateOpacityBrush(Foreground, 0.38),
                    backgroundFontSize);
                (_backgroundWordStarts, _backgroundWordWidths) =
                    BuildWordMetrics(background, _backgroundText, backgroundFontSize);
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

        private (double[] Starts, double[] Widths) BuildWordMetrics(
            LyricLineItem? line,
            FormattedText text,
            double fontSize)
        {
            var words = line?.Words ?? new List<LyricWordItem>();
            var starts = new double[words.Count];
            var widths = new double[words.Count];
            var cursor = 0d;
            for (var index = 0; index < words.Count; index++)
            {
                var layout = CreateFormatted(words[index].Text, Foreground, fontSize);
                starts[index] = cursor;
                widths[index] = layout.WidthIncludingTrailingWhitespace;
                cursor += widths[index];
            }

            if (cursor > 0 && Math.Abs(cursor - text.WidthIncludingTrailingWhitespace) > 1)
            {
                var scale = text.WidthIncludingTrailingWhitespace / cursor;
                cursor = 0;
                for (var index = 0; index < widths.Length; index++)
                {
                    starts[index] = cursor;
                    widths[index] *= scale;
                    cursor += widths[index];
                }
            }

            return (starts, widths);
        }

        private void DrawSweptText(
            DrawingContext context,
            FormattedText text,
            FormattedText mutedText,
            Point origin,
            bool animate,
            double filledWidth,
            LinearGradientBrush mask)
        {
            context.DrawText(mutedText, origin);
            var width = text.WidthIncludingTrailingWhitespace;
            if (!animate || filledWidth >= width)
            {
                context.DrawText(text, origin);
                return;
            }

            if (filledWidth <= 0 || width <= 0) return;

            var feather = Math.Min(SweepFeatherWidth, width / 2);
            mask.GradientStops[0].Offset = Math.Clamp((filledWidth - feather) / width, 0, 1);
            mask.GradientStops[1].Offset = Math.Clamp((filledWidth + feather) / width, 0, 1);
            using (context.PushOpacityMask(mask, new Rect(origin, new Size(width, text.Height))))
                context.DrawText(text, origin);
        }

        /// <summary>
        /// 计算已唱进度的像素宽度。
        /// 有逐字时间轴时按单词进度累加；否则按行起止时间线性推进（整行扫光）。
        /// </summary>
        private double ComputeFilledWidth(
            LyricLineItem? line,
            double[] wordStarts,
            double[] wordWidths)
        {
            if (line == null) return 0;

            var words = line.Words;
            if (words.Count > 0 && wordStarts.Length == words.Count)
            {
                var filled = 0d;
                for (var index = 0; index < words.Count; index++)
                {
                    var progress = GetWordProgress(words[index]);
                    if (progress <= 0) break;
                    filled = wordStarts[index] + wordWidths[index] * progress;
                }

                return filled;
            }

            // 无逐字时间轴：不做扫光，静态全亮
            return double.MaxValue;
        }

        private double GetWordProgress(LyricWordItem word)
        {
            var startMs = word.Start.TotalMilliseconds;
            var endMs = word.End.TotalMilliseconds;
            if (PositionMs <= startMs) return 0;
            if (endMs <= startMs || PositionMs >= endMs) return 1;
            return Math.Clamp((PositionMs - startMs) / (endMs - startMs), 0, 1);
        }

        /// <summary>
        /// 位置已深入行内（兜底判断，用于时间轴异常时回退整行全亮）
        /// </summary>
        private bool IsWellIntoLine(LyricLineItem? line) =>
            line != null && PositionMs >= line.Start.TotalMilliseconds + 1500;

        private static bool HasWordTiming(LyricLineItem? line) => line?.HasWordTiming == true;

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

        private static LinearGradientBrush CreateSweepMask() => new()
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.White, 0),
                new GradientStop(Colors.Transparent, 1)
            }
        };

        private void InvalidateMetrics()
        {
            _mainText = null;
            _mutedMainText = null;
            _backgroundPrefixText = null;
            _backgroundText = null;
            _mutedBackgroundText = null;
            _suffixText = null;
            _wordStarts = Array.Empty<double>();
            _wordWidths = Array.Empty<double>();
            _backgroundWordStarts = Array.Empty<double>();
            _backgroundWordWidths = Array.Empty<double>();
            InvalidateMeasure();
            InvalidateVisual();
        }
    }
}
