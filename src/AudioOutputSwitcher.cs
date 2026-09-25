// ============================================================================
//  音频切换器 (Audio Output Switcher)
//  一键切换 Windows 默认音频输出设备
//  单文件 / 免安装 / 免管理员权限 / 依赖 .NET Framework 4.x（Win10、Win11 自带）
//
//  编译： csc /target:winexe /optimize+ /out:音频切换器.exe
//             /r:System.dll /r:System.Core.dll /r:System.Drawing.dll
//             /r:System.Windows.Forms.dll AudioOutputSwitcher.cs
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AudioSwitcher
{
    // ========================================================================
    //  1. Windows 核心音频 (Core Audio) COM 互操作
    // ========================================================================

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
        public PROPERTYKEY(Guid g, int p) { fmtid = g; pid = p; }
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PROPVARIANT
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr pwszVal;
    }

    internal enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    internal enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [Flags]
    internal enum DeviceState
    {
        ACTIVE = 0x1,
        DISABLED = 0x2,
        NOTPRESENT = 0x4,
        UNPLUGGED = 0x8,
        ALL = 0xF
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorComObject { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int pcDevices);
        [PreserveSig] int Item(int nDevice, out IMMDevice ppDevice);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out int pdwState);
    }

    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int cProps);
        [PreserveSig] int GetAt(int iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);
        [PreserveSig] int Commit();
    }

    /// <summary>未公开的 PolicyConfig 接口：Windows 上修改默认播放设备的标准做法。</summary>
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    internal class CPolicyConfigClient { }

    [Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, out IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bDefault, out IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, out IntPtr pMode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bFxStore, ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bFxStore, ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bVisible);
    }

    internal static class Native
    {
        [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PROPVARIANT pvar);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterWindowMessage(string lpString);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(int dwProcessId);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        public const int STD_OUTPUT_HANDLE = -11;

        /// <summary>向控制台写一行（GUI 程序在命令行下运行时用）。</summary>
        public static void WriteLine(string text)
        {
            try
            {
                IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
                if (h == IntPtr.Zero || h == new IntPtr(-1))
                {
                    AttachConsole(-1);
                    h = CreateFileW("CONOUT$", 0x40000000u, 0x3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
                }
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return;
                byte[] bytes = Encoding.UTF8.GetBytes(text + "\r\n");
                uint written;
                WriteFile(h, bytes, (uint)bytes.Length, out written, IntPtr.Zero);
            }
            catch { }
        }
    }

    // ========================================================================
    //  2. 设备模型与操作
    // ========================================================================

    internal class AudioDevice
    {
        public string Id;
        public string Name;      // 设备名，例如“扬声器”“耳机”
        public string Adapter;   // 声卡名，例如“Realtek(R) Audio”
        public bool IsDefault;

        public string Display
        {
            get
            {
                if (string.IsNullOrEmpty(Adapter) || Adapter == Name) return Name;
                return Name + "  —  " + Adapter;
            }
        }

        public override string ToString() { return Display; }
    }

    internal static class Audio
    {
        private static readonly PROPERTYKEY PKEY_FriendlyName =
            new PROPERTYKEY(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
        private static readonly PROPERTYKEY PKEY_DeviceDesc =
            new PROPERTYKEY(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
        private static readonly PROPERTYKEY PKEY_AdapterName =
            new PROPERTYKEY(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 6);

        /// <summary>列出当前可用的（已启用且已接入）播放设备。</summary>
        public static List<AudioDevice> GetActiveOutputs()
        {
            var list = new List<AudioDevice>();
            string defaultId = GetDefaultOutputId();

            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection collection = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                if (enumerator.EnumAudioEndpoints(EDataFlow.eRender, (int)DeviceState.ACTIVE, out collection) != 0)
                    return list;

                int count;
                if (collection.GetCount(out count) != 0) return list;

                for (int i = 0; i < count; i++)
                {
                    IMMDevice dev = null;
                    IPropertyStore store = null;
                    try
                    {
                        if (collection.Item(i, out dev) != 0 || dev == null) continue;
                        string id;
                        if (dev.GetId(out id) != 0) continue;

                        string name = "", desc = "", adapter = "";
                        if (dev.OpenPropertyStore(0 /* STGM_READ */, out store) == 0 && store != null)
                        {
                            name = ReadString(store, PKEY_FriendlyName);
                            desc = ReadString(store, PKEY_DeviceDesc);
                            adapter = ReadString(store, PKEY_AdapterName);
                        }
                        if (string.IsNullOrWhiteSpace(name)) name = string.IsNullOrWhiteSpace(desc) ? "未命名输出设备" : desc;
                        if (string.IsNullOrWhiteSpace(adapter)) adapter = desc;
                        if (adapter == name) adapter = "";

                        string title, sub;
                        SplitDeviceName(name.Trim(), (adapter ?? "").Trim(), desc.Trim(), out title, out sub);
                        list.Add(new AudioDevice
                        {
                            Id = id,
                            Name = title,
                            Adapter = sub,
                            IsDefault = (defaultId != null && string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase))
                        });
                    }
                    finally
                    {
                        Release(store);
                        Release(dev);
                    }
                }
            }
            finally
            {
                Release(collection);
                Release(enumerator);
            }
            return list;
        }

        /// <summary>
        /// Windows 给出的友好名经常自带声卡名，例如「音响 (Realtek(R) Audio)」。
        /// 这里把它拆成干净的标题 + 声卡名，避免界面上重复显示。
        /// </summary>
        private static void SplitDeviceName(string friendly, string adapter, string desc,
                                            out string title, out string sub)
        {
            title = friendly;
            sub = adapter;

            if (string.IsNullOrEmpty(adapter) || string.Equals(friendly, adapter, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(friendly, adapter, StringComparison.OrdinalIgnoreCase)) sub = "";
                else sub = adapter;
                return;
            }

            int idx = friendly.IndexOf(adapter, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return;   // 名字里没有声卡名：标题保持原样，声卡名放副标题

            string t = friendly.Remove(idx, adapter.Length);
            t = t.Replace("( ", "(").Replace(" )", ")").Replace("()", "").Replace("[]", "");
            t = Regex.Replace(t, @"\((\d+)-?\)", "($1)");   // 「(2-)」这类重复设备标记整理成「(2)」
            t = Regex.Replace(t, @"\s{2,}", " ").Trim();
            t = t.TrimEnd('-', '–', '—').Trim();
            if (t.EndsWith("(", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 1).Trim();
            if (t.Length == 0) t = friendly;

            title = t;
            sub = adapter;
        }

        private static string ReadString(IPropertyStore store, PROPERTYKEY key)
        {
            PROPVARIANT pv;
            if (store.GetValue(ref key, out pv) != 0) return "";
            try
            {
                // VT_LPWSTR = 31
                if (pv.vt == 31 && pv.pwszVal != IntPtr.Zero) return Marshal.PtrToStringUni(pv.pwszVal) ?? "";
                return "";
            }
            finally { Native.PropVariantClear(ref pv); }
        }

        public static string GetDefaultOutputId()
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice dev = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out dev) != 0 || dev == null)
                    return null;
                string id;
                return dev.GetId(out id) == 0 ? id : null;
            }
            catch { return null; }
            finally
            {
                Release(dev);
                Release(enumerator);
            }
        }

        public static AudioDevice GetDefaultOutput()
        {
            string id = GetDefaultOutputId();
            if (id == null) return null;
            foreach (var d in GetActiveOutputs())
                if (string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        /// <summary>把指定设备设为默认输出（同时应用到“常规/多媒体/通信”三种场景）。</summary>
        public static bool SetDefaultOutput(string deviceId, out string error)
        {
            error = null;
            IPolicyConfig policy = null;
            try
            {
                policy = (IPolicyConfig)new CPolicyConfigClient();
                int hr = 0;
                hr |= policy.SetDefaultEndpoint(deviceId, ERole.eConsole);
                hr |= policy.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                hr |= policy.SetDefaultEndpoint(deviceId, ERole.eCommunications);
                if (hr != 0)
                {
                    error = "切换失败（错误码 0x" + hr.ToString("X8") + "），请确认设备仍处于连接状态。";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "切换失败：" + ex.Message;
                return false;
            }
            finally { Release(policy); }
        }

        /// <summary>在列表里按序号 / 名称 / 设备 ID 查找设备。</summary>
        public static AudioDevice Find(List<AudioDevice> devices, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            query = query.Trim();

            int index;
            if (int.TryParse(query, out index))
            {
                if (index >= 1 && index <= devices.Count) return devices[index - 1];
                if (index >= 0 && index < devices.Count) return devices[index];
            }

            foreach (var d in devices)
                if (string.Equals(d.Id, query, StringComparison.OrdinalIgnoreCase)) return d;

            foreach (var d in devices)
                if (string.Equals(d.Name, query, StringComparison.OrdinalIgnoreCase)) return d;

            foreach (var d in devices)
                if (d.Display.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return d;

            return null;
        }

        /// <summary>在最近使用的两台设备之间来回切换。</summary>
        public static bool Toggle(out AudioDevice target, out string error)
        {
            target = null;
            error = null;

            var devices = GetActiveOutputs();
            if (devices.Count == 0) { error = "没有检测到可用的音频输出设备。"; return false; }
            if (devices.Count == 1) { error = "当前只有一台可用的输出设备（" + devices[0].Name + "），无法切换。"; return false; }

            AudioDevice current = null;
            foreach (var d in devices) if (d.IsDefault) { current = d; break; }

            AudioDevice next = null;
            string last = Config.LastDeviceId;
            if (!string.IsNullOrEmpty(last) && (current == null || !string.Equals(last, current.Id, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var d in devices)
                    if (string.Equals(d.Id, last, StringComparison.OrdinalIgnoreCase)) { next = d; break; }
            }
            if (next == null)
            {
                // 没有历史记录：挑一台不是当前默认的设备
                foreach (var d in devices)
                    if (current == null || !string.Equals(d.Id, current.Id, StringComparison.OrdinalIgnoreCase)) { next = d; break; }
            }
            if (next == null) { error = "找不到可切换的目标设备。"; return false; }

            if (current != null) Config.LastDeviceId = current.Id;   // 记住“刚才用的那台”，方便切回来
            if (!SetDefaultOutput(next.Id, out error)) return false;

            Config.Save();
            target = next;
            return true;
        }

        private static void Release(object comObject)
        {
            try { if (comObject != null && Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject); }
            catch { }
        }
    }

    // ========================================================================
    //  3. 配置文件（记录上次设备、快捷键、开机自启）
    // ========================================================================

    internal static class Config
    {
        public static string LastDeviceId = "";
        public static string Hotkey = "Ctrl+Alt+S";
        public static bool AutoStart = false;
        /// <summary>每个播放设备单独绑定的按键：设备 ID -> 按键文本</summary>
        public static Dictionary<string, string> DeviceKeys =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string _activeFile;   // 实际读到的那个配置文件

        /// <summary>
        /// 配置文件候选位置：优先用户配置目录，其次程序所在目录，最后系统临时目录。
        /// </summary>
        private static string[] CandidateFiles()
        {
            var list = new List<string>();
            try
            {
                list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                      "AudioOutputSwitcher", "config.ini"));
            }
            catch { }
            try
            {
                string dir = Path.GetDirectoryName(UiKit.ExePath);
                if (!string.IsNullOrEmpty(dir)) list.Add(Path.Combine(dir, "AudioSwitcher.ini"));
            }
            catch { }
            try { list.Add(Path.Combine(Path.GetTempPath(), "AudioSwitcher.ini")); }
            catch { }
            return list.ToArray();
        }

        public static string FilePath { get { return _activeFile ?? (CandidateFiles().Length > 0 ? CandidateFiles()[0] : ""); } }

        public static void Load()
        {
            foreach (string path in CandidateFiles())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string v = line.Substring(eq + 1).Trim();
                        switch (k)
                        {
                            case "lastdeviceid": LastDeviceId = v; break;
                            case "hotkey": if (v.Length > 0) Hotkey = v; break;
                            case "autostart": AutoStart = (v == "1" || v.ToLowerInvariant() == "true"); break;
                            case "": break;
                            default:
                                if (k.StartsWith("key.") && k.Length > 4)
                                    DeviceKeys[k.Substring(4).Trim()] = v;   // 字典本身忽略大小写
                                break;
                        }
                    }
                    _activeFile = path;
                    return;
                }
                catch { }
            }
        }

        public static void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 音频切换器配置");
            sb.AppendLine("LastDeviceId=" + LastDeviceId);
            sb.AppendLine("Hotkey=" + Hotkey);
            sb.AppendLine("AutoStart=" + (AutoStart ? "1" : "0"));
            foreach (var pair in DeviceKeys)
                if (!string.IsNullOrEmpty(pair.Value)) sb.AppendLine("Key." + pair.Key + "=" + pair.Value);
            string text = sb.ToString();

            foreach (string path in CandidateFiles())
            {
                try
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, text, Encoding.UTF8);
                    _activeFile = path;
                    return;
                }
                catch { }
            }
        }

        /// <summary>排查用：逐个试写候选位置，报告哪个能用。</summary>
        public static string Diagnose()
        {
            var sb = new StringBuilder();
            sb.AppendLine("临时目录 = " + SafeTempPath());
            sb.AppendLine("程序位置 = " + UiKit.ExePath);
            foreach (string p in CandidateFiles())
            {
                sb.Append("  " + p + " → ");
                try
                {
                    string dir = Path.GetDirectoryName(p);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(p, "# 音频切换器诊断\r\n", Encoding.UTF8);
                    sb.AppendLine("可写 ✓");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("不可写：" + ex.GetType().Name + " - " + ex.Message);
                }
            }
            return sb.ToString();
        }

        private static string SafeTempPath()
        {
            try { return Path.GetTempPath(); } catch { return "(未知)"; }
        }
    }

    // ========================================================================
    //  4. 小工具：图标、自绘按钮、快捷方式
    // ========================================================================

    internal static class UiKit
    {
        public static readonly Color Accent = Color.FromArgb(0, 150, 136);
        public static readonly Color AccentDark = Color.FromArgb(0, 121, 107);
        public static readonly Color TextMain = Color.FromArgb(38, 50, 56);
        public static readonly Color TextSub = Color.FromArgb(120, 144, 156);
        public static readonly Color Surface = Color.White;
        public static readonly Color Line = Color.FromArgb(224, 230, 233);

        public const string FontFamilyDefault = "Microsoft YaHei UI";

        public static Font Font(float size, FontStyle style)
        {
            try { return new Font(FontFamilyDefault, size, style); }
            catch { return new Font(FontFamily.GenericSansSerif, size, style); }
        }

        /// <summary>画一个小喇叭图标（用于托盘、窗口和 exe 图标）。</summary>
        public static Bitmap DrawSpeaker(int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var bg = new SolidBrush(Accent))
                    g.FillEllipse(bg, 0, 0, size - 1, size - 1);

                float u = size / 16f;
                using (var white = new SolidBrush(Color.White))
                {
                    // 喇叭主体
                    var body = new PointF[] {
                        new PointF(3.4f*u, 6.2f*u),
                        new PointF(6.0f*u, 6.2f*u),
                        new PointF(8.8f*u, 3.6f*u),
                        new PointF(8.8f*u, 12.4f*u),
                        new PointF(6.0f*u, 9.8f*u),
                        new PointF(3.4f*u, 9.8f*u)
                    };
                    g.FillPolygon(white, body);

                    // 声波
                    using (var pen = new Pen(Color.White, Math.Max(1f, 1.15f * u)))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        g.DrawArc(pen, 8.0f * u, 5.6f * u, 3.6f * u, 4.8f * u, -60f, 120f);
                        if (size >= 24) g.DrawArc(pen, 8.4f * u, 3.6f * u, 6.0f * u, 8.8f * u, -60f, 120f);
                    }
                }
            }
            return bmp;
        }

        public static Icon CreateSpeakerIcon(int size)
        {
            using (var bmp = DrawSpeaker(size))
            {
                IntPtr h = bmp.GetHicon();
                using (var tmp = Icon.FromHandle(h))
                {
                    var icon = (Icon)tmp.Clone();
                    Native.DestroyIcon(h);
                    return icon;
                }
            }
        }

        /// <summary>把喇叭图案写成 .ico 文件（PNG 压缩格式，Windows Vista 及以上均可识别）。</summary>
        public static bool WriteIconFile(string path)
        {
            try
            {
                byte[] png;
                using (var bmp = DrawSpeaker(256))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    png = ms.ToArray();
                }
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write((ushort)0);            // reserved
                    bw.Write((ushort)1);            // type = icon
                    bw.Write((ushort)1);            // 1 张图
                    bw.Write((byte)0);              // 宽 256 → 0
                    bw.Write((byte)0);              // 高 256 → 0
                    bw.Write((byte)0);              // 调色板
                    bw.Write((byte)0);              // reserved
                    bw.Write((ushort)1);            // planes
                    bw.Write((ushort)32);           // bpp
                    bw.Write((uint)png.Length);
                    bw.Write((uint)22);             // 数据偏移
                    bw.Write(png);
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>创建 / 更新一个 .lnk 快捷方式（通过 WScript.Shell）。</summary>
        public static bool CreateShortcut(string linkPath, string targetPath, string arguments, string description)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return false;
                object shell = Activator.CreateInstance(shellType);
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                                                     new object[] { linkPath });
                Type linkType = link.GetType();
                linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { targetPath });
                linkType.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { arguments });
                linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link,
                                      new object[] { Path.GetDirectoryName(targetPath) });
                linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link,
                                      new object[] { targetPath + ",0" });
                linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { description });
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                Marshal.ReleaseComObject(link);
                Marshal.ReleaseComObject(shell);
                return true;
            }
            catch { return false; }
        }

        public static string ExePath
        {
            get
            {
                try
                {
                    string p = Assembly.GetEntryAssembly() != null
                        ? Assembly.GetEntryAssembly().Location
                        : Process0.ExecutablePath;
                    return string.IsNullOrEmpty(p) ? Application.ExecutablePath : p;
                }
                catch { return Application.ExecutablePath; }
            }
        }

        private static class Process0
        {
            public static string ExecutablePath
            {
                get { return System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName; }
            }
        }

        public static string StartupDir
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.Startup); }
        }

        /// <summary>读出一个 .lnk 当前指向的目标路径（读不到就返回 null）。</summary>
        public static string ReadShortcutTarget(string linkPath)
        {
            object shell = null, link = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                shell = Activator.CreateInstance(shellType);
                link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                                              new object[] { linkPath });
                Type linkType = link.GetType();
                object target = linkType.InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null);
                return target as string;
            }
            catch { return null; }
            finally
            {
                try { if (link != null) Marshal.ReleaseComObject(link); } catch { }
                try { if (shell != null) Marshal.ReleaseComObject(shell); } catch { }
            }
        }

        public static string DesktopDir
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
        }
    }

    // ========================================================================
    //  5. 切换成功的提示气泡（右下角，自动消失）
    // ========================================================================

    internal class ToastForm : Form
    {
        private readonly Timer _timer = new Timer();

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        public ToastForm(string title, string message, int scale)
        {
            int S = scale;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = UiKit.Surface;
            Size = new Size(S * 360, S * 84);
            Opacity = 0.0;

            var bar = new Panel();
            bar.BackColor = UiKit.Accent;
            bar.SetBounds(0, 0, S * 6, S * 84);
            Controls.Add(bar);

            var lblTitle = new Label();
            lblTitle.Text = title;
            lblTitle.Font = UiKit.Font(9f, FontStyle.Regular);
            lblTitle.ForeColor = UiKit.TextSub;
            lblTitle.AutoEllipsis = true;
            lblTitle.SetBounds(S * 22, S * 12, S * 326, S * 20);
            Controls.Add(lblTitle);

            var lblMsg = new Label();
            lblMsg.Text = message;
            lblMsg.Font = UiKit.Font(11f, FontStyle.Bold);
            lblMsg.ForeColor = UiKit.TextMain;
            lblMsg.AutoEllipsis = true;
            lblMsg.SetBounds(S * 22, S * 34, S * 326, S * 26);
            Controls.Add(lblMsg);

            var lblHint = new Label();
            lblHint.Text = "如需切回，再按一次快捷键即可";
            lblHint.Font = UiKit.Font(7.5f, FontStyle.Regular);
            lblHint.ForeColor = UiKit.TextSub;
            lblHint.SetBounds(S * 22, S * 60, S * 326, S * 18);
            Controls.Add(lblHint);

            var area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - S * 18, area.Bottom - Height - S * 18);

            Opacity = 0.96;
            _timer.Interval = 2200;
            _timer.Tick += delegate { _timer.Stop(); Close(); };
            _timer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(UiKit.Line))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        public static void Popup(string title, string message)
        {
            try
            {
                int scale;
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                    scale = Math.Max(1, (int)Math.Round(g.DpiX / 96.0));
                var f = new ToastForm(title, message, scale);
                f.Show();
                Application.DoEvents();
                while (f.Visible)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(30);
                }
            }
            catch { }
        }
    }

    // ========================================================================
    //  6. 主窗口
    // ========================================================================

    /// <summary>
    /// 专门接收全局热键的消息窗口。
    /// 主窗口隐藏/显示、属性变化都可能重建句柄，而热键是绑在句柄上的——
    /// 绑到这个独立的隐藏窗口上，就不会再因为主窗口动来动去而失效。
    /// 同时它还监听系统的 “TaskbarCreated” 广播，用于在托盘重建后把图标补回来。
    /// </summary>
    internal class HotkeyWindow : NativeWindow
    {
        private const int WM_HOTKEY = 0x0312;
        private readonly int _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

        public event Action<int> HotkeyPressed;
        public event Action TaskbarRecreated;

        public HotkeyWindow()
        {
            var cp = new CreateParams();
            cp.Caption = "音频切换器-热键接收窗口";
            cp.Parent = new IntPtr(-3);          // HWND_MESSAGE：消息专用窗口
            cp.Style = 0;
            CreateHandle(cp);
        }

        public bool Register(int id, uint mods, uint vk)
        {
            try { return Native.RegisterHotKey(Handle, id, mods, vk); }
            catch { return false; }
        }

        public void Unregister(int id)
        {
            try { if (Handle != IntPtr.Zero) Native.UnregisterHotKey(Handle, id); } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                var handler = HotkeyPressed;
                if (handler != null) handler(m.WParam.ToInt32());
                return;
            }
            if (_taskbarCreated != 0 && m.Msg == _taskbarCreated)
            {
                var handler = TaskbarRecreated;
                if (handler != null) handler();
                return;
            }
            base.WndProc(ref m);
        }
    }

    internal class MainForm : Form
    {
        private const int HOTKEY_ID = 0xB1B1;
        private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;
        private const int WIDTH = 520;          // 设计宽度（96dpi 逻辑像素）

        private readonly int S;                 // DPI 缩放系数（96dpi = 1）
        private readonly bool _startHidden;
        private bool _allowShow;
        private bool _trayHintShown;
        private bool _hotkeyOk;

        private List<AudioDevice> _devices = new List<AudioDevice>();
        private ListBox _list;
        private Label _lblCurrent;
        private Label _lblSub;
        private Label _lblStatus;
        private Button _btnToggle, _btnSet, _btnRefresh;
        private Label _lblHotkey;
        private Label _lblHotkeyHint;
        private Button _btnHotkey;
        private Button _btnAssign;
        private bool _recording;
        private uint _curMods, _curVk;
        private string _recordingDeviceId;                                   // 正在为哪个设备录制（null = 全局快捷键）
        private readonly Dictionary<int, string> _deviceKeyIds = new Dictionary<int, string>();
        private readonly HashSet<string> _deviceKeyFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _nextHotkeyId = 0xB200;
        private NotifyIcon _tray;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _miAutoStart;
        private ToolStripMenuItem _miToggle;
        private Icon _icon;
        private HotkeyWindow _hotkeys;          // 热键固定绑在这个隐藏窗口上
        private Timer _housekeeping;            // 定时自检：设备变化、开机时热键补注册
        private int _hotkeyRetry;
        private string _deviceSignature = "";
        private int _autoStartCheckTick;

        public MainForm(bool startHidden)
        {
            _startHidden = startHidden;
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
                S = Math.Max(1, (int)Math.Round(g.DpiX / 96.0));

            _hotkeys = new HotkeyWindow();
            _hotkeys.HotkeyPressed += OnHotkeyPressed;
            _hotkeys.TaskbarRecreated += RestoreTrayIcon;

            BuildUi();
            BuildTray();
            RegisterConfiguredHotkey();
            LoadDevices();
            EnsureAutoStart();

            _housekeeping = new Timer();
            _housekeeping.Interval = 20000;
            _housekeeping.Tick += delegate { Housekeeping(); };
            _housekeeping.Start();
        }

        // ---------- 界面 ----------

        private void BuildUi()
        {
            Text = "音频切换器";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = UiKit.Surface;
            ClientSize = new Size(S * WIDTH, S * 486);
            KeyPreview = true;
            Font = UiKit.Font(9f, FontStyle.Regular);
            Icon = _icon = UiKit.CreateSpeakerIcon(32);

            _lblCurrent = new Label();
            _lblCurrent.Font = UiKit.Font(12f, FontStyle.Bold);
            _lblCurrent.ForeColor = UiKit.AccentDark;
            _lblCurrent.AutoEllipsis = true;
            _lblCurrent.SetBounds(S * 16, S * 16, S * (WIDTH - 32), S * 26);
            Controls.Add(_lblCurrent);

            _lblSub = new Label();
            _lblSub.Text = "双击设备即可切换输出。";
            _lblSub.Font = UiKit.Font(8f, FontStyle.Regular);
            _lblSub.ForeColor = UiKit.TextSub;
            _lblSub.AutoEllipsis = true;
            _lblSub.SetBounds(S * 16, S * 44, S * (WIDTH - 32), S * 20);
            Controls.Add(_lblSub);

            _list = new ListBox();
            _list.BorderStyle = BorderStyle.FixedSingle;
            _list.DrawMode = DrawMode.OwnerDrawFixed;
            _list.ItemHeight = S * 44;
            _list.IntegralHeight = false;
            _list.BackColor = UiKit.Surface;
            _list.SetBounds(S * 16, S * 70, S * (WIDTH - 32), S * 240);
            _list.DrawItem += List_DrawItem;
            _list.DoubleClick += delegate { SetSelectedAsDefault(); };
            _list.SelectedIndexChanged += delegate { UpdateButtons(); };
            Controls.Add(_list);

            _btnToggle = MakeButton("一键切换", S * 16, S * 322, S * 120, true);
            _btnToggle.Click += delegate { DoToggle(); };
            Controls.Add(_btnToggle);

            _btnSet = MakeButton("设为默认", S * 144, S * 322, S * 110, false);
            _btnSet.Click += delegate { SetSelectedAsDefault(); };
            Controls.Add(_btnSet);

            _btnRefresh = MakeButton("刷新", S * (WIDTH - 96), S * 322, S * 80, false);
            _btnRefresh.Click += delegate { LoadDevices(); };
            Controls.Add(_btnRefresh);

            _btnAssign = MakeButton("给选中设备设按键", S * 262, S * 322, S * 154, false);
            _btnAssign.Click += delegate { BeginDeviceKeyCapture(); };
            Controls.Add(_btnAssign);

            // ---- 自定义快捷键 ----
            _lblHotkey = new Label();
            _lblHotkey.Text = "快捷切换键";
            _lblHotkey.Font = UiKit.Font(9f, FontStyle.Regular);
            _lblHotkey.ForeColor = UiKit.TextMain;
            _lblHotkey.SetBounds(S * 16, S * 372, S * 90, S * 22);
            Controls.Add(_lblHotkey);

            _btnHotkey = MakeButton("", S * 108, S * 366, S * 170, false);
            _btnHotkey.Font = UiKit.Font(9.5f, FontStyle.Bold);
            _btnHotkey.Click += delegate { BeginHotkeyCapture(); };
            Controls.Add(_btnHotkey);

            _lblHotkeyHint = new Label();
            _lblHotkeyHint.Font = UiKit.Font(7.5f, FontStyle.Regular);
            _lblHotkeyHint.ForeColor = UiKit.TextSub;
            _lblHotkeyHint.SetBounds(S * 288, S * 364, S * (WIDTH - 304), S * 46);
            Controls.Add(_lblHotkeyHint);

            _lblStatus = new Label();
            _lblStatus.Font = UiKit.Font(8f, FontStyle.Regular);
            _lblStatus.ForeColor = UiKit.TextSub;
            _lblStatus.SetBounds(S * 16, S * 414, S * (WIDTH - 32), S * 56);
            Controls.Add(_lblStatus);

            Activated += delegate { if (Visible) LoadDevices(); };
            KeyDown += MainForm_KeyDown;
            MouseDown += delegate { if (_recording) CancelHotkeyCapture(); };
            FormClosing += MainForm_FormClosing;
        }

        private Button MakeButton(string text, int x, int y, int w, bool primary)
        {
            var b = new Button();
            b.Text = text;
            b.Font = UiKit.Font(9.5f, primary ? FontStyle.Bold : FontStyle.Regular);
            b.SetBounds(x, y, w, S * 32);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = UiKit.Line;
            b.BackColor = primary ? UiKit.Accent : Color.FromArgb(247, 249, 250);
            b.ForeColor = primary ? Color.White : UiKit.TextMain;
            b.FlatAppearance.MouseOverBackColor = primary ? UiKit.AccentDark : Color.FromArgb(238, 243, 245);
            b.UseVisualStyleBackColor = false;
            b.Cursor = Cursors.Hand;
            return b;
        }

        private void List_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _devices.Count) return;
            AudioDevice d = _devices[e.Index];
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

            using (var brush = new SolidBrush(selected ? Color.FromArgb(232, 245, 243) : UiKit.Surface))
                e.Graphics.FillRectangle(brush, e.Bounds);

            int pad = S * 14;
            int cy = e.Bounds.Top + (e.Bounds.Height / 2);

            // 默认设备标记
            if (d.IsDefault)
            {
                using (var dot = new SolidBrush(UiKit.Accent))
                    e.Graphics.FillEllipse(dot, e.Bounds.Left + pad - S * 2, cy - S * 5, S * 10, S * 10);
            }
            else
            {
                using (var pen = new Pen(UiKit.Line, Math.Max(1f, S)))
                    e.Graphics.DrawEllipse(pen, e.Bounds.Left + pad - S * 2, cy - S * 5, S * 10, S * 10);
            }

            int textLeft = e.Bounds.Left + pad + S * 22;
            using (var titleFont = UiKit.Font(10.5f, d.IsDefault ? FontStyle.Bold : FontStyle.Regular))
            using (var subFont = UiKit.Font(8f, FontStyle.Regular))
            using (var titleBrush = new SolidBrush(UiKit.TextMain))
            using (var subBrush = new SolidBrush(UiKit.TextSub))
            {
                string title = d.Name + (d.IsDefault ? "   （当前默认）" : "");
                e.Graphics.DrawString(title, titleFont, titleBrush, textLeft, e.Bounds.Top + S * 6);
                string sub = string.IsNullOrEmpty(d.Adapter) ? "音频输出设备" : d.Adapter;
                e.Graphics.DrawString(sub, subFont, subBrush, textLeft, e.Bounds.Top + S * 25);
            }

            using (var pen = new Pen(Color.FromArgb(240, 244, 246)))
                e.Graphics.DrawLine(pen, e.Bounds.Left + S * 12, e.Bounds.Bottom - 1, e.Bounds.Right - S * 12, e.Bounds.Bottom - 1);

            // 右側：该设备绑定的按键（如果有）
            string keyText = DeviceKeyDisplay(d.Id);
            if (keyText.Length > 0)
            {
                using (var kf = UiKit.Font(8.5f, FontStyle.Bold))
                {
                    SizeF sz = e.Graphics.MeasureString(keyText, kf);
                    int bw = (int)Math.Ceiling(sz.Width) + S * 18;
                    int bh = S * 22;
                    int bx = e.Bounds.Right - S * 14 - bw;
                    int by = cy - bh / 2;
                    bool bad = _deviceKeyFailed.Contains(d.Id);
                    using (var bg = new SolidBrush(bad ? Color.FromArgb(255, 236, 230) : Color.FromArgb(232, 245, 243)))
                        e.Graphics.FillRectangle(bg, bx, by, bw, bh);
                    using (var pen = new Pen(bad ? Color.FromArgb(230, 145, 120) : Color.FromArgb(180, 215, 210)))
                        e.Graphics.DrawRectangle(pen, bx, by, bw - 1, bh - 1);
                    using (var brush = new SolidBrush(bad ? Color.FromArgb(190, 90, 60) : UiKit.AccentDark))
                        e.Graphics.DrawString(keyText, kf, brush, bx + S * 9, by + S * 3);
                }
            }
        }

        // ---------- 托盘与快捷键 ----------

        private void BuildTray()
        {
            _menu = new ContextMenuStrip();
            _menu.Font = UiKit.Font(9f, FontStyle.Regular);

            var miOpen = new ToolStripMenuItem("打开切换窗口");
            miOpen.Click += delegate { ShowWindow(); };
            _menu.Items.Add(miOpen);

            _miToggle = new ToolStripMenuItem("一键切换输出");
            _miToggle.Click += delegate { DoToggle(); };
            _menu.Items.Add(_miToggle);

            _menu.Items.Add(new ToolStripSeparator());

            var miDesktop = new ToolStripMenuItem("创建桌面快捷方式（一键切换）");
            miDesktop.Click += delegate { CreateDesktopShortcut(); };
            _menu.Items.Add(miDesktop);

            _miAutoStart = new ToolStripMenuItem("开机自动启动（静默驻留托盘）");
            _miAutoStart.CheckOnClick = true;
            _miAutoStart.Checked = File.Exists(Path.Combine(UiKit.StartupDir, "音频切换器.lnk"));
            _miAutoStart.Click += delegate { SetAutoStart(_miAutoStart.Checked); };
            _menu.Items.Add(_miAutoStart);

            _menu.Items.Add(new ToolStripSeparator());

            var miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { _tray.Visible = false; Application.Exit(); };
            _menu.Items.Add(miExit);

            _tray = new NotifyIcon();
            _tray.Icon = _icon;
            _tray.Text = "音频输出切换器";
            _tray.Visible = true;
            _tray.ContextMenuStrip = _menu;
            _tray.DoubleClick += delegate { ShowWindow(); };
        }

        /// <summary>读取配置里的快捷键并注册。</summary>
        private void RegisterConfiguredHotkey()
        {
            uint mods, vk;
            if (TryParseHotkey(Config.Hotkey, out mods, out vk))
            {
                if (_hotkeys.Register(HOTKEY_ID, mods, vk))
                {
                    _curMods = mods; _curVk = vk; _hotkeyOk = true;
                    _hotkeyRetry = 0;
                }
                else
                {
                    _hotkeyRetry = 8;       // 开机时可能是别的启动程序抢先了，稍后自动重试
                    _lblStatus.Text = "快捷键 " + HotkeyDisplay(mods, vk) +
                        " 暂时被其他程序占用了，程序会自动重试，也可以点上面的按钮换一个。";
                }
            }
            else if (!string.IsNullOrWhiteSpace(Config.Hotkey))
            {
                _lblStatus.Text = "配置里的快捷键「" + Config.Hotkey + "」无法识别，请重新设置。";
            }
            UpdateHotkeyUi();
        }

        /// <summary>改键：先注销旧的，再注册新的；失败就回滚并提示。</summary>
        private void ApplyHotkey(uint mods, uint vk)
        {
            if (_recordingDeviceId != null) { ApplyDeviceKey(_recordingDeviceId, mods, vk); return; }

            uint oldMods = _curMods, oldVk = _curVk;
            if (_hotkeyOk) { _hotkeys.Unregister(HOTKEY_ID); _hotkeyOk = false; }
            _curMods = 0; _curVk = 0;

            if (vk != 0)
            {
                if (_hotkeys.Register(HOTKEY_ID, mods, vk))
                {
                    _curMods = mods; _curVk = vk; _hotkeyOk = true;
                }
                else
                {
                    if (oldVk != 0 && _hotkeys.Register(HOTKEY_ID, oldMods, oldVk))
                    {
                        _curMods = oldMods; _curVk = oldVk; _hotkeyOk = true;
                    }
                    _recording = false;
                    UpdateHotkeyUi();
                    _lblStatus.Text = "「" + HotkeyDisplay(mods, vk) + "」被别的程序占用了，换一个组合试试。";
                    return;
                }
            }

            Config.Hotkey = (vk == 0) ? "" : HotkeyKeyText(mods, vk);
            Config.Save();
            _recording = false;
            UpdateHotkeyUi();
            _lblStatus.Text = (vk == 0)
                ? "已清除快捷键：以后用按钮或托盘菜单切换（随时可以再设回来）。"
                : "快捷键已设为 " + HotkeyDisplay(mods, vk) + "，窗口关闭后在后台也能用。";
        }

        /// <summary>给某台设备单独绑一个按键。</summary>
        private void ApplyDeviceKey(string deviceId, uint mods, uint vk)
        {
            string name = DeviceNameById(deviceId);

            if (vk == 0)
            {
                Config.DeviceKeys.Remove(deviceId);
                Config.Save();
                EndCapture();
                RegisterDeviceHotkeys();
                UpdateHotkeyUi();
                _list.Invalidate();
                _lblStatus.Text = "已清除「" + name + "」的按键。";
                return;
            }

            uint gm, gv;
            if (TryParseHotkey(Config.Hotkey, out gm, out gv) && gv == vk && gm == mods)
            {
                EndCapture();
                UpdateHotkeyUi();
                _lblStatus.Text = HotkeyDisplay(mods, vk) + " 已经是一键切换的快捷键了，给它换一个。";
                return;
            }

            Config.DeviceKeys[deviceId] = HotkeyKeyText(mods, vk);
            Config.Save();
            EndCapture();
            RegisterDeviceHotkeys();
            UpdateHotkeyUi();
            _list.Invalidate();

            if (_deviceKeyFailed.Contains(deviceId))
                _lblStatus.Text = "「" + HotkeyDisplay(mods, vk) + "」没能生效（可能被其他程序或设备占用了），换个键试试。";
            else
                _lblStatus.Text = "已给「" + name + "」绑定按键 " + HotkeyDisplay(mods, vk) + "，按下它就直接切到这台设备。";
        }

        private void EndCapture()
        {
            _recording = false;
            _recordingDeviceId = null;
        }

        /// <summary>给每台已绑键的设备注册全局热键；设备插拔后会自动重来一遍。</summary>
        private void RegisterDeviceHotkeys()
        {
            foreach (int id in new List<int>(_deviceKeyIds.Keys))
            {
                _hotkeys.Unregister(id);
            }
            _deviceKeyIds.Clear();
            _deviceKeyFailed.Clear();

            foreach (AudioDevice d in _devices)
            {
                string text;
                if (!Config.DeviceKeys.TryGetValue(d.Id, out text) || string.IsNullOrWhiteSpace(text)) continue;
                uint mods, vk;
                if (!TryParseHotkey(text, out mods, out vk)) { _deviceKeyFailed.Add(d.Id); continue; }
                int id = _nextHotkeyId++;
                if (_hotkeys.Register(id, mods, vk)) _deviceKeyIds[id] = d.Id;
                else _deviceKeyFailed.Add(d.Id);
            }
        }

        private string DeviceNameById(string deviceId)
        {
            AudioDevice d = FindDeviceById(deviceId);
            return d != null ? d.Name : "该设备";
        }

        private AudioDevice FindDeviceById(string deviceId)
        {
            foreach (AudioDevice d in _devices)
                if (string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        private string DeviceKeyDisplay(string deviceId)
        {
            string text;
            if (!Config.DeviceKeys.TryGetValue(deviceId, out text) || string.IsNullOrWhiteSpace(text)) return "";
            uint mods, vk;
            return TryParseHotkey(text, out mods, out vk) ? HotkeyDisplay(mods, vk) : "";
        }

        private void BeginHotkeyCapture() { BeginCapture(null); }

        private void BeginDeviceKeyCapture()
        {
            if (_recording) { CancelHotkeyCapture(); return; }
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _devices.Count)
            {
                _lblStatus.Text = "请先在列表里选中一台设备，再点“给选中设备设按键”。";
                return;
            }
            BeginCapture(_devices[i].Id);
        }

        private void BeginCapture(string deviceId)
        {
            _recording = true;
            _recordingDeviceId = deviceId;
            string who = (deviceId == null) ? "一键切换" : ("「" + DeviceNameById(deviceId) + "」");
            Button target = (deviceId == null) ? _btnHotkey : _btnAssign;
            target.Text = "请按组合键…";
            target.BackColor = UiKit.Accent;
            target.ForeColor = Color.White;
            _lblHotkeyHint.Text = "正在为 " + who + " 设键：可以只按一个键（如 F4），也可以按 Ctrl+Alt+… 组合。Esc 取消，Backspace/Delete 清除。";
            _lblStatus.Text = "正在设置 " + who + " 的按键，请直接按下你想要的按键。";
            ActiveControl = target;
        }

        private void CancelHotkeyCapture()
        {
            EndCapture();
            UpdateHotkeyUi();
            _lblStatus.Text = "已取消，按键保持不变。";
        }

        /// <summary>这些键单独使用不影响打字：功能键、小键盘、翻页/方向键等。</summary>
        private static bool IsSafeBareKey(uint vk)
        {
            if (vk >= 0x70 && vk <= 0x87) return true;   // F1–F24
            if (vk >= 0x60 && vk <= 0x6F) return true;   // 小键盘数字与 * + - / .
            if (vk >= 0x21 && vk <= 0x28) return true;   // PageUp/PageDown/End/Home/方向键
            if (vk == 0x2D || vk == 0x2E) return true;   // Insert / Delete
            return false;
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (!_recording) return;
            e.Handled = true; e.SuppressKeyPress = true;

            Button target = (_recordingDeviceId == null) ? _btnHotkey : _btnAssign;
            Keys code = e.KeyCode;
            // 只按了修饰键：先显示进度，继续等主键
            if (code == Keys.ControlKey || code == Keys.ShiftKey || code == Keys.Menu ||
                code == Keys.LWin || code == Keys.RWin)
            {
                string part = ModsText(Control.ModifierKeys);
                target.Text = (part.Length > 0 ? part + " + " : "") + "…";
                return;
            }

            if (Control.ModifierKeys == Keys.None)
            {
                if (code == Keys.Escape) { CancelHotkeyCapture(); return; }
                if (code == Keys.Back || code == Keys.Delete) { ApplyHotkey(0, 0); return; }
            }

            uint vk = (uint)code;
            if (VkName(vk) == null)
            {
                _lblHotkeyHint.Text = "这个键不支持，换一个（字母、数字、F1–F24、方向键、Home/End、小键盘都可以）。";
                return;
            }

            uint mods = 0;
            if ((Control.ModifierKeys & Keys.Control) != 0) mods |= MOD_CONTROL;
            if ((Control.ModifierKeys & Keys.Alt) != 0) mods |= MOD_ALT;
            if ((Control.ModifierKeys & Keys.Shift) != 0) mods |= MOD_SHIFT;

            // 单独一个键（没有任何组合）——会被系统全局独占，先提醒一次
            if (mods == 0 && !IsSafeBareKey(vk))
            {
                string show = PrettyKeyName(vk);
                DialogResult r = MessageBox.Show(
                    "你选的是单独的「" + show + "」键。\n\n" +
                    "设好以后这个键会被全局独占：\n" +
                    "· 在任何程序里按到它，都会立刻切换音频设备；\n" +
                    "· 它原本的用途（例如输入这个字母/数字）就没有了。\n\n" +
                    "建议改用 F1–F12、小键盘或方向键这类平时不用的键。\n\n" +
                    "仍然要用「" + show + "」吗？",
                    "确认使用单个按键", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes)
                {
                    _lblHotkeyHint.Text = "换个键吧：建议 F1–F12、小键盘或方向键。";
                    return;
                }
            }
            ApplyHotkey(mods, vk);
        }

        /// <summary>刷新界面上显示的快捷键文案。</summary>
        private void UpdateHotkeyUi()
        {
            bool has = _curVk != 0;
            if (!_recording)
            {
                _btnHotkey.Text = has ? HotkeyDisplay(_curMods, _curVk) : "点这里设置";
                _btnHotkey.BackColor = Color.FromArgb(247, 249, 250);
                _btnHotkey.ForeColor = has ? UiKit.TextMain : UiKit.TextSub;
                _btnAssign.Text = "给选中设备设按键";
                _btnAssign.BackColor = Color.FromArgb(247, 249, 250);
                _btnAssign.ForeColor = UiKit.TextMain;
                _lblHotkeyHint.Text = has
                    ? "点它可改这个键；给某台设备单独设键请用左边的按钮。"
                    : "点它设置一键切换的快捷键。";
            }
            if (_btnAssign != null) _btnAssign.Enabled = _list.SelectedIndex >= 0;
            if (_miToggle != null)
                _miToggle.Text = "一键切换输出" + (has ? "  (" + HotkeyDisplay(_curMods, _curVk) + ")" : "");
            _lblSub.Text = SubtitleText();
            if (string.IsNullOrEmpty(_lblStatus.Text))
                _lblStatus.Text = has
                    ? "关闭窗口后程序会缩到右下角托盘继续待命，按 " + HotkeyDisplay(_curMods, _curVk) + " 随时切换；右键托盘图标可退出。"
                    : "关闭窗口后程序会缩到右下角托盘继续待命；右键托盘图标可退出。";
        }

        private string SubtitleText()
        {
            string baseText = "共 " + _devices.Count + " 台设备　·　双击即可切换";
            return _curVk != 0 ? baseText + "　·　快捷键 " + HotkeyDisplay(_curMods, _curVk) : baseText;
        }

        /// <summary>热键都由隐藏的热键窗口转过来。</summary>
        private void OnHotkeyPressed(int id)
        {
            if (id == HOTKEY_ID) { DoToggle(); return; }

            string deviceId;
            if (_deviceKeyIds.TryGetValue(id, out deviceId))
            {
                AudioDevice d = FindDeviceById(deviceId);
                if (d != null) SwitchTo(d);
                else LoadDevices();          // 设备信息过期了（刚插上/刚拔掉），刷新一下
            }
        }

        /// <summary>资源管理器重启（或开机时托盘还没准备好）之后，把托盘图标补回来。</summary>
        private void RestoreTrayIcon()
        {
            try
            {
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Visible = true;
                }
            }
            catch { }
            LoadDevices();
        }

        /// <summary>
        /// 每 20 秒自检一次：
        /// · 设备有变化（开机时音频设备还没就绪、插拔设备）就重新读取，顺便把设备的按键登记好；
        /// · 开机时快捷键如果被其他启动程序抢先占用，这里会自动补注册。
        /// </summary>
        private void Housekeeping()
        {
            try
            {
                if (_recording) return;

                List<AudioDevice> list = Audio.GetActiveOutputs();
                if (DeviceSignature(list) != _deviceSignature)
                {
                    LoadDevices();
                    return;
                }

                if (_hotkeyRetry > 0 && _curVk == 0 && !string.IsNullOrWhiteSpace(Config.Hotkey))
                {
                    _hotkeyRetry--;
                    RegisterConfiguredHotkey();
                }

                // 每 5 分钟顺带确认一次开机启动项还指着当前程序
                if (++_autoStartCheckTick >= 15)
                {
                    _autoStartCheckTick = 0;
                    EnsureAutoStart();
                }
            }
            catch { }
        }

        private static string DeviceSignature(List<AudioDevice> list)
        {
            var sb = new StringBuilder();
            foreach (AudioDevice d in list) sb.Append(d.Id).Append('|');
            return sb.ToString();
        }

        // ---- 快捷键名称表（虚拟键码 <-> 可写进配置的名字）----

        private static readonly Dictionary<int, string> KeyNames = BuildKeyNames();

        private static Dictionary<int, string> BuildKeyNames()
        {
            var d = new Dictionary<int, string>();
            for (int i = 1; i <= 24; i++) d[0x70 + i - 1] = "F" + i;
            for (int i = 0; i <= 9; i++) d[0x60 + i] = "NUMPAD" + i;
            d[0x6A] = "NUMPADMULTIPLY"; d[0x6B] = "NUMPADADD"; d[0x6D] = "NUMPADSUBTRACT";
            d[0x6E] = "NUMPADDECIMAL"; d[0x6F] = "NUMPADDIVIDE";
            d[0x20] = "SPACE"; d[0x09] = "TAB"; d[0x24] = "HOME"; d[0x23] = "END";
            d[0x2D] = "INSERT"; d[0x2E] = "DELETE"; d[0x21] = "PAGEUP"; d[0x22] = "PAGEDOWN";
            d[0x26] = "UP"; d[0x28] = "DOWN"; d[0x25] = "LEFT"; d[0x27] = "RIGHT";
            return d;
        }

        private static string VkName(uint vk)
        {
            int k = (int)vk;
            if (k >= 'A' && k <= 'Z') return ((char)k).ToString();
            if (k >= '0' && k <= '9') return ((char)k).ToString();
            string n;
            return KeyNames.TryGetValue(k, out n) ? n : null;
        }

        private static uint NameToVk(string name)
        {
            if (name.Length == 1)
            {
                char c = name[0];
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return c;
            }
            foreach (var kv in KeyNames)
                if (string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)) return (uint)kv.Key;
            return 0;
        }

        /// <summary>写进配置文件的形式，例如 Ctrl+Alt+F9</summary>
        private static string HotkeyKeyText(uint mods, uint vk)
        {
            var sb = new StringBuilder();
            if ((mods & MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((mods & MOD_ALT) != 0) sb.Append("Alt+");
            if ((mods & MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((mods & MOD_WIN) != 0) sb.Append("Win+");
            sb.Append(VkName(vk) ?? "?");
            return sb.ToString();
        }

        /// <summary>界面上显示的形式，例如 Ctrl + Alt + F9</summary>
        public static string HotkeyDisplay(uint mods, uint vk)
        {
            var sb = new StringBuilder();
            if ((mods & MOD_CONTROL) != 0) sb.Append("Ctrl + ");
            if ((mods & MOD_ALT) != 0) sb.Append("Alt + ");
            if ((mods & MOD_SHIFT) != 0) sb.Append("Shift + ");
            if ((mods & MOD_WIN) != 0) sb.Append("Win + ");
            sb.Append(PrettyKeyName(vk));
            return sb.ToString();
        }

        public static string ModsText(Keys mods)
        {
            var sb = new StringBuilder();
            if ((mods & Keys.Control) != 0) sb.Append("Ctrl");
            if ((mods & Keys.Alt) != 0) sb.Append(sb.Length > 0 ? " + Alt" : "Alt");
            if ((mods & Keys.Shift) != 0) sb.Append(sb.Length > 0 ? " + Shift" : "Shift");
            return sb.ToString();
        }

        private static string PrettyKeyName(uint vk)
        {
            string n = VkName(vk);
            if (string.IsNullOrEmpty(n)) return "?";
            switch (n)
            {
                case "SPACE": return "空格";
                case "UP": return "↑";
                case "DOWN": return "↓";
                case "LEFT": return "←";
                case "RIGHT": return "→";
                case "NUMPADMULTIPLY": return "数字键盘 *";
                case "NUMPADADD": return "数字键盘 +";
                case "NUMPADSUBTRACT": return "数字键盘 -";
                case "NUMPADDIVIDE": return "数字键盘 /";
                case "NUMPADDECIMAL": return "数字键盘 .";
            }
            if (n.StartsWith("NUMPAD")) return "数字键盘 " + n.Substring(6);
            if (n.Length == 1) return n.ToUpperInvariant();
            if (n.Length == 2 || (n[0] == 'F' && n.Length <= 3)) return n;
            return n.Substring(0, 1) + n.Substring(1).ToLowerInvariant();
        }

        public static bool TryParseHotkey(string text, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (string piece in text.Split('+'))
            {
                string p = piece.Trim().ToUpperInvariant();
                if (p.Length == 0) continue;
                if (p == "CTRL" || p == "CONTROL") { mods |= MOD_CONTROL; continue; }
                if (p == "ALT") { mods |= MOD_ALT; continue; }
                if (p == "SHIFT") { mods |= MOD_SHIFT; continue; }
                if (p == "WIN" || p == "WINDOWS") { mods |= MOD_WIN; continue; }
                uint code = NameToVk(p);
                if (code == 0) return false;
                vk = code;
            }
            if (vk == 0) return false;
            // 允许单独的按键（不带 Ctrl/Alt/Shift）；界面里对字母、数字这类会先弹窗提醒
            return true;
        }

        // ---------- 逻辑 ----------

        private void LoadDevices()
        {
            string selectedId = null;
            if (_list.SelectedIndex >= 0 && _list.SelectedIndex < _devices.Count)
                selectedId = _devices[_list.SelectedIndex].Id;

            _devices = Audio.GetActiveOutputs();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var d in _devices) _list.Items.Add(d.Display);
            _list.EndUpdate();

            AudioDevice current = null;
            foreach (var d in _devices) if (d.IsDefault) { current = d; break; }
            _lblCurrent.Text = current != null ? "当前输出：" + current.Name : "当前输出：未知";

            int sel = 0;
            if (selectedId != null)
                for (int i = 0; i < _devices.Count; i++)
                    if (_devices[i].Id == selectedId) { sel = i; break; }
            if (_devices.Count > 0) _list.SelectedIndex = sel;

            _lblSub.Text = SubtitleText();

            UpdateButtons();
            _deviceSignature = DeviceSignature(_devices);
            if (!_recording) RegisterDeviceHotkeys();   // 设备插拔后重新登记各自的按键
        }

        private void UpdateButtons()
        {
            bool has = _devices.Count > 1;
            _btnToggle.Enabled = has;
            _btnSet.Enabled = _list.SelectedIndex >= 0;
            if (_btnAssign != null) _btnAssign.Enabled = _list.SelectedIndex >= 0;
        }

        private void SetSelectedAsDefault()
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _devices.Count) return;
            SwitchTo(_devices[i]);
        }

        private void DoToggle()
        {
            AudioDevice target; string error;
            AudioDevice before = Audio.GetDefaultOutput();
            if (Audio.Toggle(out target, out error))
            {
                AfterSwitch(target, before);
            }
            else
            {
                if (Visible && ActiveForm != null) { _lblStatus.Text = error; LoadDevices(); }
                else ToastForm.Popup("无法切换音频输出", error);
            }
        }

        private void SwitchTo(AudioDevice device)
        {
            if (device == null) return;
            AudioDevice before = Audio.GetDefaultOutput();
            if (before != null && before.Id == device.Id)
            {
                _lblStatus.Text = "「" + device.Name + "」已经是当前默认输出。";
                return;
            }
            string error;
            if (!Audio.SetDefaultOutput(device.Id, out error))
            {
                _lblStatus.Text = error;
                return;
            }
            if (before != null) { Config.LastDeviceId = before.Id; Config.Save(); }

            foreach (var d in _devices) d.IsDefault = (d.Id == device.Id);
            AfterSwitch(device, before);
        }

        private void AfterSwitch(AudioDevice device, AudioDevice before)
        {
            _lblStatus.Text = "已切换到「" + device.Name + "」"
                + (before != null ? "（原设备：" + before.Name + "）" : "")
                + "　" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            _list.Invalidate();

            bool windowFocused = Visible && (ActiveForm != null || ContainsFocus);
            if (!windowFocused)
                ToastForm.Popup("音频输出已切换", device.Name + (string.IsNullOrEmpty(device.Adapter) ? "" : "（" + device.Adapter + "）"));

            if (_tray != null && _tray.Visible) _tray.Text = "音频输出：" + device.Name;
            LoadDevices();
        }

        private void ShowWindow()
        {
            _allowShow = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            LoadDevices();
        }

        private void CreateDesktopShortcut()
        {
            string path = Path.Combine(UiKit.DesktopDir, "快速切换音频输出.lnk");
            bool ok = UiKit.CreateShortcut(path, UiKit.ExePath, "--toggle",
                                           "双击即可在最近使用的两台音频输出设备之间切换");
            _lblStatus.Text = ok ? "已创建桌面快捷方式：快速切换音频输出" : "创建桌面快捷方式失败。";
        }

        private void SetAutoStart(bool enable)
        {
            string path = Path.Combine(UiKit.StartupDir, "音频切换器.lnk");
            try
            {
                if (enable)
                {
                    if (!UiKit.CreateShortcut(path, UiKit.ExePath, "--tray", "开机自动驻留托盘，随时切换音频输出"))
                    { _lblStatus.Text = "设置开机自启失败。"; _miAutoStart.Checked = false; return; }
                    Config.AutoStart = true;
                }
                else
                {
                    if (File.Exists(path)) File.Delete(path);
                    Config.AutoStart = false;
                }
                Config.Save();
                _lblStatus.Text = enable ? "已开启开机自动启动（静默驻留托盘）。" : "已关闭开机自动启动。";
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "设置开机自启失败：" + ex.Message;
                _miAutoStart.Checked = !enable;
            }
        }

        /// <summary>
        /// 开机启动项自愈：程序换过位置、或快捷方式被误删/损坏时，自动指向当前 exe。
        /// （改启动文件夹需要以用户身份运行，本程序本身就是，所以能做这件事。）
        /// </summary>
        private void EnsureAutoStart()
        {
            try
            {
                if (!Config.AutoStart) return;
                string path = Path.Combine(UiKit.StartupDir, "音频切换器.lnk");
                string exe = UiKit.ExePath;
                string current = File.Exists(path) ? UiKit.ReadShortcutTarget(path) : null;
                if (!string.Equals(current, exe, StringComparison.OrdinalIgnoreCase))
                    UiKit.CreateShortcut(path, exe, "--tray", "开机自动驻留托盘，随时切换音频输出");
            }
            catch { }
        }

        // ---------- 生命周期 ----------

        protected override void SetVisibleCore(bool value)
        {
            if (_startHidden && !_allowShow)
            {
                CreateHandle();
                base.SetVisibleCore(false);
                return;
            }
            base.SetVisibleCore(value);
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!_trayHintShown)
                {
                    _trayHintShown = true;
                    try
                    {
                        _tray.BalloonTipTitle = "音频切换器仍在后台运行";
                        _tray.BalloonTipText = "按 " + Config.Hotkey + " 或双击托盘图标可继续使用；右键托盘图标可退出。";
                        _tray.ShowBalloonTip(3000);
                    }
                    catch { }
                }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { if (_hotkeyOk) _hotkeys.Unregister(HOTKEY_ID); } catch { }
            try { if (_housekeeping != null) { _housekeeping.Stop(); _housekeeping.Dispose(); } } catch { }
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            base.OnFormClosed(e);
        }
    }

    // ========================================================================
    //  7. 程序入口
    // ========================================================================

    internal static class Program
    {
        private const string Help =
            "音频切换器\r\n" +
            "用法：\r\n" +
            "  音频切换器.exe                 打开切换窗口（带托盘与快捷键）\r\n" +
            "  音频切换器.exe --tray          仅驻留托盘，不显示窗口（开机自启用）\r\n" +
            "  音频切换器.exe --toggle        立刻在最近两台设备间切换，然后退出\r\n" +
            "  音频切换器.exe --set 名称      按序号/名称/ID 切换到指定设备\r\n" +
            "  音频切换器.exe --list          打印所有可用输出设备\r\n";

        internal static void Error(string title, string message)
        {
            try { MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            catch { }
        }

        [STAThread]
        internal static void Main(string[] args)
        {
            bool consoleMode = false;
            string shotPath = null;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Trim().TrimStart('-', '/').ToLowerInvariant() == "shot") shotPath = args[i + 1];

            foreach (string a in args)
            {
                string t = a.Trim().TrimStart('-', '/').ToLowerInvariant();
                if (t == "list" || t == "devices" || t == "toggle" || t == "set" ||
                    t == "help" || t == "?" || t == "diag" || t == "icon")
                    consoleMode = true;
            }

            if (shotPath != null)
            {
                try { Native.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Config.Load();
                try
                {
                    using (var f = new MainForm(false))
                    {
                        f.StartPosition = FormStartPosition.Manual;
                        f.Location = new Point(-4000, -4000);   // 挪到屏幕外，用户看不到
                        f.Show();
                        Application.DoEvents();
                        f.CreateControl();
                        f.Refresh();
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(250);
                        Application.DoEvents();
                        using (var bmp = new Bitmap(f.Width, f.Height))
                        {
                            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                            using (var ms = new MemoryStream())
                            {
                                bmp.Save(ms, ImageFormat.Png);
                                File.WriteAllBytes(shotPath, ms.ToArray());
                                Native.WriteLine("截图已保存：" + shotPath + " (" + ms.Length + " 字节)");
                            }
                        }
                        f.Hide();
                    }
                }
                catch (Exception ex) { Native.WriteLine("截图失败:" + ex.ToString()); }
                return;
            }

            if (consoleMode)
            {
                SafeConsole(() => RunCommand(args));
                return;
            }

            try { Native.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate (object s, System.Threading.ThreadExceptionEventArgs e)
            {
                Error("音频切换器", "程序遇到问题：" + e.Exception.Message);
            };

            Config.Load();

            bool startHidden = false;
            foreach (string a in args)
                if (a.Trim().TrimStart('-', '/').ToLowerInvariant() == "tray" ||
                    a.Trim().TrimStart('-', '/').ToLowerInvariant() == "hide") startHidden = true;

            try
            {
                Application.Run(new MainForm(startHidden));
            }
            catch (Exception ex)
            {
                Error("音频切换器", "程序启动失败：" + ex.Message);
            }
        }

        private static void SafeConsole(Action action)
        {
            try { action(); }
            catch (Exception ex) { Native.WriteLine("出错：" + ex.Message); }
        }

        private static void RunCommand(string[] args)
        {
            Config.Load();

            string mode = args.Length > 0 ? args[0].Trim().TrimStart('-', '/').ToLowerInvariant() : "";
            string arg1 = args.Length > 1 ? args[1] : "";

            if (mode == "help" || mode == "?") { Native.WriteLine(Help); return; }

            if (mode == "diag")
            {
                Native.WriteLine(Config.Diagnose());
                return;
            }

            if (mode == "icon")
            {
                Native.WriteLine(UiKit.WriteIconFile(arg1) ? "图标已生成：" + arg1 : "图标生成失败");
                return;
            }

            if (mode == "list" || mode == "devices")
            {
                var devices = Audio.GetActiveOutputs();
                Native.WriteLine("共 " + devices.Count + " 台可用输出设备：");
                for (int i = 0; i < devices.Count; i++)
                    Native.WriteLine(string.Format("  [{0}] {1}{2}",
                        i + 1, devices[i].Display, devices[i].IsDefault ? "   <= 当前默认" : ""));
                return;
            }

            if (mode == "set")
            {
                var devices = Audio.GetActiveOutputs();
                AudioDevice device = Audio.Find(devices, arg1);
                if (device == null) { Native.WriteLine("未找到匹配「" + arg1 + "」的输出设备。"); return; }
                AudioDevice before = null;
                foreach (var d in devices) if (d.IsDefault) { before = d; break; }
                string error;
                if (!Audio.SetDefaultOutput(device.Id, out error)) { Native.WriteLine(error); return; }
                if (before != null) { Config.LastDeviceId = before.Id; Config.Save(); }
                Native.WriteLine("已切换到：" + device.Display);
                TryShowToast("音频输出已切换", device.Name + (string.IsNullOrEmpty(device.Adapter) ? "" : "（" + device.Adapter + "）"));
                return;
            }

            if (mode == "toggle")
            {
                bool quiet = false;
                foreach (string a in args)
                    if (a.Trim().TrimStart('-', '/').ToLowerInvariant() == "quiet") quiet = true;

                AudioDevice target; string error;
                if (Audio.Toggle(out target, out error))
                {
                    Native.WriteLine("已切换到：" + target.Display);
                    if (!quiet) TryShowToast("音频输出已切换", target.Name + (string.IsNullOrEmpty(target.Adapter) ? "" : "（" + target.Adapter + "）"));
                }
                else
                {
                    Native.WriteLine(error);
                    if (!quiet) TryShowToast("无法切换音频输出", error);
                }
                return;
            }

            Native.WriteLine(Help);
        }

        private static void TryShowToast(string title, string message)
        {
            try
            {
                Native.SetProcessDPIAware();
                ToastForm.Popup(title, message);
            }
            catch { }
        }
    }
}
