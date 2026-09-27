using System;

namespace AutoMute
{
    /// <summary>
    /// Turns a stream of audio frames into loudness readings (dBFS) and, when
    /// asked, a spectrum-based high-band level and dominant pitch.
    /// Loudness is evaluated every BlockFrames frames (~10.7 ms at 48 kHz).
    /// </summary>
    public sealed class LevelAnalyzer
    {
        public const int BlockFrames = 512;
        public const int FftSize = 2048;          // ~43 ms at 48 kHz, ~23 Hz resolution
        private const int HistoryBlocks = 512;    // ~5 s of loudness history

        private readonly int sampleRate;
        private readonly double[] history = new double[HistoryBlocks];
        private int historyPos;
        private double blockSum;
        private int blockFill;

        private readonly float[] monoRing = new float[FftSize];
        private int ringPos;
        private readonly double[] window = new double[FftSize];
        private readonly double[] re = new double[FftSize];
        private readonly double[] im = new double[FftSize];
        private readonly double norm;

        public LevelAnalyzer(int sampleRate)
        {
            this.sampleRate = sampleRate;
            double wsum = 0;
            for (int i = 0; i < FftSize; i++)
            {
                window[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (FftSize - 1)); // Hann
                wsum += window[i] * window[i];
            }
            // Parseval scaling so band power is in the same units as the
            // time-domain mean square, i.e. directly comparable to loudness dBFS.
            norm = 2.0 / (FftSize * wsum);
        }

        public double BlockSeconds
        {
            get { return (double)BlockFrames / sampleRate; }
        }

        /// <summary>Add one frame. meanSquare = average of the squared channel
        /// samples; mono = average of the channel samples. Returns true when a
        /// loudness block has just completed.</summary>
        public bool AddFrame(double meanSquare, float mono)
        {
            monoRing[ringPos] = mono;
            ringPos = (ringPos + 1) & (FftSize - 1);

            blockSum += meanSquare;
            blockFill++;
            if (blockFill < BlockFrames) return false;

            history[historyPos] = blockSum / blockFill;
            historyPos = (historyPos + 1) % HistoryBlocks;
            blockSum = 0;
            blockFill = 0;
            return true;
        }

        /// <summary>Loudness averaged over the last windowMs (at least one block).</summary>
        public double LevelDb(int windowMs)
        {
            // History starts (and is reset) as zeros, so time with no audio
            // counts as silence - a burst right after quiet is still averaged.
            int blocks = (int)Math.Round(windowMs / 1000.0 / BlockSeconds);
            if (blocks < 1) blocks = 1;
            if (blocks > HistoryBlocks) blocks = HistoryBlocks;

            double sum = 0;
            int idx = historyPos;
            for (int i = 0; i < blocks; i++)
            {
                idx = (idx - 1 + HistoryBlocks) % HistoryBlocks;
                sum += history[idx];
            }
            return ToDb(sum / blocks);
        }

        public void Spectrum(double cutoffHz, out double highBandDb, out double dominantHz)
        {
            for (int i = 0; i < FftSize; i++)
            {
                re[i] = monoRing[(ringPos + i) & (FftSize - 1)] * window[i]; // oldest -> newest
                im[i] = 0;
            }
            Fft(re, im);

            double binHz = (double)sampleRate / FftSize;
            double high = 0, best = 0;
            int bestBin = 0;
            for (int k = 1; k <= FftSize / 2; k++)
            {
                double p = re[k] * re[k] + im[k] * im[k];
                if (k * binHz >= cutoffHz) high += p;
                if (p > best)
                {
                    best = p;
                    bestBin = k;
                }
            }
            highBandDb = ToDb(high * norm);
            dominantHz = best * norm > 1e-8 ? bestBin * binHz : 0.0;
        }

        public void Reset()
        {
            Array.Clear(history, 0, history.Length);
            Array.Clear(monoRing, 0, monoRing.Length);
            historyPos = blockFill = ringPos = 0;
            blockSum = 0;
        }

        public static double ToDb(double meanSquare)
        {
            return meanSquare > 1e-10 ? 10.0 * Math.Log10(meanSquare) : -100.0;
        }

        /// <summary>In-place iterative radix-2 FFT (length must be a power of 2).</summary>
        public static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    double t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2.0 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                int half = len >> 1;
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1.0, ci = 0.0;
                    for (int k = 0; k < half; k++)
                    {
                        int a = i + k, b = a + half;
                        double tr = re[b] * cr - im[b] * ci;
                        double ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                        double ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }
    }
}
