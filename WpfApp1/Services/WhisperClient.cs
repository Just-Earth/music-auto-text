using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace WpfApp1.Services
{
    public partial class WhisperClient
    {
        private readonly string _scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "whisper_transcribe.py");

        public WhisperClient()
        {
        }

        public record Segment(double Start, double End, string Text);

        public async Task<List<Segment>> TranscribeAsync(string audioPath, string model = "small")
        {
            var list = new List<Segment>();
            if (!File.Exists(_scriptPath)) throw new FileNotFoundException("whisper_transcribe.py not found", _scriptPath);
            if (!File.Exists(audioPath)) throw new FileNotFoundException("audio not found", audioPath);

            var psi = new ProcessStartInfo()
            {
                FileName = "python",
                Arguments = $"\"{_scriptPath}\" -i \"{audioPath}\" -m {model}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };

            using var p = Process.Start(psi);
            if (p == null) throw new Exception("Failed to start python process");
            string outStr = await p.StandardOutput.ReadToEndAsync();
            string err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(err))
            {
                // try parse JSON error
                try
                {
                    var j = JsonDocument.Parse(err);
                    if (j.RootElement.TryGetProperty("error", out var _))
                    {
                        throw new Exception("whisper script error: " + err);
                    }
                }
                catch { /* ignore parse */ }
            }

            if (string.IsNullOrWhiteSpace(outStr)) return list;
            try
            {
                var doc = JsonDocument.Parse(outStr);
                if (doc.RootElement.TryGetProperty("segments", out var segs))
                {
                    foreach (var s in segs.EnumerateArray())
                    {
                        var st = s.GetProperty("start").GetDouble();
                        var en = s.GetProperty("end").GetDouble();
                        var t = s.GetProperty("text").GetString() ?? string.Empty;
                        list.Add(new Segment(st, en, t));
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Failed to parse whisper output: " + ex.Message + "\nOutput:\n" + outStr);
            }

            return list;
        }

        public record WordInfo(double Start, double End, string Word);

        public async Task<List<WordInfo>> TranscribeWithAlignmentAsync(string audioPath, string textPath, string model = "small")
        {
            var list = new List<WordInfo>();
            var script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "whisper_forced_align.py");
            if (!File.Exists(script)) throw new FileNotFoundException("whisper_forced_align.py not found", script);
            if (!File.Exists(audioPath)) throw new FileNotFoundException("audio not found", audioPath);
            if (!File.Exists(textPath)) throw new FileNotFoundException("text not found", textPath);

            var psi = new ProcessStartInfo()
            {
                FileName = "python",
                Arguments = $"\"{script}\" -a \"{audioPath}\" -t \"{textPath}\" -m {model}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };

            using var p = Process.Start(psi);
            if (p == null) throw new Exception("Failed to start python process");
            string outStr = await p.StandardOutput.ReadToEndAsync();
            string err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(err))
            {
                try
                {
                    var j = JsonDocument.Parse(err);
                    if (j.RootElement.TryGetProperty("error", out var _))
                    {
                        throw new Exception("whisper align script error: " + err);
                    }
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(outStr)) return list;
            try
            {
                var doc = JsonDocument.Parse(outStr);
                if (doc.RootElement.TryGetProperty("words", out var words))
                {
                    foreach (var w in words.EnumerateArray())
                    {
                        var st = w.GetProperty("start").GetDouble();
                        var en = w.GetProperty("end").GetDouble();
                        var t = w.GetProperty("word").GetString() ?? string.Empty;
                        list.Add(new WordInfo(st, en, t));
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Failed to parse whisper align output: " + ex.Message + "\nOutput:\n" + outStr);
            }

            return list;
        }
    }
}
