using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VoiceHubComponent.Models;

namespace VoiceHubComponent.Services
{
    /// <summary>
    /// 歌词原始数据载荷
    /// </summary>
    public sealed record LyricPayload(string? Lrc, string? Translation, string? Yrc, string? Ttml, string? Qrc = null, string? Roma = null)
    {
        public static LyricPayload Empty { get; } = new(null, null, null, null);

        public LyricPayload WithTranslation(string? translation)
        {
            return new LyricPayload(Lrc, translation, Yrc, Ttml, Qrc, Roma);
        }
    }

    /// <summary>
    /// 歌词解析结果
    /// </summary>
    public sealed record ParsedLyrics(List<LyricLineItem> Lines, string Format);

    /// <summary>
    /// 歌词格式解析与优先级处理。
    /// 格式优先级：TTML(0) &gt; QRC/YRC(1) &gt; LRC(2)。
    /// </summary>
    public static class LyricParser
    {
        public const int RankTtml = 0;
        public const int RankWord = 1;
        public const int RankLine = 2;
        public const int RankNone = 3;

        private static readonly Regex LrcTimeRegex = new(@"\[(\d{1,2}):(\d{2})(?:[\.:](\d{1,3}))?\]", RegexOptions.Compiled);
        private static readonly Regex EnhancedWordRegex = new(@"<(\d{1,2}):(\d{2})(?:[\.:](\d{1,3}))?>([^<]*)", RegexOptions.Compiled);
        private static readonly Regex QrcLineRegex = new(@"^\[(\d+),(\d+)\](.*)$", RegexOptions.Compiled);
        private static readonly Regex QrcWordRegex = new(@"([^(]*)\((\d+),(\d+)\)", RegexOptions.Compiled);
        private static readonly Regex LrcMetaRegex = new(@"^\[[a-z]+:", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex QrcContentRegex = new("LyricContent=\"([\\s\\S]*?)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 当前载荷的最高阶格式 rank（数字越小越高阶）
        /// </summary>
        public static int GetPayloadRank(LyricPayload payload)
        {
            if (!string.IsNullOrWhiteSpace(payload.Ttml)) return RankTtml;
            if (!string.IsNullOrWhiteSpace(payload.Qrc) || !string.IsNullOrWhiteSpace(payload.Yrc)) return RankWord;
            if (!string.IsNullOrWhiteSpace(payload.Lrc)) return RankLine;
            return RankNone;
        }

        /// <summary>
        /// 按优先级解析歌词：TTML &gt; QRC &gt; YRC &gt; LRC
        /// </summary>
        public static ParsedLyrics ParseBestLyrics(LyricPayload payload)
        {
            if (!string.IsNullOrWhiteSpace(payload.Ttml))
            {
                var ttmlLines = ParseTtml(payload.Ttml);
                if (ttmlLines.Count > 0)
                {
                    return new ParsedLyrics(ttmlLines, "ttml");
                }
            }

            if (!string.IsNullOrWhiteSpace(payload.Qrc))
            {
                var qrcLines = ParseQrc(payload.Qrc);
                if (qrcLines.Count > 0)
                {
                    return new ParsedLyrics(qrcLines, "qrc");
                }
            }

            if (!string.IsNullOrWhiteSpace(payload.Yrc))
            {
                var trimmed = payload.Yrc.Trim();

                // QQ 音乐 XML 格式 QRC
                if (trimmed.StartsWith("<") || trimmed.Contains("LyricContent=\""))
                {
                    var qrcLines = ParseQrc(payload.Yrc);
                    if (qrcLines.Count > 0)
                    {
                        return new ParsedLyrics(qrcLines, "qrc");
                    }
                }

                // 网易云 JSON 逐字格式
                if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
                {
                    if (TryParseYrcJson(payload.Yrc, out var yrcLines) && yrcLines.Count > 0)
                    {
                        return new ParsedLyrics(yrcLines, "yrc");
                    }
                }

                var smartLines = ParseSmartLrc(payload.Yrc);
                if (smartLines.Count > 0)
                {
                    return new ParsedLyrics(smartLines, "lrc");
                }
            }

            return new ParsedLyrics(ParseSmartLrc(payload.Lrc), "lrc");
        }

        /// <summary>
        /// 智能 LRC 解析：增强逐字 &gt; 行内多时间戳 &gt; 普通行级
        /// </summary>
        public static List<LyricLineItem> ParseSmartLrc(string? lrc)
        {
            if (string.IsNullOrWhiteSpace(lrc))
            {
                return new List<LyricLineItem>();
            }

            var rawLines = lrc.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (rawLines.Any(line => EnhancedWordRegex.IsMatch(line)))
            {
                var enhancedLines = ParseEnhancedLrc(lrc);
                if (enhancedLines.Count > 0)
                {
                    return enhancedLines;
                }
            }

            if (rawLines.Any(line => LrcTimeRegex.Matches(line).Count > 1))
            {
                var wordByWordLines = ParseWordByWordLrc(lrc);
                if (wordByWordLines.Count > 0)
                {
                    return wordByWordLines;
                }
            }

            return ParseLineLrc(lrc);
        }

        private static List<LyricLineItem> ParseLineLrc(string? lrc)
        {
            var result = new List<LyricLineItem>();
            if (string.IsNullOrWhiteSpace(lrc))
            {
                return result;
            }

            foreach (var rawLine in lrc.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || LrcMetaRegex.IsMatch(line))
                {
                    continue;
                }

                var matches = LrcTimeRegex.Matches(line);
                if (matches.Count == 0)
                {
                    continue;
                }

                var text = LrcTimeRegex.Replace(line, string.Empty).Trim();
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                foreach (Match match in matches)
                {
                    result.Add(new LyricLineItem
                    {
                        Start = ParseLrcTimestamp(match),
                        Text = text
                    });
                }
            }

            result = result.OrderBy(line => line.Start).ToList();
            for (var i = 0; i < result.Count; i++)
            {
                result[i].End = i + 1 < result.Count
                    ? result[i + 1].Start
                    : result[i].Start + TimeSpan.FromSeconds(5);
            }

            return result;
        }

        private static List<LyricLineItem> ParseWordByWordLrc(string lrc)
        {
            var result = new List<LyricLineItem>();
            foreach (var rawLine in lrc.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || LrcMetaRegex.IsMatch(line))
                {
                    continue;
                }

                var matches = LrcTimeRegex.Matches(line).Cast<Match>().ToList();
                if (matches.Count == 0)
                {
                    continue;
                }

                var segments = new List<(TimeSpan Start, string Text)>();
                for (var i = 0; i < matches.Count; i++)
                {
                    var match = matches[i];
                    var contentStart = match.Index + match.Length;
                    var contentEnd = i + 1 < matches.Count ? matches[i + 1].Index : line.Length;
                    var text = line[contentStart..contentEnd];
                    if (!string.IsNullOrEmpty(text))
                    {
                        segments.Add((ParseLrcTimestamp(match), text));
                    }
                }

                if (segments.Count == 0)
                {
                    continue;
                }

                var lineText = string.Concat(segments.Select(segment => segment.Text)).Trim();
                if (string.IsNullOrEmpty(lineText))
                {
                    continue;
                }

                var words = new List<LyricWordItem>();
                for (var i = 0; i < segments.Count; i++)
                {
                    var start = segments[i].Start;
                    var end = i + 1 < segments.Count ? segments[i + 1].Start : start + TimeSpan.FromSeconds(1);
                    words.Add(new LyricWordItem { Start = start, End = end, Text = segments[i].Text });
                }

                result.Add(new LyricLineItem
                {
                    Start = segments.Min(segment => segment.Start),
                    End = segments.Last().Start + TimeSpan.FromSeconds(1),
                    Text = lineText,
                    Words = words
                });
            }

            CompleteLineEndTimes(result);
            return result;
        }

        private static List<LyricLineItem> ParseEnhancedLrc(string lrc)
        {
            var result = new List<LyricLineItem>();
            foreach (var rawLine in lrc.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || LrcMetaRegex.IsMatch(line))
                {
                    continue;
                }

                var lineMatch = LrcTimeRegex.Match(line);
                if (!lineMatch.Success)
                {
                    continue;
                }

                var contentAfterLineTime = line[(lineMatch.Index + lineMatch.Length)..];
                var wordMatches = EnhancedWordRegex.Matches(contentAfterLineTime).Cast<Match>().ToList();
                var text = wordMatches.Count > 0
                    ? string.Concat(wordMatches.Select(match => match.Groups[4].Value)).Trim()
                    : EnhancedWordRegex.Replace(contentAfterLineTime, string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var start = ParseLrcTimestamp(lineMatch);
                var end = wordMatches.Count > 0
                    ? ParseEnhancedTimestamp(wordMatches.Last()) + TimeSpan.FromSeconds(1)
                    : start + TimeSpan.FromSeconds(1);

                var words = new List<LyricWordItem>();
                if (wordMatches.Count > 0)
                {
                    for (var i = 0; i < wordMatches.Count; i++)
                    {
                        var wordText = wordMatches[i].Groups[4].Value;
                        if (string.IsNullOrEmpty(wordText))
                        {
                            continue;
                        }

                        var wordStart = ParseEnhancedTimestamp(wordMatches[i]);
                        var wordEnd = i + 1 < wordMatches.Count
                            ? ParseEnhancedTimestamp(wordMatches[i + 1])
                            : wordStart + TimeSpan.FromSeconds(1);
                        words.Add(new LyricWordItem { Start = wordStart, End = wordEnd, Text = wordText });
                    }
                }

                result.Add(new LyricLineItem { Start = start, End = end, Text = text, Words = words });
            }

            CompleteLineEndTimes(result);
            return result;
        }

        /// <summary>
        /// 解析 QRC 逐字歌词（QQ 音乐），保留逐字时间轴
        /// </summary>
        public static List<LyricLineItem> ParseQrc(string? content)
        {
            var result = new List<LyricLineItem>();
            if (string.IsNullOrWhiteSpace(content))
            {
                return result;
            }

            var rawContent = ExtractQrcLyricContent(content) ?? content;
            foreach (var rawLine in rawContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || LrcMetaRegex.IsMatch(line))
                {
                    continue;
                }

                var lineMatch = QrcLineRegex.Match(line);
                if (!lineMatch.Success)
                {
                    continue;
                }

                var startMs = long.Parse(lineMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                var durationMs = long.Parse(lineMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                var lineContent = lineMatch.Groups[3].Value;

                var words = new List<LyricWordItem>();
                foreach (Match wordMatch in QrcWordRegex.Matches(lineContent))
                {
                    var wordText = wordMatch.Groups[1].Value;
                    if (string.IsNullOrEmpty(wordText))
                    {
                        continue;
                    }

                    var wordOffset = long.Parse(wordMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                    var wordDuration = long.Parse(wordMatch.Groups[3].Value, CultureInfo.InvariantCulture);
                    words.Add(new LyricWordItem
                    {
                        Start = TimeSpan.FromMilliseconds(startMs + wordOffset),
                        End = TimeSpan.FromMilliseconds(startMs + wordOffset + Math.Max(wordDuration, 1)),
                        Text = wordText
                    });
                }

                var text = string.Concat(words.Select(word => word.Text)).Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                result.Add(new LyricLineItem
                {
                    Start = TimeSpan.FromMilliseconds(startMs),
                    End = TimeSpan.FromMilliseconds(startMs + durationMs),
                    Text = text,
                    Words = words
                });
            }

            CompleteLineEndTimes(result);
            return result;
        }

        /// <summary>
        /// 解析网易云 YRC JSON 逐字歌词
        /// </summary>
        public static bool TryParseYrcJson(string content, out List<LyricLineItem> lines)
        {
            lines = new List<LyricLineItem>();
            try
            {
                using var document = JsonDocument.Parse(content);
                var contentElement = FindYrcContentElement(document.RootElement);
                if (contentElement == null || contentElement.Value.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                foreach (var lineElement in contentElement.Value.EnumerateArray())
                {
                    if (lineElement.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var words = new List<LyricWordItem>();
                    foreach (var wordElement in lineElement.EnumerateArray())
                    {
                        if (wordElement.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        var items = wordElement.EnumerateArray().ToList();
                        if (items.Count < 3)
                        {
                            continue;
                        }

                        var wordText = items[0].ValueKind switch
                        {
                            JsonValueKind.String => items[0].GetString() ?? string.Empty,
                            JsonValueKind.Array => string.Concat(items[0].EnumerateArray()
                                .Where(part => part.ValueKind == JsonValueKind.String)
                                .Select(part => part.GetString())),
                            _ => string.Empty
                        };
                        if (string.IsNullOrEmpty(wordText))
                        {
                            continue;
                        }

                        if (!TryGetDouble(items[1], out var wordStart) || !TryGetDouble(items[2], out var wordEnd))
                        {
                            continue;
                        }

                        words.Add(new LyricWordItem
                        {
                            Start = TimeSpan.FromMilliseconds(wordStart),
                            End = TimeSpan.FromMilliseconds(Math.Max(wordEnd, wordStart + 1)),
                            Text = wordText
                        });
                    }

                    if (words.Count == 0)
                    {
                        continue;
                    }

                    var lineStart = words.Min(word => word.Start);
                    var lineEnd = words.Max(word => word.End);
                    lines.Add(new LyricLineItem
                    {
                        Start = lineStart,
                        End = lineEnd,
                        Text = string.Concat(words.Select(word => word.Text)).Trim(),
                        Words = words
                    });
                }

                if (lines.Count == 0)
                {
                    return false;
                }

                CompleteLineEndTimes(lines);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static JsonElement? FindYrcContentElement(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return root.ValueKind == JsonValueKind.Array ? root : null;
            }

            foreach (var propertyName in new[] { "lyricContent", "yrcContent", "content" })
            {
                if (root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.Array)
                {
                    return element;
                }
            }

            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    var nested = FindYrcContentElement(property.Value);
                    if (nested != null)
                    {
                        return nested;
                    }
                }
            }

            return null;
        }

        private static bool TryGetDouble(JsonElement element, out double value)
        {
            value = 0;
            switch (element.ValueKind)
            {
                case JsonValueKind.Number when element.TryGetDouble(out var number):
                    value = number;
                    return true;
                case JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                    value = parsed;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 解析 TTML 歌词（AMLL 格式），保留逐字时间轴、背景声与对唱标记
        /// </summary>
        public static List<LyricLineItem> ParseTtml(string? content)
        {
            var result = new List<LyricLineItem>();
            if (string.IsNullOrWhiteSpace(content))
            {
                return result;
            }

            try
            {
                var document = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
                var body = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "body");
                if (body != null)
                {
                    var divs = body.Elements().Where(element => element.Name.LocalName == "div").ToList();
                    var paragraphContainers = divs.Count > 0 ? divs : new List<XElement> { body };
                    for (var divIndex = 0; divIndex < paragraphContainers.Count; divIndex++)
                    {
                        var isDuetSide = divs.Count > 1 && divIndex > 0;
                        foreach (var paragraph in paragraphContainers[divIndex].Descendants()
                                     .Where(element => element.Name.LocalName == "p"))
                        {
                            var parsedLine = ParseTtmlParagraph(paragraph);
                            if (parsedLine != null)
                            {
                                parsedLine.IsDuet = isDuetSide;
                                result.Add(parsedLine);
                            }
                        }
                    }
                }
            }
            catch
            {
                // XML 解析失败时回退到正则行级解析
                result = ParseTtmlByRegex(content);
            }

            CompleteLineEndTimes(result);
            return result;
        }

        private static LyricLineItem? ParseTtmlParagraph(XElement paragraph)
        {
            var beginAttribute = paragraph.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "begin");
            if (beginAttribute == null || !TryParseTtmlTime(beginAttribute.Value, out var start))
            {
                return null;
            }

            TimeSpan end;
            var endAttribute = paragraph.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "end");
            var durAttribute = paragraph.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "dur");
            if (endAttribute != null && TryParseTtmlTime(endAttribute.Value, out var explicitEnd))
            {
                end = explicitEnd;
            }
            else if (durAttribute != null && TryParseTtmlTime(durAttribute.Value, out var duration))
            {
                end = start + duration;
            }
            else
            {
                end = TimeSpan.Zero;
            }

            var isBg = paragraph.Attributes()
                .Any(attribute => attribute.Name.LocalName == "is-bg" &&
                                  string.Equals(attribute.Value, "true", StringComparison.OrdinalIgnoreCase));

            var words = new List<LyricWordItem>();
            string? translation = null;
            string? romanization = null;

            foreach (var child in paragraph.Elements())
            {
                if (child.Name.LocalName == "span")
                {
                    var spanBegin = child.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "begin");
                    if (spanBegin == null || !TryParseTtmlTime(spanBegin.Value, out var wordStart))
                    {
                        continue;
                    }

                    TimeSpan wordEnd;
                    var spanEnd = child.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "end");
                    var spanDur = child.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "dur");
                    if (spanEnd != null && TryParseTtmlTime(spanEnd.Value, out var spanEndValue))
                    {
                        wordEnd = spanEndValue;
                    }
                    else if (spanDur != null && TryParseTtmlTime(spanDur.Value, out var spanDuration))
                    {
                        wordEnd = wordStart + spanDuration;
                    }
                    else
                    {
                        wordEnd = end > TimeSpan.Zero ? end : wordStart + TimeSpan.FromMilliseconds(200);
                    }

                    var wordText = child.Value;
                    if (string.IsNullOrEmpty(wordText))
                    {
                        continue;
                    }

                    words.Add(new LyricWordItem { Start = wordStart, End = wordEnd, Text = wordText });
                }
                else if (child.Name.LocalName == "translation")
                {
                    var lang = child.Attributes()
                        .FirstOrDefault(attribute => attribute.Name.LocalName == "lang")?.Value ?? string.Empty;
                    var value = child.Value.Trim();
                    if (string.IsNullOrEmpty(value))
                    {
                        continue;
                    }

                    if (lang.Contains("roma", StringComparison.OrdinalIgnoreCase))
                    {
                        romanization ??= value;
                    }
                    else
                    {
                        translation ??= value;
                    }
                }
            }

            var text = words.Count > 0
                ? string.Concat(words.Select(word => word.Text))
                : paragraph.Value;
            text = text.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (end <= TimeSpan.Zero)
            {
                end = words.Count > 0 ? words.Max(word => word.End) : start + TimeSpan.FromSeconds(5);
            }

            return new LyricLineItem
            {
                Start = start,
                End = end,
                Text = text,
                Words = words,
                IsBG = isBg,
                Translation = translation,
                Romanization = romanization
            };
        }

        private static List<LyricLineItem> ParseTtmlByRegex(string content)
        {
            var result = new List<LyricLineItem>();
            var ttmlLineRegex = new Regex(
                @"<(?:p|span)[^>]*?begin=""([^""]+)""[^>]*?(?:end=""([^""]+)""|dur=""([^""]+)"")?[^>]*>(.*?)</(?:p|span)>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var xmlTagRegex = new Regex(@"<[^>]+>", RegexOptions.Compiled);

            foreach (Match match in ttmlLineRegex.Matches(content))
            {
                var text = DecodeXmlText(xmlTagRegex.Replace(match.Groups[4].Value, string.Empty)).Trim();
                if (string.IsNullOrWhiteSpace(text) || !TryParseTtmlTime(match.Groups[1].Value, out var start))
                {
                    continue;
                }

                TimeSpan end;
                if (match.Groups[2].Success && TryParseTtmlTime(match.Groups[2].Value, out var explicitEnd))
                {
                    end = explicitEnd;
                }
                else if (match.Groups[3].Success && TryParseTtmlTime(match.Groups[3].Value, out var duration))
                {
                    end = start + duration;
                }
                else
                {
                    end = start + TimeSpan.FromSeconds(5);
                }

                result.Add(new LyricLineItem { Start = start, End = end, Text = text });
            }

            return result;
        }

        private static TimeSpan ParseLrcTimestamp(Match match)
        {
            var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var millisecondsText = match.Groups[3].Success ? match.Groups[3].Value : "0";
            var normalizedMilliseconds = millisecondsText.PadRight(3, '0')[..3];
            var milliseconds = int.Parse(normalizedMilliseconds, CultureInfo.InvariantCulture);
            return TimeSpan.FromMilliseconds(minutes * 60_000 + seconds * 1_000 + milliseconds);
        }

        private static TimeSpan ParseEnhancedTimestamp(Match match)
        {
            var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var millisecondsText = match.Groups[3].Success ? match.Groups[3].Value : "0";
            var normalizedMilliseconds = millisecondsText.PadRight(3, '0')[..3];
            var milliseconds = int.Parse(normalizedMilliseconds, CultureInfo.InvariantCulture);
            return TimeSpan.FromMilliseconds(minutes * 60_000 + seconds * 1_000 + milliseconds);
        }

        /// <summary>
        /// 补齐/修正行结束时间，保证不越过下一行
        /// </summary>
        public static void CompleteLineEndTimes(List<LyricLineItem> lines)
        {
            lines.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (var i = 0; i < lines.Count; i++)
            {
                var nextLine = i + 1 < lines.Count ? lines[i + 1] : null;
                if (nextLine != null && (lines[i].End <= lines[i].Start || lines[i].End > nextLine.Start))
                {
                    lines[i].End = nextLine.Start;
                }
                else if (lines[i].End <= lines[i].Start)
                {
                    lines[i].End = lines[i].Start + TimeSpan.FromSeconds(5);
                }
            }
        }

        private static string? ExtractQrcLyricContent(string rawContent)
        {
            var match = QrcContentRegex.Match(rawContent);
            if (!match.Success)
            {
                return null;
            }

            return DecodeXmlText(match.Groups[1].Value)
                .Replace("\\n", "\n")
                .Replace("\\r", "\r");
        }

        public static bool TryParseTtmlTime(string value, out TimeSpan time)
        {
            value = value.Trim();
            if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
            {
                time = TimeSpan.FromMilliseconds(milliseconds);
                return true;
            }

            if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                time = TimeSpan.FromSeconds(seconds);
                return true;
            }

            var parts = value.Split(':');
            if (parts.Length is 1 or 2 or 3)
            {
                if (TryParseClockPart(parts[0], out var first) &&
                    TryParseClockPart(parts.Length > 1 ? parts[1] : "0", out var second) &&
                    TryParseClockPart(parts.Length > 2 ? parts[2] : "0", out var third))
                {
                    time = parts.Length switch
                    {
                        3 => TimeSpan.FromHours(first) + TimeSpan.FromMinutes(second) + TimeSpan.FromSeconds(third),
                        2 => TimeSpan.FromMinutes(first) + TimeSpan.FromSeconds(second),
                        _ => TimeSpan.FromSeconds(first)
                    };
                    return true;
                }
            }

            time = TimeSpan.Zero;
            return false;
        }

        private static bool TryParseClockPart(string value, out double parsed)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
        }

        private static string DecodeXmlText(string value)
        {
            return value
                .Replace("&quot;", "\"")
                .Replace("&apos;", "'")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&amp;", "&");
        }

        /// <summary>
        /// 按起始时间就近对齐翻译行
        /// </summary>
        public static void AlignTranslations(List<LyricLineItem> lyrics, IReadOnlyList<LyricLineItem> translations)
        {
            var i = 0;
            var j = 0;
            var tolerance = TimeSpan.FromMilliseconds(300);

            while (i < lyrics.Count && j < translations.Count)
            {
                var diff = lyrics[i].Start - translations[j].Start;
                if (diff.Duration() <= tolerance)
                {
                    lyrics[i].Translation = translations[j].Text;
                    i++;
                    j++;
                }
                else if (diff < TimeSpan.Zero)
                {
                    i++;
                }
                else
                {
                    j++;
                }
            }
        }

        /// <summary>
        /// 按起始时间就近对齐罗马音行
        /// </summary>
        public static void AlignRomanizations(List<LyricLineItem> lyrics, IReadOnlyList<LyricLineItem> romanizations)
        {
            var i = 0;
            var j = 0;
            var tolerance = TimeSpan.FromMilliseconds(300);

            while (i < lyrics.Count && j < romanizations.Count)
            {
                var diff = lyrics[i].Start - romanizations[j].Start;
                if (diff.Duration() <= tolerance)
                {
                    lyrics[i].Romanization = romanizations[j].Text;
                    i++;
                    j++;
                }
                else if (diff < TimeSpan.Zero)
                {
                    i++;
                }
                else
                {
                    j++;
                }
            }
        }

        /// <summary>
        /// 根据最后一行歌词估算歌曲时长
        /// </summary>
        public static TimeSpan GetEstimatedDuration(IReadOnlyList<LyricLineItem> lyrics, TimeSpan fallback)
        {
            if (lyrics.Count == 0)
            {
                return fallback;
            }

            var lastLineEnd = lyrics.Max(line => line.End);
            return lastLineEnd > TimeSpan.FromSeconds(30) ? lastLineEnd + TimeSpan.FromSeconds(3) : fallback;
        }
    }
}
