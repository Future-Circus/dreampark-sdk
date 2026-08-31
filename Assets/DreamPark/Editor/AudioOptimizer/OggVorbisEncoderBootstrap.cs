#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.AudioOptimization
{
    /// <summary>
    /// Reflection-based wrapper around the OggVorbisEncoder assembly. We
    /// never take a compile-time dependency on it — the DLL is downloaded
    /// per-machine by <see cref="OggVorbisEncoderInstaller"/> on first
    /// use of the Audio Optimizer, so the SDK has to compile without it.
    ///
    /// At runtime (after install), we sniff loaded assemblies for the
    /// expected types, cache the MethodInfo / PropertyInfo / ConstructorInfo
    /// handles once, then call them per clip. Reflection overhead is
    /// negligible compared to disk I/O + Vorbis encoding cost.
    ///
    /// Why OggVorbisEncoder and not FFmpeg / NAudio? Pure managed C#,
    /// ~140 KB total, no native binaries to package per host. Same
    /// install-and-go pattern as Magick.NET but with 1/200th the
    /// install footprint.
    /// </summary>
    public static class OggVorbisEncoderBootstrap
    {
        // Cached reflection handles. `_resolvedSuccessfully` flips to true
        // only when EnsureResolved finds a working binding — failed
        // lookups don't latch, so subsequent calls after install (or
        // after a domain reload that loads the new assembly) re-scan and
        // pick up the new types.
        private static bool _resolvedSuccessfully;

        private static Type _vorbisInfoType;          // OggVorbisEncoder.VorbisInfo
        private static Type _oggStreamType;           // OggVorbisEncoder.OggStream
        private static Type _oggPageType;             // OggVorbisEncoder.OggPage
        private static Type _oggPacketType;           // OggVorbisEncoder.OggPacket
        private static Type _commentsType;            // OggVorbisEncoder.Comments
        private static Type _processingStateType;     // OggVorbisEncoder.ProcessingState
        private static Type _headerPacketBuilderType; // OggVorbisEncoder.HeaderPacketBuilder

        private static MethodInfo _initVbrMethod;     // VorbisInfo.InitVariableBitRate(int,int,float)
        private static ConstructorInfo _oggStreamCtor;// new OggStream(int serial)
        private static ConstructorInfo _commentsCtor; // new Comments()
        private static MethodInfo _commentsAddTag;    // Comments.AddTag(string, string)

        private static MethodInfo _buildInfoPacket;
        private static MethodInfo _buildCommentsPacket;
        private static MethodInfo _buildBooksPacket;

        private static MethodInfo _packetInMethod;    // OggStream.PacketIn(OggPacket)
        private static MethodInfo _pageOutMethod;     // OggStream.PageOut(out OggPage, bool force) → bool
        private static PropertyInfo _finishedProp;    // OggStream.Finished

        private static MethodInfo _processingCreate;  // ProcessingState.Create(VorbisInfo)
        private static MethodInfo _writeDataMethod;   // ProcessingState.WriteData(float[][], int)
        private static MethodInfo _packetOutMethod;   // ProcessingState.PacketOut(out OggPacket) → bool
        private static MethodInfo _writeEndOfStream;  // ProcessingState.WriteEndOfStream()

        private static PropertyInfo _pageHeaderProp;
        private static PropertyInfo _pageBodyProp;

        public static bool IsAvailable
        {
            get
            {
                EnsureResolved();
                return _resolvedSuccessfully;
            }
        }

        public static string StatusMessage
        {
            get
            {
                EnsureResolved();
                if (_vorbisInfoType == null)
                    return "OggVorbisEncoder not loaded — first use will download it.";
                return "OggVorbisEncoder v" + GetVersion() + " ready.";
            }
        }

        // ─── Encode entry point — called by the Executor ────────────────

        /// <summary>
        /// Read <paramref name="wavAbsolutePath"/> (uncompressed PCM /
        /// 32-bit float WAV), optionally resample and mix to mono, then
        /// encode the result as Vorbis to <paramref name="oggAbsolutePath"/>.
        ///
        /// <paramref name="targetSampleRate"/>: pass 0 to preserve the
        /// source rate. Otherwise the source is resampled (linear interp).
        ///
        /// <paramref name="forceToMono"/>: if true and the source is
        /// stereo, channels are averaged before encoding.
        ///
        /// <paramref name="quality"/>: libvorbis quality 0..1. 0.7 is
        /// transparent for most game audio; 0.5 is a noticeable but
        /// acceptable drop on music.
        /// </summary>
        public static void EncodeWavToOgg(
            string wavAbsolutePath,
            string oggAbsolutePath,
            int targetSampleRate,
            bool forceToMono,
            float quality)
        {
            if (!IsAvailable)
                throw new InvalidOperationException(
                    "OggVorbisEncoder is not installed. " + StatusMessage);

            // ── Step 1: read source WAV → float PCM ───────────────────
            var wav = WavIO.Read(wavAbsolutePath);
            float[] interleaved = wav.interleaved;
            int channels = wav.channels;
            int sampleRate = wav.sampleRate;
            int samplesPerChannel = wav.samplesPerChannel;

            // ── Step 2: mono / sample-rate conversion ─────────────────
            if (forceToMono && channels == 2)
            {
                interleaved = WavIO.ToMono(interleaved, channels);
                channels = 1;
                // samplesPerChannel is unchanged — ToMono returns one
                // sample per frame, so the count per-channel stays the
                // same; only the channel count drops.
            }

            int outSampleRate = targetSampleRate > 0 ? targetSampleRate : sampleRate;
            if (outSampleRate != sampleRate)
            {
                interleaved = WavIO.Resample(interleaved, channels, sampleRate, outSampleRate, out samplesPerChannel);
            }

            // ── Step 3: de-interleave for ProcessingState.WriteData ────
            // OggVorbisEncoder expects float[channels][samples]. Our WAV
            // reader produces interleaved-per-frame; transpose it.
            float[][] planar = WavIO.DeInterleave(interleaved, channels, samplesPerChannel);

            // ── Step 4: drive the Vorbis encoder via reflection ────────
            Directory.CreateDirectory(Path.GetDirectoryName(oggAbsolutePath));
            using (var fileOut = File.Create(oggAbsolutePath))
            {
                EncodeToStream(planar, channels, samplesPerChannel, outSampleRate, quality, fileOut);
            }
        }

        /// <summary>
        /// The actual reflection-driven encode pass. Mirrors the canonical
        /// OggVorbisEncoder usage example: build VorbisInfo, write three
        /// header packets, then loop PCM chunks → packets → pages → file.
        /// </summary>
        private static void EncodeToStream(
            float[][] planar,
            int channels,
            int samplesPerChannel,
            int sampleRate,
            float quality,
            Stream destination)
        {
            // VorbisInfo.InitVariableBitRate(channels, sampleRate, quality)
            object info = _initVbrMethod.Invoke(null, new object[] { channels, sampleRate, quality });

            // new OggStream(serial)
            int serial = new System.Random().Next();
            object oggStream = _oggStreamCtor.Invoke(new object[] { serial });

            // new Comments() — minimal, no tags. Adding an ARTIST/TITLE
            // tag would expand the file by ~50 bytes; skipping keeps the
            // output as small as possible.
            object comments = _commentsCtor.Invoke(null);

            // Three header packets (info / comments / setup books)
            object infoPacket     = _buildInfoPacket    .Invoke(null, new object[] { info });
            object commentsPacket = _buildCommentsPacket.Invoke(null, new object[] { comments });
            object booksPacket    = _buildBooksPacket   .Invoke(null, new object[] { info });

            _packetInMethod.Invoke(oggStream, new object[] { infoPacket });
            _packetInMethod.Invoke(oggStream, new object[] { commentsPacket });
            _packetInMethod.Invoke(oggStream, new object[] { booksPacket });

            // Flush header pages — force=true ensures audio data starts
            // on a fresh page (per the spec).
            FlushPages(oggStream, destination, force: true);

            // ProcessingState.Create(info)
            object proc = _processingCreate.Invoke(null, new object[] { info });

            // Push PCM in chunks. OggVorbisEncoder accepts the entire
            // clip in one WriteData call, but big clips can balloon
            // intermediate buffers — chunking to ~1 second keeps memory
            // bounded for long music tracks.
            const int chunkSize = 4096; // samples per channel per chunk
            int written = 0;
            while (written < samplesPerChannel)
            {
                int thisChunk = Math.Min(chunkSize, samplesPerChannel - written);

                // ProcessingState.WriteData(float[][] data, int samples)
                // expects the float[][] to start at offset 0 and read
                // `samples` items. We slice into temporary arrays so
                // the encoder reads from the right window each chunk.
                float[][] chunk = new float[channels][];
                for (int c = 0; c < channels; c++)
                {
                    chunk[c] = new float[thisChunk];
                    Array.Copy(planar[c], written, chunk[c], 0, thisChunk);
                }
                // 1.2.x signature is (data, length, read_offset). We
                // pre-sliced the chunk to start at index 0, so read_offset
                // is always 0. The 2-arg fallback covers pre-1.2.0 builds
                // where the offset parameter didn't exist.
                int paramCount = _writeDataMethod.GetParameters().Length;
                object[] writeArgs = paramCount == 3
                    ? new object[] { chunk, thisChunk, 0 }
                    : new object[] { chunk, thisChunk };
                _writeDataMethod.Invoke(proc, writeArgs);

                DrainPackets(proc, oggStream, destination, finalPass: false);
                written += thisChunk;
            }

            // End the stream — flush remaining packets / pages.
            _writeEndOfStream.Invoke(proc, null);
            DrainPackets(proc, oggStream, destination, finalPass: true);
        }

        /// <summary>
        /// Pull packets out of <paramref name="proc"/> and push them into
        /// <paramref name="oggStream"/>, then flush pages to disk. The
        /// "while !Finished && PacketOut" idiom matches the canonical
        /// example from the OggVorbisEncoder README.
        /// </summary>
        private static void DrainPackets(object proc, object oggStream, Stream destination, bool finalPass)
        {
            // PacketOut takes one `out OggPacket` parameter. Reflection
            // surfaces `out` as a regular parameter slot that we read
            // after Invoke. We populate args[0] with null and Invoke
            // mutates it in place.
            while (!IsFinished(oggStream))
            {
                var packetArgs = new object[] { null };
                bool got = (bool)_packetOutMethod.Invoke(proc, packetArgs);
                if (!got) break;

                _packetInMethod.Invoke(oggStream, new object[] { packetArgs[0] });
                FlushPages(oggStream, destination, force: finalPass);
            }
        }

        /// <summary>
        /// Drain all pages currently buffered in <paramref name="oggStream"/>
        /// and write each to <paramref name="destination"/>. The
        /// <paramref name="force"/> flag tells the stream to emit a page
        /// even if it isn't full — used for header pages and the final
        /// end-of-stream page.
        /// </summary>
        private static void FlushPages(object oggStream, Stream destination, bool force)
        {
            while (true)
            {
                var args = new object[] { null, force };
                bool gotPage = (bool)_pageOutMethod.Invoke(oggStream, args);
                if (!gotPage) break;

                object page = args[0];
                byte[] header = (byte[])_pageHeaderProp.GetValue(page);
                byte[] body   = (byte[])_pageBodyProp.GetValue(page);
                destination.Write(header, 0, header.Length);
                destination.Write(body, 0, body.Length);
            }
        }

        private static bool IsFinished(object oggStream)
        {
            return (bool)_finishedProp.GetValue(oggStream);
        }

        // ─── Resolution / version ───────────────────────────────────────

        private static string GetVersion()
        {
            if (_vorbisInfoType == null) return "?";
            return _vorbisInfoType.Assembly.GetName().Version?.ToString() ?? "unknown";
        }

        /// <summary>
        /// Force a re-scan of loaded assemblies. Called by the window
        /// just after install completes but before the domain reload
        /// actually fires (the new DLL is on disk; once
        /// AssetDatabase.Refresh triggers a recompile, the reopen hook
        /// re-evaluates IsAvailable in the fresh domain).
        /// </summary>
        public static void Invalidate()
        {
            _resolvedSuccessfully = false;
            _vorbisInfoType = null;
        }

        /// <summary>
        /// Sniff every loaded assembly for OggVorbisEncoder types. Only a
        /// fully-successful resolve latches — if any member is missing
        /// (e.g. a future version renamed something) we stay unresolved
        /// and IsAvailable reports false, so the executor's clear-error
        /// path fires.
        /// </summary>
        private static void EnsureResolved()
        {
            if (_resolvedSuccessfully && _vorbisInfoType != null) return;
            _vorbisInfoType = null;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (string.IsNullOrEmpty(name)) continue;
                if (!name.Equals("OggVorbisEncoder", StringComparison.OrdinalIgnoreCase)) continue;

                _vorbisInfoType          = asm.GetType("OggVorbisEncoder.VorbisInfo");
                _oggStreamType           = asm.GetType("OggVorbisEncoder.OggStream");
                _oggPageType             = asm.GetType("OggVorbisEncoder.OggPage");
                _oggPacketType           = asm.GetType("OggVorbisEncoder.OggPacket");
                _commentsType            = asm.GetType("OggVorbisEncoder.Comments");
                _processingStateType     = asm.GetType("OggVorbisEncoder.ProcessingState");
                _headerPacketBuilderType = asm.GetType("OggVorbisEncoder.HeaderPacketBuilder");

                if (_vorbisInfoType == null
                    || _oggStreamType == null
                    || _oggPageType == null
                    || _oggPacketType == null
                    || _commentsType == null
                    || _processingStateType == null
                    || _headerPacketBuilderType == null)
                    continue;

                _initVbrMethod = _vorbisInfoType.GetMethod(
                    "InitVariableBitRate",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(int), typeof(int), typeof(float) },
                    null);

                _oggStreamCtor = _oggStreamType.GetConstructor(new[] { typeof(int) });
                _commentsCtor  = _commentsType.GetConstructor(Type.EmptyTypes);
                _commentsAddTag = _commentsType.GetMethod("AddTag", new[] { typeof(string), typeof(string) });

                _buildInfoPacket     = _headerPacketBuilderType.GetMethod("BuildInfoPacket",     new[] { _vorbisInfoType });
                _buildCommentsPacket = _headerPacketBuilderType.GetMethod("BuildCommentsPacket", new[] { _commentsType });
                _buildBooksPacket    = _headerPacketBuilderType.GetMethod("BuildBooksPacket",    new[] { _vorbisInfoType });

                _packetInMethod = _oggStreamType.GetMethod("PacketIn", new[] { _oggPacketType });

                // PageOut(out OggPage, bool force) — `out` parameters are
                // expressed as ByRef types in reflection. Probe explicitly.
                _pageOutMethod = FindPageOutMethod();

                _finishedProp = _oggStreamType.GetProperty("Finished");

                _processingCreate = _processingStateType.GetMethod(
                    "Create",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { _vorbisInfoType },
                    null);
                // OggVorbisEncoder 1.2.x signature is
                //   WriteData(Single[][] data, Int32 length, Int32 read_offset)
                // — read_offset was added in 1.2.0 (older versions had a
                // 2-arg overload). We probe the 3-arg variant first and
                // fall back to the 2-arg for forward/backward compat.
                _writeDataMethod = _processingStateType.GetMethod(
                    "WriteData",
                    new[] { typeof(float[][]), typeof(int), typeof(int) })
                                 ?? _processingStateType.GetMethod(
                    "WriteData",
                    new[] { typeof(float[][]), typeof(int) });
                _packetOutMethod = FindPacketOutMethod();
                _writeEndOfStream = _processingStateType.GetMethod("WriteEndOfStream", Type.EmptyTypes);

                _pageHeaderProp = _oggPageType.GetProperty("Header");
                _pageBodyProp   = _oggPageType.GetProperty("Body");

                _resolvedSuccessfully = _initVbrMethod != null
                    && _oggStreamCtor != null
                    && _commentsCtor != null
                    && _buildInfoPacket != null
                    && _buildCommentsPacket != null
                    && _buildBooksPacket != null
                    && _packetInMethod != null
                    && _pageOutMethod != null
                    && _finishedProp != null
                    && _processingCreate != null
                    && _writeDataMethod != null
                    && _packetOutMethod != null
                    && _writeEndOfStream != null
                    && _pageHeaderProp != null
                    && _pageBodyProp != null;

                if (!_resolvedSuccessfully)
                {
                    Debug.LogWarning(
                        "[AudioOptimizer] OggVorbisEncoder loaded but a required member is missing. "
                        + "This usually means the pinned version's API drifted. "
                        + "Bump OggVorbisEncoderInstaller.PinnedVersion or update OggVorbisEncoderBootstrap reflection.");
                }
                break;
            }
        }

        /// <summary>
        /// PageOut has signature <c>bool PageOut(out OggPage page, bool force)</c>.
        /// Find it by walking methods with matching name + arity since
        /// expressing a ByRef type in <c>GetMethod(name, Type[])</c> is awkward.
        /// </summary>
        private static MethodInfo FindPageOutMethod()
        {
            foreach (var m in _oggStreamType.GetMethods())
            {
                if (m.Name != "PageOut") continue;
                var ps = m.GetParameters();
                if (ps.Length != 2) continue;
                if (!ps[0].ParameterType.IsByRef) continue;
                if (ps[1].ParameterType != typeof(bool)) continue;
                if (m.ReturnType != typeof(bool)) continue;
                return m;
            }
            return null;
        }

        private static MethodInfo FindPacketOutMethod()
        {
            foreach (var m in _processingStateType.GetMethods())
            {
                if (m.Name != "PacketOut") continue;
                var ps = m.GetParameters();
                if (ps.Length != 1) continue;
                if (!ps[0].ParameterType.IsByRef) continue;
                if (m.ReturnType != typeof(bool)) continue;
                return m;
            }
            return null;
        }

        // ─── Inlined WAV reader ─────────────────────────────────────────
        // Originally lived in WavReader.cs as a sibling class. Inlining it
        // here as a private nested class because Unity's Editor compile
        // refused to resolve the cross-file reference for reasons we
        // couldn't pin down (same namespace, same folder, no asmdef). Same
        // code, same behavior — just no longer split across two files.

        private static class WavIO
        {
            public class WavData
            {
                public float[] interleaved;
                public int samplesPerChannel;
                public int sampleRate;
                public int channels;
            }

            public static WavData Read(string wavAbsolutePath)
            {
                using (var fs = File.OpenRead(wavAbsolutePath))
                using (var br = new BinaryReader(fs))
                {
                    if (ReadFourCC(br) != "RIFF")
                        throw new InvalidDataException("Not a RIFF file: " + wavAbsolutePath);
                    br.ReadUInt32(); // riff size, ignored
                    if (ReadFourCC(br) != "WAVE")
                        throw new InvalidDataException("RIFF file is not WAVE: " + wavAbsolutePath);

                    int audioFormat = 0;
                    int channels = 0;
                    int sampleRate = 0;
                    int bitsPerSample = 0;
                    byte[] pcmBytes = null;

                    while (fs.Position < fs.Length - 8)
                    {
                        string chunkId = ReadFourCC(br);
                        uint chunkSize = br.ReadUInt32();
                        long chunkStart = fs.Position;

                        if (chunkId == "fmt ")
                        {
                            audioFormat   = br.ReadInt16();
                            channels      = br.ReadInt16();
                            sampleRate    = br.ReadInt32();
                            br.ReadInt32(); // byte rate
                            br.ReadInt16(); // block align
                            bitsPerSample = br.ReadInt16();
                        }
                        else if (chunkId == "data")
                        {
                            pcmBytes = br.ReadBytes((int)chunkSize);
                            break;
                        }

                        long endOfChunk = chunkStart + chunkSize;
                        if ((chunkSize & 1) != 0) endOfChunk++;
                        if (endOfChunk > fs.Length) endOfChunk = fs.Length;
                        fs.Position = endOfChunk;
                    }

                    if (pcmBytes == null)
                        throw new InvalidDataException("No data chunk in " + wavAbsolutePath);
                    if (channels < 1 || channels > 2)
                        throw new InvalidDataException(
                            "Unsupported channel count: " + channels +
                            ". The Audio Optimizer handles mono and stereo only.");
                    if (sampleRate <= 0)
                        throw new InvalidDataException("Invalid sample rate in " + wavAbsolutePath);

                    float[] interleaved;
                    int totalSamples;

                    if (audioFormat == 3 || (audioFormat == 0xFFFE && bitsPerSample == 32))
                    {
                        if (bitsPerSample != 32)
                            throw new InvalidDataException("Float WAV must be 32-bit, got " + bitsPerSample);
                        totalSamples = pcmBytes.Length / 4;
                        interleaved = new float[totalSamples];
                        Buffer.BlockCopy(pcmBytes, 0, interleaved, 0, pcmBytes.Length);
                    }
                    else if (audioFormat == 1 || audioFormat == 0xFFFE)
                    {
                        if (bitsPerSample == 16)
                        {
                            totalSamples = pcmBytes.Length / 2;
                            interleaved = new float[totalSamples];
                            for (int i = 0; i < totalSamples; i++)
                            {
                                short s = (short)(pcmBytes[i * 2] | (pcmBytes[i * 2 + 1] << 8));
                                interleaved[i] = s / 32768f;
                            }
                        }
                        else if (bitsPerSample == 8)
                        {
                            totalSamples = pcmBytes.Length;
                            interleaved = new float[totalSamples];
                            for (int i = 0; i < totalSamples; i++)
                                interleaved[i] = (pcmBytes[i] - 128) / 128f;
                        }
                        else if (bitsPerSample == 24)
                        {
                            totalSamples = pcmBytes.Length / 3;
                            interleaved = new float[totalSamples];
                            for (int i = 0; i < totalSamples; i++)
                            {
                                int b0 = pcmBytes[i * 3];
                                int b1 = pcmBytes[i * 3 + 1];
                                int b2 = pcmBytes[i * 3 + 2];
                                int s = (b2 << 24) | (b1 << 16) | (b0 << 8);
                                s >>= 8;
                                interleaved[i] = s / 8388608f;
                            }
                        }
                        else if (bitsPerSample == 32)
                        {
                            totalSamples = pcmBytes.Length / 4;
                            interleaved = new float[totalSamples];
                            for (int i = 0; i < totalSamples; i++)
                            {
                                int s = pcmBytes[i * 4]
                                      | (pcmBytes[i * 4 + 1] << 8)
                                      | (pcmBytes[i * 4 + 2] << 16)
                                      | (pcmBytes[i * 4 + 3] << 24);
                                interleaved[i] = s / 2147483648f;
                            }
                        }
                        else
                        {
                            throw new InvalidDataException("Unsupported bit depth: " + bitsPerSample);
                        }
                    }
                    else
                    {
                        throw new InvalidDataException(
                            "WAV uses non-PCM compression (audioFormat=" + audioFormat + "). "
                            + "Only uncompressed PCM and 32-bit float WAVs are supported.");
                    }

                    return new WavData
                    {
                        interleaved = interleaved,
                        samplesPerChannel = totalSamples / channels,
                        sampleRate = sampleRate,
                        channels = channels,
                    };
                }
            }

            private static string ReadFourCC(BinaryReader br)
            {
                return Encoding.ASCII.GetString(br.ReadBytes(4));
            }

            public static float[] Resample(float[] interleaved, int channels, int fromHz, int toHz, out int newSamplesPerChannel)
            {
                int samplesPerChannelIn = interleaved.Length / channels;
                if (fromHz == toHz)
                {
                    newSamplesPerChannel = samplesPerChannelIn;
                    return interleaved;
                }

                double ratio = (double)toHz / fromHz;
                newSamplesPerChannel = (int)(samplesPerChannelIn * ratio);
                float[] outSamples = new float[newSamplesPerChannel * channels];

                for (int i = 0; i < newSamplesPerChannel; i++)
                {
                    double srcIdx = i / ratio;
                    int srcIdxFloor = (int)srcIdx;
                    int srcIdxCeil = srcIdxFloor + 1;
                    if (srcIdxCeil >= samplesPerChannelIn) srcIdxCeil = samplesPerChannelIn - 1;
                    float frac = (float)(srcIdx - srcIdxFloor);

                    for (int c = 0; c < channels; c++)
                    {
                        float a = interleaved[srcIdxFloor * channels + c];
                        float b = interleaved[srcIdxCeil * channels + c];
                        outSamples[i * channels + c] = a + (b - a) * frac;
                    }
                }
                return outSamples;
            }

            public static float[] ToMono(float[] interleaved, int channels)
            {
                if (channels == 1) return interleaved;
                int samples = interleaved.Length / channels;
                float[] mono = new float[samples];
                for (int i = 0; i < samples; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                        sum += interleaved[i * channels + c];
                    mono[i] = sum / channels;
                }
                return mono;
            }

            public static float[][] DeInterleave(float[] interleaved, int channels, int samplesPerChannel)
            {
                var planar = new float[channels][];
                for (int c = 0; c < channels; c++)
                {
                    planar[c] = new float[samplesPerChannel];
                    for (int s = 0; s < samplesPerChannel; s++)
                        planar[c][s] = interleaved[s * channels + c];
                }
                return planar;
            }
        }
    }
}
#endif
