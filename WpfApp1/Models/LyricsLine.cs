namespace WpfApp1.Models
{
    public class LyricsLine
    {
        public TimeSpan Timestamp { get; set; }
        public string Text { get; set; } = string.Empty;
        public bool IsScream { get; set; } = false;
    }
}
