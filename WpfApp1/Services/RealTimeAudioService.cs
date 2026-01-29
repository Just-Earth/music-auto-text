using System;
using System.Numerics;
using System.Threading.Tasks;
using NAudio.Wave;
using System.Runtime.InteropServices;

namespace WpfApp1.Services
{
    // Lightweight real-time audio analyzer using WASAPI loopback and FFT
    public class RealTimeAudioService : IDisposable
    {
        private WasapiLoopbackCapture? _capture;
        private readonly int _fftSize;

        // raised on each analysis frame with normalized band levels 0..1 (low, mid, high)
        public event Action<double[], double>? OnBandsReady; // (bands, timestamp)

        public RealTimeAudioService(int fftSize = 1024)
        {
            _fftSize = Math.Max(256, fftSize);
        }

        public void Start()
        {
            if (_capture != null) return;
            try
            {
                // If running as Desktop app on Windows, ensure COM is initialized
                try { if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) System.Threading.Thread.CurrentThread.SetApartmentState(System.Threading.ApartmentState.MTA); } catch { }
                _capture = new WasapiLoopbackCapture();
                _capture.DataAvailable += Capture_DataAvailable;
                _capture.RecordingStopped += Capture_RecordingStopped;
                _capture.StartRecording();
            }
            catch
            {
                _capture = null;
            }
        }

        public void Stop()
        {
            try
            {
                _capture?.StopRecording();
            }
            catch { }
        }

        private void Capture_RecordingStopped(object? sender, StoppedEventArgs e)
        {
            try { _capture?.Dispose(); } catch { }
            _capture = null;
        }

        private float[] _leftover = Array.Empty<float>();

        private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
        {
            try
            {
                // convert bytes to floats (assume 32-bit float or 16-bit PCM)
                var wf = _capture!.WaveFormat;
                int bytesPerSample = wf.BitsPerSample / 8;
                int channels = wf.Channels;

                // convert to floats
                int frames = e.BytesRecorded / (bytesPerSample * channels);
                var floats = new float[frames * channels];
                if (wf.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
                {
                    Buffer.BlockCopy(e.Buffer, 0, floats, 0, e.BytesRecorded);
                }
                else if (wf.Encoding == WaveFormatEncoding.Pcm && bytesPerSample == 2)
                {
                    // 16-bit PCM
                    for (int i = 0; i < frames * channels; i++)
                    {
                        int idx = i * 2;
                        short s = (short)(e.Buffer[idx] | (e.Buffer[idx + 1] << 8));
                        floats[i] = s / 32768f;
                    }
                }
                else
                {
                    // unsupported format
                    return;
                }

                // convert to mono by averaging channels
                int monoLen = frames;
                var mono = new float[monoLen];
                for (int f = 0; f < frames; f++)
                {
                    float acc = 0;
                    for (int c = 0; c < channels; c++) acc += floats[f * channels + c];
                    mono[f] = acc / channels;
                }

                // prepend leftover
                if (_leftover.Length > 0)
                {
                    var combined = new float[_leftover.Length + mono.Length];
                    Array.Copy(_leftover, 0, combined, 0, _leftover.Length);
                    Array.Copy(mono, 0, combined, _leftover.Length, mono.Length);
                    mono = combined;
                }

                int pos = 0;
                while (pos + _fftSize <= mono.Length)
                {
                    var window = new Complex[_fftSize];
                    for (int i = 0; i < _fftSize; i++) window[i] = new Complex(mono[pos + i] * Hamming(i, _fftSize), 0);
                    FFT(window);

                    var mags = new double[_fftSize / 2];
                    for (int i = 0; i < mags.Length; i++) mags[i] = window[i].Magnitude;

                        // compute multi-band spectrum for a more detailed visualizer
                        double sampleRate = wf.SampleRate;
                        int bandCount = 32; // more bands -> more detailed bars
                        var bands = new double[bandCount];

                        // frequency range: 20Hz .. nyquist
                        double fMin = 20.0;
                        double fMax = sampleRate / 2.0;

                        // geometric spacing of bands for perceptual scaling
                        double logMin = Math.Log10(Math.Max(1.0, fMin));
                        double logMax = Math.Log10(Math.Max(logMin + 1e-6, fMax));

                        // sum mags into bands using log-spaced boundaries
                        for (int i = 0; i < bandCount; i++)
                        {
                            double frac0 = i / (double)bandCount;
                            double frac1 = (i + 1) / (double)bandCount;
                            double f0 = Math.Pow(10.0, logMin + (logMax - logMin) * frac0);
                            double f1 = Math.Pow(10.0, logMin + (logMax - logMin) * frac1);

                            int idx0 = (int)Math.Floor(f0 / (sampleRate / _fftSize));
                            int idx1 = (int)Math.Ceiling(f1 / (sampleRate / _fftSize));
                            idx0 = Math.Max(0, Math.Min(mags.Length - 1, idx0));
                            idx1 = Math.Max(0, Math.Min(mags.Length - 1, idx1));
                            if (idx1 < idx0) idx1 = idx0;

                            double sum = 0.0;
                            for (int b = idx0; b <= idx1; b++) sum += mags[b];
                            // normalize by number of bins in this band
                            int bins = Math.Max(1, idx1 - idx0 + 1);
                            bands[i] = sum / bins;
                        }

                        // normalize bands to 0..1 using percentile/peak scaling and tanh for smoothing
                        double peak = 1e-9;
                        for (int i = 0; i < bandCount; i++) if (bands[i] > peak) peak = bands[i];
                        for (int i = 0; i < bandCount; i++)
                        {
                            var v = bands[i] / peak;
                            // compress and map into 0..1
                            bands[i] = Math.Tanh(v * 2.5);
                            if (double.IsNaN(bands[i]) || double.IsInfinity(bands[i])) bands[i] = 0.0;
                        }
                    var ts = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                    OnBandsReady?.Invoke(bands, ts);

                    pos += _fftSize / 2; // 50% overlap
                }

                // leftover
                int rem = mono.Length - pos;
                if (rem > 0)
                {
                    _leftover = new float[rem];
                    Array.Copy(mono, pos, _leftover, 0, rem);
                }
                else _leftover = Array.Empty<float>();
            }
            catch { }
        }

        private static double Hamming(int n, int N) => 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * n / (N - 1));

        // simple in-place Cooley-Tukey FFT for Complex[] buffer
        private void FFT(Complex[] buffer)
        {
            int n = buffer.Length;
            int m = (int)Math.Log(n, 2);
            for (int i = 0; i < n; i++)
            {
                int j = ReverseBits(i, m);
                if (j > i) { var t = buffer[i]; buffer[i] = buffer[j]; buffer[j] = t; }
            }
            for (int s = 1; s <= m; s++)
            {
                int m2 = 1 << s;
                int m2h = m2 >> 1;
                double theta = -2.0 * Math.PI / m2;
                var wm = new Complex(Math.Cos(theta), Math.Sin(theta));
                for (int k = 0; k < n; k += m2)
                {
                    Complex w = Complex.One;
                    for (int j = 0; j < m2h; j++)
                    {
                        var t = w * buffer[k + j + m2h];
                        var u = buffer[k + j];
                        buffer[k + j] = u + t;
                        buffer[k + j + m2h] = u - t;
                        w *= wm;
                    }
                }
            }
        }

        private int ReverseBits(int x, int bits)
        {
            int y = 0;
            for (int i = 0; i < bits; i++) { y = (y << 1) | (x & 1); x >>= 1; }
            return y;
        }

        public void Dispose()
        {
            try { Stop(); } catch { }
            try { _capture?.Dispose(); } catch { }
        }
    }
}
