using System.Threading;

namespace TavernVoice
{
    internal sealed class PcmRingBuffer
    {
        private readonly float[] samples;
        private readonly object sync = new object();
        private int read;
        private int write;
        private int count;
        private long readCallbacks;
        private long requestedSamples;
        private long zeroFilledSamples;
        private int lastRequestSamples;

        internal PcmRingBuffer(int capacity) => samples = new float[capacity + 1];
        internal long ReadCallbacks => Interlocked.Read(ref readCallbacks);
        internal long RequestedSamples => Interlocked.Read(ref requestedSamples);
        internal long ZeroFilledSamples => Interlocked.Read(ref zeroFilledSamples);
        internal int LastRequestSamples => Volatile.Read(ref lastRequestSamples);

        internal int BufferedSamples
        {
            get
            {
                lock (sync) return count;
            }
        }

        internal void Write(short[] source, int count)
        {
            lock (sync)
            {
                for (int i = 0; i < count; i++)
                {
                    if (this.count == samples.Length)
                    {
                        read = (read + 1) % samples.Length;
                        this.count--;
                    }
                    samples[write] = source[i] / 32768f;
                    write = (write + 1) % samples.Length;
                    this.count++;
                }
            }
        }

        internal void Read(float[] destination)
        {
            bool underrun = false;
            int zeros = 0;
            lock (sync)
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    if (count == 0)
                    {
                        destination[i] = 0f;
                        underrun = true;
                        zeros++;
                    }
                    else
                    {
                        destination[i] = samples[read];
                        read = (read + 1) % samples.Length;
                        count--;
                    }
                }
            }
            Interlocked.Increment(ref readCallbacks);
            Interlocked.Add(ref requestedSamples, destination.Length);
            Interlocked.Add(ref zeroFilledSamples, zeros);
            Volatile.Write(ref lastRequestSamples, destination.Length);
            if (underrun) VoiceDiagnostics.Underrun();
        }

        internal void Clear()
        {
            lock (sync)
            {
                read = write;
                count = 0;
            }
        }
    }
}
