using System;
using System.Net.Http;
using System.Windows.Automation;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;

namespace WpfApp1.Services
{
    public class MusicInfo
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string CoverUrl { get; set; } = string.Empty;
    }

        

        

        
    
        
    public class MusicInfoService
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left; public int top; public int right; public int bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize; public int flags; public IntPtr hwndActive; public IntPtr hwndFocus; public IntPtr hwndCapture; public IntPtr hwndMenuOwner; public IntPtr hwndMoveSize; public IntPtr hwndCaret; public RECT rcCaret;
        }

        private const int CCHCLASSNAME = 256;
        private const int GW_HWNDNEXT = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public event Action<string, string, string, string?>? OnMusicChanged;

        private System.Threading.Timer? _checkTimer;
        private string _lastTrack = string.Empty;
        // remember last combined key (title + cover) so cover-only updates still trigger
        private string _lastTrackKey = string.Empty;

        public void StartListening() => _checkTimer = new System.Threading.Timer(CheckMusic, null, 0, 1000);
        public void StopListening() { _checkTimer?.Dispose(); _checkTimer = null; }

        private void CheckMusic(object? state)
        {
            try
            {
                // detect music via window/process/UIA
                MusicInfo info = GetCurrentMusicInfo();

                if (info != null && (!string.IsNullOrEmpty(info.Title) || !string.IsNullOrEmpty(info.CoverUrl)))
                {
                    var key = (info.Title ?? string.Empty) + "|" + (info.CoverUrl ?? string.Empty);
                    if (key != _lastTrackKey)
                    {
                        _lastTrackKey = key;
                        _lastTrack = info.Title ?? string.Empty;
                        OnMusicChanged?.Invoke(info.Title ?? string.Empty, info.Artist ?? string.Empty, info.Album ?? string.Empty, info.CoverUrl);
                    }
                }
                else
                {
                    // diagnostic: dump visible window titles to temp file for debugging when nothing detected
                    try
                    {
                        var titles = GetAllWindowTitles();
                        var sb = new StringBuilder();
                        sb.AppendLine($"{DateTime.Now:o} - no music detected");
                        foreach (var t in titles)
                        {
                            sb.AppendLine(t);
                        }
                        sb.AppendLine("----");
                        var tf = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "musicinfo_windows.txt");
                        System.IO.File.AppendAllText(tf, sb.ToString());
                    }
                    catch { }
                }
            }
            catch { }
        }

        private System.Collections.Generic.List<string> GetAllWindowTitles()
        {
            var list = new System.Collections.Generic.List<string>();
            try
            {
                EnumWindows((hWnd, lParam) =>
                {
                    try
                    {
                        if (!IsWindowVisible(hWnd)) return true;
                        var sb = new StringBuilder(512);
                        GetWindowText(hWnd, sb, sb.Capacity);
                        var t = sb.ToString();
                        if (!string.IsNullOrWhiteSpace(t)) list.Add(t);
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return list;
        }

        public MusicInfo GetCurrentMusicInfo()
        {
            var s = GetSpotifyInfo(); if (!string.IsNullOrEmpty(s.Title)) return s;
            var y = GetYandexMusicInfo(); if (!string.IsNullOrEmpty(y.Title)) return y;
            var o = GetOtherMusicInfo(); if (!string.IsNullOrEmpty(o.Title)) return o;
            return new MusicInfo();
        }

        private string? TryExtractCoverUrlFromAutomation(AutomationElement? root)
        {
            try
            {
                if (root == null) return null;

                // search for Image control types first
                var imgCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image);
                var imgs = root.FindAll(TreeScope.Descendants, imgCond);
                for (int i = 0; i < imgs.Count; i++)
                {
                    try
                    {
                        var el = imgs[i];
                        var name = (el.Current.Name ?? string.Empty).Trim();
                        if (!string.IsNullOrWhiteSpace(name) && (name.StartsWith("http") || name.Contains(".jpg") || name.Contains(".png"))) return name;
                        // sometimes HelpText contains the url
                        var help = el.GetCurrentPropertyValue(AutomationElement.HelpTextProperty) as string;
                        if (!string.IsNullOrWhiteSpace(help) && (help.StartsWith("http") || help.Contains(".jpg") || help.Contains(".png"))) return help;
                    }
                    catch { }
                }

                // fallback: search any element whose Name/HelpText looks like an URL to an image
                var anyCond = new PropertyCondition(AutomationElement.IsOffscreenProperty, false);
                var all = root.FindAll(TreeScope.Descendants, anyCond);
                for (int i = 0; i < all.Count; i++)
                {
                    try
                    {
                        var el = all[i];
                        var name = (el.Current.Name ?? string.Empty).Trim();
                        if (!string.IsNullOrWhiteSpace(name) && (name.StartsWith("http") || name.Contains(".jpg") || name.Contains(".png"))) return name;
                        var help = el.GetCurrentPropertyValue(AutomationElement.HelpTextProperty) as string;
                        if (!string.IsNullOrWhiteSpace(help) && (help.StartsWith("http") || help.Contains(".jpg") || help.Contains(".png"))) return help;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private MusicInfo GetSpotifyInfo()
        {
            var info = new MusicInfo();
            try
            {
                IntPtr hwnd = FindWindow("Chrome_WidgetWin_0", null);
                if (hwnd != IntPtr.Zero)
                {
                    StringBuilder windowText = new StringBuilder(256);
                    GetWindowText(hwnd, windowText, 256);
                    string title = windowText.ToString();
                    if (title.Contains("Spotify") && !title.Contains("Spotify Premium"))
                    {
                        string cleanTitle = title.Replace(" - Spotify", "").Trim();
                        if (cleanTitle.Contains(" - "))
                        {
                            var parts = cleanTitle.Split(new[] { " - " }, StringSplitOptions.None);
                            if (parts.Length >= 2)
                            {
                                if (parts[0].Contains(" ") && !parts[1].Contains(" ")) { info.Artist = parts[0]; info.Title = parts[1]; }
                                else { info.Artist = parts[1]; info.Title = parts[0]; }
                            }
                        }
                        else info.Title = cleanTitle;
                        info.Source = "Spotify";
                        try
                        {
                            // attempt to extract cover URL from UI Automation for the Spotify window (desktop or web)
                            var root = AutomationElement.FromHandle(hwnd);
                            var cover = TryExtractCoverUrlFromAutomation(root);
                            if (!string.IsNullOrWhiteSpace(cover)) info.CoverUrl = cover;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return info;
        }

        private MusicInfo GetYandexMusicInfo()
        {
            var info = new MusicInfo();
            try
            {
                // First: try to detect native Yandex Music process windows by checking running processes
                try
                {
                    var procs = System.Diagnostics.Process.GetProcesses();
                    foreach (var p in procs)
                    {
                        try
                        {
                            var pname = (p.ProcessName ?? string.Empty).ToLowerInvariant();
                            // match likely Yandex Music app names
                            if (!(pname.Contains("yandex") || pname.Contains("yamusic") || pname.Contains("yandexmusic") || pname.Contains("ya.music"))) continue;

                            // enumerate top-level windows and find those belonging to this process
                            IntPtr matched = IntPtr.Zero;
                            EnumWindows((hWnd, lParam) =>
                            {
                                try
                                {
                                    if (!IsWindowVisible(hWnd)) return true;
                                    GetWindowThreadProcessId(hWnd, out uint pid);
                                    if (pid != (uint)p.Id) return true;
                                    var sb = new StringBuilder(512);
                                    GetWindowText(hWnd, sb, sb.Capacity);
                                    var title = sb.ToString()?.Trim() ?? string.Empty;
                                    if (string.IsNullOrWhiteSpace(title)) return true;
                                    matched = hWnd;
                                    return false; // stop
                                }
                                catch { }
                                return true;
                            }, IntPtr.Zero);

                            if (matched != IntPtr.Zero)
                            {
                                // Try to read UI text via UI Automation � useful for native apps that don't include track in window title
                                try
                                {
                                    // lazy: use System.Windows.Automation
                                    var root = System.Windows.Automation.AutomationElement.FromHandle(matched);
                                    if (root != null)
                                    {
                                        var cond = new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Text);
                                        var texts = root.FindAll(System.Windows.Automation.TreeScope.Descendants, cond);
                                        var list = new System.Collections.Generic.List<string>();
                                        for (int ti = 0; ti < texts.Count; ti++)
                                        {
                                            try
                                            {
                                                var name = texts[ti].Current.Name ?? string.Empty;
                                                name = name.Trim();
                                                if (string.IsNullOrWhiteSpace(name)) continue;
                                                // filter out obvious UI chrome
                                                if (name.IndexOf("������", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                                                if (name.Length > 200) continue;
                                                list.Add(name);
                                            }
                                            catch { }
                                        }

                                        // Heuristic: find adjacent pair that looks like Title + Artist or Artist + Title
                                        for (int i = 0; i < list.Count - 1; i++)
                                        {
                                            var a = list[i];
                                            var b = list[i + 1];
                                            if (a.Length >= 2 && b.Length >= 2)
                                            {
                                                // pick longer as title
                                                if (a.Length >= b.Length)
                                                {
                                                    info.Title = a;
                                                    info.Artist = b;
                                                }
                                                else
                                                {
                                                    info.Title = b;
                                                    info.Artist = a;
                                                }
                                                info.Source = "Yandex Music";
                                                return info;
                                            }
                                        }
                                        // fallback: if any text exists, use the longest as title
                                        if (list.Count > 0)
                                        {
                                            var longest = list.OrderByDescending(s => s.Length).FirstOrDefault();
                                            info.Title = longest ?? string.Empty;
                                            info.Source = "Yandex Music";
                                            // also attempt to find cover image via UIA
                                            try
                                            {
                                                var cover = TryExtractCoverUrlFromAutomation(root);
                                                if (!string.IsNullOrWhiteSpace(cover)) info.CoverUrl = cover;
                                            }
                                            catch { }
                                            return info;
                                        }
                                    }
                                }
                                catch { }

                                // if UIA failed, fall back to window title parsing
                                var sb = new StringBuilder(512);
                                GetWindowText(matched, sb, sb.Capacity);
                                var title = sb.ToString();
                                var cleanTitle = title.Replace(" � ������.������", string.Empty).Replace(" � Yandex.Music", string.Empty).Trim();
                                var sep = cleanTitle.Contains(" - ") ? " - " : (cleanTitle.Contains(" � ") ? " � " : " - ");
                                if (cleanTitle.Contains(sep))
                                {
                                    var parts = cleanTitle.Split(new[] { sep }, StringSplitOptions.None);
                                    if (parts.Length >= 2)
                                    {
                                        info.Artist = parts[0].Trim();
                                        info.Title = parts[1].Trim();
                                    }
                                    else info.Title = cleanTitle;
                                }
                                else info.Title = cleanTitle;
                                info.Source = "Yandex Music";
                                return info;
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                // If process-based detection failed, fall back to scanning all windows for Yandex markers
                IntPtr found = IntPtr.Zero;
                EnumWindows((hWnd, lParam) =>
                {
                    try
                    {
                        if (!IsWindowVisible(hWnd)) return true;
                        var sb = new StringBuilder(512);
                        GetWindowText(hWnd, sb, sb.Capacity);
                        var title = sb.ToString();
                        if (string.IsNullOrWhiteSpace(title)) return true;
                        if (title.Contains("������.������") || title.Contains("Yandex.Music") || title.Contains("music.yandex.ru") )
                        {
                            found = hWnd;
                            return false;
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);

                if (found != IntPtr.Zero)
                {
                    var sb = new StringBuilder(512);
                    GetWindowText(found, sb, sb.Capacity);
                    var title = sb.ToString();
                    var cleanTitle = title.Replace(" � ������.������", string.Empty).Replace(" � Yandex.Music", string.Empty).Trim();
                    var sep = cleanTitle.Contains(" - ") ? " - " : (cleanTitle.Contains(" � ") ? " � " : " - ");
                    if (cleanTitle.Contains(sep))
                    {
                        var parts = cleanTitle.Split(new[] { sep }, StringSplitOptions.None);
                        if (parts.Length >= 2)
                        {
                            info.Artist = parts[0].Trim();
                            info.Title = parts[1].Trim();
                        }
                        else info.Title = cleanTitle;
                    }
                    else info.Title = cleanTitle;
                    info.Source = "Yandex Music";
                }
                else
                {
                    // fallback: check processes for known browser names and read MainWindowTitle
                    try
                    {
                        var procs = System.Diagnostics.Process.GetProcesses();
                    foreach (var p in procs)
                        {
                            try
                            {
                                var name = (p.ProcessName ?? "").ToLowerInvariant();
                                // focus on browser-like processes
                                if (!(name.Contains("yandex") || name.Contains("browser") || name.Contains("chrome") || name.Contains("msedge") || name.Contains("opera") || name.Contains("firefox"))) continue;
                                var title = p.MainWindowTitle ?? string.Empty;
                                if (string.IsNullOrWhiteSpace(title)) continue;

                                // if title explicitly mentions Yandex � prefer it
                                if (title.Contains("������.������") || title.Contains("Yandex.Music") || title.Contains("music.yandex.ru") || title.Contains("������"))
                                {
                                    var cleanTitle = title.Replace(" � ������.������", "").Replace(" � Yandex.Music", "").Trim();
                                    var sep = cleanTitle.Contains(" - ") ? " - " : (cleanTitle.Contains(" � ") ? " � " : " - ");
                                    if (cleanTitle.Contains(sep))
                                    {
                                        var parts = cleanTitle.Split(new[] { sep }, StringSplitOptions.None);
                                        if (parts.Length >= 2)
                                        {
                                            info.Artist = parts[0].Trim();
                                            info.Title = parts[1].Trim();
                                        }
                                    }
                                    else info.Title = cleanTitle;
                                    info.Source = "Yandex Music";
                                    break;
                                }

                                // fallback: if browser window title looks like "Artist - Title" or "Title - Artist", accept it as music
                                if (title.Contains(" - ") || title.Contains(" � "))
                                {
                                    var cleanTitle = title.Replace(" � ������.������", "").Replace(" � Yandex.Music", "").Trim();
                                    var sep = cleanTitle.Contains(" - ") ? " - " : " � ";
                                    var parts = cleanTitle.Split(new[] { sep }, StringSplitOptions.None);
                                    if (parts.Length >= 2)
                                    {
                                        info.Artist = parts[0].Trim();
                                        info.Title = parts[1].Trim();
                                    }
                                    else info.Title = cleanTitle;
                                    info.Source = "Yandex Music";
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return info;
        }

        private MusicInfo GetOtherMusicInfo()
        {
            var info = new MusicInfo();
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                StringBuilder windowText = new StringBuilder(256);
                GetWindowText(hwnd, windowText, 256);
                string title = windowText.ToString();
                string[] musicPlayers = { "Apple Music", "Deezer", "VK ������", "YouTube Music" };
                foreach (var player in musicPlayers)
                {
                    if (title.Contains(player))
                    {
                        string cleanTitle = title.Split(new[] { " - " + player, " � " + player }, StringSplitOptions.None)[0];
                        if (cleanTitle.Contains(" - "))
                        {
                            var parts = cleanTitle.Split(new[] { " - " }, StringSplitOptions.None);
                            if (parts.Length >= 2) { info.Artist = parts[0]; info.Title = parts[1]; }
                        }
                        else info.Title = cleanTitle;
                        info.Source = player;
                        break;
                    }
                }
            }
            catch { }
            return info;
        }

        public async Task<string?> GetAlbumCover(string artist, string title)
        {
            try
            {
                // First: search MusicBrainz for a recording matching artist+title, get a release MBID, then query Cover Art Archive
                try
                {
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("MusicAutoText/1.0 (https://example.com)");

                    var q = $"recording:\"{title}\" AND artist:\"{artist}\"";
                    var mbUrl = $"https://musicbrainz.org/ws/2/recording/?query={Uri.EscapeDataString(q)}&fmt=json&limit=1";
                    var mbResp = await client.GetStringAsync(mbUrl);
                    using var mbDoc = JsonDocument.Parse(mbResp);
                    if (mbDoc.RootElement.TryGetProperty("recordings", out var recordings) && recordings.ValueKind == JsonValueKind.Array && recordings.GetArrayLength() > 0)
                    {
                        var firstRec = recordings[0];
                        // try to get a release id from recording -> releases[0].id
                        if (firstRec.TryGetProperty("releases", out var releases) && releases.ValueKind == JsonValueKind.Array && releases.GetArrayLength() > 0)
                        {
                            var rel = releases[0];
                            if (rel.TryGetProperty("id", out var relIdEl) && relIdEl.ValueKind == JsonValueKind.String)
                            {
                                var relId = relIdEl.GetString();
                                if (!string.IsNullOrWhiteSpace(relId))
                                {
                                    // Query Cover Art Archive JSON for the release
                                    var caaUrl = $"https://coverartarchive.org/release/{relId}";
                                    try
                                    {
                                        var caaJson = await client.GetStringAsync(caaUrl);
                                        using var caaDoc = JsonDocument.Parse(caaJson);
                                        if (caaDoc.RootElement.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
                                        {
                                            // prefer front image
                                            for (int i = 0; i < images.GetArrayLength(); i++)
                                            {
                                                var img = images[i];
                                                if (img.TryGetProperty("front", out var frontEl) && frontEl.ValueKind == JsonValueKind.True)
                                                {
                                                    if (img.TryGetProperty("image", out var imageUrlEl) && imageUrlEl.ValueKind == JsonValueKind.String)
                                                    {
                                                        var imageUrl = imageUrlEl.GetString();
                                                        if (!string.IsNullOrWhiteSpace(imageUrl)) return imageUrl;
                                                    }
                                                }
                                            }

                                            // fallback: first image
                                            var firstImg = images[0];
                                            if (firstImg.TryGetProperty("image", out var firstImageUrlEl) && firstImageUrlEl.ValueKind == JsonValueKind.String)
                                            {
                                                var imageUrl = firstImageUrlEl.GetString();
                                                if (!string.IsNullOrWhiteSpace(imageUrl)) return imageUrl;
                                            }
                                        }
                                    }
                                    catch { /* no cover art for this release */ }
                                }
                            }
                        }
                    }
                }
                catch { /* continue to fallback */ }

                // Fallback: iTunes Search API (no API key)
                try
                {
                    var q2 = Uri.EscapeDataString($"{artist} {title}");
                    var itunesUrl = $"https://itunes.apple.com/search?term={q2}&entity=song&limit=1";
                    using var client2 = new HttpClient();
                    var text = await client2.GetStringAsync(itunesUrl);
                    using var doc2 = JsonDocument.Parse(text);
                    if (doc2.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0)
                    {
                        var first = results[0];
                        if (first.TryGetProperty("artworkUrl100", out var art) && art.ValueKind == JsonValueKind.String)
                        {
                            var artUrl = art.GetString();
                            if (!string.IsNullOrWhiteSpace(artUrl))
                            {
                                return artUrl.Replace("100x100bb.jpg", "600x600bb.jpg").Replace("100x100-75", "600x600-75");
                            }
                        }
                    }
                }
                catch { }

                return null;
            }
            catch { return null; }
        }
    }
}
