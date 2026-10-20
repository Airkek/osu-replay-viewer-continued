using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace osu_replay_renderer_netcore.Audio.Conversion
{
    internal static class FloatWaveReader
    {
        public static AudioBuffer Read(Stream stream)
        {
            var header = ReadHeader(stream);
            return ReadPcm(stream, header.Format, header.DataBytes);
        }

        public static (AudioFormat Format, uint DataBytes) ReadHeader(Stream stream)
        {
            Span<byte> header = stackalloc byte[12];
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8))
                throw new InvalidDataException("Expected RIFF WAVE audio");

            AudioFormat format = null;
            Span<byte> chunkHeader = stackalloc byte[8];
            while (true)
            {
                stream.ReadExactly(chunkHeader);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
                if (chunkHeader[..4].SequenceEqual("fmt "u8))
                {
                    if (size < 16 || size > 65536)
                        throw new InvalidDataException("Invalid WAVE format chunk");
                    var fmt = new byte[(int)size];
                    stream.ReadExactly(fmt);
                    format = ReadFormat(fmt);
                }
                else if (chunkHeader[..4].SequenceEqual("data"u8))
                {
                    if (format == null)
                        throw new InvalidDataException("WAVE format must precede PCM data");
                    return (format, size);
                }
                else
                {
                    if (size == uint.MaxValue)
                        throw new InvalidDataException("Unknown size for WAVE metadata chunk");
                    Skip(stream, size);
                }

                if ((size & 1) != 0) Skip(stream, 1);
            }
        }

        private static AudioFormat ReadFormat(ReadOnlySpan<byte> fmt)
        {
            ushort encoding = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
            int channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
            uint rate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
            int alignment = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
            int bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
            bool isFloat = encoding == 3;
            if (encoding == 0xfffe && fmt.Length >= 40 && BinaryPrimitives.ReadUInt16LittleEndian(fmt[16..]) >= 22)
            {
                isFloat = new Guid(fmt.Slice(24, 16)) == new Guid("00000003-0000-0010-8000-00aa00389b71");
            }

            if (!isFloat || bits != 32 || channels < 1 || rate == 0 || rate > int.MaxValue || alignment != channels * sizeof(float))
                throw new InvalidDataException("Expected 32-bit float PCM with a valid sample rate");

            return new AudioFormat { Channels = channels, SampleRate = (int)rate, PCMSize = sizeof(float) };
        }

        private static AudioBuffer ReadPcm(Stream stream, AudioFormat format, uint size)
        {
            bool unknownSize = size == uint.MaxValue;
            long remaining = unknownSize ? long.MaxValue : size;
            var blocks = new List<float[]>();
            int totalValues = 0;
            while (remaining > 0)
            {
                var block = new float[AudioBuffer.BlockLength];
                var bytes = MemoryMarshal.AsBytes(block.AsSpan());
                int requested = (int)Math.Min(remaining, bytes.Length);
                int read = 0;
                while (read < requested)
                {
                    int count = stream.Read(bytes.Slice(read, requested - read));
                    if (count == 0) break;
                    read += count;
                }

                if (!unknownSize && read != requested)
                    throw new EndOfStreamException("Truncated WAVE PCM data");
                if (read % sizeof(float) != 0)
                    throw new InvalidDataException("Incomplete float PCM sample");
                if (read == 0) break;

                int values = read / sizeof(float);
                if (!BitConverter.IsLittleEndian)
                {
                    var integers = MemoryMarshal.Cast<float, int>(block.AsSpan(0, values));
                    for (int i = 0; i < integers.Length; i++)
                        integers[i] = BinaryPrimitives.ReverseEndianness(integers[i]);
                }
                blocks.Add(block);
                totalValues = checked(totalValues + values);
                remaining -= read;
                if (read < requested) break;
            }

            if (totalValues % format.Channels != 0)
                throw new InvalidDataException("Incomplete PCM channel frame");
            return AudioBuffer.FromBlocks(format, blocks, totalValues);
        }

        private static void Skip(Stream stream, uint bytes)
        {
            Span<byte> scratch = stackalloc byte[4096];
            while (bytes > 0)
            {
                int count = (int)Math.Min(bytes, (uint)scratch.Length);
                stream.ReadExactly(scratch[..count]);
                bytes -= (uint)count;
            }
        }
    }
}
