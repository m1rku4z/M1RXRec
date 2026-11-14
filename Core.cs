using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace M1RX;

public class Cfg
{
    public int W = 1920, H = 1080, Fps = 60, Mbps = 20, ReplaySec = 60;
    public int RecVk = 0x78, RecMod = 0, RepVk = 0x79, RepMod = 0, KeyMode = 0, Corner = 3;
    public string Codec = "h264", Gpu = "Auto", Mic = "";
    public string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "M1RXRec");
    public bool MicOn, KeysOn = true, MouseOn = true;
    public double Scale = 1, SysVol = 1, MicVol = 1;
    public int AudMode = 0, MicVk = 0x7A, MicMod = 0;
    public List<string> Apps = new();
    static readonly string F = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "M1RXRec.json");
    static readonly JsonSerializerOptions O = new() { IncludeFields = true, WriteIndented = true };
    public static Cfg Load() { try { return JsonSerializer.Deserialize<Cfg>(File.ReadAllText(F), O) ?? new Cfg(); } catch { return new Cfg(); } }
    public void Save() { try { File.WriteAllText(F, JsonSerializer.Serialize(this, O)); } catch { } }
}

static class N
{
    public delegate IntPtr HP(int c, IntPtr w, IntPtr l);
    [DllImport("user32")] public static extern bool RegisterHotKey(IntPtr h, int id, uint m, uint vk);
    [DllImport("user32")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32")] public static extern bool SetProcessDPIAware();
    [DllImport("user32")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32")] public static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32")] public static extern IntPtr SetWindowsHookEx(int id, HP f, IntPtr m, uint t);
    [DllImport("user32")] public static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32")] public static extern IntPtr CallNextHookEx(IntPtr h, int c, IntPtr w, IntPtr l);
    [DllImport("kernel32")] public static extern IntPtr GetModuleHandle(string n);
}

// ---------- Overlay: click-through window, captured by the screen grab itself (zero extra encode cost) ----------
public class Ov : Window
{
    readonly Cfg c;
    readonly WrapPanel wp = new() { MaxWidth = 460 };
    readonly HashSet<int> d = new();
    N.HP kh, mh;
    IntPtr h1, h2;
    static readonly int[] Cmp = { 0x57, 0x41, 0x53, 0x44, 0x10, 0x11, 0x20 };

    public Ov(Cfg c)
    {
        this.c = c;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight; Content = wp;
        SourceInitialized += (s, e) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            N.SetWindowLong(h, -20, N.GetWindowLong(h, -20) | 0x08000000 | 0x80 | 0x20);
        };
        SizeChanged += (s, e) => Place();
    }

    public void Set(bool on)
    {
        if (on && h1 == IntPtr.Zero)
        {
            kh = K; mh = M;
            var m = N.GetModuleHandle(Process.GetCurrentProcess().MainModule.ModuleName);
            h1 = N.SetWindowsHookEx(13, kh, m, 0);
            h2 = N.SetWindowsHookEx(14, mh, m, 0);
            wp.LayoutTransform = new ScaleTransform(c.Scale, c.Scale);
            d.Clear(); Refresh(); Show(); Place();
        }
        else if (!on && h1 != IntPtr.Zero)
        {
            N.UnhookWindowsHookEx(h1); N.UnhookWindowsHookEx(h2);
            h1 = h2 = IntPtr.Zero; Hide();
        }
    }

    void Place()
    {
        var w = SystemParameters.WorkArea;
        Left = c.Corner % 2 == 0 ? w.Left + 20 : w.Right - ActualWidth - 20;
        Top = c.Corner < 2 ? w.Top + 20 : w.Bottom - ActualHeight - 20;
    }

    static string Nm(int k) => k switch
    {
        1 => "LMB", 2 => "RMB", 4 => "MMB", 0x10 => "Shift", 0x11 => "Ctrl", 0x12 => "Alt", 0x20 => "Space",
        _ => KeyInterop.KeyFromVirtualKey(k).ToString()
    };

    void Refresh()
    {
        wp.Children.Clear();
        IEnumerable<int> ks = c.KeyMode == 0
            ? (c.KeysOn ? Cmp : Array.Empty<int>()).Concat(c.MouseOn ? new[] { 1, 2 } : Array.Empty<int>())
            : d.Where(k => k <= 4 ? c.MouseOn : c.KeysOn).OrderBy(k => k).ToList();
        foreach (var k in ks)
        {
            bool on = d.Contains(k);
            wp.Children.Add(new Border
            {
                Margin = new Thickness(3), Padding = new Thickness(12, 6, 12, 6), CornerRadius = new CornerRadius(8),
                Background = on ? new SolidColorBrush(Color.FromRgb(0xE1, 0x1D, 0x2E)) : new SolidColorBrush(Color.FromArgb(150, 20, 20, 20)),
                Child = new TextBlock { Text = Nm(k), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 16 }
            });
        }
    }

    void Chg(int k, bool dn)
    {
        if (dn ? d.Add(k) : d.Remove(k)) Dispatcher.BeginInvoke(new Action(Refresh));
    }

    IntPtr K(int n, IntPtr w, IntPtr l)
    {
        if (n >= 0)
        {
            int m = (int)w, vk = Marshal.ReadInt32(l);
            vk = vk is 0xA0 or 0xA1 ? 0x10 : vk is 0xA2 or 0xA3 ? 0x11 : vk is 0xA4 or 0xA5 ? 0x12 : vk;
            Chg(vk, m == 0x100 || m == 0x104);
        }
        return N.CallNextHookEx(IntPtr.Zero, n, w, l);
    }

    IntPtr M(int n, IntPtr w, IntPtr l)
    {
        if (n >= 0)
            switch ((int)w)
            {
                case 0x201: Chg(1, true); break; case 0x202: Chg(1, false); break;
                case 0x204: Chg(2, true); break; case 0x205: Chg(2, false); break;
                case 0x207: Chg(4, true); break; case 0x208: Chg(4, false); break;
            }
        return N.CallNextHookEx(IntPtr.Zero, n, w, l);
    }
}
