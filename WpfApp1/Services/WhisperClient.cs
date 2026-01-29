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

        public async Task<List<Segment>> TranscribeAsync(string audioPath, string model = "small", string device = "cpu")
        {
            // Whisper integration disabled — return empty result to avoid calling external python.
            await Task.CompletedTask;
            return new List<Segment>();
        }

        public record WordInfo(double Start, double End, string Word);

        public async Task<List<WordInfo>> TranscribeWithAlignmentAsync(string audioPath, string textPath, string model = "small", string device = "cpu")
        {
            // Whisper forced-alignment disabled — return empty result.
            await Task.CompletedTask;
            return new List<WordInfo>();
        }
    }
}
