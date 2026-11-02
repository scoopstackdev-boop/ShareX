#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace ShareX
{
    public enum DesktopAudioMode
    {
        // A DirectShow device such as virtual-audio-capturer, or none
        Device,
        All,
        AllExcept,
        Only
    }

    // Captures app audio with Windows process loopback (Windows 10 2004 and newer), mixes it
    // and streams 48 kHz stereo float PCM to ffmpeg through a named pipe
    public sealed class AppAudioCapture : IDisposable
    {
        public const int SampleRate = 48000;
        public const int Channels = 2;

        public string PipePath => @"\\.\pipe\" + pipeName;
        public string Error { get; private set; }

        public static bool IsSupported => Environment.OSVersion.Version.Build >= 19041;

        private readonly DesktopAudioMode mode;
        private readonly HashSet<string> apps;
        private readonly string pipeName = "sharex-audio-" + Guid.NewGuid().ToString("N");
        private readonly Dictionary<uint, LoopbackSource> sources = new Dictionary<uint, LoopbackSource>();
        private readonly object sourcesLock = new object();
        private readonly int ownProcessId = Environment.ProcessId;

        private NamedPipeServerStream pipe;
        private Thread writerThread, scanThread;
        private volatile bool stopping;

        public AppAudioCapture(DesktopAudioMode mode, IEnumerable<string> apps)
        {
            this.mode = mode;
            this.apps = new HashSet<string>(apps.Select(NormalizeExe).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
        }

        public static string NormalizeExe(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length > 0 && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
            return name;
        }

        public static List<string> ParseApps(string text)
        {
            return (text ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(NormalizeExe).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public void Start()
        {
            pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.None, 0, 1 << 20);

            scanThread = new Thread(ScanLoop) { IsBackground = true, Name = "App audio scan" };
            scanThread.SetApartmentState(ApartmentState.MTA);
            scanThread.Start();

            writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "App audio writer" };
            writerThread.Start();
        }

        // Keeps the sources in line with what is playing: everything except ShareX, or per app
        private void ScanLoop()
        {
            if (mode == DesktopAudioMode.All)
            {
                // Exclude mode on our own process captures every other app in one stream
                AddSource((uint)ownProcessId, true);
                return;
            }

            while (!stopping)
            {
                try
                {
                    foreach (uint pid in GetAudioSessionProcessIds())
                    {
                        if (pid == 0 || pid == ownProcessId) continue;

                        lock (sourcesLock)
                        {
                            if (sources.ContainsKey(pid)) continue;
                        }

                        string exe = GetProcessExe(pid);
                        if (exe == null || exe.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)) continue;

                        bool listed = apps.Contains(exe);

                        if ((mode == DesktopAudioMode.Only && listed) || (mode == DesktopAudioMode.AllExcept && !listed))
                        {
                            AddSource(pid, false);
                        }
                    }

                    RemoveExitedSources();
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e, "App audio scan failed");
                }

                for (int i = 0; i < 20 && !stopping; i++) Thread.Sleep(100);
            }
        }

        private void AddSource(uint pid, bool exclude)
        {
            LoopbackSource source = new LoopbackSource(pid, exclude);

            lock (sourcesLock)
            {
                sources[pid] = source;
            }

            string error = source.Start();

            if (error != null)
            {
                DebugHelper.WriteLine($"App audio capture for process {pid} failed: {error}");
                if (Error == null) Error = error;
            }
            else
            {
                DebugHelper.WriteLine($"App audio capture started for process {pid} ({(exclude ? "everything except it" : GetProcessExe(pid))})");
            }
        }

        private void RemoveExitedSources()
        {
            List<LoopbackSource> exited = new List<LoopbackSource>();

            lock (sourcesLock)
            {
                foreach (KeyValuePair<uint, LoopbackSource> pair in sources.ToList())
                {
                    if (GetProcessExe(pair.Key) == null)
                    {
                        exited.Add(pair.Value);
                        sources.Remove(pair.Key);
                    }
                }
            }

            foreach (LoopbackSource source in exited) source.Dispose();
        }

        // Writes mixed audio at real-time pace, filling gaps with silence so ffmpeg sees one continuous stream
        private void WriterLoop()
        {
            try
            {
                pipe.WaitForConnection();
                Stopwatch clock = Stopwatch.StartNew();
                long written = 0;

                while (!stopping)
                {
                    long due = clock.ElapsedTicks * SampleRate / Stopwatch.Frequency;
                    int frames = (int)Math.Min(due - written, SampleRate);

                    if (frames > 0)
                    {
                        float[] mix = new float[frames * Channels];

                        lock (sourcesLock)
                        {
                            foreach (LoopbackSource source in sources.Values)
                            {
                                source.MixInto(mix, frames);
                            }
                        }

                        byte[] bytes = new byte[mix.Length * 4];

                        for (int i = 0; i < mix.Length; i++)
                        {
                            float sample = Math.Max(-1f, Math.Min(1f, mix[i]));
                            BitConverter.TryWriteBytes(new Span<byte>(bytes, i * 4, 4), sample);
                        }

                        pipe.Write(bytes, 0, bytes.Length);
                        written += frames;
                    }

                    Thread.Sleep(10);
                }
            }
            catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is InvalidOperationException)
            {
                // ffmpeg closed the pipe
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "App audio writer failed");
            }
        }

        public void Dispose()
        {
            stopping = true;

            try { pipe?.Dispose(); } catch { }

            List<LoopbackSource> all;

            lock (sourcesLock)
            {
                all = sources.Values.ToList();
                sources.Clear();
            }

            foreach (LoopbackSource source in all) source.Dispose();
        }

        private static string GetProcessExe(uint pid)
        {
            try
            {
                using (Process p = Process.GetProcessById((int)pid))
                {
                    if (p.HasExited) return null;
                    return p.ProcessName + ".exe";
                }
            }
            catch
            {
                return null;
            }
        }

        // Exe names of apps with an audio session on the default output, for the settings picker
        public static List<string> GetAudioApps()
        {
            List<string> result = new List<string>();

            Thread thread = new Thread(() =>
            {
                try
                {
                    int own = Environment.ProcessId;

                    foreach (uint pid in GetAudioSessionProcessIds())
                    {
                        if (pid == 0 || pid == own) continue;
                        string exe = GetProcessExe(pid);
                        if (exe != null && !result.Contains(exe, StringComparer.OrdinalIgnoreCase)) result.Add(exe);
                    }
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                }
            });

            // Core Audio wants a multithreaded apartment, the UI thread is single threaded
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            thread.Join(3000);

            return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<uint> GetAudioSessionProcessIds()
        {
            List<uint> pids = new List<uint>();
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioSessionManager2 manager = null;
            IAudioSessionEnumerator sessions = null;

            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

                if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out device) != 0 || device == null) return pids;

                Guid iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object managerObject) != 0) return pids;
                manager = (IAudioSessionManager2)managerObject;

                if (manager.GetSessionEnumerator(out sessions) != 0 || sessions == null) return pids;
                sessions.GetCount(out int count);

                for (int i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out IAudioSessionControl2 session) == 0 && session != null)
                    {
                        if (session.GetProcessId(out uint pid) == 0 && pid != 0 && !pids.Contains(pid))
                        {
                            pids.Add(pid);
                        }

                        Marshal.ReleaseComObject(session);
                    }
                }
            }
            finally
            {
                if (sessions != null) Marshal.ReleaseComObject(sessions);
                if (manager != null) Marshal.ReleaseComObject(manager);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }

            return pids;
        }

        // One process loopback stream, buffered until the writer mixes it
        private sealed class LoopbackSource : IDisposable
        {
            private const int Capacity = SampleRate * Channels * 2;

            private readonly uint processId;
            private readonly bool exclude;
            private readonly float[] ring = new float[Capacity];
            private readonly object ringLock = new object();
            private int head, count;
            private Thread thread;
            private volatile bool stopping;
            private string startError;
            private readonly ManualResetEventSlim started = new ManualResetEventSlim();

            public LoopbackSource(uint processId, bool exclude)
            {
                this.processId = processId;
                this.exclude = exclude;
            }

            // Returns null when capture is running, otherwise the reason
            public string Start()
            {
                thread = new Thread(CaptureLoop) { IsBackground = true, Name = "App audio " + processId };
                thread.SetApartmentState(ApartmentState.MTA);
                thread.Start();

                if (!started.Wait(5000)) return "audio activation timed out";
                return startError;
            }

            private void CaptureLoop()
            {
                IAudioClient client = null;
                IAudioCaptureClient capture = null;
                IntPtr format = IntPtr.Zero;

                try
                {
                    client = Activate(processId, exclude);

                    format = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WAVEFORMATEX)));
                    Marshal.StructureToPtr(new WAVEFORMATEX()
                    {
                        wFormatTag = 3, // IEEE float
                        nChannels = Channels,
                        nSamplesPerSec = SampleRate,
                        wBitsPerSample = 32,
                        nBlockAlign = Channels * 4,
                        nAvgBytesPerSec = SampleRate * Channels * 4,
                        cbSize = 0
                    }, format, false);

                    const uint flags = 0x00020000 /* LOOPBACK */ | 0x80000000 /* AUTOCONVERTPCM */ | 0x08000000 /* SRC_DEFAULT_QUALITY */;
                    Check(client.Initialize(0, flags, 2000000 /* 200 ms */, 0, format, IntPtr.Zero), "Initialize");

                    Guid captureId = typeof(IAudioCaptureClient).GUID;
                    Check(client.GetService(ref captureId, out object captureObject), "GetService");
                    capture = (IAudioCaptureClient)captureObject;

                    Check(client.Start(), "Start");
                }
                catch (Exception e)
                {
                    startError = e.Message;
                    started.Set();
                    Release(capture, client, format);
                    return;
                }

                started.Set();

                try
                {
                    while (!stopping)
                    {
                        while (capture.GetNextPacketSize(out uint packet) == 0 && packet > 0)
                        {
                            if (capture.GetBuffer(out IntPtr data, out uint frames, out uint bufferFlags, out _, out _) != 0) break;

                            float[] samples = new float[frames * Channels];

                            // 0x2 = silent, the buffer contents are undefined
                            if ((bufferFlags & 0x2) == 0 && data != IntPtr.Zero)
                            {
                                Marshal.Copy(data, samples, 0, samples.Length);
                            }

                            capture.ReleaseBuffer(frames);
                            Push(samples);
                        }

                        Thread.Sleep(5);
                    }

                    client.Stop();
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e, "App audio capture stopped");
                }
                finally
                {
                    Release(capture, client, format);
                }
            }

            private static void Release(IAudioCaptureClient capture, IAudioClient client, IntPtr format)
            {
                if (capture != null) Marshal.ReleaseComObject(capture);
                if (client != null) Marshal.ReleaseComObject(client);
                if (format != IntPtr.Zero) Marshal.FreeHGlobal(format);
            }

            private void Push(float[] samples)
            {
                lock (ringLock)
                {
                    foreach (float sample in samples)
                    {
                        ring[(head + count) % Capacity] = sample;

                        if (count < Capacity)
                        {
                            count++;
                        }
                        else
                        {
                            // Full, the oldest sample goes
                            head = (head + 1) % Capacity;
                        }
                    }
                }
            }

            public void MixInto(float[] mix, int frames)
            {
                lock (ringLock)
                {
                    // More than 200 ms waiting means this source drifted ahead, catch it up
                    int max = SampleRate * Channels / 5 + frames * Channels;

                    if (count > max)
                    {
                        int drop = (count - max) / Channels * Channels;
                        head = (head + drop) % Capacity;
                        count -= drop;
                    }

                    int n = Math.Min(count, frames * Channels);

                    for (int i = 0; i < n; i++)
                    {
                        mix[i] += ring[(head + i) % Capacity];
                    }

                    head = (head + n) % Capacity;
                    count -= n;
                }
            }

            public void Dispose()
            {
                stopping = true;
                thread?.Join(1000);
            }
        }

        private static void Check(int hr, string what)
        {
            if (hr != 0) throw new Exception($"{what} failed (0x{hr:X8})");
        }

        #region Process loopback activation

        private static IAudioClient Activate(uint processId, bool exclude)
        {
            AUDIOCLIENT_ACTIVATION_PARAMS parameters = new AUDIOCLIENT_ACTIVATION_PARAMS()
            {
                ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
                TargetProcessId = processId,
                ProcessLoopbackMode = exclude ? 1 : 0 // exclude or include the process tree
            };

            int paramsSize = Marshal.SizeOf(typeof(AUDIOCLIENT_ACTIVATION_PARAMS));
            IntPtr paramsPtr = Marshal.AllocHGlobal(paramsSize);
            IntPtr propVariant = Marshal.AllocHGlobal(24);

            try
            {
                Marshal.StructureToPtr(parameters, paramsPtr, false);

                // PROPVARIANT holding a VT_BLOB that points at the parameters
                for (int i = 0; i < 24; i++) Marshal.WriteByte(propVariant, i, 0);
                Marshal.WriteInt16(propVariant, 0, 65); // VT_BLOB
                Marshal.WriteInt32(propVariant, 8, paramsSize);
                Marshal.WriteIntPtr(propVariant, 16, paramsPtr);

                ActivationHandler handler = new ActivationHandler();
                Guid iid = typeof(IAudioClient).GUID;
                int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, propVariant, handler, out IActivateAudioInterfaceAsyncOperation operation);
                Check(hr, "ActivateAudioInterfaceAsync");

                if (!handler.Done.WaitOne(5000)) throw new Exception("audio activation timed out");

                operation.GetActivateResult(out int result, out object audioInterface);
                Check(result, "Process loopback activation");

                return (IAudioClient)audioInterface;
            }
            finally
            {
                Marshal.FreeHGlobal(propVariant);
                Marshal.FreeHGlobal(paramsPtr);
            }
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEvent Done = new ManualResetEvent(false);

            public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
            {
                Done.Set();
                return 0;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AUDIOCLIENT_ACTIVATION_PARAMS
        {
            public int ActivationType;
            public uint TargetProcessId;
            public int ProcessLoopbackMode;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WAVEFORMATEX
        {
            public short wFormatTag;
            public short nChannels;
            public int nSamplesPerSec;
            public int nAvgBytesPerSec;
            public short nBlockAlign;
            public short wBitsPerSample;
            public short cbSize;
        }

        [DllImport("Mmdevapi.dll", ExactSpelling = true)]
        private static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, ref Guid riid, IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation activationOperation);

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceAsyncOperation
        {
            void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        }

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceCompletionHandler
        {
            [PreserveSig]
            int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
        }

        // Marks the handler as callable from any thread, required by ActivateAudioInterfaceAsync
        [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAgileObject
        {
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr eventHandle);
            [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject
        {
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IntPtr sessionControl);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr audioVolume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int sessionCount);
            [PreserveSig] int GetSession(int sessionIndex, out IAudioSessionControl2 session);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName(out IntPtr name);
            [PreserveSig] int SetDisplayName(IntPtr value, IntPtr eventContext);
            [PreserveSig] int GetIconPath(out IntPtr path);
            [PreserveSig] int SetIconPath(IntPtr value, IntPtr eventContext);
            [PreserveSig] int GetGroupingParam(out Guid groupingParam);
            [PreserveSig] int SetGroupingParam(ref Guid groupingParam, IntPtr eventContext);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);

            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier(out IntPtr id);
            [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
            [PreserveSig] int GetProcessId(out uint processId);
        }

        #endregion Process loopback activation
    }
}
