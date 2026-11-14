using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NAudio.CoreAudioApi;

namespace M1RX;

static class P
{
    [STAThread]
    static void Main()
    {
        N.SetProcessDPIAware();
        Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata(30)); // light UI anim
        new Application().Run(new Win());
    }
}

class Win : Window
{
    readonly Cfg c = Cfg.Load();
    readonly Rec r; readonly Ov ov; readonly Mix mx;
    ComboBox cam; Button hMic; Slider sv, mv; WrapPanel apl;
    IntPtr hw; int cap;
    TextBox tw, th, tf, tb, tr, td;
    ComboBox cp, cg, cc, cm, ck, cn;
    CheckBox kOn, mOn, micOn;
    Slider sc;
    Button bRec, bRep, bSave, hRec, hRep;
    TextBlock st;
    Border head;
    readonly RotateTransform rt = new(0, .5, .5);

    static Brush Br(byte a, byte b, byte d) => new SolidColorBrush(Color.FromRgb(a, b, d));
    static readonly Brush Red = Br(0xE1, 0x1D, 0x2E), Dk = Br(0x5A, 0x08, 0x10);
    static readonly ControlTemplate BT = (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
        "<Border x:Name='b' Background='{TemplateBinding Background}' CornerRadius='12' Padding='{TemplateBinding Padding}' RenderTransformOrigin='.5,.5'>" +
        "<Border.RenderTransform><ScaleTransform x:Name='s'/></Border.RenderTransform>" +
        "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
        "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='s' Property='ScaleX' Value='1.04'/><Setter TargetName='s' Property='ScaleY' Value='1.04'/><Setter TargetName='b' Property='Opacity' Value='0.9'/></Trigger>" +
        "<Trigger Property='IsPressed' Value='True'><Setter TargetName='s' Property='ScaleX' Value='0.96'/><Setter TargetName='s' Property='ScaleY' Value='0.96'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");

    static readonly (string n, int w, int h, int f, int b)[] PS =
    {
        ("144p 30fps 2M",256,144,30,2),("360p 30fps 3M",640,360,30,3),("480p 30fps 4M",854,480,30,4),
        ("720p 30fps 6M",1280,720,30,6),("720p 60fps 10M",1280,720,60,10),("1080p 30fps 12M",1920,1080,30,12),
        ("1080p 60fps 20M",1920,1080,60,20),("1080p 120fps 40M",1920,1080,120,40),
        ("2K 60fps 40M",2560,1440,60,40),("2K 120fps 70M",2560,1440,120,70),
        ("4K 30fps 50M",3840,2160,30,50),("4K 60fps 80M",3840,2160,60,80),("4K 120fps 120M",3840,2160,120,120),("Custom",0,0,0,0)
    };

    Button Btn(string t, Brush bg, double fs = 16) => new()
    {
        Content = t, Template = BT, Background = bg, Foreground = Brushes.White, FontSize = fs, FontWeight = FontWeights.Bold,
        Padding = new Thickness(18, 12, 18, 12), Margin = new Thickness(6), Cursor = Cursors.Hand
    };
    TextBox Tb(string v) => new() { Text = v, Padding = new Thickness(6, 4, 6, 4), MinWidth = 90 };
    ComboBox Cb(IEnumerable<string> it, int sel) { var x = new ComboBox { Padding = new Thickness(6, 4, 6, 4), MinWidth = 180 }; foreach (var i in it) x.Items.Add(i); x.SelectedIndex = Math.Max(0, sel); return x; }
    static UIElement Row(string l, UIElement x)
    {
        var g = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        g.Children.Add(new TextBlock { Text = l, Width = 120, VerticalAlignment = VerticalAlignment.Center, Foreground = Dk });
        g.Children.Add(x); return g;
    }
    static Border Card(string t, params UIElement[] k)
    {
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = t, FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Red, Margin = new Thickness(0, 0, 0, 6) });
        foreach (var e in k) s.Children.Add(e);
        return new Border
        {
            Child = s, Width = 450, Margin = new Thickness(10), Padding = new Thickness(18), CornerRadius = new CornerRadius(16),
            Background = Brushes.White, BorderBrush = Br(0xF3, 0xC1, 0xC6), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0xE1, 0x1D, 0x2E), Opacity = .15, BlurRadius = 18, ShadowDepth = 3 }
        };
    }
    static string Hk(int vk, int m) => ((m & 2) > 0 ? "Ctrl+" : "") + ((m & 1) > 0 ? "Alt+" : "") + ((m & 4) > 0 ? "Shift+" : "") + KeyInterop.KeyFromVirtualKey(vk);

    public Win()
    {
        Title = "M1RXRec"; Width = 1000; Height = 780; MinWidth = 940; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI");
        Background = new LinearGradientBrush(Colors.White, Color.FromRgb(0xFF, 0xE1, 0xE4), 90);
        mx = new Mix(c); r = new Rec(c, mx); ov = new Ov(c);
        mx.Warn += w => Dispatcher.Invoke(() => Info(w));
        r.Died += e => Dispatcher.Invoke(() => { r.Tidy(); Info("ffmpeg หยุด: " + e.Trim()); Sync(); });

        // header (3D tilt + rotating gradient)
        var hb = new LinearGradientBrush(Color.FromRgb(0xE1, 0x1D, 0x2E), Color.FromRgb(0x6E, 0x08, 0x12), 0) { RelativeTransform = rt };
        var hs = new StackPanel();
        hs.Children.Add(new TextBlock { Text = "M1RXRec", FontSize = 44, FontWeight = FontWeights.Black, Foreground = Brushes.White });
        hs.Children.Add(new TextBlock { Text = "Low-overhead GPU screen recorder  •  Replay  •  Overlay", Foreground = Brushes.White, Opacity = .85 });
        head = new Border { Background = hb, CornerRadius = new CornerRadius(20), Padding = new Thickness(26, 18, 26, 18), Margin = new Thickness(16, 16, 16, 6), Child = hs, Projection = new System.Windows.Media.Media3D.PlaneProjection() };
        head.MouseMove += (s, e) =>
        {
            var p = e.GetPosition(head); var pp = (System.Windows.Media.Media3D.PlaneProjection)head.Projection;
            pp.RotationY = (p.X / head.ActualWidth - .5) * 12; pp.RotationX = -(p.Y / head.ActualHeight - .5) * 12;
        };
        head.MouseLeave += (s, e) => { var pp = (System.Windows.Media.Media3D.PlaneProjection)head.Projection; pp.RotationX = 0; pp.RotationY = 0; };

        // quality
        cp = Cb(PS.Select(x => x.n), Array.FindIndex(PS, x => x.w == c.W && x.h == c.H && x.f == c.Fps && x.b == c.Mbps) is var i && i >= 0 ? i : PS.Length - 1);
        tw = Tb(c.W + ""); th = Tb(c.H + ""); tf = Tb(c.Fps + ""); tb = Tb(c.Mbps + "");
        cp.SelectionChanged += (s, e) =>
        {
            var p = PS[cp.SelectedIndex]; if (p.w == 0) return;
            tw.Text = p.w + ""; th.Text = p.h + ""; tf.Text = p.f + ""; tb.Text = p.b + "";
        };
        cc = Cb(new[] { "h264", "hevc", "av1" }, Array.IndexOf(new[] { "h264", "hevc", "av1" }, c.Codec));
        cg = Cb(new[] { "Auto", "NVIDIA", "AMD", "Intel" }, Array.IndexOf(new[] { "Auto", "NVIDIA", "AMD", "Intel" }, c.Gpu));
        var q = Card("คุณภาพ", Row("Preset", cp), Row("กว้าง (px)", tw), Row("สูง (px)", th), Row("FPS", tf), Row("Bitrate (Mbps)", tb), Row("Codec", cc), Row("GPU encoder", cg));

        // audio
        var mics = new List<string>();
        try { foreach (var d in new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)) mics.Add(d.FriendlyName); } catch { }
        cm = Cb(mics, Math.Max(0, mics.IndexOf(c.Mic)));
        micOn = new CheckBox { Content = "เอาไมค์เข้าคลิป", IsChecked = c.MicOn, Margin = new Thickness(0, 6, 0, 0) };
        cam = Cb(new[] { "ทั้งระบบ", "เฉพาะแอปที่ติ๊ก", "ทุกแอป ยกเว้นที่ติ๊ก", "ไม่เอาเสียงเกม" }, c.AudMode);
        sv = new Slider { Minimum = 0, Maximum = 2, Value = c.SysVol, MinWidth = 180 };
        mv = new Slider { Minimum = 0, Maximum = 2, Value = c.MicVol, MinWidth = 180 };
        sv.ValueChanged += (s, e) => c.SysVol = sv.Value; mv.ValueChanged += (s, e) => c.MicVol = mv.Value;
        apl = new WrapPanel { MaxWidth = 410, Margin = new Thickness(0, 4, 0, 4) };
        var rf = Btn("↻ รีเฟรชรายชื่อแอป", Dk, 12); rf.Click += (s, e) => Apps();
        Apps();
        var a = Card("เสียง", Row("เสียงในคลิป", cam), apl, rf, Row("ระดับเสียงเกม", sv), Row("ไมค์", cm), Row("ระดับไมค์", mv), micOn);

        // replay
        tr = Tb(c.ReplaySec + ""); td = Tb(c.Dir); td.MinWidth = 220;
        var rp = Card("Replay / โฟลเดอร์", Row("ย้อนหลัง (วินาที)", tr), Row("โฟลเดอร์เซฟ", td));

        // overlay
        kOn = new CheckBox { Content = "โชว์คีย์บอร์ด", IsChecked = c.KeysOn };
        mOn = new CheckBox { Content = "โชว์เมาส์", IsChecked = c.MouseOn, Margin = new Thickness(0, 4, 0, 0) };
        ck = Cb(new[] { "ย่อ (WASD/Shift/Ctrl/Space)", "เต็ม (ทุกปุ่มที่กด)" }, c.KeyMode);
        cn = Cb(new[] { "ซ้ายบน", "ขวาบน", "ซ้ายล่าง", "ขวาล่าง" }, c.Corner);
        sc = new Slider { Minimum = .6, Maximum = 2, Value = c.Scale, MinWidth = 180 };
        var ovc = Card("Overlay", kOn, mOn, Row("โหมด", ck), Row("มุม", cn), Row("ขนาด", sc));

        // hotkeys
        hRec = Btn("", Red, 14); hRep = Btn("", Red, 14); hMic = Btn("", Red, 14);
        hRec.Click += (s, e) => { cap = 1; HkOff(); hRec.Content = "กดปุ่มใหม่… (Esc ยกเลิก)"; };
        hRep.Click += (s, e) => { cap = 2; HkOff(); hRep.Content = "กดปุ่มใหม่… (Esc ยกเลิก)"; };
        hMic.Click += (s, e) => { cap = 3; HkOff(); hMic.Content = "กดปุ่มใหม่… (Esc ยกเลิก)"; };
        var hk = Card("คีย์ลัด (ทุกปุ่ม + Ctrl/Alt/Shift)", Row("เริ่ม/หยุดอัดยาว", hRec), Row("เซฟ Replay", hRep), Row("สลับไมค์ เปิด/ปิด", hMic));

        var wrap = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var x in new[] { q, a, rp, ovc, hk }) wrap.Children.Add(x);

        // footer
        bRec = Btn("● อัดยาว", Red, 20); bRep = Btn("⟲ เปิด Replay", Dk, 20); bSave = Btn("💾 เซฟ Replay", Br(0x33, 0x33, 0x33), 20);
        bRec.Click += (s, e) => ToggleRec(); bRep.Click += (s, e) => ToggleRep(); bSave.Click += (s, e) => SaveRep();
        st = new TextBlock { Text = "พร้อม", Foreground = Dk, Margin = new Thickness(12, 0, 12, 6), TextWrapping = TextWrapping.Wrap };
        var ft = new StackPanel { Margin = new Thickness(10, 0, 10, 10) };
        ft.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { bRec, bRep, bSave } });
        ft.Children.Add(st);

        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(ft, Dock.Bottom);
        root.Children.Add(head); root.Children.Add(ft);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = wrap });
        Content = root;

        SourceInitialized += (s, e) => { hw = new WindowInteropHelper(this).Handle; HwndSource.FromHwnd(hw).AddHook(Hook); Reg(); Anim(true); };
        PreviewKeyDown += CapKey;
        Closing += (s, e) => { Read(); r.StopAll(); ov.Set(false); ov.Close(); };
    }

    void Anim(bool on)
    {
        if (on) rt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(12)) { RepeatBehavior = RepeatBehavior.Forever });
        else rt.BeginAnimation(RotateTransform.AngleProperty, null); // zero UI animation cost while recording
    }

    int I(TextBox t, int d) => int.TryParse(t.Text, out var v) && v > 0 ? v : d;
    void Read()
    {
        c.W = I(tw, 1920) & ~1; c.H = I(th, 1080) & ~1; c.Fps = I(tf, 60); c.Mbps = I(tb, 20);
        c.ReplaySec = Math.Clamp(I(tr, 60), 10, 600); c.Dir = td.Text;
        c.Gpu = (string)cg.SelectedItem; c.Codec = (string)cc.SelectedItem;
        c.Mic = cm.SelectedItem as string ?? ""; c.MicOn = micOn.IsChecked == true;
        c.KeysOn = kOn.IsChecked == true; c.MouseOn = mOn.IsChecked == true;
        c.AudMode = cam.SelectedIndex; c.SysVol = sv.Value; c.MicVol = mv.Value;
        c.KeyMode = ck.SelectedIndex; c.Corner = cn.SelectedIndex; c.Scale = sc.Value; c.Save();
    }

    void HkOff() { N.UnregisterHotKey(hw, 1); N.UnregisterHotKey(hw, 2); N.UnregisterHotKey(hw, 3); }
    void Reg()
    {
        HkOff();
        N.RegisterHotKey(hw, 1, (uint)(c.RecMod | 0x4000), (uint)c.RecVk);
        N.RegisterHotKey(hw, 2, (uint)(c.RepMod | 0x4000), (uint)c.RepVk);
        N.RegisterHotKey(hw, 3, (uint)(c.MicMod | 0x4000), (uint)c.MicVk);
        hMic.Content = Hk(c.MicVk, c.MicMod);
        hRec.Content = Hk(c.RecVk, c.RecMod); hRep.Content = Hk(c.RepVk, c.RepMod);
    }
    void CapKey(object s, KeyEventArgs e)
    {
        if (cap == 0) return;
        var k = e.Key == Key.System ? e.SystemKey : e.Key;
        if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        e.Handled = true;
        int vk = KeyInterop.VirtualKeyFromKey(k), m = (int)Keyboard.Modifiers & 7;
        if (!(k == Key.Escape && m == 0)) { if (cap == 1) { c.RecVk = vk; c.RecMod = m; } else if (cap == 2) { c.RepVk = vk; c.RepMod = m; } else { c.MicVk = vk; c.MicMod = m; } c.Save(); }
        cap = 0; Reg();
    }
    IntPtr Hook(IntPtr h, int m, IntPtr w, IntPtr l, ref bool hd)
    {
        if (m == 0x312) { hd = true; if ((int)w == 1) ToggleRec(); else if ((int)w == 2) SaveRep(); else ToggleMic(); }
        return IntPtr.Zero;
    }

    void ToggleMic()
    {
        mx.MicMuted = !mx.MicMuted;
        System.Media.SystemSounds.Beep.Play();
        Info(mx.MicMuted ? "🎤 ไมค์ปิด" : "🎤 ไมค์เปิด");
    }
    void Apps()
    {
        apl.Children.Clear();
        var n = Mix.SessionNames();
        foreach (var p in System.Diagnostics.Process.GetProcesses()) { try { if (p.MainWindowTitle.Length > 0) n.Add(p.ProcessName); } catch { } }
        foreach (var x in c.Apps) n.Add(x);
        n.Remove("M1RXRec");
        foreach (var x in n.OrderBy(z => z, StringComparer.OrdinalIgnoreCase))
        {
            var cb = new CheckBox { Content = x, IsChecked = c.Apps.Contains(x, StringComparer.OrdinalIgnoreCase), Margin = new Thickness(0, 2, 14, 2) };
            cb.Checked += (s, e) => { if (!c.Apps.Contains(x)) c.Apps.Add(x); c.Save(); };
            cb.Unchecked += (s, e) => { c.Apps.RemoveAll(z => z.Equals(x, StringComparison.OrdinalIgnoreCase)); c.Save(); };
            apl.Children.Add(cb);
        }
    }
    void Info(string t) => st.Text = t;
    void ToggleRec()
    {
        if (r.Recording) { r.StopRec(); Info("บันทึกแล้ว"); }
        else
        {
            Read(); var f = r.StartRec();
            Info(f == null ? "ไม่พบ ffmpeg.exe ข้างไฟล์โปรแกรม" : "● กำลังอัด → " + f);
        }
        Sync();
    }
    void ToggleRep()
    {
        if (r.Replaying) { r.StopRep(); Info("ปิด Replay"); }
        else { Read(); Info(r.StartRep() ? $"Replay เปิด ย้อนหลัง {c.ReplaySec}s — กดคีย์ลัดเพื่อเซฟ" : "ไม่พบ ffmpeg.exe"); }
        Sync();
    }
    async void SaveRep()
    {
        if (!r.Replaying) { Info("เปิด Replay ก่อน"); return; }
        var f = await r.SaveAsync();
        System.Media.SystemSounds.Asterisk.Play();
        Info(f == null ? "เซฟไม่สำเร็จ" : "เซฟคลิป: " + f);
    }
    void Sync()
    {
        bool a = r.Recording || r.Replaying;
        Anim(!a);
        ov.Set(a && (c.KeysOn || c.MouseOn));
        bRec.Content = r.Recording ? "■ หยุดอัด" : "● อัดยาว";
        bRep.Content = r.Replaying ? "⟲ ปิด Replay" : "⟲ เปิด Replay";
    }
}
