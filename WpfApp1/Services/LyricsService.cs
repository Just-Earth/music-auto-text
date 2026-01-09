using System.Collections.Generic;
using System.Threading.Tasks;
using WpfApp1.Models;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using NAudio.Wave;

namespace WpfApp1.Services
{
    public class LyricsService
    {
        private static readonly HttpClient _http = new HttpClient();

        // Fetch raw lyrics from lyrics.ovh by artist and title
        public async Task<string?> FetchLyricsRawByArtistTitleAsync(string artist, string title)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return null;
                var url = $"https://api.lyrics.ovh/v1/{Uri.EscapeDataString(artist)}/{Uri.EscapeDataString(title)}";
                var res = await _http.GetAsync(url);
                if (!res.IsSuccessStatusCode) return null;
                var json = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("lyrics", out var lyricsEl))
                {
                    return lyricsEl.GetString();
                }
            }
            catch
            {
                // ignore
            }
            return null;
        }

        // Original helper retained for internal parsing
        public List<LyricsLine> ParseLyricsToLines(string raw)
        {
            var lines = new List<LyricsLine>();
            var parts = raw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var t = TimeSpan.Zero;
            foreach (var p in parts)
            {
                // assign naive timestamps uniformly
                lines.Add(new LyricsLine { Timestamp = t, Text = p.Trim() });
                t = t.Add(TimeSpan.FromSeconds(3));
            }
            return lines;
        }

        // Fallback: split filename words
        public List<LyricsLine> FallbackFromTitle(string title)
        {
            var fallback = new List<LyricsLine>();
            var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var t = TimeSpan.Zero;
            foreach (var w in words)
            {
                fallback.Add(new LyricsLine { Timestamp = t, Text = w });
                t = t.Add(TimeSpan.FromSeconds(2));
            }
            return fallback;
        }

        // Estimate timestamps for given number of chunks by analyzing audio energy using NAudio.
        // Returns a list of TimeSpan timestamps (one per chunk) distributed preferentially over vocal/high-energy regions.
        public async Task<List<TimeSpan>> EstimateTimestampsFromAudioAsync(string filePath, int chunkCount)
        {
            return await Task.Run(() =>
            {
                var result = new List<TimeSpan>();
                if (string.IsNullOrWhiteSpace(filePath) || chunkCount <= 0 || !System.IO.File.Exists(filePath))
                {
                    return result;
                }

                try
                {
                    using var reader = new Mp3FileReader(filePath);
                    // convert to floats
                    var sampleProvider = reader.ToSampleProvider();
                    int sampleRate = sampleProvider.WaveFormat.SampleRate;
                    int channels = sampleProvider.WaveFormat.Channels;

                    // window size in ms for energy calculation
                    const int windowMs = 100;
                    int windowSamples = (sampleRate * windowMs) / 1000;
                    if (windowSamples <= 0) windowSamples = 1024;

                    var buffer = new float[windowSamples * channels];
                    var rmsList = new List<float>();

                    while (true)
                    {
                        int read = sampleProvider.Read(buffer, 0, buffer.Length);
                        if (read <= 0) break;

                        // compute RMS for this window (average across channels)
                        double sumSq = 0.0;
                        int samplesCount = read / channels;
                        for (int n = 0; n < read; n++)
                        {
                            var s = buffer[n];
                            sumSq += s * s;
                        }
                        double meanSq = samplesCount > 0 ? sumSq / (samplesCount * (double)channels) : 0.0;
                        var rms = (float)Math.Sqrt(meanSq);
                        rmsList.Add(rms);

                        // if partial final buffer, break
                        if (read < buffer.Length) break;
                    }

                    if (rmsList.Count == 0)
                    {
                        return result;
                    }

                    // find threshold based on max RMS
                    var maxRms = 0f;
                    foreach (var r in rmsList) if (r > maxRms) maxRms = r;
                    var threshold = Math.Max(0.005f, maxRms * 0.3f);

                    // mark voice/high-energy windows
                    var voiceFlags = new List<int>(rmsList.Count);
                    for (int i = 0; i < rmsList.Count; i++) voiceFlags.Add(rmsList[i] >= threshold ? 1 : 0);

                    // group contiguous voice windows into segments
                    var segments = new List<(int start, int length)>();
                    int idx = 0;
                    while (idx < voiceFlags.Count)
                    {
                        if (voiceFlags[idx] == 1)
                        {
                            int start = idx;
                            int len = 0;
                            while (idx < voiceFlags.Count && voiceFlags[idx] == 1)
                            {
                                len++; idx++;
                            }
                            segments.Add((start, len));
                        }
                        else idx++;
                    }

                    // if no voice segments, fallback to uniform timestamps across audio duration
                    var totalDuration = reader.TotalTime.TotalSeconds;
                    if (segments.Count == 0)
                    {
                        for (int i = 0; i < chunkCount; i++)
                        {
                            var t = TimeSpan.FromSeconds(Math.Max(0.0, (i * totalDuration) / Math.Max(1, chunkCount)));
                            result.Add(t);
                        }
                        return result;
                    }

                    // total voice windows count
                    int totalVoiceWindows = 0;
                    foreach (var s in segments) totalVoiceWindows += s.length;

                    // map chunk indices to positions inside voice windows
                    for (int i = 0; i < chunkCount; i++)
                    {
                        // target is fractional index among voice windows
                        double target = (i + 0.0) * totalVoiceWindows / chunkCount;
                        int cum = 0;
                        TimeSpan timestamp = TimeSpan.Zero;
                        bool assigned = false;
                        foreach (var seg in segments)
                        {
                            if (target < cum + seg.length)
                            {
                                int offsetInSeg = (int)Math.Floor(target - cum);
                                int windowIndex = seg.start + Math.Max(0, Math.Min(seg.length - 1, offsetInSeg));
                                double seconds = (windowIndex * windowMs) / 1000.0;
                                timestamp = TimeSpan.FromSeconds(seconds);
                                assigned = true;
                                break;
                            }
                            cum += seg.length;
                        }

                        if (!assigned)
                        {
                            // place at end of last segment
                            var last = segments[segments.Count - 1];
                            double seconds = ((last.start + last.length - 1) * windowMs) / 1000.0;
                            timestamp = TimeSpan.FromSeconds(seconds);
                        }

                        // clamp to total duration
                        if (timestamp.TotalSeconds > totalDuration) timestamp = TimeSpan.FromSeconds(totalDuration - 0.1);
                        if (timestamp < TimeSpan.Zero) timestamp = TimeSpan.Zero;

                        result.Add(timestamp);
                    }

                    // ensure timestamps are sorted and unique increasing
                    result.Sort();
                    for (int i = 1; i < result.Count; i++)
                    {
                        if (result[i] <= result[i - 1])
                        {
                            result[i] = result[i - 1].Add(TimeSpan.FromMilliseconds(200));
                        }
                    }

                    return result;
                }
                catch
                {
                    return new List<TimeSpan>();
                }
            });
        }

        // Align lyrics lines to audio by mapping cumulative text length to cumulative audio energy.
        // Also mark lines as IsScream if overlapping high-amplitude windows.
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

        // Compute alignment offset (ms) between lines and audio using DTW between audio energy and synthetic text impulses
        public async Task<double> ComputeAlignmentOffsetMs(string filePath, List<string> lines)
        {
            return await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(filePath) || lines == null || lines.Count == 0 || !System.IO.File.Exists(filePath))
                    return 0.0;

                try
                {
                    using var reader = new Mp3FileReader(filePath);
                    var sampleProvider = reader.ToSampleProvider();
                    int sampleRate = sampleProvider.WaveFormat.SampleRate;
                    int channels = sampleProvider.WaveFormat.Channels;

                    const int windowMs = 50; // use 50ms windows for DTW
                    int windowSamples = (sampleRate * windowMs) / 1000;
                    if (windowSamples <= 0) windowSamples = 1024;

                    var buffer = new float[windowSamples * channels];
                    var rmsList = new List<double>();

                    while (true)
                    {
                        int read = sampleProvider.Read(buffer, 0, buffer.Length);
                        if (read <= 0) break;

                        double sumSq = 0.0;
                        int samplesCount = read / channels;
                        for (int n = 0; n < read; n++)
                        {
                            var s = buffer[n];
                            sumSq += s * s;
                        }
                        double meanSq = samplesCount > 0 ? sumSq / (samplesCount * (double)channels) : 0.0;
                        var rms = Math.Sqrt(meanSq);
                        rmsList.Add(rms);

                        if (read < buffer.Length) break;
                    }

                    int N = rmsList.Count;
                    if (N == 0) return 0.0;

                    // normalize audio energy to 0..1
                    double maxR = 0; foreach (var r in rmsList) if (r > maxR) maxR = r;
                    if (maxR <= 0) maxR = 1e-9;
                    var audio = new double[N];
                    for (int i = 0; i < N; i++) audio[i] = rmsList[i] / maxR;

                    // create synthetic text impulse sequence S of length N (one impulse per line at estimated position)
                    var S = new double[N];
                    double totalChars = 0; foreach (var ln in lines) totalChars += Math.Max(1, ln.Length);
                    if (totalChars <= 0) totalChars = 1.0;
                    double acc = 0.0;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        acc += Math.Max(1, lines[i].Length);
                        int idx = (int)Math.Round((acc / totalChars) * (N - 1));
                        if (idx < 0) idx = 0; if (idx >= N) idx = N - 1;
                        S[idx] = 1.0; // impulse
                    }

                    // DTW between audio and S
                    int M = N;
                    var cost = new double[M + 1, N + 1];
                    const double INF = 1e12;
                    for (int i = 0; i <= M; i++) for (int j = 0; j <= N; j++) cost[i, j] = INF;
                    cost[0, 0] = 0.0;

                    for (int i = 1; i <= M; i++)
                    {
                        for (int j = 1; j <= N; j++)
                        {
                            double d = Math.Abs(audio[j - 1] - S[i - 1]);
                            double c = d + Math.Min(cost[i - 1, j], Math.Min(cost[i, j - 1], cost[i - 1, j - 1]));
                            cost[i, j] = c;
                        }
                    }

                    // backtrack path
                    int ii = M, jj = N;
                    var pathAudioIdx = new List<int>();
                    var pathTextIdx = new List<int>();
                    while (ii > 0 && jj > 0)
                    {
                        pathTextIdx.Add(ii - 1);
                        pathAudioIdx.Add(jj - 1);

                        double c = cost[ii, jj];
                        double c1 = cost[ii - 1, jj - 1];
                        double c2 = cost[ii - 1, jj];
                        double c3 = cost[ii, jj - 1];
                        if (c1 <= c2 && c1 <= c3)
                        {
                            ii--; jj--;
                        }
                        else if (c2 <= c3)
                        {
                            ii--;
                        }
                        else
                        {
                            jj--;
                        }
                    }

                    pathAudioIdx.Reverse(); pathTextIdx.Reverse();

                    // for each text impulse index, find matching audio indices from path
                    var matches = new List<(int textIdx, int audioIdx)>();
                    for (int p = 0; p < pathTextIdx.Count; p++)
                    {
                        int tIdx = pathTextIdx[p];
                        int aIdx = pathAudioIdx[p];
                        if (S[tIdx] > 0.5)
                        {
                            matches.Add((tIdx, aIdx));
                        }
                    }

                    if (matches.Count == 0)
                    {
                        // fallback to cross-correlation simple method
                        // compute best shift where impulses overlap audio peaks
                        int bestShift = 0; int bestScore = -1;
                        int maxShift = Math.Max(1, 3000 / windowMs);
                        for (int shift = -maxShift; shift <= maxShift; shift++)
                        {
                            int score = 0;
                            for (int t = 0; t < N; t++)
                            {
                                int sIdx = t - shift;
                                if (sIdx < 0 || sIdx >= N) continue;
                                if (S[sIdx] >= 0.5 && audio[t] > 0.3) score++;
                            }
                            if (score > bestScore) { bestScore = score; bestShift = shift; }
                        }

                        return bestShift * windowMs;
                    }

                    double sumDiff = 0; int count = 0;
                    foreach (var m in matches)
                    {
                        // expected position for textIdx is roughly textIdx (since S indices correspond to N)
                        double expected = m.textIdx;
                        double actual = m.audioIdx;
                        sumDiff += (actual - expected);
                        count++;
                    }
                    if (count == 0) return 0.0;
                    double avg = sumDiff / count;
                    double offsetMs = avg * windowMs;
                    return offsetMs;
                }
                catch
                {
                    return 0.0;
                }
            });
        }
    }
}
