using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace M1RX;

// ---- COM for WASAPI process loopback (Win10 2004+) ----
[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ACl
{
    [PreserveSig] int Initialize(int mode, int flags, long dur, long per, IntPtr fmt, IntPtr sess);
    [PreserveSig] int GetBufferSize(out uint n);
    [PreserveSig] int GetStreamLatency(out long l);
    [PreserveSig] int GetCurrentPadding(out uint p);
    [PreserveSig] int IsFormatSupported(int mode, IntPtr fmt, out IntPtr close);
    [PreserveSig] int GetMixFormat(out IntPtr fmt);
    [PreserveSig] int GetDevicePeriod(out long d, out long m);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr h);
    [PreserveSig] int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ACap
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devPos, out ulong qpc);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint n);
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAsyncOp
{
    void GetActivateResult([MarshalAs(UnmanagedType.Error)] out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object o);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IComp { void ActivateCompleted(IAsyncOp op); }

[ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAgile { }

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
class Hnd : IComp, IAgile
{
    public ManualResetEvent Done = new(false);
    public int Hr; public object Obj;
    public void ActivateCompleted(IAsyncOp op) { op.GetActivateResult(out Hr, out Obj); Done.Set(); }
}

// capture audio of ONE process tree -> 48k s16 stereo into Buf
class PLoop : IDisposable
{
    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr prm, IComp handler, out IAsyncOp op);

    public static string Err;
    public readonly BufferedWaveProvider Buf = new(new WaveFormat(48000, 16, 2)) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(1), ReadFully = false };
    public volatile bool Dead;
    readonly int pid; volatile bool stop; readonly Thread t;
    public PLoop(int pid) { this.pid = pid; t = new Thread(Loop) { IsBackground = true }; t.Start(); }

    static ACl Activate(int pid)
    {
        var pp = Marshal.AllocHGlobal(12);                 // AUDIOCLIENT_ACTIVATION_PARAMS
        Marshal.WriteInt32(pp, 0, 1);                      // PROCESS_LOOPBACK
        Marshal.WriteInt32(pp, 4, pid);
        Marshal.WriteInt32(pp, 8, 0);                      // INCLUDE_TARGET_PROCESS_TREE
        var pv = Marshal.AllocHGlobal(24);                 // PROPVARIANT VT_BLOB
        Marshal.WriteInt64(pv, 0, 0); Marshal.WriteInt64(pv, 8, 0); Marshal.WriteInt64(pv, 16, 0);
        Marshal.WriteInt16(pv, 0, 65);
        Marshal.WriteInt32(pv, 8, 12);
        Marshal.WriteIntPtr(pv, 16, pp);
        var h = new Hnd();
        ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(ACl).GUID, pv, h, out _);
        if (!h.Done.WaitOne(5000)) throw new TimeoutException("activate timeout");
        if (h.Hr != 0) throw new InvalidOperationException("activate hr=0x" + h.Hr.ToString("X"));
        return (ACl)h.Obj;
    }

    void Loop()
    {
        bool started = false;
        try
        {
            var ac = Activate(pid);
            var f = Marshal.AllocHGlobal(18);
            Marshal.WriteInt16(f, 0, 1); Marshal.WriteInt16(f, 2, 2); Marshal.WriteInt32(f, 4, 48000);
            Marshal.WriteInt32(f, 8, 192000); Marshal.WriteInt16(f, 12, 4); Marshal.WriteInt16(f, 14, 16); Marshal.WriteInt16(f, 16, 0);
            int hr = ac.Initialize(0, 0x00020000 | 0x00040000 | unchecked((int)0x80000000) | 0x08000000, 2000000, 0, f, IntPtr.Zero);
            if (hr != 0) throw new InvalidOperationException("init hr=0x" + hr.ToString("X"));
            var ev = new AutoResetEvent(false);
            ac.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
            ac.GetService(typeof(ACap).GUID, out var o);
            var cc = (ACap)o;
            ac.Start(); started = true;
            var tmp = new byte[8192];
            while (!stop)
            {
                ev.WaitOne(100);
                if (cc.GetNextPacketSize(out var n) != 0) break;
                while (n > 0)
                {
                    if (cc.GetBuffer(out var p, out var fr, out var fl, out _, out _) != 0) break;
                    int bytes = (int)fr * 4;
                    if (bytes > tmp.Length) tmp = new byte[bytes];
                    if ((fl & 2) != 0) Array.Clear(tmp, 0, bytes); else Marshal.Copy(p, tmp, 0, bytes);
                    cc.ReleaseBuffer(fr);
                    Buf.AddSamples(tmp, 0, bytes);
                    cc.GetNextPacketSize(out n);
                }
            }
            ac.Stop();
        }
        catch (Exception e) { if (!started) Err = "เสียงรายแอปใช้ไม่ได้ (ต้อง Windows 10 2004+): " + e.Message; }
        Dead = true;
    }
    public void Dispose() { stop = true; t.Join(500); }
}

// mixer: mic + system/app sources -> 48k s16 stereo, wall-clock paced, written to ffmpeg pipes
public class Mix
{
    class Src { public BufferedWaveProvider B; public ISampleProvider S; public PLoop P; }
    readonly Cfg c; readonly object lk = new(); readonly List<Stream> sinks = new();
    readonly Dictionary<string, Src> apps = new();
    Src mic, sys; WasapiCapture micCap; WasapiLoopbackCapture sysCap;
    Thread th; volatile bool run; int refs; bool warned;
    public volatile bool MicMuted;
    public event Action<string> Warn;
    public Mix(Cfg c) { this.c = c; }

    static WaveFormat Std(WaveFormat f) => f is WaveFormatExtensible e ? e.ToStandardWaveFormat() : f;
    static ISampleProvider Norm(BufferedWaveProvider b)
    {
        ISampleProvider sp = b.ToSampleProvider();
        if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2) sp = new MultiplexingSampleProvider(new[] { sp }, 2);
        if (sp.WaveFormat.SampleRate != 48000) sp = new WdlResamplingSampleProvider(sp, 48000);
        return sp;
    }
    static BufferedWaveProvider NewBuf(WaveFormat f) => new(Std(f)) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(1), ReadFully = false };

    public static HashSet<string> SessionNames()
    {
        var h = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var en = new MMDeviceEnumerator();
            var ss = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).AudioSessionManager.Sessions;
            for (int i = 0; i < ss.Count; i++)
            {
                var s = ss[i];
                if (s.IsSystemSoundsSession) continue;
                try { h.Add(Process.GetProcessById((int)s.GetProcessID).ProcessName); } catch { }
            }
        }
        catch { }
        h.Remove("M1RXRec");
        return h;
    }

    public void Acquire() { lock (lk) { if (refs++ == 0) Start(); } }
    public void Release() { lock (lk) { if (refs > 0 && --refs == 0) Stop(); } }

    public Stream NewSink(string name)
    {
        var p = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
        p.WaitForConnectionAsync().ContinueWith(t => { if (t.IsCompletedSuccessfully) lock (lk) sinks.Add(p); });
        return p;
    }
    public void Drop(Stream s) { lock (lk) sinks.Remove(s); try { s.Dispose(); } catch { } Release(); }

    void Start()
    {
        MicMuted = false; warned = false;
        try
        {
            if (c.MicOn && c.Mic != "")
            {
                var dev = new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault(d => d.FriendlyName == c.Mic);
                if (dev != null)
                {
                    micCap = new WasapiCapture(dev);
                    var b = NewBuf(micCap.WaveFormat);
                    micCap.DataAvailable += (s, e) => b.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    micCap.StartRecording();
                    mic = new Src { B = b, S = Norm(b) };
                }
            }
            if (c.AudMode == 0)
            {
                sysCap = new WasapiLoopbackCapture();
                var b = NewBuf(sysCap.WaveFormat);
                sysCap.DataAvailable += (s, e) => b.AddSamples(e.Buffer, 0, e.BytesRecorded);
                sysCap.StartRecording();
                sys = new Src { B = b, S = Norm(b) };
            }
        }
        catch (Exception e) { Warn?.Invoke("เสียง: " + e.Message); }
        run = true;
        th = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        th.Start();
    }

    void Stop()
    {
        run = false; th?.Join(1000);
        try { micCap?.StopRecording(); micCap?.Dispose(); } catch { }
        try { sysCap?.StopRecording(); sysCap?.Dispose(); } catch { }
        micCap = null; sysCap = null; mic = null; sys = null;
        foreach (var a in apps.Values) a.P.Dispose();
        apps.Clear();
        lock (lk) { foreach (var s in sinks) try { s.Dispose(); } catch { } sinks.Clear(); }
    }

    HashSet<string> Targets()
    {
        var ex = new HashSet<string>(c.Apps, StringComparer.OrdinalIgnoreCase);
        if (c.AudMode == 1) return ex;
        if (c.AudMode == 2) { var all = SessionNames(); all.ExceptWith(ex); return all; }
        return new();
    }

    static int RootPid(string n)
    {
        try
        {
            return Process.GetProcessesByName(n)
                .OrderBy(p => { try { return p.StartTime; } catch { return DateTime.MaxValue; } })
                .FirstOrDefault()?.Id ?? 0;
        }
        catch { return 0; }
    }

    void Rescan()
    {
        var want = Targets();
        foreach (var n in apps.Keys.ToList())
            if (!want.Contains(n) || apps[n].P.Dead) { apps[n].P.Dispose(); apps.Remove(n); }
        if (PLoop.Err != null) { if (!warned) { warned = true; Warn?.Invoke(PLoop.Err); } return; }
        foreach (var n in want)
            if (!apps.ContainsKey(n))
            {
                int pid = RootPid(n);
                if (pid > 0) { var p = new PLoop(pid); apps[n] = new Src { P = p, B = p.Buf, S = Norm(p.Buf) }; }
            }
    }

    static void Trim(Src s) { if (s.B.BufferedDuration > TimeSpan.FromMilliseconds(400)) s.B.ClearBuffer(); }

    void Loop()
    {
        var sw = Stopwatch.StartNew();
        long sent = 0, nextScan = 0;
        var acc = new float[960]; var tmp = new float[960]; var pcm = new byte[1920];
        while (run)
        {
            long ms = sw.ElapsedMilliseconds;
            if (ms >= nextScan) { try { Rescan(); } catch { } nextScan = ms + 2000; }
            if (ms * 48 - sent < 480) { Thread.Sleep(2); continue; }
            sent += 480;
            Array.Clear(acc, 0, 960);
            void Add(Src s, double vol, bool mute)
            {
                if (s == null) return;
                Trim(s);
                int n = s.S.Read(tmp, 0, 960);
                if (mute) return;
                float v = (float)vol;
                for (int i = 0; i < n; i++) acc[i] += tmp[i] * v;
            }
            Add(mic, c.MicVol, MicMuted);
            Add(sys, c.SysVol, false);
            foreach (var a in apps.Values) Add(a, c.SysVol, false);
            for (int i = 0; i < 960; i++)
            {
                short v = (short)Math.Clamp(acc[i] * 32767f, -32768f, 32767f);
                pcm[i * 2] = (byte)v; pcm[i * 2 + 1] = (byte)(v >> 8);
            }
            lock (lk)
                for (int i = sinks.Count - 1; i >= 0; i--)
                    try { sinks[i].Write(pcm, 0, 1920); } catch { sinks.RemoveAt(i); }
        }
    }
}
