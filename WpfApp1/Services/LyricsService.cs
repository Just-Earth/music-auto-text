using System.Collections.Generic;
using System.Threading.Tasks;
using WpfApp1.Models;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace WpfApp1.Services
{
    public class LyricsService
    {
        private static readonly HttpClient _http = new HttpClient();

        // Try Genius API if token provided, otherwise fallback to lyrics.ovh and naive split
        public async Task<List<LyricsLine>> FetchLyricsByFilenameAsync(string filename)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(filename).Replace('_', ' ').Trim();
            string artist = "", title = name;
            var parts = name.Split('-', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                artist = parts[0].Trim();
                title = parts[1].Trim();
            }

            // Try Genius if token available
            var token = Environment.GetEnvironmentVariable("GENIUS_API_TOKEN");
            if (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.genius.com/search?q={Uri.EscapeDataString(artist + " " + title)}");
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    var res = await _http.SendAsync(req);
                    if (res.IsSuccessStatusCode)
                    {
                        var json = await res.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        var hits = doc.RootElement.GetProperty("response").GetProperty("hits");
                        if (hits.GetArrayLength() > 0)
                        {
                            var first = hits[0].GetProperty("result");
                            if (first.TryGetProperty("path", out var pathEl))
                            {
                                var path = pathEl.GetString();
                                if (!string.IsNullOrWhiteSpace(path))
                                {
                                    var pageUrl = "https://genius.com" + path;
                                    var html = await _http.GetStringAsync(pageUrl);
                                    var rawLyrics = ExtractLyricsFromGeniusHtml(html);
                                    if (!string.IsNullOrWhiteSpace(rawLyrics))
                                    {
                                        return ParseLyricsToLines(rawLyrics);
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // ignore and fallback
                }
            }

            // Try lyrics.ovh as a public API
            try
            {
                if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
                {
                    var url = $"https://api.lyrics.ovh/v1/{Uri.EscapeDataString(artist)}/{Uri.EscapeDataString(title)}";
                    var res = await _http.GetAsync(url);
                    if (res.IsSuccessStatusCode)
                    {
                        var json = await res.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("lyrics", out var lyricsEl))
                        {
                            var raw = lyricsEl.GetString() ?? string.Empty;
                            return ParseLyricsToLines(raw);
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            // fallback: split filename words
            await Task.Delay(50);
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

        private string ExtractLyricsFromGeniusHtml(string html)
        {
            // Genius uses <div data-lyrics-container="true"> blocks for lyrics; collect their inner text
            try
            {
                var matches = Regex.Matches(html, "<div[^>]*data-lyrics-container=\"true\"[^>]*>(.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                var sb = new System.Text.StringBuilder();
                foreach (Match m in matches)
                {
                    var inner = m.Groups[1].Value;
                    // remove tags
                    inner = Regex.Replace(inner, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
                    inner = Regex.Replace(inner, "<.*?>", string.Empty);
                    inner = HttpUtility.HtmlDecode(inner);
                    sb.AppendLine(inner.Trim());
                }
                return sb.ToString().Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private List<LyricsLine> ParseLyricsToLines(string raw)
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
    }
}
