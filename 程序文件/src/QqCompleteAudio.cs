using System;
using System.IO;
using System.Threading;

namespace HarmonicaStudio
{
    internal static class QqCompleteAudio
    {
        // Counting MPEG Layer III frames works for both CBR and VBR, without
        // estimating duration from file size or mistaking a preview for a song.
        internal static void Validate(string filename, int expectedSeconds, CancellationToken token)
        {
            if (expectedSeconds <= 0) throw Incomplete("无法核对该版本的完整时长，请换一个时长明确的版本，或从官方客户端取得普通音频后导入。");
            double actual;
            using (var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read)) actual = ReadDuration(stream, token);
            double tolerance = Math.Max(3.0, expectedSeconds * 0.02);
            if (actual <= 0 || Math.Abs(actual - expectedSeconds) > tolerance)
                throw Incomplete("QQ 音乐返回的音频与歌曲完整时长不符，可能只是试听片段，已停止自动转录。请换版本或导入官方客户端取得的普通完整音频。");
        }
        private static InvalidDataException Incomplete(string message)
        { var error = new InvalidDataException(message); error.Data["QqIncompleteAudio"] = true; return error; }

        internal static double ReadDuration(Stream stream, CancellationToken token)
        {
            if (stream == null || !stream.CanSeek || stream.Length > SongAudioDownload.MaximumDownloadBytes) return 0;
            long start = 0;
            byte[] header = new byte[10];
            if (stream.Read(header, 0, header.Length) == header.Length && header[0] == 'I' && header[1] == 'D' && header[2] == '3')
            {
                for (int i = 6; i <= 9; i++) if ((header[i] & 0x80) != 0) return 0;
                start = 10L + (header[6] << 21) + (header[7] << 14) + (header[8] << 7) + header[9] + ((header[5] & 0x10) == 0 ? 0 : 10);
            }
            long firstLimit = Math.Min(stream.Length - 4, start + 8192);
            int frames = 0; double seconds = 0;
            long position = start;
            while (position + 4 <= stream.Length)
            {
                if ((frames & 255) == 0) token.ThrowIfCancellationRequested();
                stream.Position = position;
                int a = stream.ReadByte(), b = stream.ReadByte(), c = stream.ReadByte(), d = stream.ReadByte();
                int length, samples, rate;
                bool valid = TryFrame(a, b, c, d, out length, out samples, out rate);
                if (valid && position + length > stream.Length) return 0;
                if (!valid)
                {
                    if (frames == 0 && position < firstLimit) { position++; continue; }
                    // Recognize ID3v1 explicitly; an incomplete final MPEG frame
                    // must not be waved through as a small padding block.
                    return frames >= 10 && stream.Length - position == 128 && a == 'T' && b == 'A' && c == 'G' ? seconds : 0;
                }
                if (frames == 0 && position + length + 4 <= stream.Length)
                {
                    stream.Position = position + length;
                    int nextLength, nextSamples, nextRate;
                    if (!TryFrame(stream.ReadByte(), stream.ReadByte(), stream.ReadByte(), stream.ReadByte(), out nextLength, out nextSamples, out nextRate))
                    { position++; continue; }
                }
                frames++; seconds += (double)samples / rate; position += length;
            }
            return frames >= 10 ? seconds : 0;
        }
        private static bool TryFrame(int a, int b, int c, int d, out int length, out int samples, out int rate)
        {
            length = samples = rate = 0;
            if (a != 0xff || b < 0 || (b & 0xe0) != 0xe0 || (b & 6) != 2 || d < 0) return false;
            int version = (b >> 3) & 3, bitrateIndex = (c >> 4) & 15, rateIndex = (c >> 2) & 3;
            if (version == 1 || bitrateIndex == 0 || bitrateIndex == 15 || rateIndex == 3) return false;
            int[] rates = new int[] { 44100, 48000, 32000 };
            int[] bitrates = version == 3 ? new int[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 } :
                new int[] { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };
            rate = rates[rateIndex] / (version == 3 ? 1 : version == 2 ? 2 : 4);
            samples = version == 3 ? 1152 : 576;
            length = (version == 3 ? 144000 : 72000) * bitrates[bitrateIndex] / rate + ((c >> 1) & 1);
            return length >= 24;
        }
    }
}
