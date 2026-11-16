using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HarmonicaStudio
{
    /// <summary>Renders and plays a local melody preview without sending any input.</summary>
    public sealed class AudioPreview : IDisposable
    {
        private const int SampleRate = 16000;
        private const double MaximumSeconds = 600.0;
        private const int MaximumNotes = 20000;
        private readonly object gate = new object();
        private CancellationTokenSource cancellation;
        private SoundPlayer activePlayer;
        private MemoryStream activeStream;
        private long version;
        private bool disposed;

        // This event is raised from the rendering thread. A UI subscriber must marshal it.
        public event Action<string> Failed;
        internal event Action<long, string> SessionFailed;
        internal long SessionId { get { lock (gate) return version; } }

        public void Play(IList<NoteEvent> notes, double position, double speed)
        {
            PlayCore(notes, position, speed, null);
        }

        public void Play(IList<NoteEvent> notes, double position, double speed, double duration)
        {
            PlayCore(notes, position, speed, duration);
        }

        private void PlayCore(IList<NoteEvent> notes, double position, double speed, double? duration)
        {
            RenderRequest request = Prepare(notes, position, speed, duration);
            lock (gate)
            {
                ThrowIfDisposed();
                CancelCurrent();
                version++;
                if (request.Notes.Count == 0) return;
                long requestVersion = version;
                CancellationTokenSource requestCancellation = new CancellationTokenSource();
                cancellation = requestCancellation;
                Task.Factory.StartNew(
                    delegate { RenderAndPlay(request.Notes, position, speed, request.Seconds, requestVersion, requestCancellation); },
                    CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
            }
        }

        private static RenderRequest Prepare(IList<NoteEvent> notes, double position, double speed, double? duration)
        {
            if (notes == null) throw new ArgumentNullException("notes");
            if (!IsFinite(position) || position < 0.0)
                throw new ArgumentOutOfRangeException("position");
            if (!IsFinite(speed) || speed < 0.5 || speed > 1.5)
                throw new ArgumentOutOfRangeException("speed", "试听速度应为 0.5 至 1.5 倍。");
            if (notes.Count > MaximumNotes)
                throw new ArgumentException("乐谱超过 20000 个音符，请先缩短或筛选旋律。", "notes");
            if (duration.HasValue && (!IsFinite(duration.Value) || duration.Value <= 0 ||
                duration.Value > ScoreIO.MaximumDurationSeconds || duration.Value < position))
                throw new ArgumentOutOfRangeException("duration", "试听结束时刻必须覆盖当前位置，且不超过 4 小时。");

            // Keep a private snapshot so editing or transposing the UI model cannot
            // alter a render that is already running.
            List<RenderNote> snapshot = new List<RenderNote>(notes.Count);
            double lastEnd = position;
            for (int i = 0; i < notes.Count; i++)
            {
                NoteEvent note = notes[i];
                if (note == null || note.Pitch < 0 || note.Pitch > 127 ||
                    note.Velocity <= 0 || note.Velocity > 127 ||
                    !IsFinite(note.Start) || note.Start < 0 ||
                    !IsFinite(note.Duration) || note.Duration <= 0)
                    continue;
                double end = note.Start + note.Duration;
                if (!IsFinite(end)) continue;
                if (end <= position) continue;
                snapshot.Add(new RenderNote
                {
                    Pitch = note.Pitch,
                    Velocity = note.Velocity,
                    Start = note.Start,
                    End = end
                });
                lastEnd = Math.Max(lastEnd, end);
            }

            if (duration.HasValue && duration.Value < lastEnd - 0.0000001)
                throw new ArgumentOutOfRangeException("duration", "试听时长必须覆盖全部音符；请先截取乐谱。");
            double seconds = (Math.Max(lastEnd, duration ?? lastEnd) - position) / speed;
            if (seconds > MaximumSeconds)
                throw new ArgumentException("本地试听每次最多 10 分钟，请截取片段或从较后位置开始。", "notes");

            return new RenderRequest { Notes = snapshot, Seconds = seconds };
        }

        public void Stop()
        {
            lock (gate)
            {
                version++;
                CancelCurrent();
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                version++;
                CancelCurrent();
            }
        }

        private void CancelCurrent()
        {
            if (cancellation != null)
            {
                // The worker owns disposal and remains alive until it releases
                // its token's wait handle and its audio stream.
                cancellation.Cancel();
                cancellation = null;
            }
            if (activePlayer != null)
            {
                activePlayer.Stop();
                activePlayer.Dispose();
                activePlayer = null;
                if (activeStream != null) activeStream.Dispose();
                activeStream = null;
            }
        }

        private void RenderAndPlay(List<RenderNote> notes, double position, double speed,
            double seconds, long requestVersion, CancellationTokenSource requestCancellation)
        {
            SoundPlayer player = null;
            MemoryStream stream = null;
            CancellationToken token = requestCancellation.Token;
            try
            {
                byte[] wave = RenderWave(notes, position, speed, seconds, token);
                token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (disposed || version != requestVersion || token.IsCancellationRequested)
                        return;
                    // SoundPlayer uses process-wide playback. Its creation,
                    // start, stop, and disposal must share one gate, including
                    // disposal of a cancelled preview before the next starts.
                    stream = new MemoryStream(wave, false);
                    player = new SoundPlayer(stream);
                    try
                    {
                        player.Load();
                        player.Play();
                        activePlayer = player;
                        activeStream = stream;
                    }
                    catch
                    {
                        player.Dispose();
                        stream.Dispose();
                        player = null;
                        stream = null;
                        throw;
                    }
                }

                int timeout = (int)Math.Ceiling((seconds + 0.25) * 1000.0);
                token.WaitHandle.WaitOne(timeout);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Action<string> handler = null;
                Action<long, string> sessionHandler = null;
                lock (gate)
                {
                    if (!disposed && version == requestVersion && !token.IsCancellationRequested)
                    {
                        handler = Failed;
                        sessionHandler = SessionFailed;
                    }
                }
                string message = "本地试听失败：" + ex.Message;
                if (sessionHandler != null)
                    foreach (Action<long, string> subscriber in sessionHandler.GetInvocationList())
                    {
                        try { subscriber(requestVersion, message); }
                        catch { /* Subscribers cannot strand the rendering worker. */ }
                    }
                if (handler != null)
                {
                    try { handler(message); }
                    catch { /* A subscriber must not strand the worker's resources. */ }
                }
            }
            finally
            {
                lock (gate)
                {
                    if (player != null && Object.ReferenceEquals(activePlayer, player))
                    {
                        activePlayer.Stop();
                        activePlayer.Dispose();
                        activePlayer = null;
                        activeStream.Dispose();
                        activeStream = null;
                    }
                    if (Object.ReferenceEquals(cancellation, requestCancellation))
                        cancellation = null;
                }
                requestCancellation.Dispose();
            }
        }

        private static byte[] RenderWave(List<RenderNote> notes, double position,
            double speed, double seconds, CancellationToken token)
        {
            int sampleCount = Math.Max(1, (int)Math.Ceiling(seconds * SampleRate));
            float[] samples = new float[sampleCount];
            for (int noteIndex = 0; noteIndex < notes.Count; noteIndex++)
            {
                token.ThrowIfCancellationRequested();
                RenderNote note = notes[noteIndex];
                double actualStart = Math.Max(note.Start, position);
                double startTime = (actualStart - position) / speed;
                double endTime = (note.End - position) / speed;
                int first = Math.Max(0, (int)Math.Floor(startTime * SampleRate));
                int last = Math.Min(sampleCount, (int)Math.Ceiling(endTime * SampleRate));
                double frequency = 440.0 * Math.Pow(2.0, (note.Pitch - 69) / 12.0);
                if (frequency >= SampleRate * 0.45) continue;

                double phaseStep = 2.0 * Math.PI * frequency / SampleRate;
                double phase = phaseStep * first;
                double sine = Math.Sin(phase);
                double cosine = Math.Cos(phase);
                double sineStep = Math.Sin(phaseStep);
                double cosineStep = Math.Cos(phaseStep);
                double amplitude = 0.30 * Math.Pow(note.Velocity / 127.0, 1.2);
                double noteSeconds = (note.End - note.Start) / speed;
                double attack = Math.Min(0.018, noteSeconds * 0.20);
                double release = Math.Min(0.045, noteSeconds * 0.35);

                // Suppress harmonics above Nyquist for the high MIDI notes.
                double h2 = frequency * 2.0 < SampleRate * 0.45 ? 0.28 : 0.0;
                double h3 = frequency * 3.0 < SampleRate * 0.45 ? 0.12 : 0.0;
                double h4 = frequency * 4.0 < SampleRate * 0.45 ? 0.035 : 0.0;
                for (int sample = first; sample < last; sample++)
                {
                    if ((sample & 4095) == 0) token.ThrowIfCancellationRequested();
                    double time = sample / (double)SampleRate;
                    double elapsed = time - (note.Start - position) / speed;
                    double remaining = endTime - time;
                    double attackEnvelope = Math.Min(1.0, Math.Max(0.0, elapsed / attack));
                    double releaseEnvelope = Math.Min(1.0, Math.Max(0.0, remaining / release));
                    // Starting in the middle of a held note still receives a
                    // short fade to keep the preview from clicking.
                    double boundaryFade = Math.Min(1.0, Math.Max(0.0, (time - startTime) / 0.008));
                    double envelope = attackEnvelope * releaseEnvelope * boundaryFade;
                    double s2 = sine * sine;
                    double reed = sine + h2 * 2.0 * sine * cosine +
                        h3 * (3.0 * sine - 4.0 * sine * s2) +
                        h4 * 4.0 * sine * cosine * (1.0 - 2.0 * s2);
                    samples[sample] += (float)(amplitude * envelope * reed);

                    double nextSine = sine * cosineStep + cosine * sineStep;
                    cosine = cosine * cosineStep - sine * sineStep;
                    sine = nextSine;
                }
            }

            double peak = 0.0;
            for (int i = 0; i < samples.Length; i++)
            {
                if ((i & 8191) == 0) token.ThrowIfCancellationRequested();
                peak = Math.Max(peak, Math.Abs(samples[i]));
            }
            double gain = peak > 0.85 ? 0.85 / peak : 1.0;
            int dataBytes = checked(sampleCount * 2);
            byte[] wave = new byte[44 + dataBytes];
            using (MemoryStream output = new MemoryStream(wave, true))
            using (BinaryWriter writer = new BinaryWriter(output, Encoding.ASCII))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataBytes);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(SampleRate);
                writer.Write(SampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataBytes);
                for (int i = 0; i < samples.Length; i++)
                {
                    if ((i & 8191) == 0) token.ThrowIfCancellationRequested();
                    writer.Write((short)Math.Round(samples[i] * gain * 32767.0));
                }
            }
            return wave;
        }

        private static bool IsFinite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException("AudioPreview");
        }

        private sealed class RenderNote
        {
            public int Pitch;
            public int Velocity;
            public double Start;
            public double End;
        }

        private sealed class RenderRequest
        {
            public List<RenderNote> Notes;
            public double Seconds;
        }
    }
}
