using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.Wave;
using WpfApp1.Models;

namespace WpfApp1.Services
{
    // Simple multilayer perceptron with one hidden layer, trained with SGD
    public class NeuralAligner
    {
        private readonly string _modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "aligner_model.json");
        private readonly string _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "training.jsonl");

        public NeuralAligner()
        {
            Directory.CreateDirectory(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data"));
        }

        public record TrainingSample
        {
            public double[] Features { get; set; } = Array.Empty<double>();
            public int Label { get; set; }
        }

        public record SavedModel
        {
            public int InputSize { get; set; }
            public int HiddenSize { get; set; }
            public double[][] Weights1 { get; set; } = Array.Empty<double[]>();
            public double[] Bias1 { get; set; } = Array.Empty<double>();
            public double[] Weights2 { get; set; } = Array.Empty<double>();
            public double Bias2 { get; set; }
            // feature normalization parameters saved with the model
            public double[] Mean { get; set; } = Array.Empty<double>();
            public double[] Std { get; set; } = Array.Empty<double>();
        }

        // Add a labeled training sample to DB (appends JSON line)
        public void AddTrainingSample(double[] features, int label)
        {
            var s = new TrainingSample { Features = features, Label = label };
            var line = JsonSerializer.Serialize(s);
            File.AppendAllText(_dbPath, line + Environment.NewLine);
        }

        public List<TrainingSample> LoadAllTrainingSamples()
        {
            var outList = new List<TrainingSample>();
            if (!File.Exists(_dbPath)) return outList;
            foreach (var line in File.ReadAllLines(_dbPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var t = JsonSerializer.Deserialize<TrainingSample>(line);
                    if (t != null) outList.Add(t);
                }
                catch { }
            }
            return outList;
        }

        // Train a small MLP on stored DB and save model
        public bool TrainFromDb(int hiddenSize = 16, int epochs = 20, double lr = 0.01)
        {
            var samples = LoadAllTrainingSamples();
            if (samples == null || samples.Count == 0) return false;
            int inputSize = samples[0].Features.Length;
            foreach (var s in samples) if (s.Features.Length != inputSize) return false; // inconsistent
            var rnd = new Random(0);

            // compute feature normalization (mean/std)
            var mean = new double[inputSize];
            var std = new double[inputSize];
            for (int j = 0; j < inputSize; j++)
            {
                double acc = 0;
                for (int i = 0; i < samples.Count; i++) acc += samples[i].Features[j];
                mean[j] = acc / samples.Count;
            }
            for (int j = 0; j < inputSize; j++)
            {
                double acc2 = 0;
                for (int i = 0; i < samples.Count; i++)
                {
                    var d = samples[i].Features[j] - mean[j];
                    acc2 += d * d;
                }
                std[j] = Math.Sqrt(Math.Max(1e-9, acc2 / samples.Count));
            }

            // normalize samples in-place for training
            var normFeatures = new double[samples.Count][];
            var labels = new int[samples.Count];
            for (int i = 0; i < samples.Count; i++)
            {
                var f = samples[i].Features;
                var nf = new double[inputSize];
                for (int j = 0; j < inputSize; j++) nf[j] = (f[j] - mean[j]) / std[j];
                normFeatures[i] = nf;
                labels[i] = samples[i].Label > 0 ? 1 : 0;
            }
            // initialize weights
            var W1 = new double[hiddenSize][];
            for (int i = 0; i < hiddenSize; i++)
            {
                W1[i] = new double[inputSize];
                for (int j = 0; j < inputSize; j++) W1[i][j] = (rnd.NextDouble() - 0.5) * 0.2;
            }
            var b1 = new double[hiddenSize];
            var W2 = new double[hiddenSize];
            for (int i = 0; i < hiddenSize; i++) W2[i] = (rnd.NextDouble() - 0.5) * 0.2;
            double b2 = 0.0;

            // simple SGD
            for (int epoch = 0; epoch < epochs; epoch++)
            {
                // shuffle
                var order = Enumerable.Range(0, samples.Count).OrderBy(x => rnd.Next()).ToArray();
                foreach (var idx in order)
                {
                    var s = samples[idx];
                    var x = normFeatures[idx];
                    int y = labels[idx];

                    // forward
                    var h = new double[hiddenSize];
                    for (int i = 0; i < hiddenSize; i++)
                    {
                        double sum = b1[i];
                        for (int j = 0; j < inputSize; j++) sum += W1[i][j] * x[j];
                        // relu
                        h[i] = Math.Max(0.0, sum);
                    }
                    double z = b2;
                    for (int i = 0; i < hiddenSize; i++) z += W2[i] * h[i];
                    double pred = Sigmoid(z);

                    // loss derivative (binary cross-entropy)
                    double d = pred - y;

                    // backprop to W2,b2
                    for (int i = 0; i < hiddenSize; i++)
                    {
                        W2[i] -= lr * d * h[i];
                    }
                    b2 -= lr * d;

                    // backprop to hidden
                    for (int i = 0; i < hiddenSize; i++)
                    {
                        double dh = d * W2[i];
                        double grad = h[i] > 0 ? dh : 0.0; // relu
                        if (grad == 0.0) continue;
                        for (int j = 0; j < inputSize; j++)
                        {
                            W1[i][j] -= lr * grad * x[j];
                        }
                        b1[i] -= lr * grad;
                    }
                }
            }

            var model = new SavedModel
            {
                InputSize = inputSize,
                HiddenSize = hiddenSize,
                Weights1 = W1,
                Bias1 = b1,
                Weights2 = W2,
                Bias2 = b2
                , Mean = mean, Std = std
            };
            File.WriteAllText(_modelPath, JsonSerializer.Serialize(model));
            return true;
        }

        private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));

        public SavedModel? LoadModel()
        {
            try
            {
                if (!File.Exists(_modelPath)) return null;
                var txt = File.ReadAllText(_modelPath);
                var m = JsonSerializer.Deserialize<SavedModel>(txt);
                return m;
            }
            catch { return null; }
        }

        // Predict onset probability per feature vector using saved model
        public double[] PredictProbabilities(double[][] features)
        {
            var model = LoadModel();
            if (model == null) return features.Select(f => 0.0).ToArray();
            int hidden = model.HiddenSize;
            int input = model.InputSize;
            var outArr = new double[features.Length];
            // prepare normalization parameters
            var mean = model.Mean ?? new double[input];
            var std = model.Std ?? new double[input];
            for (int k = 0; k < features.Length; k++)
            {
                var x = features[k];
                if (x.Length != input) { outArr[k] = 0.0; continue; }
                // normalize using stored mean/std
                var xn = new double[input];
                for (int j = 0; j < input; j++)
                {
                    var s = std.Length > j ? std[j] : 1.0;
                    if (s == 0) s = 1.0;
                    var m = mean.Length > j ? mean[j] : 0.0;
                    xn[j] = (x[j] - m) / s;
                }
                var h = new double[hidden];
                for (int i = 0; i < hidden; i++)
                {
                    double sum = model.Bias1[i];
                    for (int j = 0; j < input; j++) sum += model.Weights1[i][j] * xn[j];
                    h[i] = Math.Max(0.0, sum);
                }
                double z = model.Bias2;
                for (int i = 0; i < hidden; i++) z += model.Weights2[i] * h[i];
                outArr[k] = Sigmoid(z);
            }

            // simple temporal smoothing (moving average) to reduce jitter
            if (outArr.Length >= 3)
            {
                var smooth = new double[outArr.Length];
                for (int i = 0; i < outArr.Length; i++)
                {
                    double acc = outArr[i]; int cnt = 1;
                    if (i - 1 >= 0) { acc += outArr[i - 1]; cnt++; }
                    if (i + 1 < outArr.Length) { acc += outArr[i + 1]; cnt++; }
                    smooth[i] = acc / cnt;
                }
                return smooth;
            }

            return outArr;
        }

        // Feature extraction: for each window returns feature vector (RMS, ZCR, spectral centroid normalized)
        public (double[][] features, double[] timestamps) ExtractFeaturesFromFile(string filePath, int windowMs = 50)
        {
            var features = new List<double[]>();
            var timestamps = new List<double>();
            if (!File.Exists(filePath)) return (features.ToArray(), timestamps.ToArray());
            try
            {
                using var reader = new Mp3FileReader(filePath);
                var sampleProvider = reader.ToSampleProvider();
                int sampleRate = sampleProvider.WaveFormat.SampleRate;
                int channels = sampleProvider.WaveFormat.Channels;

                int windowSamples = Math.Max(256, (sampleRate * windowMs) / 1000);
                var buffer = new float[windowSamples * channels];
                long windowIndex = 0;

                while (true)
                {
                    int read = sampleProvider.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    int samplesCount = read / channels;
                    // mono mix
                    var mono = new double[samplesCount];
                    for (int s = 0; s < samplesCount; s++)
                    {
                        double sum = 0;
                        for (int c = 0; c < channels; c++) sum += buffer[s * channels + c];
                        mono[s] = sum / channels;
                    }

                    // RMS
                    double sumSq = 0; double maxAbs = 0; int zc = 0;
                    for (int i = 0; i < samplesCount; i++)
                    {
                        var v = mono[i];
                        sumSq += v * v;
                        if (Math.Abs(v) > maxAbs) maxAbs = Math.Abs(v);
                        if (i > 0 && Math.Sign(mono[i]) != Math.Sign(mono[i - 1])) zc++;
                    }
                    double rms = Math.Sqrt(sumSq / Math.Max(1, samplesCount));
                    double zcr = (double)zc / Math.Max(1, samplesCount);

                    // spectral centroid via simple DFT of short window (samplesCount may be variable)
                    int N = 1;
                    while (N < samplesCount) N <<= 1;
                    if (N < 64) N = 64;
                    var padded = new Complex[N];
                    for (int i = 0; i < samplesCount && i < N; i++) padded[i] = new Complex(mono[i], 0);
                    for (int i = samplesCount; i < N; i++) padded[i] = Complex.Zero;
                    // naive DFT (costly but simple) - use Cooley-Tukey iterative FFT
                    FFT(padded);
                    double magSum = 0; double centroid = 0;
                    int half = N / 2;
                    for (int k = 1; k < half; k++)
                    {
                        double mag = padded[k].Magnitude;
                        magSum += mag;
                        centroid += k * mag;
                    }
                    double specCent = magSum > 1e-9 ? (centroid / magSum) / half : 0.0; // normalized 0..1

                    features.Add(new double[] { rms, zcr, specCent, maxAbs });
                    double timeSec = (double)windowIndex * windowMs / 1000.0;
                    timestamps.Add(timeSec);
                    windowIndex++;

                    if (read < buffer.Length) break;
                }
            }
            catch
            {
                // ignore
            }
            return (features.ToArray(), timestamps.ToArray());
        }

        // Align text lines to audio using trained model: returns list of LyricsLine with timestamps and IsScream flag
        public List<LyricsLine> AlignTextToAudioWithModel(string filePath, List<string> lines, int windowMs = 50)
        {
            var outList = new List<LyricsLine>();
            if (lines == null || lines.Count == 0) return outList;
            var (features, timestamps) = ExtractFeaturesFromFile(filePath, windowMs);
            if (features.Length == 0) return outList;

            double[][] featArray = features.Select(f => f).ToArray();
            var probs = PredictProbabilities(featArray);

            // map cumulative text fractions to approximate indices, then search in neighborhood for high probability
            double totalChars = lines.Sum(l => Math.Max(1, l.Length));
            double acc = 0; int lastIdx = 0;
            int windows = probs.Length;
            int neighborhood = Math.Max(1,  (int)(200.0 / 50.0)); // 200ms

            for (int i = 0; i < lines.Count; i++)
            {
                acc += Math.Max(1, lines[i].Length);
                double frac = acc / Math.Max(1, totalChars);
                int estIdx = (int)Math.Round(frac * (windows - 1));
                if (estIdx < lastIdx) estIdx = lastIdx;
                int start = Math.Max(0, estIdx - neighborhood);
                int end = Math.Min(windows - 1, estIdx + neighborhood);

                // find index with best adjusted score in [start,end]
                int best = estIdx; double bestScore = double.NegativeInfinity; double bestP = 0.0;
                double penaltyFactor = 0.5; // penalize earlier windows
                for (int w = start; w <= end; w++)
                {
                    // prefer indices at or after estIdx to avoid "rushing" earlier
                    double penalty = 0.0;
                    if (w < estIdx)
                    {
                        penalty = penaltyFactor * (double)(estIdx - w) / (double)neighborhood;
                    }
                    double score = probs[w] - penalty;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = w;
                        bestP = probs[w];
                    }
                }

                // fallback: if all probs low, use estIdx
                if (bestP < 0.05) { best = estIdx; bestP = probs[Math.Max(0, Math.Min(estIdx, probs.Length - 1))]; }

                double timeSec = timestamps[Math.Max(0, Math.Min(best, timestamps.Length - 1))];
                var isScream = bestP > 0.8 && features[best][0] > 0.2; // heuristic: high prob & high RMS
                outList.Add(new LyricsLine { Timestamp = TimeSpan.FromSeconds(timeSec), Text = lines[i], IsScream = isScream });
                lastIdx = best;
            }

            // ensure monotonic timestamps
            for (int i = 1; i < outList.Count; i++)
            {
                if (outList[i].Timestamp <= outList[i - 1].Timestamp)
                    outList[i].Timestamp = outList[i - 1].Timestamp.Add(TimeSpan.FromMilliseconds(80));
            }

            return outList;
        }

        // -- FFT implementation (in-place Cooley-Tukey) --
        private void FFT(Complex[] buffer)
        {
            int n = buffer.Length;
            int m = (int)Math.Log(n, 2);
            // bit reversal
            for (int i = 0; i < n; i++)
            {
                int j = ReverseBits(i, m);
                if (j > i)
                {
                    var tmp = buffer[i]; buffer[i] = buffer[j]; buffer[j] = tmp;
                }
            }

            for (int s = 1; s <= m; s++)
            {
                int m2 = 1 << s;
                int m2Half = m2 >> 1;
                double theta = -2.0 * Math.PI / m2;
                Complex wm = new Complex(Math.Cos(theta), Math.Sin(theta));
                for (int k = 0; k < n; k += m2)
                {
                    Complex w = Complex.One;
                    for (int j = 0; j < m2Half; j++)
                    {
                        Complex t = w * buffer[k + j + m2Half];
                        Complex u = buffer[k + j];
                        buffer[k + j] = u + t;
                        buffer[k + j + m2Half] = u - t;
                        w *= wm;
                    }
                }
            }
        }

        private int ReverseBits(int x, int bits)
        {
            int y = 0;
            for (int i = 0; i < bits; i++)
            {
                y = (y << 1) | (x & 1);
                x >>= 1;
            }
            return y;
        }
    }
}
