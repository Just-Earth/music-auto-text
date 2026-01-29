using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text.Json;
using System.Web;
using NAudio.Wave;
using WpfApp1.Models;

namespace WpfApp1.Services
{
    public class LyricsService
    {
        private static readonly HttpClient _http = new HttpClient();

        // Fetch raw lyrics from lyrics.ovh by artist and title
        public async Task<string?> FetchLyricsRawByArtistTitleAsync(string artist, string title)
        {
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return null;
            try
            {
                var a = HttpUtility.UrlEncode(artist);
                var t = HttpUtility.UrlEncode(title);
                var url = $"https://api.lyrics.ovh/v1/{a}/{t}";
                var resp = await _http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return null;
                var txt = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(txt);
                if (doc.RootElement.TryGetProperty("lyrics", out var lyricsEl) && lyricsEl.ValueKind == JsonValueKind.String)
                {
                    var lyrics = lyricsEl.GetString();
                    return string.IsNullOrWhiteSpace(lyrics) ? null : lyrics;
                }
                return null;
            }
            catch { return null; }
        }

        // Parse raw lyrics text into lines (naive uniform timestamps)
        public List<LyricsLine> ParseLyricsToLines(string raw)
        {
            var lines = new List<LyricsLine>();
            if (string.IsNullOrWhiteSpace(raw)) return lines;
            var parts = raw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var t = TimeSpan.Zero;
            foreach (var p in parts)
            {
                var txt = p.Trim();
                var ll = new LyricsLine { Timestamp = t, Text = txt };
                // compute first word
                var first = txt.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                ll.FirstWord = first;
                ll.FirstWordNormalized = Regex.Replace(first.ToLowerInvariant(), "[^\\p{L}\\p{Nd}]+", "");
                lines.Add(ll);
                t = t.Add(TimeSpan.FromSeconds(3));
            }
            return lines;
        }

        // Fallback: split filename words
        public List<LyricsLine> FallbackFromTitle(string title)
        {
            var fallback = new List<LyricsLine>();
            if (string.IsNullOrWhiteSpace(title)) return fallback;
            var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var t = TimeSpan.Zero;
            foreach (var w in words)
            {
                var ll = new LyricsLine { Timestamp = t, Text = w };
                ll.FirstWord = w;
                ll.FirstWordNormalized = Regex.Replace(w.ToLowerInvariant(), "[^\\p{L}\\p{Nd}]+", "");
                fallback.Add(ll);
                t = t.Add(TimeSpan.FromSeconds(2));
            }
            return fallback;
        }

        // Estimate approximate timestamps by uniform spacing or energy (fallback)
        public async Task<List<TimeSpan>> EstimateTimestampsFromAudioAsync(string filePath, int chunkCount)
        {
            return await Task.Run(() =>
            {
                var result = new List<TimeSpan>();
                try
                {
                    if (string.IsNullOrWhiteSpace(filePath) || chunkCount <= 0 || !System.IO.File.Exists(filePath)) return result;
                    using var reader = new Mp3FileReader(filePath);
                    var total = reader.TotalTime.TotalSeconds;
                    for (int i = 0; i < chunkCount; i++) result.Add(TimeSpan.FromSeconds(total * i / Math.Max(1, chunkCount)));
                }
                catch { }
                return result;
            });
        }

        // Align lyrics lines to audio by mapping cumulative text length to cumulative audio energy.
        // Also mark lines as IsScream if overlapping high-amplitude windows.
        // Keep existing implementation minimal and robust.
        public async Task<List<LyricsLine>> AlignLyricsToAudioAsync(string filePath, List<string> lines)
        {
            return await Task.Run(() =>
            {
                var output = new List<LyricsLine>();
                if (string.IsNullOrWhiteSpace(filePath) || lines == null || lines.Count == 0 || !System.IO.File.Exists(filePath))
                    return output;

                try
                {
                    using var reader = new Mp3FileReader(filePath);
                    var sampleProvider = reader.ToSampleProvider();
                    int sampleRate = sampleProvider.WaveFormat.SampleRate;
                    int channels = sampleProvider.WaveFormat.Channels;

                    const int windowMs = 50; // finer resolution
                    int windowSamples = (sampleRate * windowMs) / 1000;
                    if (windowSamples <= 0) windowSamples = 1024;

                    var buffer = new float[windowSamples * channels];
                    var rmsList = new List<double>();
                    var peakList = new List<double>();

                    while (true)
                    {
                        int read = sampleProvider.Read(buffer, 0, buffer.Length);
                        if (read <= 0) break;

                        double sumSq = 0.0;
                        double maxAbs = 0.0;
                        int samplesCount = read / channels;
                        for (int n = 0; n < read; n++)
                        {
                            var s = buffer[n];
                            sumSq += s * s;
                            var abs = Math.Abs(s);
                            if (abs > maxAbs) maxAbs = abs;
                        }
                        double meanSq = samplesCount > 0 ? sumSq / (samplesCount * (double)channels) : 0.0;
                        var rms = Math.Sqrt(meanSq);
                        rmsList.Add(rms);
                        peakList.Add(maxAbs);

                        if (read < buffer.Length) break;
                    }

                    if (rmsList.Count == 0) return output;

                    double totalEnergy = 0.0;
                    foreach (var v in rmsList) totalEnergy += v;
                    if (totalEnergy <= 0) totalEnergy = 1e-9;

                    // cumulative energy fractions
                    var cumEnergy = new double[rmsList.Count];
                    double acc = 0.0;
                    for (int i = 0; i < rmsList.Count; i++)
                    {
                        acc += rmsList[i];
                        cumEnergy[i] = acc / totalEnergy;
                    }

                    // cumulative text length fractions
                    var textLens = new double[lines.Count];
                    double totalChars = 0.0;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        textLens[i] = Math.Max(1, lines[i].Length);
                        totalChars += textLens[i];
                    }
                    if (totalChars <= 0) totalChars = 1.0;
                    var cumText = new double[lines.Count];
                    double ta = 0.0;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        ta += textLens[i];
                        cumText[i] = ta / totalChars;
                    }

                    double totalDuration = reader.TotalTime.TotalSeconds;

                    // determine scream windows: peak high and rms relatively high
                    double maxPeak = 0.0;
                    foreach (var p in peakList) if (p > maxPeak) maxPeak = p;
                    double peakThreshold = Math.Max(0.35, maxPeak * 0.6);

                    var screamWindows = new bool[peakList.Count];
                    for (int i = 0; i < peakList.Count; i++)
                    {
                        screamWindows[i] = peakList[i] >= peakThreshold && rmsList[i] >= 0.4 * cumEnergy[Math.Min(i, cumEnergy.Length - 1)];
                    }

                    // map each line's cumulative text fraction to a window index using cumEnergy
                    int lastIndex = 0;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        double targetFrac = cumText[i];
                        int idxWindow = Array.BinarySearch(cumEnergy, targetFrac);
                        if (idxWindow < 0) idxWindow = ~idxWindow;
                        if (idxWindow < 0) idxWindow = 0;
                        if (idxWindow >= cumEnergy.Length) idxWindow = cumEnergy.Length - 1;

                        // ensure monotonic increasing
                        if (idxWindow < lastIndex) idxWindow = lastIndex;
                        lastIndex = idxWindow;

                        double seconds = idxWindow * windowMs / 1000.0;
                        if (seconds > totalDuration) seconds = Math.Max(0.0, totalDuration - 0.05);

                        // check scream presence in a small neighborhood around the window
                        int neighborhood = Math.Max(1, (int)(200.0 / windowMs)); // 200ms neighborhood
                        int start = Math.Max(0, idxWindow - neighborhood);
                        int end = Math.Min(peakList.Count - 1, idxWindow + neighborhood);
                        bool isScream = false;
                        for (int w = start; w <= end; w++) if (screamWindows[w]) { isScream = true; break; }

                        output.Add(new LyricsLine { Timestamp = TimeSpan.FromSeconds(seconds), Text = lines[i], IsScream = isScream });
                    }

                    // ensure monotonic non-decreasing timestamps
                    for (int i = 1; i < output.Count; i++)
                    {
                        if (output[i].Timestamp <= output[i - 1].Timestamp)
                        {
                            output[i].Timestamp = output[i - 1].Timestamp.Add(TimeSpan.FromMilliseconds(120));
                        }
                    }

                    return output;
                }
                catch
                {
                    return new List<LyricsLine>();
                }
            });
        }

        // Align each LyricsLine by finding the first word of the line inside word-level timestamps
        // provided by Whisper forced-alignment (list of WordInfo). This sets LyricsLine.Timestamp to
        // the Start time of the matched first word when found.
        public void AlignLinesByFirstWord(List<LyricsLine> lines, List<WhisperClient.WordInfo> words)
        {
            if (lines == null || lines.Count == 0 || words == null || words.Count == 0) return;

            static string Normalize(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return string.Empty;
                s = s.ToLowerInvariant();
                return Regex.Replace(s, "[^\\p{L}\\p{Nd}]+", "");
            }

            int searchFrom = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line.Text)) continue;

                var first = line.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(first)) continue;
                var key = Normalize(first);
                if (string.IsNullOrEmpty(key)) continue;

                bool matched = false;
                for (int w = searchFrom; w < words.Count; w++)
                {
                    var wn = Normalize(words[w].Word);
                    if (string.IsNullOrEmpty(wn)) continue;
                    if (wn == key || wn.Contains(key) || key.Contains(wn))
                    {
                        line.Timestamp = TimeSpan.FromSeconds(words[w].Start);
                        line.FirstWordStart = words[w].Start;
                        if (string.IsNullOrWhiteSpace(line.FirstWord)) line.FirstWord = first;
                        if (string.IsNullOrWhiteSpace(line.FirstWordNormalized)) line.FirstWordNormalized = key;
                        searchFrom = w + 1;
                        matched = true;
                        break;
                    }
                }

                if (matched) continue;

                for (int w = 0; w < words.Count; w++)
                {
                    var wn = Normalize(words[w].Word);
                    if (string.IsNullOrEmpty(wn)) continue;
                    if (wn == key || wn.Contains(key) || key.Contains(wn))
                    {
                        line.Timestamp = TimeSpan.FromSeconds(words[w].Start);
                        line.FirstWordStart = words[w].Start;
                        if (string.IsNullOrWhiteSpace(line.FirstWord)) line.FirstWord = first;
                        if (string.IsNullOrWhiteSpace(line.FirstWordNormalized)) line.FirstWordNormalized = key;
                        searchFrom = w + 1;
                        matched = true;
                        break;
                    }
                }
            }
        }

        // Simplified: compute alignment offset not implemented, return 0
        public Task<double> ComputeAlignmentOffsetMs(string filePath, List<string> lines)
        {
            return Task.FromResult(0.0);
        }
    }
}
