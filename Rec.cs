using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;

namespace M1RX;

// ffmpeg ddagrab (DXGI, GPU) -> hardware encoder; audio comes from Mix via named pipe
public class Rec
{
    readonly Cfg c; readonly Mix mx;
    Process lng, rep; Stream lp, rpp;
    readonly string tmp = Path.Combine(Path.GetTempPath(), "m1rx_replay");
    public static readonly string Ff = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
    public event Action<string> Died;
    public Rec(Cfg c, Mix mx) { this.c = c; this.mx = mx; }
    public bool Recording => lng is { HasExited: false };
    public bool Replaying => rep is { HasExited: false };
    bool Aud => (c.MicOn && c.Mic != "") || c.AudMode != 3;

    string Gpu()
    {
        if (c.Gpu != "Auto") return c.Gpu;
        try
        {
            foreach (var o in new ManagementObjectSearcher("select Name from Win32_VideoController").Get())
            {
                var n = o["Name"]?.ToString() ?? "";
                if (n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
                if (n.Contains("AMD") || n.Contains("Radeon")) return "AMD";
            }
        }
        catch { }
        return "Intel";
    }

    string Args(string tail, out Stream pipe)
    {
        var g = Gpu();
        int nw = N.GetSystemMetrics(0), nh = N.GetSystemMetrics(1);
        bool scale = nw != c.W || nh != c.H;
        string vf = scale
            ? $"-vf \"hwdownload,format=bgra,scale={c.W}:{c.H}:flags=fast_bilinear,format=nv12\""
            : g == "Intel" ? "-vf \"hwmap=derive_device=qsv,format=qsv\"" : "";
        string suf = g == "NVIDIA" ? "nvenc" : g == "AMD" ? "amf" : "qsv";
        string opt = g == "NVIDIA" ? "-preset p1 -tune ll -rc cbr"
                   : g == "AMD" ? "-quality speed -rc cbr"
                   : "-preset veryfast -look_ahead 0";
        int m = c.Mbps;
        string enc = $"-c:v {c.Codec}_{suf} {opt} -b:v {m}M -maxrate {m}M -bufsize {m * 2}M -g {c.Fps * 2}";
        string ain = "", map = "-map 0:v";
        pipe = null;
        if (Aud)
        {
            mx.Acquire();
            var nm = "m1rx_" + Guid.NewGuid().ToString("N");
            pipe = mx.NewSink(nm);
            ain = $"-thread_queue_size 1024 -f s16le -ar 48000 -ac 2 -i \"\\\\.\\pipe\\{nm}\"";
            map = "-map 0:v -map 1:a -c:a aac -b:a 160k -af aresample=async=1000";
        }
        return $"-hide_banner -loglevel error -y -f lavfi -i \"ddagrab=0:framerate={c.Fps}:draw_mouse=1\" {ain} {map} {vf} {enc} {tail}";
    }

    Process Run(string a)
    {
        var err = new StringBuilder();
        var p = new Process
        {
            StartInfo = new ProcessStartInfo(Ff, a) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true },
            EnableRaisingEvents = true
        };
        p.ErrorDataReceived += (s, e) => { if (e.Data != null && err.Length < 800) err.AppendLine(e.Data); };
        p.Exited += (s, e) => Died?.Invoke(err.ToString());
        p.Start();
        p.BeginErrorReadLine();
        return p;
    }

    void Stop(ref Process p, ref Stream pipe)
    {
        if (p != null)
        {
            p.EnableRaisingEvents = false; // intentional stop, no Died event
            if (pipe != null) { mx.Drop(pipe); pipe = null; }
            try
            {
                if (!p.HasExited) { p.StandardInput.Write("q"); p.StandardInput.Flush(); if (!p.WaitForExit(6000)) p.Kill(); }
            }
            catch { try { p.Kill(); } catch { } }
            p = null;
        }
    }

    // call after ffmpeg died on its own: release audio
    public void Tidy()
    {
        if (lng is { HasExited: true }) Stop(ref lng, ref lp);
        if (rep is { HasExited: true }) Stop(ref rep, ref rpp);
    }

    public string StartRec()
    {
        if (!File.Exists(Ff)) return null;
        Directory.CreateDirectory(c.Dir);
        var f = Path.Combine(c.Dir, $"M1RX_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        lng = Run(Args($"-movflags +frag_keyframe+empty_moov \"{f}\"", out lp));
        return f;
    }
    public void StopRec() => Stop(ref lng, ref lp);

    public bool StartRep()
    {
        if (!File.Exists(Ff)) return false;
        try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        Directory.CreateDirectory(tmp);
        int wrap = c.ReplaySec / 2 + 2;
        rep = Run(Args($"-f segment -segment_time 2 -segment_format mpegts -segment_wrap {wrap} -reset_timestamps 1 \"{tmp}\\s%03d.ts\"", out rpp));
        return true;
    }
    public void StopRep() { Stop(ref rep, ref rpp); try { Directory.Delete(tmp, true); } catch { } }
    public void StopAll() { StopRec(); StopRep(); }

    // newest segments -> mp4 by stream copy (no re-encode)
    public Task<string> SaveAsync() => Task.Run(() =>
    {
        try
        {
            int n = c.ReplaySec / 2 + 1;
            var segs = new DirectoryInfo(tmp).GetFiles("s*.ts").OrderByDescending(x => x.LastWriteTime).Take(n).Reverse().ToList();
            if (segs.Count == 0) return null;
            var sv = Path.Combine(tmp, "save"); Directory.CreateDirectory(sv);
            var list = new StringBuilder(); int i = 0;
            foreach (var s in segs)
            {
                var d = Path.Combine(sv, $"{i++:D4}.ts");
                using (var a = new FileStream(s.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var b = File.Create(d)) a.CopyTo(b);
                list.AppendLine($"file '{d.Replace("\\", "/")}'");
            }
            var lf = Path.Combine(sv, "list.txt"); File.WriteAllText(lf, list.ToString());
            Directory.CreateDirectory(c.Dir);
            var o = Path.Combine(c.Dir, $"Replay_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            var p = Process.Start(new ProcessStartInfo(Ff, $"-hide_banner -loglevel error -y -f concat -safe 0 -i \"{lf}\" -c copy -movflags +faststart \"{o}\"") { CreateNoWindow = true, UseShellExecute = false });
            p.WaitForExit();
            try { Directory.Delete(sv, true); } catch { }
            return File.Exists(o) ? o : null;
        }
        catch { return null; }
    });
}
