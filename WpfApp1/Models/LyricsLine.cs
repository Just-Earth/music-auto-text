using System;
using System.Text.RegularExpressions;
using System.Collections.Generic;

namespace WpfApp1.Models
{
    public class LyricsLine
    {
        // core fields
        public TimeSpan Timestamp { get; set; }
        public string Text { get; set; } = string.Empty;
        public bool IsScream { get; set; } = false;

        // first word and its normalized form for matching
        public string FirstWord { get; set; } = string.Empty;
        public string FirstWordNormalized { get; set; } = string.Empty;
        // timestamp (seconds) of the first word if known
        public double FirstWordStart { get; set; } = 0.0;

        // optional explicit end timestamp for the line (if zero, callers should use a default duration)
        public TimeSpan EndTimestamp { get; set; } = TimeSpan.Zero;

        // confidence / score from alignment (0..1)
        public double Confidence { get; set; } = 0.0;

        // last time this line was matched to playback (for smoothing / debouncing)
        public DateTimeOffset? LastMatchedAt { get; set; } = null;

        // Normalize helper used for matching first words
        private static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            s = s.ToLowerInvariant();
            return Regex.Replace(s, @"[^\p{L}\p{Nd}]+", "");
        }

        // compute/update first word and normalized form from current Text
        public void UpdateFirstWordFromText()
        {
            if (string.IsNullOrWhiteSpace(Text))
            {
                FirstWord = string.Empty;
                FirstWordNormalized = string.Empty;
                return;
            }

            var parts = Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var first = parts.Length > 0 ? parts[0] : string.Empty;
            FirstWord = first;
            FirstWordNormalized = Normalize(first);
        }

        // Return effective end (use callers' defaultDuration when EndTimestamp not set)
        public TimeSpan EffectiveEnd(TimeSpan defaultDuration)
        {
            if (EndTimestamp > Timestamp) return EndTimestamp;
            return Timestamp.Add(defaultDuration);
        }

        // Determine whether a playback position should be considered inside this line.
        // Uses small lead/tail to provide hysteresis and avoid rapid switching.
        public bool Contains(TimeSpan playbackPosition, TimeSpan lead, TimeSpan tail, TimeSpan defaultDuration)
        {
            var start = Timestamp - lead;
            if (start < TimeSpan.Zero) start = TimeSpan.Zero;
            var end = (EndTimestamp > Timestamp) ? EndTimestamp + tail : Timestamp.Add(defaultDuration).Add(tail);
            return playbackPosition >= start && playbackPosition <= end;
        }

        // Find active line index with hysteresis + monotonic preference to reduce jumps.
        // - lines: list of lyrics
        // - playbackPosition: current playback time
        // - lastIndex: previously active index (or -1)
        // - defaultDuration: duration to assume per-line when EndTimestamp not set (e.g. 3s)
        // - lead/tail: hysteresis window before/after line
        public static int FindActiveLineIndex(IList<LyricsLine> lines, TimeSpan playbackPosition, int lastIndex = -1, TimeSpan? defaultDuration = null, TimeSpan? lead = null, TimeSpan? tail = null)
        {
            if (lines == null || lines.Count == 0) return -1;
            var d = defaultDuration ?? TimeSpan.FromSeconds(3);
            var l = lead ?? TimeSpan.FromMilliseconds(250);
            var t = tail ?? TimeSpan.FromMilliseconds(450);

            // quick win: if lastIndex still valid and contains playbackPosition, keep it
            if (lastIndex >= 0 && lastIndex < lines.Count)
            {
                if (lines[lastIndex].Contains(playbackPosition, l, t, d))
                {
                    return lastIndex;
                }
            }

            // search nearby around lastIndex first to prefer continuity
            int radius = 6;
            if (lastIndex >= 0 && lastIndex < lines.Count)
            {
                int from = Math.Max(0, lastIndex - radius);
                int to = Math.Min(lines.Count - 1, lastIndex + radius);
                for (int i = from; i <= to; i++)
                {
                    if (lines[i].Contains(playbackPosition, l, t, d)) return i;
                }
            }

            // full scan
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Contains(playbackPosition, l, t, d)) return i;
            }

            // fallback: last line with Timestamp <= playbackPosition
            int idx = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Timestamp <= playbackPosition) idx = i;
                else break;
            }
            if (idx >= 0) return idx;

            // if nothing matched, return first
            return 0;
        }
    }
}
