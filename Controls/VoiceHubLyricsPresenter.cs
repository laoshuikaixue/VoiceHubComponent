using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VoiceHubComponent.Models;
using VoiceHubComponent.Services;

namespace VoiceHubComponent.Controls
{
    /// <summary>
    /// 歌词演示器：双层 StackPanel 上滑淡入切行，支持逐字/整行扫光、背景声行配对。
    /// 由外部定时器按节奏调用 ShowStatus / ShowLines 驱动。
    /// </summary>
    public sealed class VoiceHubLyricsPresenter : UserControl
    {
        private const double TransitionDurationMs = 380;
        private static readonly TimeSpan TransitionFrameInterval = TimeSpan.FromMilliseconds(16);
        private readonly Grid _root = new();
        private StackPanel _front = CreateLayer();
        private StackPanel _back = CreateLayer();
        private readonly DispatcherTimer _transitionTimer = new();
        private readonly List<WordLyricsText> _activeWordControls = new();
        private string _frameSignature = string.Empty;
        private TransitionState? _transition;

        public bool ShowTranslation { get; set; } = true;
        public bool ShowRomanization { get; set; }
        public bool WordByWord { get; set; } = true;
        public double BaseFontSize { get; set; } = 15;

        public VoiceHubLyricsPresenter()
        {
            _root.ClipToBounds = true;
            _root.Children.Add(_back);
            _root.Children.Add(_front);
            Content = _root;
            _transitionTimer.Interval = TransitionFrameInterval;
            _transitionTimer.Tick += TransitionTimerOnTick;
            DetachedFromVisualTree += (_, _) => CompleteTransition();
        }

        /// <summary>
        /// 设置变更时强制重建当前帧
        /// </summary>
        public void RefreshSettings()
        {
            _frameSignature = string.Empty;
        }

        /// <summary>
        /// 显示状态文案（加载中、暂无歌词、前奏等）
        /// </summary>
        public void ShowStatus(string text, string signature)
        {
            if (string.Equals(_frameSignature, signature, StringComparison.Ordinal)) return;
            _frameSignature = signature;
            CompleteTransition();
            _activeWordControls.Clear();
            _back.Children.Clear();
            _back.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = BaseFontSize,
                Opacity = 0.68,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            });
            StartTransition();
        }

        /// <summary>
        /// 显示当前歌词帧
        /// </summary>
        public void ShowLines(IReadOnlyList<LyricLineItem> lines, string format, double positionMs)
        {
            var activeLines = LyricsLineSelector.SelectActive(lines, format, positionMs);
            if (activeLines.Count == 0)
            {
                ShowStatus("♪", "prelude");
                return;
            }

            var signature = BuildSignature(activeLines);
            if (!string.Equals(signature, _frameSignature, StringComparison.Ordinal))
            {
                _frameSignature = signature;
                RebuildFrame(activeLines, positionMs);
            }
            else
            {
                UpdateActiveControls(activeLines, positionMs);
            }
        }

        /// <summary>
        /// 计算下一次应刷新的延迟。
        /// 扫光模式（含逐字与整行扫光）固定 33ms，行级静态模式按下一边界自适应。
        /// </summary>
        public TimeSpan GetNextRefreshDelay(IReadOnlyList<LyricLineItem> lines, double positionMs)
        {
            if (WordByWord && lines.Any(line => line.HasWordTiming))
            {
                return TimeSpan.FromMilliseconds(33);
            }

            var nextBoundary = lines
                .SelectMany(line => new[] { line.Start.TotalMilliseconds, line.End.TotalMilliseconds })
                .Where(time => time > positionMs + 1)
                .DefaultIfEmpty(positionMs + 1000)
                .Min();
            var delay = nextBoundary - positionMs;
            return TimeSpan.FromMilliseconds(Math.Clamp(delay, 30, 1000));
        }

        private void RebuildFrame(IReadOnlyList<LyricsLineSelection> activeLines, double position)
        {
            CompleteTransition();
            _activeWordControls.Clear();
            _back.Children.Clear();
            var hasDuet = activeLines.Any(item => item.IsDuetSide);
            foreach (var selection in activeLines)
            {
                var line = selection.Line;
                var control = new WordLyricsText
                {
                    Line = line,
                    BackgroundLine = selection.BackgroundLine,
                    PositionMs = position,
                    LineFontSize = line.IsBG ? Math.Max(9, BaseFontSize * 0.76) : BaseFontSize,
                    Foreground = Foreground ?? Brushes.White,
                    TextAlignment = selection.IsDuetSide
                        ? TextAlignment.Right
                        : hasDuet
                            ? TextAlignment.Left
                            : TextAlignment.Center,
                    WordByWord = WordByWord,
                    ShowTranslation = ShowTranslation,
                    ShowRomanization = ShowRomanization,
                    Opacity = line.IsBG ? 0.72 : 1,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    MaxWidth = 760
                };
                _back.Children.Add(control);
                _activeWordControls.Add(control);
            }

            StartTransition();
        }

        private void UpdateActiveControls(
            IReadOnlyList<LyricsLineSelection> activeLines,
            double position)
        {
            if (_activeWordControls.Count != activeLines.Count)
            {
                RebuildFrame(activeLines, position);
                return;
            }

            for (var index = 0; index < activeLines.Count; index++)
            {
                var control = _activeWordControls[index];
                var line = activeLines[index].Line;
                control.Line = line;
                control.BackgroundLine = activeLines[index].BackgroundLine;
                control.PositionMs = position;
                control.WordByWord = WordByWord;
            }
        }

        private void StartTransition()
        {
            var oldLayer = _front;
            var newLayer = _back;
            var oldTransform = (TranslateTransform)oldLayer.RenderTransform!;
            var newTransform = (TranslateTransform)newLayer.RenderTransform!;
            var offset = Math.Max(20, BaseFontSize * 1.5);
            oldLayer.Opacity = oldLayer.Children.Count == 0 ? 0 : 1;
            oldTransform.Y = 0;
            newLayer.Opacity = 0;
            newTransform.Y = offset;
            newLayer.IsVisible = true;
            oldLayer.IsVisible = true;

            _front = newLayer;
            _back = oldLayer;
            _transition = new TransitionState(
                oldLayer,
                oldTransform,
                newLayer,
                newTransform,
                Environment.TickCount64,
                offset);
            _transitionTimer.Start();
        }

        private void TransitionTimerOnTick(object? sender, EventArgs e)
        {
            var transition = _transition;
            if (transition == null) return;
            var progress = Math.Clamp(
                (Environment.TickCount64 - transition.StartedAtTick) / TransitionDurationMs,
                0,
                1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            transition.OldLayer.Opacity = 1 - eased;
            transition.OldTransform.Y = -transition.Offset * eased;
            transition.NewLayer.Opacity = eased;
            transition.NewTransform.Y = transition.Offset * (1 - eased);
            if (progress >= 1) CompleteTransition();
        }

        private string BuildSignature(IReadOnlyList<LyricsLineSelection> activeLines) => string.Join(
            "\u001f",
            activeLines.Select(item => string.Join(
                "\u001e",
                item.LineIndex,
                item.Line.Start.TotalMilliseconds,
                item.Line.End.TotalMilliseconds,
                item.Line.Text,
                ShowTranslation ? item.Line.Translation ?? string.Empty : string.Empty,
                ShowRomanization ? item.Line.Romanization ?? string.Empty : string.Empty,
                item.Line.IsBG,
                item.IsDuetSide,
                item.BackgroundLine?.Start.TotalMilliseconds,
                item.BackgroundLine?.End.TotalMilliseconds,
                item.BackgroundLine?.Text)));

        private static StackPanel CreateLayer() => new()
        {
            Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new TranslateTransform(),
            IsVisible = false,
            Opacity = 0
        };

        private void CompleteTransition()
        {
            _transitionTimer.Stop();
            _transition = null;
            _front.IsVisible = true;
            _front.Opacity = 1;
            ((TranslateTransform)_front.RenderTransform!).Y = 0;
            _back.Children.Clear();
            _back.IsVisible = false;
            _back.Opacity = 0;
            ((TranslateTransform)_back.RenderTransform!).Y = 0;
        }

        private sealed record TransitionState(
            StackPanel OldLayer,
            TranslateTransform OldTransform,
            StackPanel NewLayer,
            TranslateTransform NewTransform,
            long StartedAtTick,
            double Offset);
    }
}
