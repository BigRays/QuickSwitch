// QuickSwitch.cs
// Listary「快速切换 (Quick Switch)」功能的独立实现：
// 1. 自动切换（默认开启）：打开/保存文件对话框获得焦点时，自动跳转到最近在资源管理器中访问的文件夹；
//    仅当对话框获得焦点期间资源管理器浏览过新文件夹时才再次跳转，不会打乱手动导航。
// 2. 手动菜单：在文件对话框中按快捷键（默认 Ctrl+`，可自定义）弹出最近文件夹菜单，任意选择切换。
// 切换机制：向"文件名"框写入目标路径并模拟回车（等价用户粘贴+回车），后台线程回读校验、未生效自动重试，
//           只有确认生效才提示"已切换"；全程读不到输入框内容时说明目标程序以管理员身份运行被系统
//           拦截（UIPI），会明确提示并以托盘"以管理员身份重启"一键解决。
// 快捷键/自动开关/提权/开机自启动选项：托盘图标右键菜单（即时生效），或手动编辑 QuickSwitch.ini 后重启；
//   开机自启动写入注册表 HKCU\...\Run 启动项（可在任务管理器的"启动应用"中管理）。
// 构建：运行同目录下的 build.cmd（使用 Windows 自带的 .NET Framework 编译器，无需安装开发环境）。

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace QuickSwitch
{
    internal static class Program
    {
        private static Mutex singleInstance;

        [STAThread]
        private static void Main()
        {
            // --wait-parent <pid>：以管理员身份重启的接力实例，先等旧实例退出再初始化，避免单实例互斥冲突
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                if (args.Length >= 3 && args[1] == "--wait-parent")
                {
                    int parentPid;
                    if (int.TryParse(args[2], out parentPid))
                    {
                        Process parent = Process.GetProcessById(parentPid);
                        if (parent != null) parent.WaitForExit(5000);
                    }
                }
            }
            catch { }

            bool createdNew;
            singleInstance = new Mutex(true, "QuickSwitch::SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("QuickSwitch 已在运行（请查看系统托盘图标）。", "QuickSwitch",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AppContext context;
            try
            {
                context = new AppContext();
            }
            catch (Exception ex)
            {
                MessageBox.Show("QuickSwitch 运行出错，即将退出：" + ex.Message, "QuickSwitch",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (context.IsRestartingElevated) return; // 已启动管理员实例，本实例直接退出

            try
            {
                Application.Run(context);
            }
            catch (Exception ex)
            {
                MessageBox.Show("QuickSwitch 运行出错，即将退出：" + ex.Message, "QuickSwitch",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    internal static class HotkeyUtil
    {
        public const uint DefaultMods = Native.MOD_CONTROL;
        public const uint DefaultVk = Native.VK_OEM_3; // `（反引号）

        public static string FormatHotkey(uint mods, uint vk)
        {
            StringBuilder sb = new StringBuilder();
            if ((mods & Native.MOD_CONTROL) != 0) sb.Append("Ctrl + ");
            if ((mods & Native.MOD_ALT) != 0) sb.Append("Alt + ");
            if ((mods & Native.MOD_SHIFT) != 0) sb.Append("Shift + ");
            if ((mods & Native.MOD_WIN) != 0) sb.Append("Win + ");
            sb.Append(VkToString(vk));
            return sb.ToString();
        }

        public static string VkToString(uint vk)
        {
            if (vk >= 0x30 && vk <= 0x39) return ((char)('0' + (vk - 0x30))).ToString();
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            switch (vk)
            {
                case 0xC0: return "`";
                case 0xBA: return ";";
                case 0xBB: return "=";
                case 0xBC: return ",";
                case 0xBD: return "-";
                case 0xBE: return ".";
                case 0xBF: return "/";
                case 0xDB: return "[";
                case 0xDC: return "\\";
                case 0xDD: return "]";
                case 0xDE: return "'";
                case 0x20: return "空格";
                default: return "键0x" + vk.ToString("X");
            }
        }

        public static bool IsValidHotkey(uint mods, uint vk)
        {
            if (vk < 0x08 || vk > 0xFE) return false;
            if (mods == 0) return false;
            if (mods == Native.MOD_SHIFT) return false; // 仅 Shift 的组合不可靠
            return true;
        }
    }

    internal sealed class AppContext : ApplicationContext
    {
        private const int PollIntervalMs = 800;
        private const int MaxHistory = 15;
        private const int MaxMenuItems = 10;

        private uint hotkeyMods = HotkeyUtil.DefaultMods;
        private uint hotkeyVk = HotkeyUtil.DefaultVk;
        private bool autoSwitchEnabled = true;
        private bool autoElevate;
        private bool isElevated;

        public bool IsRestartingElevated { get; private set; }

        private readonly dynamic shell;
        private readonly NotifyIcon tray;
        private readonly Icon trayIcon;
        private readonly HotkeyWindow hotkeyWindow = new HotkeyWindow();
        private readonly Timer pollTimer = new Timer();
        private readonly ContextMenuStrip switchMenu = new ContextMenuStrip();
        private readonly List<string> history = new List<string>();
        private readonly Dictionary<long, string> explorerPaths = new Dictionary<long, string>();
        private readonly Dictionary<string, Image> iconCache =
            new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        // 记录每个文件对话框上次获焦时的"最近文件夹"，用于判断是否发生了新的资源管理器导航
        private readonly Dictionary<IntPtr, string> dialogLastHead = new Dictionary<IntPtr, string>();

        // 同一对话框的切换请求代数：新请求自增，旧的后台重试流程检测到被取代后自动放弃
        private readonly Dictionary<IntPtr, int> switchGen = new Dictionary<IntPtr, int>();
        private readonly object switchGenLock = new object();

        private readonly Native.WinEventProc winEventProc;
        private IntPtr foregroundHook;
        private IntPtr destroyHook;
        private ToolStripMenuItem autoSwitchItem;
        private ToolStripMenuItem autoElevateItem;

        public AppContext()
        {
            switchMenu.ShowItemToolTips = true;
            switchMenu.Font = new Font("Segoe UI", 9F);

            LoadConfig();
            isElevated = IsProcessElevated();

            // PopupTip 可能从后台线程调用（切换校验流程），先记录 UI 线程用于封送
            PopupTip.UiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            PopupTip.UiThreadId = Thread.CurrentThread.ManagedThreadId;

            // 配置了"始终以管理员身份运行"而当前不是管理员：启动管理员实例后本实例退出
            if (autoElevate && !isElevated && TryRestartElevated())
            {
                IsRestartingElevated = true;
                return;
            }

            shell = CreateShell();

            trayIcon = CreateTrayIcon();
            tray = new NotifyIcon();
            tray.Icon = (trayIcon != null) ? trayIcon : SystemIcons.Application;
            tray.ContextMenuStrip = BuildTrayMenu();
            tray.Visible = true;
            UpdateTrayText();

            hotkeyWindow.HotkeyPressed += new Action(ShowSwitchMenu);
            if (!ApplyHotkey(hotkeyMods, hotkeyVk))
            {
                MessageBox.Show(
                    "快捷键 " + HotkeyText + " 注册失败，可能已被其他程序占用。\n\n" +
                    "请右键点击托盘图标，选择\"设置快捷键…\"更换组合（自动切换功能不依赖快捷键）。",
                    "QuickSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            winEventProc = new Native.WinEventProc(OnWinEvent);
            foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND,
                Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
            destroyHook = Native.SetWinEventHook(Native.EVENT_OBJECT_DESTROY,
                Native.EVENT_OBJECT_DESTROY, IntPtr.Zero, winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);

            pollTimer.Interval = PollIntervalMs;
            pollTimer.Tick += new EventHandler(delegate { PollTick(); });
            PollTick();
            pollTimer.Start();

            PopupTip.ShowTip((autoSwitchEnabled
                ? "QuickSwitch 已启动（" + (isElevated ? "管理员" : "普通") + "权限）：文件对话框将自动跳转到最近访问的文件夹"
                : "QuickSwitch 已启动（自动切换已关闭）") + "，手动菜单快捷键：" + HotkeyText);
        }

        private static bool IsProcessElevated()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        // 请求以管理员身份启动一个新实例（--wait-parent 接力），成功返回 true；UAC 被取消返回 false
        private bool TryRestartElevated()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.Arguments = "--wait-parent " + Process.GetCurrentProcess().Id;
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                return Process.Start(psi) != null;
            }
            catch (Win32Exception) { return false; } // 用户在 UAC 中选择了"否"
            catch { return false; }
        }

        private string HotkeyText
        {
            get { return HotkeyUtil.FormatHotkey(hotkeyMods, hotkeyVk); }
        }

        private void UpdateTrayText()
        {
            string t = "QuickSwitch 快速切换 (" + HotkeyText + ")" + (isElevated ? " [管理员]" : "");
            if (t.Length > 63) t = t.Substring(0, 63);
            tray.Text = t;
        }

        private bool ApplyHotkey(uint mods, uint vk)
        {
            hotkeyWindow.Unregister();
            if (hotkeyWindow.Register(1, mods, vk))
            {
                hotkeyMods = mods;
                hotkeyVk = vk;
                UpdateTrayText();
                return true;
            }
            hotkeyWindow.Register(1, hotkeyMods, hotkeyVk); // 恢复原热键
            UpdateTrayText();
            return false;
        }

        private string ConfigPath
        {
            get { return Path.Combine(Application.StartupPath, "QuickSwitch.ini"); }
        }

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    SaveConfig(); // 首次运行生成默认配置
                    return;
                }
                uint mods = 0, vk = 0;
                string[] lines = File.ReadAllLines(ConfigPath);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("[")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (String.Equals(key, "Modifiers", StringComparison.OrdinalIgnoreCase)) mods = ParseUInt(val);
                    else if (String.Equals(key, "Vk", StringComparison.OrdinalIgnoreCase)) vk = ParseUInt(val);
                    else if (String.Equals(key, "AutoSwitch", StringComparison.OrdinalIgnoreCase))
                    {
                        autoSwitchEnabled = !(val == "0" || String.Equals(val, "false", StringComparison.OrdinalIgnoreCase));
                    }
                    else if (String.Equals(key, "AutoElevate", StringComparison.OrdinalIgnoreCase))
                    {
                        autoElevate = !(val == "0" || String.Equals(val, "false", StringComparison.OrdinalIgnoreCase));
                    }
                }
                if (HotkeyUtil.IsValidHotkey(mods, vk))
                {
                    hotkeyMods = mods;
                    hotkeyVk = vk;
                }
            }
            catch { }
        }

        private static uint ParseUInt(string s)
        {
            try
            {
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return Convert.ToUInt32(s, 16);
                return Convert.ToUInt32(s);
            }
            catch { return 0; }
        }

        private void SaveConfig()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("; QuickSwitch 配置");
                sb.AppendLine("; 推荐通过托盘菜单图形化修改（即时生效）");
                sb.AppendLine("; 手动修改本文件后需重启 QuickSwitch 生效");
                sb.AppendLine("[Settings]");
                sb.AppendLine("; 修饰键：Ctrl=2  Alt=1  Shift=4  Win=8（可相加组合，如 Ctrl+Alt=3）");
                sb.AppendLine("; 虚拟键码：A-Z=65-90  0-9=48-57  F1-F12=112-123  `(反引号)=192  空格=32");
                sb.AppendLine("Modifiers=" + hotkeyMods);
                sb.AppendLine("Vk=" + hotkeyVk);
                sb.AppendLine("; 自动切换：1=开启（默认） 0=关闭");
                sb.AppendLine("AutoSwitch=" + (autoSwitchEnabled ? "1" : "0"));
                sb.AppendLine("; 始终以管理员身份运行：1=每次启动时请求管理员权限（操作以管理员运行的程序如 VSCode 需要） 0=普通权限（默认）");
                sb.AppendLine("AutoElevate=" + (autoElevate ? "1" : "0"));
                File.WriteAllText(ConfigPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private void ShowHotkeySettings()
        {
            using (HotkeyForm form = new HotkeyForm(hotkeyMods, hotkeyVk))
            {
                form.ShowDialog();
                if (!form.Confirmed) return;
                if (ApplyHotkey(form.PickedMods, form.PickedVk))
                {
                    SaveConfig();
                    PopupTip.ShowTip("快捷键已设置为：" + HotkeyText);
                }
                else
                {
                    MessageBox.Show("快捷键 " + HotkeyUtil.FormatHotkey(form.PickedMods, form.PickedVk) +
                        " 注册失败（可能已被其他程序占用），已保留原快捷键 " + HotkeyText + "。",
                        "QuickSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void ToggleAutoSwitch()
        {
            autoSwitchEnabled = !autoSwitchEnabled;
            if (autoSwitchItem != null) autoSwitchItem.Checked = autoSwitchEnabled;
            SaveConfig();
            PopupTip.ShowTip(autoSwitchEnabled
                ? "已开启自动切换：打开/保存对话框将自动跳转到最近访问的文件夹"
                : "已关闭自动切换，可按 " + HotkeyText + " 手动选择");
        }

        private static dynamic CreateShell()
        {
            try
            {
                Type t = Type.GetTypeFromProgID("Shell.Application");
                if (t == null) return null;
                return Activator.CreateInstance(t);
            }
            catch { return null; }
        }

        // 开机自启动：注册表 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 启动项。
        // 状态以注册表为准（而非 ini），用户在任务管理器"启动应用"里禁用后菜单能如实反映。
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "QuickSwitch";

        private static bool IsAutoStartRegistered()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                    return key != null && key.GetValue(RunValueName) != null;
            }
            catch { return false; }
        }

        private static void SetAutoStart(bool enable)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null) throw new Exception("无法打开注册表启动项（HKCU…\\Run）。");
                if (enable) key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue(RunValueName, false);
            }
        }

        private ContextMenuStrip BuildTrayMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            autoSwitchItem = new ToolStripMenuItem("自动切换到最近文件夹");
            autoSwitchItem.Checked = autoSwitchEnabled;
            autoSwitchItem.ToolTipText = "文件对话框获得焦点时，自动跳转到最近在资源管理器访问的文件夹";
            autoSwitchItem.Click += new EventHandler(delegate { ToggleAutoSwitch(); });
            m.Items.Add(autoSwitchItem);

            if (!isElevated)
            {
                ToolStripItem restartItem = m.Items.Add("以管理员身份重启", null, new EventHandler(delegate
                {
                    if (TryRestartElevated()) ExitApp();
                    else PopupTip.ShowTip("未能以管理员身份重启：请在 UAC 提示中选择\"是\"。");
                }));
                restartItem.ToolTipText = "要操作的程序以管理员身份运行（如设置了管理员兼容属性的 VSCode）时，QuickSwitch 也需要管理员权限";
            }

            autoElevateItem = new ToolStripMenuItem("始终以管理员身份运行");
            autoElevateItem.Checked = autoElevate;
            autoElevateItem.ToolTipText = "每次启动 QuickSwitch 时自动请求管理员权限（在 UAC 弹窗中选择\"是\"）";
            autoElevateItem.Click += new EventHandler(delegate
            {
                autoElevate = !autoElevate;
                autoElevateItem.Checked = autoElevate;
                SaveConfig();
                if (!autoElevate)
                {
                    PopupTip.ShowTip("已关闭\"始终以管理员身份运行\"。");
                    return;
                }
                if (isElevated)
                {
                    PopupTip.ShowTip("已保存：以后每次启动 QuickSwitch 都会请求管理员权限。");
                    return;
                }
                if (TryRestartElevated()) ExitApp();
                else PopupTip.ShowTip("已保存。本次 UAC 未确认，QuickSwitch 暂以普通权限运行，下次启动会再次请求。");
            });
            m.Items.Add(autoElevateItem);

            ToolStripMenuItem autoStartItem = new ToolStripMenuItem("开机自启动");
            autoStartItem.Checked = IsAutoStartRegistered();
            autoStartItem.ToolTipText = "随 Windows 启动自动运行（写入注册表 HKCU…\\Run 启动项，" +
                "也可在任务管理器的\"启动应用\"中管理）";
            autoStartItem.Click += new EventHandler(delegate
            {
                try
                {
                    bool enabled = !IsAutoStartRegistered();
                    SetAutoStart(enabled);
                    autoStartItem.Checked = IsAutoStartRegistered();
                    PopupTip.ShowTip(enabled ? "已开启开机自启动。" : "已关闭开机自启动。");
                }
                catch (Exception ex)
                {
                    PopupTip.ShowTip("修改开机自启动失败：" + ex.Message);
                }
            });
            m.Items.Add(autoStartItem);

            m.Items.Add("设置快捷键…", null, new EventHandler(delegate { ShowHotkeySettings(); }));
            m.Items.Add("使用说明", null, new EventHandler(delegate
            {
                PopupTip.ShowTip("文件对话框将自动跳转到最近访问的文件夹；按 " + HotkeyText +
                    " 可弹出菜单手动选择其他文件夹。");
            }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("退出", null, new EventHandler(delegate { ExitApp(); }));
            return m;
        }

        // WinEvent 回调：前台窗口切换 / 窗口销毁
        private void OnWinEvent(IntPtr hHook, uint eventType, IntPtr hwnd, int idObject, int idChild,
            uint dwEventThread, uint dwmsEventTime)
        {
            if (hwnd == IntPtr.Zero) return;
            if (eventType == Native.EVENT_OBJECT_DESTROY)
            {
                if (idObject == Native.OBJID_WINDOW)
                {
                    dialogLastHead.Remove(hwnd);
                    lock (switchGenLock) switchGen.Remove(hwnd);
                }
                return;
            }
            if (eventType == Native.EVENT_SYSTEM_FOREGROUND)
            {
                // 前台切到资源管理器：立即把它正在显示的目录记为"最新"（不等轮询），
                // 这样用户切回文件对话框时，跳转目标就是刚才正在浏览的窗口/文件夹
                if (IsWindowClass(hwnd, "CabinetWClass"))
                {
                    PollExplorerWindows();
                    TouchForegroundExplorer(hwnd);
                }
                else
                {
                    OnForegroundChanged(hwnd);
                }
            }
        }

        // 把前台资源管理器窗口当前显示的目录写入 MRU 首位
        private void TouchForegroundExplorer(IntPtr hwnd)
        {
            string path;
            if (explorerPaths.TryGetValue(hwnd.ToInt64(), out path) &&
                !String.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                TouchHistory(path);
            }
        }

        // 自动切换核心：对话框获得焦点时，若"最近文件夹"较上次获焦发生了变化（即期间在资源管理器
        // 浏览过新文件夹），则自动跳转过去；否则不打扰（保留用户手动导航的结果）
        private void OnForegroundChanged(IntPtr hwnd)
        {
            if (!autoSwitchEnabled) return;
            if (hwnd == IntPtr.Zero) return;
            if (!IsWindowClass(hwnd, "#32770")) return;

            IntPtr edit = FindFileNameEdit(hwnd);
            if (edit == IntPtr.Zero) return; // 不是文件对话框（其他 #32770 窗口）

            // 切换前强制刷新一遍资源管理器状态：覆盖"刚打开新窗口、COM 记录尚未就绪"的时序
            PollExplorerWindows();

            string head = (history.Count > 0) ? history[0] : null;

            string prevHead;
            bool headChanged = !dialogLastHead.TryGetValue(hwnd, out prevHead) ||
                !String.Equals(prevHead, head ?? "", StringComparison.OrdinalIgnoreCase);
            dialogLastHead[hwnd] = head ?? "";

            if (!headChanged) return;
            if (String.IsNullOrEmpty(head) || !Directory.Exists(head)) return;

            SwitchTo(hwnd, head);
        }

        private void ShowSwitchMenu()
        {
            if (switchMenu.Visible) return;

            IntPtr dlg = Native.GetForegroundWindow();
            if (dlg == IntPtr.Zero) return;

            StringBuilder sb = new StringBuilder(64);
            Native.GetClassName(dlg, sb, 64);
            if (sb.ToString() != "#32770")
            {
                string title = GetWindowTitle(dlg);
                if (title.Length > 40) title = title.Substring(0, 40) + "…";
                string extra = String.IsNullOrEmpty(title) ? "" : "：" + title;
                PopupTip.ShowTip("前台窗口不是标准文件对话框" + extra + "。请先打开 打开/保存 文件对话框，再按 " + HotkeyText);
                return;
            }

            IntPtr edit = FindFileNameEdit(dlg);
            if (edit == IntPtr.Zero)
            {
                PopupTip.ShowTip("未找到\"文件名\"输入框：该程序可能使用自绘对话框（如 Chrome / VS Code / Firefox），暂不受支持。");
                return;
            }

            switchMenu.Items.Clear();

            ToolStripLabel header = new ToolStripLabel("切换到最近使用的文件夹");
            header.Font = new Font(switchMenu.Font, FontStyle.Bold);
            switchMenu.Items.Add(header);

            int shown = Math.Min(history.Count, MaxMenuItems);
            for (int i = 0; i < shown; i++)
            {
                string path = history[i];
                ToolStripItem item = switchMenu.Items.Add(DisplayNameOf(path), GetMenuIcon(path),
                    new EventHandler(delegate { SwitchTo(dlg, path); }));
                item.ToolTipText = path;
            }
            if (shown == 0)
            {
                ToolStripItem empty = switchMenu.Items.Add("（暂无记录，请先在资源管理器中打开文件夹）");
                empty.Enabled = false;
            }

            switchMenu.Items.Add(new ToolStripSeparator());
            switchMenu.Items.Add("浏览其他文件夹…", null,
                new EventHandler(delegate { BrowseAndSwitch(dlg); }));

            switchMenu.Show(Cursor.Position);
        }

        // 切换对话框所在文件夹：后台线程执行"等待对话框就绪 → 写入路径 → 回读校验 → 回车 → 确认消费"，
        // 未生效自动重试；只有确认输入已被对话框消费才提示"已切换"。
        // 写入采用 EM_SETSEL+EM_REPLACESEL（等价用户全选后粘贴，会触发对话框的编辑通知，兼容性最好）；
        // 且仅当回读确认输入框当前内容就是目标路径时才发回车，绝不会把默认文件名误当保存目标提交。
        private void SwitchTo(IntPtr dialog, string path)
        {
            if (String.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            int myGen;
            lock (switchGenLock)
            {
                int cur;
                switchGen.TryGetValue(dialog, out cur);
                myGen = cur + 1;
                switchGen[dialog] = myGen;
            }

            Native.SetForegroundWindow(dialog);
            ThreadPool.QueueUserWorkItem(delegate { RunSwitch(dialog, path, myGen); });
        }

        private bool IsCurrentSwitch(IntPtr dialog, int gen)
        {
            lock (switchGenLock)
            {
                int cur;
                return switchGen.TryGetValue(dialog, out cur) && cur == gen;
            }
        }

        private void RunSwitch(IntPtr dialog, string path, int gen)
        {
            bool ok = false;
            bool blockedByElevation = false;
            try
            {
                ok = TrySwitch(dialog, path, gen, out blockedByElevation);
            }
            catch { }

            if (ok && IsCurrentSwitch(dialog, gen))
            {
                lock (switchGenLock) switchGen.Remove(dialog);
            }
            if (!ok && !IsCurrentSwitch(dialog, gen)) return;   // 已有更新的切换请求接管
            if (!Native.IsWindow(dialog)) return;               // 对话框已被用户关闭，无需提示

            if (ok) PopupTip.ShowTip("已切换到：" + path);
            else if (blockedByElevation && !isElevated)
            {
                PopupTip.ShowTip("未能切换到：" + path +
                    "\n该程序以管理员身份运行，输入被系统安全机制拦截。\n请右键托盘图标 →\"以管理员身份重启\"后重试。");
            }
            else PopupTip.ShowTip("未能切换到：" + path);
        }

        private bool TrySwitch(IntPtr dialog, string path, int gen, out bool blockedByElevation)
        {
            blockedByElevation = false;
            uint dialogPid;
            Native.GetWindowThreadProcessId(dialog, out dialogPid);

            // 阶段1：等待对话框初始化完成——"文件名"框出现且内容连续两次读取一致，
            // 避免对话框初始化（填入默认文件名）覆盖我们刚写入的路径
            for (int i = 0; i < 10; i++)
            {
                if (!Native.IsWindow(dialog) || !IsCurrentSwitch(dialog, gen)) return false;
                IntPtr e0 = FindFileNameEdit(dialog);
                string a = (e0 != IntPtr.Zero) ? GetEditText(e0) : null;
                Thread.Sleep(150);
                IntPtr e1 = FindFileNameEdit(dialog);
                string b = (e1 != IntPtr.Zero) ? GetEditText(e1) : null;
                if (a != null && b != null && String.Equals(a, b, StringComparison.Ordinal)) break;
            }

            bool everReadText = false;   // 读到过任何输入框内容（管理员窗口被拦截时永远读不到）
            bool everWroteOk = false;    // 有过一次写入成功
            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (!Native.IsWindow(dialog) || !IsCurrentSwitch(dialog, gen)) return false;
                uint pid;
                Native.GetWindowThreadProcessId(dialog, out pid);
                if (pid != dialogPid) return false;   // 窗口句柄被系统复用成了别的窗口

                IntPtr edit = FindFileNameEdit(dialog);
                if (edit == IntPtr.Zero) { Thread.Sleep(200); continue; }

                string before = GetEditText(edit);
                if (before.Length > 0) everReadText = true;

                if (!WritePathToEdit(edit, path))
                {
                    // 连续多次既写不进也读不到内容：典型的 UIPI 拦截（目标以管理员运行），提前结束
                    if (attempt >= 2 && !everReadText && !everWroteOk)
                    {
                        blockedByElevation = true;
                        return false;
                    }
                    Thread.Sleep(250);
                    continue;
                }
                everWroteOk = true;

                Thread.Sleep(80);
                if (!String.Equals(GetEditText(edit), path, StringComparison.OrdinalIgnoreCase))
                {
                    Thread.Sleep(150);
                    continue;
                }

                // 回车：对话框把输入框中的"文件夹路径"当作导航目标，不会当作保存文件名
                Native.PostMessage(edit, Native.WM_KEYDOWN, (IntPtr)Native.VK_RETURN, IntPtr.Zero);
                Native.PostMessage(edit, Native.WM_KEYUP, (IntPtr)Native.VK_RETURN, IntPtr.Zero);
                Thread.Sleep(420);
                if (!Native.IsWindow(dialog)) return false;
                if (!IsCurrentSwitch(dialog, gen)) return false;
                if (!String.Equals(GetEditText(edit), path, StringComparison.OrdinalIgnoreCase))
                    return true;    // 输入被对话框消费 → 已跳转
                Thread.Sleep(200);   // 回车未生效（对话框仍在初始化等），继续重试
            }
            blockedByElevation = !everReadText && !everWroteOk;
            return false;
        }

        private static bool WritePathToEdit(IntPtr edit, string path)
        {
            try
            {
                Native.SendMessage(edit, Native.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
                Native.SendMessage(edit, Native.EM_REPLACESEL, IntPtr.Zero, path);
                if (String.Equals(GetEditText(edit), path, StringComparison.OrdinalIgnoreCase)) return true;
                Native.SendMessage(edit, Native.WM_SETTEXT, IntPtr.Zero, path);
                return String.Equals(GetEditText(edit), path, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // 跨进程读取 Edit 控件的实际内容：GetWindowText 只返回窗口管理器缓存的标题文本，
        // 对其他进程的 Edit 控件恒为空（曾导致校验永远失败、普通对话框被误判为提权拦截），
        // 必须用 WM_GETTEXT 消息让系统跨进程封送控件真实文本。
        // 目标以管理员运行时 WM_GETTEXT 会被 UIPI 静默拦截，返回空串——与写入失败一起构成提权判定依据。
        private static string GetEditText(IntPtr edit)
        {
            StringBuilder sb = new StringBuilder(1024);
            try
            {
                Native.SendMessage(edit, Native.WM_GETTEXT, (IntPtr)1024, sb);
            }
            catch { }
            return sb.ToString();
        }

        private void BrowseAndSwitch(IntPtr dialog)
        {
            using (FolderBrowserDialog folderDialog = new FolderBrowserDialog())
            {
                folderDialog.Description = "选择要切换到的文件夹";
                folderDialog.ShowNewFolderButton = false;
                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    TouchHistory(folderDialog.SelectedPath);
                    SwitchTo(dialog, folderDialog.SelectedPath);
                }
            }
        }

        // 轮询：记录资源管理器导航 + 兜底检查前台文件对话框（补偿可能错过的前台事件）
        private void PollTick()
        {
            PollExplorerWindows();

            if (dialogLastHead.Count > 0)
            {
                List<IntPtr> dead = new List<IntPtr>();
                foreach (KeyValuePair<IntPtr, string> kv in dialogLastHead)
                    if (!Native.IsWindow(kv.Key)) dead.Add(kv.Key);
                foreach (IntPtr k in dead) dialogLastHead.Remove(k);
            }

            IntPtr fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                // 兜底：前台资源管理器一直在前台（如切换标签页）时不产生前台事件，
                // 轮询中保持把它显示的目录记为"最新"
                if (IsWindowClass(fg, "CabinetWClass"))
                    TouchForegroundExplorer(fg);
                OnForegroundChanged(fg);
            }
        }

        // 轮询资源管理器窗口，记录每个窗口当前所在文件夹（检测到路径变化即写入 MRU）
        private void PollExplorerWindows()
        {
            if (shell == null) return;
            try
            {
                dynamic wins = shell.Windows();
                int count = (int)wins.Count;
                List<long> seen = new List<long>();
                for (int i = 0; i < count; i++)
                {
                    object raw = null;
                    try { raw = wins.Item(i); } catch { }
                    if (raw == null) continue;
                    try
                    {
                        dynamic w = raw;
                        string exe = Convert.ToString(w.FullName);
                        if (String.IsNullOrEmpty(exe) ||
                            exe.IndexOf("explorer.exe", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        long hwnd = Convert.ToInt64(w.HWND);
                        string path = Convert.ToString(w.Document.Folder.Self.Path);
                        if (String.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;

                        seen.Add(hwnd);
                        string prev;
                        if (!explorerPaths.TryGetValue(hwnd, out prev) ||
                            !String.Equals(prev, path, StringComparison.OrdinalIgnoreCase))
                        {
                            explorerPaths[hwnd] = path;
                            TouchHistory(path);
                        }
                    }
                    catch { }
                }

                List<long> gone = new List<long>();
                foreach (KeyValuePair<long, string> kv in explorerPaths)
                    if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
                foreach (long key in gone) explorerPaths.Remove(key);
            }
            catch { }
        }

        private void TouchHistory(string path)
        {
            for (int i = 0; i < history.Count; i++)
            {
                if (String.Equals(history[i], path, StringComparison.OrdinalIgnoreCase))
                {
                    history.RemoveAt(i);
                    break;
                }
            }
            history.Insert(0, path);
            if (history.Count > MaxHistory) history.RemoveAt(history.Count - 1);
        }

        // 定位文件对话框中的"文件名"编辑框，同时校验窗口确为文件对话框：
        // 新式 (Vista+) 对话框包含 DirectUIHWND，其中文件名编辑框位于 ComboBox 内；
        // 旧式对话框包含 SysListView32，文件名编辑框控件 ID 为 edt1 (0x480)。
        // 其他 #32770 窗口（如"运行"对话框、打印设置）不满足特征，返回 IntPtr.Zero，避免误操作。
        private static IntPtr FindFileNameEdit(IntPtr dialog)
        {
            List<IntPtr> edits = new List<IntPtr>();
            bool hasDirectUI = false;
            bool hasListView = false;
            Native.EnumChildWindows(dialog, delegate(IntPtr h, IntPtr lp)
            {
                string cls = GetClassNameOf(h);
                if (cls == "Edit")
                {
                    if (Native.IsWindowVisible(h)) edits.Add(h);
                }
                else if (cls == "DirectUIHWND")
                {
                    hasDirectUI = true;
                }
                else if (cls == "SysListView32" || cls == "SHELLDLL_DefView")
                {
                    hasListView = true;
                }
                return true;
            }, IntPtr.Zero);

            if (hasDirectUI)
            {
                foreach (IntPtr e in edits)
                    if (IsWindowClass(Native.GetParent(e), "ComboBox"))
                        return e;
                foreach (IntPtr e in edits)
                    if (Native.GetDlgCtrlID(e) == 0x0480)
                        return e;
                return IntPtr.Zero;
            }
            if (hasListView)
            {
                foreach (IntPtr e in edits)
                    if (Native.GetDlgCtrlID(e) == 0x0480)
                        return e;
                if (edits.Count == 1)
                    return edits[0];
            }
            return IntPtr.Zero;
        }

        private static bool IsWindowClass(IntPtr hWnd, string className)
        {
            if (hWnd == IntPtr.Zero) return false;
            return String.Equals(GetClassNameOf(hWnd), className, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetClassNameOf(IntPtr hWnd)
        {
            StringBuilder sb = new StringBuilder(64);
            Native.GetClassName(hWnd, sb, 64);
            return sb.ToString();
        }

        private static string GetWindowTitle(IntPtr hWnd)
        {
            StringBuilder sb = new StringBuilder(128);
            Native.GetWindowText(hWnd, sb, 128);
            return sb.ToString();
        }

        private static string DisplayNameOf(string path)
        {
            try
            {
                string name = Path.GetFileName(path.TrimEnd('\\'));
                if (!String.IsNullOrEmpty(name)) return name;
            }
            catch { }
            return path;
        }

        private Image GetMenuIcon(string path)
        {
            Image img;
            if (iconCache.TryGetValue(path, out img)) return img;

            img = GetFolderImage(path);
            if (img == null) img = GetFolderImage(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (img == null) img = SystemIcons.Application.ToBitmap();
            iconCache[path] = img;
            return img;
        }

        private static Image GetFolderImage(string path)
        {
            try
            {
                Native.SHFILEINFO info = new Native.SHFILEINFO();
                uint size = (uint)Marshal.SizeOf(typeof(Native.SHFILEINFO));
                IntPtr result = Native.SHGetFileInfo(path, 0, ref info, size,
                    Native.SHGFI_ICON | Native.SHGFI_SMALLICON);
                if (result != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                {
                    Image img = Icon.FromHandle(info.hIcon).ToBitmap();
                    Native.DestroyIcon(info.hIcon);
                    return img;
                }
            }
            catch { }
            return null;
        }

        private static Icon CreateTrayIcon()
        {
            try
            {
                Native.SHFILEINFO info = new Native.SHFILEINFO();
                uint size = (uint)Marshal.SizeOf(typeof(Native.SHFILEINFO));
                string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                IntPtr result = Native.SHGetFileInfo(windowsDir, 0, ref info, size,
                    Native.SHGFI_ICON | Native.SHGFI_LARGEICON);
                if (result != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                    return Icon.FromHandle(info.hIcon);
            }
            catch { }
            return null;
        }

        private void ExitApp()
        {
            pollTimer.Stop();
            hotkeyWindow.Unregister();
            if (foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(foregroundHook);
            if (destroyHook != IntPtr.Zero) Native.UnhookWinEvent(destroyHook);
            tray.Visible = false;
            tray.Dispose();
            Application.Exit();
        }
    }

    // 快捷键设置对话框：直接按下新组合键进行捕获
    internal sealed class HotkeyForm : Form
    {
        private readonly Label currentLabel;
        private readonly Label captureLabel;
        private readonly Label hintLabel;
        private readonly Button okButton;
        private readonly Button cancelButton;
        private readonly Button resetButton;

        private uint pickedMods;
        private uint pickedVk;

        public HotkeyForm(uint mods, uint vk)
        {
            Text = "设置快捷键 - QuickSwitch";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(430, 216);
            Font = new Font("Segoe UI", 9F);
            KeyPreview = true;
            KeyDown += new KeyEventHandler(OnHotkeyKey);

            currentLabel = new Label();
            currentLabel.AutoSize = true;
            currentLabel.Location = new Point(14, 14);
            currentLabel.Text = "当前快捷键：" + HotkeyUtil.FormatHotkey(mods, vk);
            Controls.Add(currentLabel);

            captureLabel = new Label();
            captureLabel.Location = new Point(14, 44);
            captureLabel.Size = new Size(402, 48);
            captureLabel.BorderStyle = BorderStyle.FixedSingle;
            captureLabel.TextAlign = ContentAlignment.MiddleCenter;
            captureLabel.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            captureLabel.Text = "请按下新的快捷键组合…";
            Controls.Add(captureLabel);

            hintLabel = new Label();
            hintLabel.AutoSize = false;
            hintLabel.Location = new Point(14, 100);
            hintLabel.Size = new Size(402, 40);
            hintLabel.Text = "组合需包含 Ctrl 或 Alt（Shift 可叠加，不支持 Win 键）。确定后立即生效，并保存到 QuickSwitch.ini。";
            Controls.Add(hintLabel);

            cancelButton = new Button();
            cancelButton.Text = "取消";
            cancelButton.Size = new Size(80, 28);
            cancelButton.Location = new Point(14, 158);
            cancelButton.Click += new EventHandler(delegate { Close(); });
            Controls.Add(cancelButton);

            resetButton = new Button();
            resetButton.Text = "恢复默认";
            resetButton.Size = new Size(80, 28);
            resetButton.Location = new Point(128, 158);
            resetButton.Click += new EventHandler(delegate
            {
                pickedMods = HotkeyUtil.DefaultMods;
                pickedVk = HotkeyUtil.DefaultVk;
                Confirmed = true;
                Close();
            });
            Controls.Add(resetButton);

            okButton = new Button();
            okButton.Text = "确定";
            okButton.Size = new Size(80, 28);
            okButton.Location = new Point(336, 158);
            okButton.Enabled = false;
            okButton.Click += new EventHandler(delegate { Confirmed = true; Close(); });
            Controls.Add(okButton);
        }

        public bool Confirmed { get; private set; }

        public uint PickedMods { get { return pickedMods; } }

        public uint PickedVk { get { return pickedVk; } }

        private void OnHotkeyKey(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;

            Keys code = e.KeyCode;
            if (code == Keys.ControlKey || code == Keys.ShiftKey || code == Keys.Menu ||
                code == Keys.LControlKey || code == Keys.RControlKey ||
                code == Keys.LShiftKey || code == Keys.RShiftKey ||
                code == Keys.LMenu || code == Keys.RMenu)
                return; // 仍在按修饰键，等待完整组合

            uint mods = 0;
            if (e.Control) mods |= Native.MOD_CONTROL;
            if (e.Alt) mods |= Native.MOD_ALT;
            if (e.Shift) mods |= Native.MOD_SHIFT;

            if (mods == 0)
            {
                captureLabel.Text = "需包含 Ctrl / Alt / Shift 修饰键";
                return;
            }
            if (mods == Native.MOD_SHIFT)
            {
                captureLabel.Text = "仅 Shift 不可用，请加 Ctrl 或 Alt";
                return;
            }

            pickedMods = mods;
            pickedVk = (uint)code;
            captureLabel.Text = HotkeyUtil.FormatHotkey(mods, (uint)code);
            okButton.Enabled = true;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    // 屏幕可见的深色提示气泡（不依赖系统通知设置，不会被 Win11 静默）
    internal sealed class PopupTip : Form
    {
        private static PopupTip current;
        private static SynchronizationContext uiContext;
        private static int uiThreadId;
        private readonly Timer closeTimer;

        // 由 AppContext 在 UI 线程初始化时记录，供后台线程封送调用
        internal static SynchronizationContext UiContext
        {
            set { uiContext = value; }
        }

        internal static int UiThreadId
        {
            set { uiThreadId = value; }
        }

        private PopupTip(string text, Point location)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(43, 43, 48);

            Label label = new Label();
            label.AutoSize = true;
            label.MaximumSize = new Size(440, 0);
            label.Text = text;
            label.ForeColor = Color.White;
            label.BackColor = Color.FromArgb(43, 43, 48);
            label.Font = new Font("Segoe UI", 9.75F);
            label.Padding = new Padding(16, 11, 16, 11);
            label.Location = new Point(0, 0);
            Controls.Add(label);
            ClientSize = new Size(label.PreferredSize.Width, label.PreferredSize.Height);

            Screen screen = Screen.FromPoint(location);
            Rectangle work = screen.WorkingArea;
            int x = location.X + 14;
            int y = location.Y + 18;
            if (x + Width > work.Right - 8) x = work.Right - Width - 8;
            if (y + Height > work.Bottom - 8) y = work.Bottom - Height - 8;
            if (x < work.Left + 8) x = work.Left + 8;
            if (y < work.Top + 8) y = work.Top + 8;
            Location = new Point(x, y);

            closeTimer = new Timer();
            closeTimer.Interval = 2000;
            closeTimer.Tick += new EventHandler(delegate { closeTimer.Stop(); Close(); });
            closeTimer.Start();

            FormClosed += new FormClosedEventHandler(delegate(object s, FormClosedEventArgs e)
            {
                if (current == this) current = null;
            });
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return cp;
            }
        }

        public static void ShowTip(string text)
        {
            try
            {
                // 后台线程（切换校验流程）调用时封送到 UI 线程
                if (uiContext != null && Thread.CurrentThread.ManagedThreadId != uiThreadId)
                {
                    uiContext.Post(delegate(object state) { ShowTip((string)state); }, text);
                    return;
                }

                if (current != null)
                {
                    try { current.Close(); current.Dispose(); } catch { }
                    current = null;
                }
                PopupTip tip = new PopupTip(text, Cursor.Position);
                current = tip;
                tip.Show();
            }
            catch { }
        }
    }

    // 接收全局热键消息的隐藏窗口
    internal sealed class HotkeyWindow : NativeWindow
    {
        public event Action HotkeyPressed;
        private int hotkeyId;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        public bool Register(int id, uint modifiers, uint vk)
        {
            hotkeyId = id;
            return Native.RegisterHotKey(Handle, id, modifiers, vk);
        }

        public void Unregister()
        {
            try { Native.UnregisterHotKey(Handle, hotkeyId); } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && HotkeyPressed != null)
                HotkeyPressed();
            base.WndProc(ref m);
        }
    }

    internal static class Native
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const int VK_RETURN = 0x0D;
        public const int VK_OEM_3 = 0xC0; // `（反引号）
        public const int WM_HOTKEY = 0x0312;
        public const int WM_SETTEXT = 0x000C;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int EM_SETSEL = 0x00B1;     // 全选（start=0, end=-1）
        public const int EM_REPLACESEL = 0x00C2; // 替换选中内容，等价用户粘贴，会触发 EN_CHANGE
        public const int WM_GETTEXT = 0x000D;    // 跨进程读取控件文本（GetWindowText 读不到别的进程的控件）

        public const uint SHGFI_ICON = 0x0100;
        public const uint SHGFI_LARGEICON = 0x0000;
        public const uint SHGFI_SMALLICON = 0x0001;

        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint EVENT_OBJECT_DESTROY = 0x8001;
        public const int OBJID_WINDOW = 0;
        public const uint WINEVENT_OUTOFCONTEXT = 0;

        public delegate void WinEventProc(IntPtr hHook, uint eventType, IntPtr hwnd, int idObject,
            int idChild, uint dwEventThread, uint dwmsEventTime);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        public delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hHook);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetDlgCtrlID(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, StringBuilder lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
    }
}
