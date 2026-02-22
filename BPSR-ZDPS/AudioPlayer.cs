using System;
using System.IO;
using System.Runtime.InteropServices;
using OpenTK.Audio.OpenAL;
using Serilog;

namespace BPSR_ZDPS
{
    /// <summary>
    /// Cross-platform audio player using OpenAL
    /// </summary>
    public class AudioPlayer : IDisposable
    {
        private int source;
        private int buffer;
        private ALDevice device;
        private ALContext context;
        private bool isPlaying;
        private bool disposed;
        private bool isInitialized;

        public AudioPlayer()
        {
            try
            {
                // Open default audio device
                device = ALC.OpenDevice(null);
                if (device.Handle == IntPtr.Zero)
                {
                    Log.Error("Failed to open OpenAL device");
                    return;
                }

                // Create audio context
                context = ALC.CreateContext(device, (int[])null!);
                if (context.Handle == IntPtr.Zero)
                {
                    Log.Error("Failed to create OpenAL context");
                    ALC.CloseDevice(device);
                    device = default;
                    return;
                }

                ALC.MakeContextCurrent(context);

                // Generate source and buffer
                source = AL.GenSource();
                buffer = AL.GenBuffer();
                isInitialized = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to initialize OpenAL audio player");
            }
        }

        public bool LoadWavFile(string filepath)
        {
            if (!isInitialized)
            {
                Log.Error("OpenAL not initialized");
                return false;
            }

            try
            {
                // Read WAV file
                var (channels, bitsPerSample, sampleRate, audioData) = ReadWavFile(filepath);

                if (audioData == null || audioData.Length == 0)
                {
                    Log.Error($"Failed to read WAV file: {filepath}");
                    return false;
                }

                // Determine OpenAL format
                ALFormat format;
                if (channels == 1)
                {
                    format = bitsPerSample == 8 ? ALFormat.Mono8 : ALFormat.Mono16;
                }
                else if (channels == 2)
                {
                    format = bitsPerSample == 8 ? ALFormat.Stereo8 : ALFormat.Stereo16;
                }
                else
                {
                    Log.Error($"Unsupported channel count: {channels}");
                    return false;
                }

                // Upload audio data to buffer
                AL.BufferData(buffer, format, audioData, sampleRate);

                // Check for errors
                var error = AL.GetError();
                if (error != ALError.NoError)
                {
                    Log.Error($"OpenAL error while loading audio: {error}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error loading WAV file: {filepath}");
                return false;
            }
        }

        public void Play(float volume = 1.0f, bool loop = false)
        {
            if (!isInitialized)
            {
                return;
            }

            try
            {
                Stop(); // Stop any existing playback

                AL.Source(source, ALSourcei.Buffer, buffer);
                AL.Source(source, ALSourcef.Gain, Math.Clamp(volume, 0.0f, 1.0f));
                AL.Source(source, ALSourceb.Looping, loop);
                AL.SourcePlay(source);
                isPlaying = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error playing audio");
            }
        }

        public void Stop()
        {
            if (!isInitialized)
            {
                return;
            }

            try
            {
                if (isPlaying)
                {
                    AL.SourceStop(source);
                    isPlaying = false;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error stopping audio");
            }
        }

        public bool IsPlaying()
        {
            if (!isInitialized || !isPlaying)
            {
                return false;
            }

            try
            {
                AL.GetSource(source, ALGetSourcei.SourceState, out int state);
                return state == (int)ALSourceState.Playing;
            }
            catch
            {
                return false;
            }
        }

        private static (int channels, int bitsPerSample, int sampleRate, byte[]? data) ReadWavFile(string filepath)
        {
            try
            {
                using var fs = new FileStream(filepath, FileMode.Open, FileAccess.Read);
                using var br = new BinaryReader(fs);

                // Read RIFF header
                var riff = new string(br.ReadChars(4));
                if (riff != "RIFF")
                {
                    Log.Error($"Invalid WAV file (not RIFF): {filepath}");
                    return (0, 0, 0, null);
                }

                br.ReadInt32(); // File size
                var wave = new string(br.ReadChars(4));
                if (wave != "WAVE")
                {
                    Log.Error($"Invalid WAV file (not WAVE): {filepath}");
                    return (0, 0, 0, null);
                }

                // Read fmt chunk
                var fmt = new string(br.ReadChars(4));
                if (fmt != "fmt ")
                {
                    Log.Error($"Invalid WAV file (no fmt chunk): {filepath}");
                    return (0, 0, 0, null);
                }

                int fmtSize = br.ReadInt32();
                int audioFormat = br.ReadInt16(); // 1 = PCM
                int channels = br.ReadInt16();
                int sampleRate = br.ReadInt32();
                br.ReadInt32(); // Byte rate
                br.ReadInt16(); // Block align
                int bitsPerSample = br.ReadInt16();

                // Skip any extra fmt data
                if (fmtSize > 16)
                {
                    br.ReadBytes(fmtSize - 16);
                }

                // Find data chunk
                while (true)
                {
                    var chunkType = new string(br.ReadChars(4));
                    int chunkSize = br.ReadInt32();

                    if (chunkType == "data")
                    {
                        // Read audio data
                        byte[] data = br.ReadBytes(chunkSize);
                        return (channels, bitsPerSample, sampleRate, data);
                    }
                    else
                    {
                        // Skip this chunk
                        br.ReadBytes(chunkSize);
                    }

                    if (fs.Position >= fs.Length)
                    {
                        Log.Error($"No data chunk found in WAV file: {filepath}");
                        return (0, 0, 0, null);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error reading WAV file: {filepath}");
                return (0, 0, 0, null);
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;

            try
            {
                Stop();

                if (buffer != 0)
                {
                    AL.DeleteBuffer(buffer);
                    buffer = 0;
                }

                if (source != 0)
                {
                    AL.DeleteSource(source);
                    source = 0;
                }

                if (context.Handle != IntPtr.Zero)
                {
                    ALC.MakeContextCurrent(ALContext.Null);
                    ALC.DestroyContext(context);
                    context = default;
                }

                if (device.Handle != IntPtr.Zero)
                {
                    ALC.CloseDevice(device);
                    device = default;
                }

                isInitialized = false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error disposing audio player");
            }
        }
    }
}
