using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace NetworkMonitor
{
    public static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        public static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int HistoryLength = 60;

        // Theme colors (Windows 11 Dark)
        private static readonly Color BgColor = Color.FromArgb(24, 24, 27);
        private static readonly Color CardBgColor = Color.FromArgb(34, 34, 39);
        private static readonly Color BorderColor = Color.FromArgb(52, 52, 60);
        private static readonly Color TextPrimary = Color.FromArgb(244, 244, 245);
        private static readonly Color TextSecondary = Color.FromArgb(161, 161, 170);
        private static readonly Color DownColor = Color.FromArgb(56, 189, 248);   // Sky Blue
        private static readonly Color UpColor = Color.FromArgb(52, 211, 153);     // Emerald
        private static readonly Color AccentBg = Color.FromArgb(46, 46, 54);
        private static readonly Color WarnBg = Color.FromArgb(72, 48, 24);
        private static readonly Color WarnFg = Color.FromArgb(251, 191, 36);

        // UI Controls
        private ComboBox adapterCombo;
        private ComboBox pidCombo;
        private Button elevateButton;
        private Button unitButton;
        private Button topMostButton;
        private Button compactButton;
        private Button expandButton;
        private Button pauseButton;
        private Button resetButton;
        private Button flowsButton;
        private Panel topBar;
        private Panel statsPanel;
        private Panel flowPanel;
        private Splitter flowSplitter;
        private FlowListView flowListView;
        private GraphPanel graphPanel;
        private Label statusLabel;

        // Stat labels
        private Label downSpeedLabel;
        private Label downPeakLabel;
        private Label downTotalLabel;
        private Label upSpeedLabel;
        private Label upPeakLabel;
        private Label upTotalLabel;

        // System tray
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;

        // Monitoring state
        private System.Windows.Forms.Timer pollTimer;
        private Stopwatch stopwatch;
        private long lastBytesRecv = -1;
        private long lastBytesSent = -1;
        private string lastAdapterId = "__ALL__";
        private int selectedPid = -1; // -1 = All processes (adapter mode)

        private double currentDownBps = 0;
        private double currentUpBps = 0;
        private double peakDownBps = 0;
        private double peakUpBps = 0;
        private long sessionRecvBytes = 0;
        private long sessionSentBytes = 0;

        private readonly double[] downHistory = new double[HistoryLength];
        private readonly double[] upHistory = new double[HistoryLength];

        private bool useBits = false;
        private bool isPaused = false;
        private bool isCompact = false;
        private bool showFlows = true;
        private Size normalSize = new Size(840, 620);

        private readonly Dictionary<string, long[]> prevFlowBytes = new Dictionary<string, long[]>();
        private readonly Dictionary<int, string> pidNameCache = new Dictionary<int, string>();

        private readonly bool isAdmin;
        private EtwNetworkMonitor etwMonitor;

        public MainForm()
        {
            isAdmin = CheckIsAdmin();
            stopwatch = Stopwatch.StartNew();

            InitializeWindow();
            InitializeControls();
            InitializeTray();
            PopulateAdapters();
            PopulateProcesses();

            if (isAdmin)
            {
                etwMonitor = new EtwNetworkMonitor();
                try
                {
                    etwMonitor.Start();
                }
                catch { }
            }

            pollTimer = new System.Windows.Forms.Timer();
            pollTimer.Interval = 1000;
            pollTimer.Tick += PollTimer_Tick;
            pollTimer.Start();

            SampleNetwork(true);
            UpdateStatusText();
        }

        private static bool CheckIsAdmin()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(id);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private void InitializeWindow()
        {
            Text = isAdmin ? "Network Bandwidth Monitor (Admin - Per-PID Ready)" : "Network Bandwidth Monitor";
            Size = normalSize;
            MinimumSize = new Size(380, 120);
            BackColor = BgColor;
            ForeColor = TextPrimary;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;

            Load += delegate
            {
                try
                {
                    int dark = 1;
                    DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                }
                catch { }
            };

            FormClosing += delegate
            {
                if (etwMonitor != null)
                {
                    etwMonitor.Dispose();
                    etwMonitor = null;
                }
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                }
            };
        }

        private void InitializeControls()
        {
            // Top Toolbar (2 rows for clean layout: Row 1 = Adapter & buttons, Row 2 = Process / PID filter)
            topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 78,
                Padding = new Padding(12, 8, 12, 4),
                BackColor = BgColor
            };

            adapterCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = CardBgColor,
                ForeColor = TextPrimary,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(12, 10),
                Width = 250,
                Font = new Font("Segoe UI", 9f)
            };
            adapterCombo.SelectedIndexChanged += delegate
            {
                ResetBaseline();
                UpdateStatusText();
            };

            unitButton = CreateToolbarButton("MB/s", 270, 56, 9);
            unitButton.Click += delegate
            {
                useBits = !useBits;
                unitButton.Text = useBits ? "Mbps" : "MB/s";
                RefreshLabels();
                graphPanel.Invalidate();
            };

            topMostButton = CreateToolbarButton("Pin", 332, 48, 9);
            topMostButton.Click += delegate
            {
                TopMost = !TopMost;
                topMostButton.BackColor = TopMost ? Color.FromArgb(30, 64, 90) : CardBgColor;
                topMostButton.ForeColor = TopMost ? DownColor : TextPrimary;
            };

            compactButton = CreateToolbarButton("Mini", 386, 52, 9);
            compactButton.Click += delegate { ToggleCompactMode(); };

            pauseButton = CreateToolbarButton("Pause", 444, 58, 9);
            pauseButton.Click += delegate
            {
                isPaused = !isPaused;
                pauseButton.Text = isPaused ? "Resume" : "Pause";
                pauseButton.BackColor = isPaused ? Color.FromArgb(80, 45, 30) : CardBgColor;
                if (!isPaused)
                {
                    ResetBaseline();
                    stopwatch.Restart();
                }
                UpdateStatusText();
            };

            resetButton = CreateToolbarButton("Reset", 508, 56, 9);
            resetButton.Click += delegate { ResetStats(); };

            Button refreshBtn = CreateToolbarButton("↻", 570, 32, 9);
            refreshBtn.Click += delegate
            {
                PopulateAdapters();
                PopulateProcesses();
            };

            flowsButton = CreateToolbarButton("Flows", 608, 56, 9);
            flowsButton.BackColor = Color.FromArgb(30, 64, 90);
            flowsButton.ForeColor = DownColor;
            flowsButton.Click += delegate
            {
                showFlows = !showFlows;
                flowPanel.Visible = showFlows && !isCompact;
                flowSplitter.Visible = flowPanel.Visible;
                flowsButton.BackColor = showFlows ? Color.FromArgb(30, 64, 90) : CardBgColor;
                flowsButton.ForeColor = showFlows ? DownColor : TextPrimary;
                if (showFlows) RefreshFlows(1.0);
            };

            // Row 2: Process / PID Filter
            Label pidLabel = new Label
            {
                Text = "Process / PID:",
                ForeColor = TextSecondary,
                Font = new Font("Segoe UI", 8.8f),
                Location = new Point(12, 46),
                AutoSize = true
            };

            pidCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,
                BackColor = CardBgColor,
                ForeColor = TextPrimary,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(102, 43),
                Width = 310,
                Font = new Font("Segoe UI", 9f)
            };
            pidCombo.SelectedIndexChanged += delegate { ApplyPidSelection(); };
            pidCombo.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    ApplyPidSelection();
                }
            };

            Button clearPidBtn = CreateToolbarButton("All PIDs", 420, 66, 42);
            clearPidBtn.Click += delegate
            {
                if (pidCombo.Items.Count > 0) pidCombo.SelectedIndex = 0;
                selectedPid = -1;
                ResetStats();
                UpdateStatusText();
            };

            elevateButton = CreateToolbarButton("Admin (PID Speed)", 494, 128, 42);
            elevateButton.BackColor = WarnBg;
            elevateButton.ForeColor = WarnFg;
            elevateButton.Visible = !isAdmin;
            elevateButton.Click += delegate { RelaunchAsAdmin(); };

            topBar.Controls.Add(adapterCombo);
            topBar.Controls.Add(unitButton);
            topBar.Controls.Add(topMostButton);
            topBar.Controls.Add(compactButton);
            topBar.Controls.Add(pauseButton);
            topBar.Controls.Add(resetButton);
            topBar.Controls.Add(refreshBtn);
            topBar.Controls.Add(flowsButton);
            topBar.Controls.Add(pidLabel);
            topBar.Controls.Add(pidCombo);
            topBar.Controls.Add(clearPidBtn);
            topBar.Controls.Add(elevateButton);

            // Stats Cards Panel
            statsPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 112,
                Padding = new Padding(12, 4, 12, 8),
                BackColor = BgColor
            };

            TableLayoutPanel statsGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = BgColor
            };
            statsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            statsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

            Panel downCard = CreateStatCard("DOWNLOAD", DownColor, out downSpeedLabel, out downPeakLabel, out downTotalLabel);
            downCard.Margin = new Padding(0, 0, 6, 0);

            Panel upCard = CreateStatCard("UPLOAD", UpColor, out upSpeedLabel, out upPeakLabel, out upTotalLabel);
            upCard.Margin = new Padding(6, 0, 0, 0);

            expandButton = CreateToolbarButton("Expand", Math.Max(10, upCard.Width - 68), 58, 6);
            expandButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            expandButton.Visible = false;
            expandButton.Click += delegate { if (isCompact) ToggleCompactMode(); };
            upCard.Controls.Add(expandButton);

            statsGrid.Controls.Add(downCard, 0, 0);
            statsGrid.Controls.Add(upCard, 1, 0);
            statsPanel.Controls.Add(statsGrid);

            // Graph Panel
            graphPanel = new GraphPanel(downHistory, upHistory, DownColor, UpColor, CardBgColor, BorderColor, TextSecondary,
                delegate(double bps) { return FormatRate(bps); });
            graphPanel.Dock = DockStyle.Fill;
            graphPanel.Margin = new Padding(12);

            Panel graphWrapper = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12, 0, 12, 6),
                BackColor = BgColor
            };
            graphWrapper.Controls.Add(graphPanel);

            // NetFlow / Active Sessions Panel
            flowPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 195,
                Padding = new Padding(12, 2, 12, 6),
                BackColor = BgColor
            };

            Label flowHeader = new Label
            {
                Dock = DockStyle.Top,
                Height = 20,
                ForeColor = TextSecondary,
                Font = new Font("Segoe UI", 8.2f, FontStyle.Bold),
                Text = "NETFLOW SESSIONS (PROTOCOL / LOCAL / REMOTE PEER)"
            };

            flowListView = new FlowListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BackColor = CardBgColor,
                ForeColor = TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 8.5f)
            };
            flowListView.Columns.Add("Proto", 56);
            flowListView.Columns.Add("Process (PID)", 145);
            flowListView.Columns.Add("Local Endpoint", 155);
            flowListView.Columns.Add("Remote Peer", 165);
            flowListView.Columns.Add("State", 90);
            flowListView.Columns.Add("↓ Recv", 75);
            flowListView.Columns.Add("↑ Sent", 75);
            flowListView.Columns.Add("↓/↑ Rate", 110);

            flowPanel.Controls.Add(flowListView);
            flowPanel.Controls.Add(flowHeader);

            // Status Bar
            statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                Padding = new Padding(12, 2, 12, 4),
                ForeColor = TextSecondary,
                Font = new Font("Segoe UI", 8.5f),
                Text = "Initializing..."
            };

            flowSplitter = new Splitter
            {
                Dock = DockStyle.Bottom,
                Height = 5,
                BackColor = BorderColor,
                MinExtra = 80,
                MinSize = 70,
                Cursor = Cursors.HSplit
            };

            Controls.Add(graphWrapper);
            Controls.Add(statsPanel);
            Controls.Add(topBar);
            Controls.Add(flowSplitter);
            Controls.Add(flowPanel);
            Controls.Add(statusLabel);
        }

        private Button CreateToolbarButton(string text, int x, int width, int y)
        {
            Button btn = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, 26),
                FlatStyle = FlatStyle.Flat,
                BackColor = CardBgColor,
                ForeColor = TextPrimary,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = BorderColor;
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.MouseOverBackColor = AccentBg;
            return btn;
        }

        private Panel CreateStatCard(string title, Color accent, out Label speedLbl, out Label peakLbl, out Label totalLbl)
        {
            Panel card = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = CardBgColor,
                Padding = new Padding(14, 10, 14, 10)
            };
            card.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(BorderColor, 1))
                {
                    e.Graphics.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1);
                }
                using (SolidBrush b = new SolidBrush(accent))
                {
                    e.Graphics.FillRectangle(b, 0, 0, 4, card.Height);
                }
            };

            Label titleLbl = new Label
            {
                Text = title,
                ForeColor = accent,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Location = new Point(14, 8),
                AutoSize = true
            };

            speedLbl = new Label
            {
                Text = "0.00 KB/s",
                ForeColor = TextPrimary,
                Font = new Font("Segoe UI", 18f, FontStyle.Bold),
                Location = new Point(12, 26),
                AutoSize = true
            };

            peakLbl = new Label
            {
                Text = "Peak: 0.00 KB/s",
                ForeColor = TextSecondary,
                Font = new Font("Segoe UI", 8.5f),
                Location = new Point(14, 68),
                AutoSize = true
            };

            totalLbl = new Label
            {
                Text = "Total: 0.00 MB",
                ForeColor = TextSecondary,
                Font = new Font("Segoe UI", 8.5f),
                Location = new Point(165, 68),
                AutoSize = true
            };

            card.Controls.Add(titleLbl);
            card.Controls.Add(speedLbl);
            card.Controls.Add(peakLbl);
            card.Controls.Add(totalLbl);

            EventHandler expandHandler = delegate { if (isCompact) ToggleCompactMode(); };
            card.DoubleClick += expandHandler;
            titleLbl.DoubleClick += expandHandler;
            speedLbl.DoubleClick += expandHandler;
            peakLbl.DoubleClick += expandHandler;
            totalLbl.DoubleClick += expandHandler;
            return card;
        }

        private void InitializeTray()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Show / Restore", null, delegate { RestoreFromTray(); });
            trayMenu.Items.Add("Toggle Mini / Full Mode", null, delegate { ToggleCompactMode(); });
            trayMenu.Items.Add("Always on Top", null, delegate { topMostButton.PerformClick(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit", null, delegate { Application.Exit(); });

            trayIcon = new NotifyIcon
            {
                Text = "Network Bandwidth Monitor",
                Icon = SystemIcons.Information,
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            trayIcon.DoubleClick += delegate { RestoreFromTray(); };

            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    Hide();
                }
            };
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ToggleCompactMode()
        {
            isCompact = !isCompact;
            if (isCompact)
            {
                normalSize = Size;
                topBar.Visible = false;
                flowPanel.Visible = false;
                flowSplitter.Visible = false;
                statusLabel.Visible = false;
                statsPanel.Height = 96;
                Size = new Size(520, 136);
                TopMost = true;
                topMostButton.BackColor = Color.FromArgb(30, 64, 90);
                topMostButton.ForeColor = DownColor;
                expandButton.Visible = true;
            }
            else
            {
                expandButton.Visible = false;
                topBar.Visible = true;
                flowPanel.Visible = showFlows;
                flowSplitter.Visible = showFlows;
                statusLabel.Visible = true;
                statsPanel.Height = 112;
                Size = normalSize;
            }
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            if (isCompact) ToggleCompactMode();
        }

        private void RelaunchAsAdmin()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NetworkMonitor.ps1");
                if (!File.Exists(scriptPath))
                {
                    foreach (string arg in Environment.GetCommandLineArgs())
                    {
                        if (arg != null && arg.EndsWith("NetworkMonitor.ps1", StringComparison.OrdinalIgnoreCase) && File.Exists(arg))
                        {
                            scriptPath = arg;
                            break;
                        }
                    }
                }
                if (!File.Exists(scriptPath))
                {
                    scriptPath = @"C:\Users\melof\NetworkMonitor\NetworkMonitor.ps1";
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.Verb = "runas";
                psi.UseShellExecute = true;

                if (exePath.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(scriptPath))
                {
                    psi.FileName = exePath;
                    psi.WorkingDirectory = Path.GetDirectoryName(scriptPath);
                    psi.Arguments = "-NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File \"" + scriptPath + "\"";
                }
                else
                {
                    psi.FileName = exePath;
                    psi.WorkingDirectory = Path.GetDirectoryName(exePath);
                }

                Process.Start(psi);
                Application.Exit();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not elevate: " + ex.Message, "Elevation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void PopulateAdapters()
        {
            string previousId = "__ALL__";
            AdapterItem selected = adapterCombo.SelectedItem as AdapterItem;
            if (selected != null) previousId = selected.Id;

            adapterCombo.Items.Clear();
            adapterCombo.Items.Add(new AdapterItem("__ALL__", "All Active Adapters"));

            try
            {
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                foreach (NetworkInterface nic in nics)
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    string statusTag = nic.OperationalStatus == OperationalStatus.Up ? "Up" : "Down";
                    adapterCombo.Items.Add(new AdapterItem(nic.Id, nic.Name + " (" + statusTag + ")"));
                }
            }
            catch { }

            int restoreIndex = 0;
            for (int i = 0; i < adapterCombo.Items.Count; i++)
            {
                AdapterItem item = (AdapterItem)adapterCombo.Items[i];
                if (item.Id == previousId)
                {
                    restoreIndex = i;
                    break;
                }
            }
            adapterCombo.SelectedIndex = restoreIndex;
            UpdateStatusText();
        }

        private void PopulateProcesses()
        {
            int prevPid = selectedPid;
            pidCombo.Items.Clear();
            pidCombo.Items.Add(new ProcessItem(-1, "All Processes (Adapter Bandwidth)"));

            HashSet<int> activeNetPids = IpHelperConnections.GetActiveNetworkPids();
            List<ProcessItem> netItems = new List<ProcessItem>();
            List<ProcessItem> otherItems = new List<ProcessItem>();

            try
            {
                Process[] procs = Process.GetProcesses();
                foreach (Process p in procs)
                {
                    try
                    {
                        if (p.Id == 0) continue;
                        bool hasNet = activeNetPids.Contains(p.Id);
                        string label = p.ProcessName + " (PID " + p.Id + ")" + (hasNet ? " [Active]" : "");
                        ProcessItem item = new ProcessItem(p.Id, label);
                        if (hasNet) netItems.Add(item);
                        else otherItems.Add(item);
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }

            netItems.Sort(delegate(ProcessItem a, ProcessItem b) { return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase); });
            otherItems.Sort(delegate(ProcessItem a, ProcessItem b) { return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase); });

            foreach (ProcessItem item in netItems) pidCombo.Items.Add(item);
            foreach (ProcessItem item in otherItems) pidCombo.Items.Add(item);

            int restoreIdx = 0;
            for (int i = 0; i < pidCombo.Items.Count; i++)
            {
                ProcessItem item = (ProcessItem)pidCombo.Items[i];
                if (item.Pid == prevPid)
                {
                    restoreIdx = i;
                    break;
                }
            }
            pidCombo.SelectedIndex = restoreIdx;
        }

        private void ApplyPidSelection()
        {
            int newPid = -1;
            ProcessItem selected = pidCombo.SelectedItem as ProcessItem;
            if (selected != null)
            {
                newPid = selected.Pid;
            }
            else
            {
                string raw = (pidCombo.Text ?? "").Trim();
                int parsed;
                if (int.TryParse(raw, out parsed) && parsed > 0)
                {
                    newPid = parsed;
                }
            }

            if (newPid != selectedPid)
            {
                selectedPid = newPid;
                ResetStats();
                UpdateStatusText();
            }
        }

        private void ResetStats()
        {
            peakDownBps = 0;
            peakUpBps = 0;
            sessionRecvBytes = 0;
            sessionSentBytes = 0;
            currentDownBps = 0;
            currentUpBps = 0;
            Array.Clear(downHistory, 0, downHistory.Length);
            Array.Clear(upHistory, 0, upHistory.Length);
            prevFlowBytes.Clear();
            if (etwMonitor != null) etwMonitor.ClearFlows();
            ResetBaseline();
            RefreshLabels();
            graphPanel.Invalidate();
            if (showFlows && !isCompact) RefreshFlows(1.0);
        }

        private void ResetBaseline()
        {
            lastBytesRecv = -1;
            lastBytesSent = -1;
            stopwatch.Restart();
            SampleNetwork(true);
        }

        private void PollTimer_Tick(object sender, EventArgs e)
        {
            if (isPaused) return;
            SampleNetwork(false);
            if (selectedPid > 0) UpdateStatusText();
            if (showFlows && !isCompact) RefreshFlows(1.0);
        }

        private string GetProcessNameCached(int pid)
        {
            if (pid <= 0) return "System";
            string name;
            if (pidNameCache.TryGetValue(pid, out name)) return name;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    name = p.ProcessName;
                }
            }
            catch
            {
                name = "PID " + pid;
            }
            if (pidNameCache.Count > 512) pidNameCache.Clear();
            pidNameCache[pid] = name;
            return name;
        }

        private void RefreshFlows(double elapsedSec)
        {
            if (flowListView == null) return;

            Dictionary<string, FlowRecord> merged = new Dictionary<string, FlowRecord>();

            // 1. Active sockets from IP Helper (works without Admin)
            List<FlowRecord> socketFlows = IpHelperConnections.GetFlows(selectedPid);
            foreach (FlowRecord sf in socketFlows)
            {
                merged[sf.Key] = sf;
            }

            // 2. Real-time byte counters from ETW (when Admin)
            if (isAdmin && etwMonitor != null)
            {
                List<FlowRecord> etwFlows = etwMonitor.GetFlowSnapshot(selectedPid);
                foreach (FlowRecord ef in etwFlows)
                {
                    FlowRecord existing;
                    if (merged.TryGetValue(ef.Key, out existing))
                    {
                        existing.RecvBytes = ef.RecvBytes;
                        existing.SentBytes = ef.SentBytes;
                    }
                    else
                    {
                        merged[ef.Key] = ef;
                    }
                }
            }

            List<FlowRecord> sorted = new List<FlowRecord>(merged.Values);
            foreach (FlowRecord f in sorted)
            {
                long[] prev;
                if (prevFlowBytes.TryGetValue(f.Key, out prev))
                {
                    long dRecv = Math.Max(0, f.RecvBytes - prev[0]);
                    long dSent = Math.Max(0, f.SentBytes - prev[1]);
                    f.RecvRate = dRecv / elapsedSec;
                    f.SentRate = dSent / elapsedSec;
                }
                prevFlowBytes[f.Key] = new long[] { f.RecvBytes, f.SentBytes };
            }

            sorted.Sort(delegate(FlowRecord a, FlowRecord b)
            {
                double rateA = a.RecvRate + a.SentRate;
                double rateB = b.RecvRate + b.SentRate;
                int cmp = rateB.CompareTo(rateA);
                if (cmp != 0) return cmp;
                long totA = a.RecvBytes + a.SentBytes;
                long totB = b.RecvBytes + b.SentBytes;
                cmp = totB.CompareTo(totA);
                if (cmp != 0) return cmp;
                return string.Compare(a.State, b.State, StringComparison.Ordinal);
            });

            int maxRows = Math.Min(120, sorted.Count);
            flowListView.BeginUpdate();
            try
            {
                flowListView.Items.Clear();
                for (int i = 0; i < maxRows; i++)
                {
                    FlowRecord f = sorted[i];
                    string procLabel = GetProcessNameCached(f.Pid) + " (" + f.Pid + ")";
                    string recvStr = (isAdmin && etwMonitor != null) ? FormatBytes(f.RecvBytes) : "-";
                    string sentStr = (isAdmin && etwMonitor != null) ? FormatBytes(f.SentBytes) : "-";
                    string rateStr = (isAdmin && etwMonitor != null)
                        ? ("↓" + FormatRate(f.RecvRate) + " ↑" + FormatRate(f.SentRate))
                        : "Admin req";

                    ListViewItem item = new ListViewItem(f.Protocol);
                    item.SubItems.Add(procLabel);
                    item.SubItems.Add(f.LocalEndpoint);
                    item.SubItems.Add(f.RemoteEndpoint);
                    item.SubItems.Add(f.State);
                    item.SubItems.Add(recvStr);
                    item.SubItems.Add(sentStr);
                    item.SubItems.Add(rateStr);
                    flowListView.Items.Add(item);
                }
            }
            finally
            {
                flowListView.EndUpdate();
            }
        }

        private void SampleNetwork(bool baselineOnly)
        {
            if (selectedPid > 0)
            {
                SamplePidNetwork(baselineOnly);
                return;
            }

            AdapterItem selected = adapterCombo.SelectedItem as AdapterItem;
            string targetId = selected != null ? selected.Id : "__ALL__";

            long totalRecv = 0;
            long totalSent = 0;

            try
            {
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                foreach (NetworkInterface nic in nics)
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    if (targetId == "__ALL__")
                    {
                        if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    }
                    else if (nic.Id != targetId)
                    {
                        continue;
                    }

                    IPInterfaceStatistics stats = nic.GetIPStatistics();
                    totalRecv += stats.BytesReceived;
                    totalSent += stats.BytesSent;
                }
            }
            catch
            {
                return;
            }

            double elapsedSec = stopwatch.Elapsed.TotalSeconds;
            stopwatch.Restart();

            if (baselineOnly || lastBytesRecv < 0 || lastAdapterId != targetId || elapsedSec <= 0.05)
            {
                lastBytesRecv = totalRecv;
                lastBytesSent = totalSent;
                lastAdapterId = targetId;
                return;
            }

            long deltaRecv = totalRecv - lastBytesRecv;
            long deltaSent = totalSent - lastBytesSent;
            if (deltaRecv < 0) deltaRecv = 0;
            if (deltaSent < 0) deltaSent = 0;

            lastBytesRecv = totalRecv;
            lastBytesSent = totalSent;

            RecordSample(deltaRecv, deltaSent, elapsedSec);
        }

        private void SamplePidNetwork(bool baselineOnly)
        {
            double elapsedSec = stopwatch.Elapsed.TotalSeconds;
            stopwatch.Restart();

            if (!isAdmin || etwMonitor == null || !etwMonitor.IsRunning)
            {
                if (!baselineOnly)
                {
                    RecordSample(0, 0, Math.Max(0.1, elapsedSec));
                }
                return;
            }

            long totalRecv;
            long totalSent;
            etwMonitor.GetPidTotals(selectedPid, out totalRecv, out totalSent);

            if (baselineOnly || lastBytesRecv < 0 || elapsedSec <= 0.05)
            {
                lastBytesRecv = totalRecv;
                lastBytesSent = totalSent;
                return;
            }

            long deltaRecv = totalRecv - lastBytesRecv;
            long deltaSent = totalSent - lastBytesSent;
            if (deltaRecv < 0) deltaRecv = 0;
            if (deltaSent < 0) deltaSent = 0;

            lastBytesRecv = totalRecv;
            lastBytesSent = totalSent;

            RecordSample(deltaRecv, deltaSent, elapsedSec);
        }

        private void RecordSample(long deltaRecv, long deltaSent, double elapsedSec)
        {
            currentDownBps = deltaRecv / elapsedSec;
            currentUpBps = deltaSent / elapsedSec;

            if (currentDownBps > peakDownBps) peakDownBps = currentDownBps;
            if (currentUpBps > peakUpBps) peakUpBps = currentUpBps;

            sessionRecvBytes += deltaRecv;
            sessionSentBytes += deltaSent;

            ShiftAndPush(downHistory, currentDownBps);
            ShiftAndPush(upHistory, currentUpBps);

            RefreshLabels();
            graphPanel.Invalidate();
        }

        private static void ShiftAndPush(double[] arr, double value)
        {
            for (int i = 0; i < arr.Length - 1; i++)
            {
                arr[i] = arr[i + 1];
            }
            arr[arr.Length - 1] = value;
        }

        private void RefreshLabels()
        {
            downSpeedLabel.Text = FormatRate(currentDownBps);
            downPeakLabel.Text = "Peak: " + FormatRate(peakDownBps);
            downTotalLabel.Text = "Total: " + FormatBytes(sessionRecvBytes);

            upSpeedLabel.Text = FormatRate(currentUpBps);
            upPeakLabel.Text = "Peak: " + FormatRate(peakUpBps);
            upTotalLabel.Text = "Total: " + FormatBytes(sessionSentBytes);

            string prefix = selectedPid > 0 ? ("PID " + selectedPid + " ") : "";
            string trayText = prefix + "↓ " + FormatRate(currentDownBps) + "  ↑ " + FormatRate(currentUpBps);
            if (trayText.Length > 63) trayText = trayText.Substring(0, 63);
            trayIcon.Text = trayText;
        }

        private void UpdateStatusText()
        {
            string state = isPaused ? "Paused" : "Monitoring (1s)";
            if (selectedPid > 0)
            {
                int tcpCount;
                int udpCount;
                IpHelperConnections.GetPidSocketCounts(selectedPid, out tcpCount, out udpCount);
                string etwState = (isAdmin && etwMonitor != null && etwMonitor.IsRunning)
                    ? "ETW Active"
                    : (isAdmin ? ("ETW Error: " + (etwMonitor != null ? etwMonitor.LastError : "")) : "Click 'Admin (PID Speed)' for byte-rate");
                statusLabel.Text = state + "  |  PID " + selectedPid + " (TCP: " + tcpCount + ", UDP: " + udpCount + ")  |  " + etwState;
            }
            else
            {
                AdapterItem selected = adapterCombo.SelectedItem as AdapterItem;
                string name = selected != null ? selected.Name : "All Active Adapters";
                statusLabel.Text = state + "  |  Interface: " + name + "  |  Select a Process/PID above to isolate an app";
            }
        }

        private string FormatRate(double bytesPerSec)
        {
            if (useBits)
            {
                double bits = bytesPerSec * 8.0;
                if (bits >= 1000000000.0) return (bits / 1000000000.0).ToString("0.00") + " Gbps";
                if (bits >= 1000000.0) return (bits / 1000000.0).ToString("0.00") + " Mbps";
                if (bits >= 1000.0) return (bits / 1000.0).ToString("0.0") + " Kbps";
                return bits.ToString("0") + " bps";
            }
            else
            {
                if (bytesPerSec >= 1073741824.0) return (bytesPerSec / 1073741824.0).ToString("0.00") + " GB/s";
                if (bytesPerSec >= 1048576.0) return (bytesPerSec / 1048576.0).ToString("0.00") + " MB/s";
                if (bytesPerSec >= 1024.0) return (bytesPerSec / 1024.0).ToString("0.0") + " KB/s";
                return bytesPerSec.ToString("0") + " B/s";
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1073741824L) return (bytes / 1073741824.0).ToString("0.00") + " GB";
            if (bytes >= 1048576L) return (bytes / 1048576.0).ToString("0.00") + " MB";
            if (bytes >= 1024L) return (bytes / 1024.0).ToString("0.0") + " KB";
            return bytes.ToString() + " B";
        }

        private class AdapterItem
        {
            public readonly string Id;
            public readonly string Name;

            public AdapterItem(string id, string name)
            {
                Id = id;
                Name = name;
            }

            public override string ToString()
            {
                return Name;
            }
        }

        private class ProcessItem
        {
            public readonly int Pid;
            public readonly string Label;

            public ProcessItem(int pid, string label)
            {
                Pid = pid;
                Label = label;
            }

            public override string ToString()
            {
                return Label;
            }
        }
    }

    public class FlowListView : ListView
    {
        public FlowListView()
        {
            DoubleBuffered = true;
        }
    }

    public class FlowRecord
    {
        public string Key;
        public string Protocol;
        public int Pid;
        public string LocalEndpoint;
        public string RemoteEndpoint;
        public string State;
        public long RecvBytes;
        public long SentBytes;
        public double RecvRate;
        public double SentRate;

        public static string MakeKey(string proto, int pid, string localEp, string remoteEp)
        {
            return proto + "|" + pid + "|" + localEp + "|" + remoteEp;
        }
    }

    public static class IpHelperConnections
    {
        private const int AF_INET = 2;
        private const int AF_INET6 = 23;
        private const int TCP_TABLE_OWNER_PID_ALL = 5;
        private const int UDP_TABLE_OWNER_PID = 1;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public uint localPort;
            public uint remoteAddr;
            public uint remotePort;
            public int owningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_UDPROW_OWNER_PID
        {
            public uint localAddr;
            public uint localPort;
            public int owningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCP6ROW_OWNER_PID
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] localAddr;
            public uint localScopeId;
            public uint localPort;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] remoteAddr;
            public uint remoteScopeId;
            public uint remotePort;
            public uint state;
            public int owningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_UDP6ROW_OWNER_PID
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] localAddr;
            public uint localScopeId;
            public uint localPort;
            public int owningPid;
        }

        private static ushort NetToHostPort(uint rawPort)
        {
            return (ushort)(((rawPort & 0xFF) << 8) | ((rawPort & 0xFF00) >> 8));
        }

        private static string TcpStateName(uint state)
        {
            switch (state)
            {
                case 1: return "CLOSED";
                case 2: return "LISTEN";
                case 3: return "SYN_SENT";
                case 4: return "SYN_RCVD";
                case 5: return "ESTABLISHED";
                case 6: return "FIN_WAIT1";
                case 7: return "FIN_WAIT2";
                case 8: return "CLOSE_WAIT";
                case 9: return "CLOSING";
                case 10: return "LAST_ACK";
                case 11: return "TIME_WAIT";
                case 12: return "DELETE_TCB";
                default: return "STATE_" + state;
            }
        }

        public static List<FlowRecord> GetFlows(int filterPid)
        {
            List<FlowRecord> list = new List<FlowRecord>();
            ReadTcp4Flows(list, filterPid);
            ReadTcp6Flows(list, filterPid);
            ReadUdp4Flows(list, filterPid);
            ReadUdp6Flows(list, filterPid);
            return list;
        }

        private static void ReadTcp4Flows(List<FlowRecord> list, int filterPid)
        {
            int buffSize = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref buffSize, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (buffSize <= 0) return;

            IntPtr ptr = Marshal.AllocHGlobal(buffSize);
            try
            {
                if (GetExtendedTcpTable(ptr, ref buffSize, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) == 0)
                {
                    int count = Marshal.ReadInt32(ptr);
                    IntPtr rowPtr = new IntPtr(ptr.ToInt64() + 4);
                    int rowSize = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));
                    for (int i = 0; i < count; i++)
                    {
                        MIB_TCPROW_OWNER_PID row = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(rowPtr, typeof(MIB_TCPROW_OWNER_PID));
                        rowPtr = new IntPtr(rowPtr.ToInt64() + rowSize);
                        if (filterPid > 0 && row.owningPid != filterPid) continue;
                        if (row.state == 2 && filterPid <= 0) continue; // skip LISTEN unless filtering by PID

                        string localEp = new IPAddress(row.localAddr).ToString() + ":" + NetToHostPort(row.localPort);
                        string remoteEp = new IPAddress(row.remoteAddr).ToString() + ":" + NetToHostPort(row.remotePort);
                        list.Add(new FlowRecord
                        {
                            Key = FlowRecord.MakeKey("TCP", row.owningPid, localEp, remoteEp),
                            Protocol = "TCP",
                            Pid = row.owningPid,
                            LocalEndpoint = localEp,
                            RemoteEndpoint = remoteEp,
                            State = TcpStateName(row.state)
                        });
                    }
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        private static void ReadTcp6Flows(List<FlowRecord> list, int filterPid)
        {
            int buffSize = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref buffSize, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0);
            if (buffSize <= 0) return;

            IntPtr ptr = Marshal.AllocHGlobal(buffSize);
            try
            {
                if (GetExtendedTcpTable(ptr, ref buffSize, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0) == 0)
                {
                    int count = Marshal.ReadInt32(ptr);
                    IntPtr rowPtr = new IntPtr(ptr.ToInt64() + 4);
                    int rowSize = Marshal.SizeOf(typeof(MIB_TCP6ROW_OWNER_PID));
                    for (int i = 0; i < count; i++)
                    {
                        MIB_TCP6ROW_OWNER_PID row = (MIB_TCP6ROW_OWNER_PID)Marshal.PtrToStructure(rowPtr, typeof(MIB_TCP6ROW_OWNER_PID));
                        rowPtr = new IntPtr(rowPtr.ToInt64() + rowSize);
                        if (filterPid > 0 && row.owningPid != filterPid) continue;
                        if (row.state == 2 && filterPid <= 0) continue;

                        string localEp = "[" + new IPAddress(row.localAddr).ToString() + "]:" + NetToHostPort(row.localPort);
                        string remoteEp = "[" + new IPAddress(row.remoteAddr).ToString() + "]:" + NetToHostPort(row.remotePort);
                        list.Add(new FlowRecord
                        {
                            Key = FlowRecord.MakeKey("TCPv6", row.owningPid, localEp, remoteEp),
                            Protocol = "TCPv6",
                            Pid = row.owningPid,
                            LocalEndpoint = localEp,
                            RemoteEndpoint = remoteEp,
                            State = TcpStateName(row.state)
                        });
                    }
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        private static void ReadUdp4Flows(List<FlowRecord> list, int filterPid)
        {
            int buffSize = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref buffSize, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
            if (buffSize <= 0) return;

            IntPtr ptr = Marshal.AllocHGlobal(buffSize);
            try
            {
                if (GetExtendedUdpTable(ptr, ref buffSize, false, AF_INET, UDP_TABLE_OWNER_PID, 0) == 0)
                {
                    int count = Marshal.ReadInt32(ptr);
                    IntPtr rowPtr = new IntPtr(ptr.ToInt64() + 4);
                    int rowSize = Marshal.SizeOf(typeof(MIB_UDPROW_OWNER_PID));
                    for (int i = 0; i < count; i++)
                    {
                        MIB_UDPROW_OWNER_PID row = (MIB_UDPROW_OWNER_PID)Marshal.PtrToStructure(rowPtr, typeof(MIB_UDPROW_OWNER_PID));
                        rowPtr = new IntPtr(rowPtr.ToInt64() + rowSize);
                        if (filterPid > 0 && row.owningPid != filterPid) continue;

                        string localEp = new IPAddress(row.localAddr).ToString() + ":" + NetToHostPort(row.localPort);
                        list.Add(new FlowRecord
                        {
                            Key = FlowRecord.MakeKey("UDP", row.owningPid, localEp, "*:*"),
                            Protocol = "UDP",
                            Pid = row.owningPid,
                            LocalEndpoint = localEp,
                            RemoteEndpoint = "*:*",
                            State = "BOUND"
                        });
                    }
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        private static void ReadUdp6Flows(List<FlowRecord> list, int filterPid)
        {
            int buffSize = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref buffSize, false, AF_INET6, UDP_TABLE_OWNER_PID, 0);
            if (buffSize <= 0) return;

            IntPtr ptr = Marshal.AllocHGlobal(buffSize);
            try
            {
                if (GetExtendedUdpTable(ptr, ref buffSize, false, AF_INET6, UDP_TABLE_OWNER_PID, 0) == 0)
                {
                    int count = Marshal.ReadInt32(ptr);
                    IntPtr rowPtr = new IntPtr(ptr.ToInt64() + 4);
                    int rowSize = Marshal.SizeOf(typeof(MIB_UDP6ROW_OWNER_PID));
                    for (int i = 0; i < count; i++)
                    {
                        MIB_UDP6ROW_OWNER_PID row = (MIB_UDP6ROW_OWNER_PID)Marshal.PtrToStructure(rowPtr, typeof(MIB_UDP6ROW_OWNER_PID));
                        rowPtr = new IntPtr(rowPtr.ToInt64() + rowSize);
                        if (filterPid > 0 && row.owningPid != filterPid) continue;

                        string localEp = "[" + new IPAddress(row.localAddr).ToString() + "]:" + NetToHostPort(row.localPort);
                        list.Add(new FlowRecord
                        {
                            Key = FlowRecord.MakeKey("UDPv6", row.owningPid, localEp, "*:*"),
                            Protocol = "UDPv6",
                            Pid = row.owningPid,
                            LocalEndpoint = localEp,
                            RemoteEndpoint = "*:*",
                            State = "BOUND"
                        });
                    }
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        public static HashSet<int> GetActiveNetworkPids()
        {
            HashSet<int> pids = new HashSet<int>();
            foreach (int p in ReadTcpPids()) pids.Add(p);
            foreach (int p in ReadUdpPids()) pids.Add(p);
            return pids;
        }

        public static void GetPidSocketCounts(int targetPid, out int tcpCount, out int udpCount)
        {
            tcpCount = 0;
            udpCount = 0;
            foreach (int p in ReadTcpPids()) { if (p == targetPid) tcpCount++; }
            foreach (int p in ReadUdpPids()) { if (p == targetPid) udpCount++; }
        }

        private static List<int> ReadTcpPids()
        {
            List<int> list = new List<int>();
            int buffSize = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref buffSize, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (buffSize <= 0) return list;

            IntPtr ptr = Marshal.AllocHGlobal(buffSize);
            try
            {
                if (GetExtendedTcpTable(ptr, ref buffSize, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) == 0)
                {
                    int count = Marshal.ReadInt32(ptr);
                    IntPtr rowPtr = new IntPtr(ptr.ToInt64() + 4);
                    int rowSize = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));
                    for (int i = 0; i < count; i++)
                    {
                        MIB_TCPROW_OWNER_PID row = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(rowPtr, typeof(MIB_TCPROW_OWNER_PID));
                        list.Add(row.owningPid);
                        rowPtr = new IntPtr(rowPtr.ToInt64() + rowSize);
                    }
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(ptr); }
            return list;
        }

        private static List<int> ReadUdpPids()
        {
            List<int> list = new List<int>();
            int buffSize = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref buffSize, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
            if (buffSize <= 0) return list;

            IntPtr ptr = Marshal.AllocHGlobal(buffSize);
            try
            {
                if (GetExtendedUdpTable(ptr, ref buffSize, false, AF_INET, UDP_TABLE_OWNER_PID, 0) == 0)
                {
                    int count = Marshal.ReadInt32(ptr);
                    IntPtr rowPtr = new IntPtr(ptr.ToInt64() + 4);
                    int rowSize = Marshal.SizeOf(typeof(MIB_UDPROW_OWNER_PID));
                    for (int i = 0; i < count; i++)
                    {
                        MIB_UDPROW_OWNER_PID row = (MIB_UDPROW_OWNER_PID)Marshal.PtrToStructure(rowPtr, typeof(MIB_UDPROW_OWNER_PID));
                        list.Add(row.owningPid);
                        rowPtr = new IntPtr(rowPtr.ToInt64() + rowSize);
                    }
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(ptr); }
            return list;
        }
    }

    public sealed class EtwNetworkMonitor : IDisposable
    {
        private readonly string sessionName = "NetworkMonitor_" + Process.GetCurrentProcess().Id;
        private readonly Guid sessionGuid = Guid.NewGuid();
        private static readonly Guid KernelNetworkProviderGuid = new Guid("7dd42a49-5329-4832-8dfd-43d979153a88");

        private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
        private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
        private const uint EVENT_TRACE_CONTROL_STOP = 1;
        private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
        private const byte TRACE_LEVEL_INFORMATION = 4;
        private const ulong KERNEL_NETWORK_KEYWORD_IPV4 = 0x10;
        private const ulong KERNEL_NETWORK_KEYWORD_IPV6 = 0x20;
        private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
        private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;

        private delegate void EventRecordCallback(ref EVENT_RECORD eventRecord);

        [StructLayout(LayoutKind.Sequential)]
        private struct WNODE_HEADER
        {
            public uint BufferSize;
            public uint ProviderId;
            public ulong HistoricalContext;
            public long TimeStamp;
            public Guid Guid;
            public uint ClientContext;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_TRACE_PROPERTIES
        {
            public WNODE_HEADER Wnode;
            public uint BufferSize;
            public uint MinimumBuffers;
            public uint MaximumBuffers;
            public uint MaximumFileSize;
            public uint LogFileMode;
            public uint FlushTimer;
            public uint EnableFlags;
            public int AgeLimit;
            public uint NumberOfBuffers;
            public uint FreeBuffers;
            public uint EventsLost;
            public uint BuffersWritten;
            public uint LogBuffersLost;
            public uint RealTimeBuffersLost;
            public IntPtr LoggerThreadId;
            public uint LogFileNameOffset;
            public uint LoggerNameOffset;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_DESCRIPTOR
        {
            public ushort Id;
            public byte Version;
            public byte Channel;
            public byte Level;
            public byte Opcode;
            public ushort Task;
            public ulong Keyword;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_HEADER
        {
            public ushort Size;
            public ushort HeaderType;
            public ushort Flags;
            public ushort EventProperty;
            public uint ThreadId;
            public uint ProcessId;
            public long TimeStamp;
            public Guid ProviderId;
            public EVENT_DESCRIPTOR EventDescriptor;
            public ulong KernelTime;
            public ulong ActivityId1;
            public ulong ActivityId2;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_RECORD
        {
            public EVENT_HEADER EventHeader;
            public uint BufferContext;
            public ushort ExtendedDataCount;
            public ushort UserDataLength;
            public IntPtr ExtendedData;
            public IntPtr UserData;
            public IntPtr UserContext;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct EVENT_TRACE_LOGFILE
        {
            public string LogFileName;
            public string LoggerName;
            public long CurrentTime;
            public uint BuffersRead;
            public uint ProcessTraceMode;
            public EVENT_TRACE CurrentEvent;
            public TRACE_LOGFILE_HEADER LogfileHeader;
            public IntPtr BufferCallback;
            public uint BufferSize;
            public uint Filled;
            public uint EventsLost;
            public EventRecordCallback EventRecordCallback;
            public uint IsKernelTrace;
            public IntPtr Context;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_TRACE
        {
            public EVENT_TRACE_HEADER Header;
            public uint InstanceId;
            public uint ParentInstanceId;
            public Guid ParentGuid;
            public IntPtr MofData;
            public uint MofLength;
            public uint BufferContext;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_TRACE_HEADER
        {
            public ushort Size;
            public ushort FieldTypeFlags;
            public uint Version;
            public uint ThreadId;
            public uint ProcessId;
            public long TimeStamp;
            public Guid Guid;
            public ulong ClientContext;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEMTIME
        {
            public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TRACE_LOGFILE_HEADER
        {
            public uint BufferSize;
            public uint Version;
            public uint ProviderVersion;
            public uint NumberOfProcessors;
            public long EndTime;
            public uint TimerResolution;
            public uint MaximumFileSize;
            public uint LogFileMode;
            public uint BuffersWritten;
            public Guid LogInstanceGuid;
            public IntPtr LoggerName;
            public IntPtr LogFileName;
            public long TimeZoneBias;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public ushort[] TimeZoneStandardName;
            public SYSTEMTIME TimeZoneStandardDate;
            public int TimeZoneStandardBias;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public ushort[] TimeZoneDaylightName;
            public SYSTEMTIME TimeZoneDaylightDate;
            public int TimeZoneDaylightBias;
            public long BootTime;
            public long PerfFreq;
            public long StartTime;
            public uint ReservedFlags;
            public uint BuffersLost;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint StartTrace(out ulong SessionHandle, string SessionName, IntPtr Properties);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ControlTrace(ulong SessionHandle, string SessionName, IntPtr Properties, uint ControlCode);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ulong OpenTrace(ref EVENT_TRACE_LOGFILE Logfile);

        [DllImport("advapi32.dll")]
        private static extern uint ProcessTrace(ulong[] HandleArray, uint HandleCount, IntPtr StartTime, IntPtr EndTime);

        [DllImport("advapi32.dll")]
        private static extern uint CloseTrace(ulong TraceHandle);

        [DllImport("advapi32.dll")]
        private static extern uint EnableTraceEx2(
            ulong TraceHandle,
            ref Guid ProviderId,
            uint ControlCode,
            byte Level,
            ulong MatchAnyKeyword,
            ulong MatchAllKeyword,
            uint Timeout,
            IntPtr EnableParameters);

        private readonly object sync = new object();
        private readonly Dictionary<int, long[]> pidBytes = new Dictionary<int, long[]>(); // [0]=recv, [1]=sent
        private readonly Dictionary<string, FlowRecord> flowBytes = new Dictionary<string, FlowRecord>();

        private ulong sessionHandle = 0;
        private ulong traceHandle = ulong.MaxValue;
        private bool startedOwnSession = false;
        private Thread workerThread;
        private EventRecordCallback callbackDelegate;

        public bool IsRunning { get; private set; }
        public string LastError { get; private set; }

        public void Start()
        {
            if (IsRunning) return;

            int propsSize = Marshal.SizeOf(typeof(EVENT_TRACE_PROPERTIES));
            int nameBytes = (sessionName.Length + 1) * 2;
            int totalSize = propsSize + nameBytes + 4;

            IntPtr propsPtr = Marshal.AllocHGlobal(totalSize);
            try
            {
                byte[] zeros = new byte[totalSize];
                Marshal.Copy(zeros, 0, propsPtr, totalSize);

                EVENT_TRACE_PROPERTIES props = new EVENT_TRACE_PROPERTIES();
                props.Wnode.BufferSize = (uint)totalSize;
                props.Wnode.Guid = sessionGuid;
                props.Wnode.ClientContext = 1;
                props.Wnode.Flags = WNODE_FLAG_TRACED_GUID;
                props.LogFileMode = EVENT_TRACE_REAL_TIME_MODE;
                props.LoggerNameOffset = (uint)propsSize;
                Marshal.StructureToPtr(props, propsPtr, false);

                uint status = StartTrace(out sessionHandle, sessionName, propsPtr);
                if (status != 0)
                {
                    LastError = "StartTrace failed (" + status + ")";
                    return;
                }
                startedOwnSession = true;
            }
            finally
            {
                Marshal.FreeHGlobal(propsPtr);
            }

            Guid providerGuid = KernelNetworkProviderGuid;
            uint enableStatus = EnableTraceEx2(
                sessionHandle,
                ref providerGuid,
                EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                TRACE_LEVEL_INFORMATION,
                KERNEL_NETWORK_KEYWORD_IPV4 | KERNEL_NETWORK_KEYWORD_IPV6,
                0,
                0,
                IntPtr.Zero);
            if (enableStatus != 0)
            {
                LastError = "EnableTraceEx2 failed (" + enableStatus + ")";
                return;
            }

            callbackDelegate = new EventRecordCallback(OnEventRecord);
            EVENT_TRACE_LOGFILE logfile = new EVENT_TRACE_LOGFILE();
            logfile.LoggerName = sessionName;
            logfile.ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD;
            logfile.EventRecordCallback = callbackDelegate;
            logfile.LogfileHeader.TimeZoneStandardName = new ushort[32];
            logfile.LogfileHeader.TimeZoneDaylightName = new ushort[32];

            traceHandle = OpenTrace(ref logfile);
            if (traceHandle == ulong.MaxValue || traceHandle == 0)
            {
                LastError = "OpenTrace failed (" + Marshal.GetLastWin32Error() + ")";
                return;
            }

            IsRunning = true;
            workerThread = new Thread(delegate()
            {
                try
                {
                    uint ptStatus = ProcessTrace(new ulong[] { traceHandle }, 1, IntPtr.Zero, IntPtr.Zero);
                    if (ptStatus != 0) LastError = "ProcessTrace (" + ptStatus + ")";
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                }
                finally
                {
                    IsRunning = false;
                }
            });
            workerThread.IsBackground = true;
            workerThread.Start();
        }

        private static ushort SwapBytes(ushort val)
        {
            return (ushort)((val << 8) | (val >> 8));
        }

        private void OnEventRecord(ref EVENT_RECORD rec)
        {
            if (rec.EventHeader.ProviderId != KernelNetworkProviderGuid) return;
            if (rec.UserData == IntPtr.Zero || rec.UserDataLength < 8) return;

            ushort id = rec.EventHeader.EventDescriptor.Id;
            bool isSend = (id == 10 || id == 26 || id == 42 || id == 58);
            bool isRecv = (id == 11 || id == 27 || id == 43 || id == 59);
            if (!isSend && !isRecv) return;

            int pid = Marshal.ReadInt32(rec.UserData, 0);
            int size = Marshal.ReadInt32(rec.UserData, 4);
            if (pid <= 0 || size <= 0) return;

            string proto = null;
            string localEp = null;
            string remoteEp = null;

            bool isV6 = (id == 26 || id == 27 || id == 58 || id == 59);
            bool isUdp = (id == 42 || id == 43 || id == 58 || id == 59);

            if (!isV6 && rec.UserDataLength >= 20)
            {
                proto = isUdp ? "UDP" : "TCP";
                uint daddr = (uint)Marshal.ReadInt32(rec.UserData, 8);
                uint saddr = (uint)Marshal.ReadInt32(rec.UserData, 12);
                ushort dport = SwapBytes((ushort)Marshal.ReadInt16(rec.UserData, 16));
                ushort sport = SwapBytes((ushort)Marshal.ReadInt16(rec.UserData, 18));
                string dStr = new IPAddress((long)daddr).ToString() + ":" + dport;
                string sStr = new IPAddress((long)saddr).ToString() + ":" + sport;
                localEp = isSend ? sStr : dStr;
                remoteEp = isSend ? dStr : sStr;
            }
            else if (isV6 && rec.UserDataLength >= 44)
            {
                proto = isUdp ? "UDPv6" : "TCPv6";
                byte[] daddr6 = new byte[16];
                byte[] saddr6 = new byte[16];
                Marshal.Copy(new IntPtr(rec.UserData.ToInt64() + 8), daddr6, 0, 16);
                Marshal.Copy(new IntPtr(rec.UserData.ToInt64() + 24), saddr6, 0, 16);
                ushort dport = SwapBytes((ushort)Marshal.ReadInt16(rec.UserData, 40));
                ushort sport = SwapBytes((ushort)Marshal.ReadInt16(rec.UserData, 42));
                string dStr = "[" + new IPAddress(daddr6).ToString() + "]:" + dport;
                string sStr = "[" + new IPAddress(saddr6).ToString() + "]:" + sport;
                localEp = isSend ? sStr : dStr;
                remoteEp = isSend ? dStr : sStr;
            }

            lock (sync)
            {
                long[] counters;
                if (!pidBytes.TryGetValue(pid, out counters))
                {
                    counters = new long[2];
                    pidBytes[pid] = counters;
                }
                if (isRecv) counters[0] += size;
                else counters[1] += size;

                if (proto != null)
                {
                    string key = FlowRecord.MakeKey(proto, pid, localEp, remoteEp);
                    FlowRecord flow;
                    if (!flowBytes.TryGetValue(key, out flow))
                    {
                        if (flowBytes.Count >= 2000) flowBytes.Clear();
                        flow = new FlowRecord
                        {
                            Key = key,
                            Protocol = proto,
                            Pid = pid,
                            LocalEndpoint = localEp,
                            RemoteEndpoint = remoteEp,
                            State = "ACTIVE"
                        };
                        flowBytes[key] = flow;
                    }
                    if (isRecv) flow.RecvBytes += size;
                    else flow.SentBytes += size;
                }
            }
        }

        public void ClearFlows()
        {
            lock (sync)
            {
                flowBytes.Clear();
            }
        }

        public List<FlowRecord> GetFlowSnapshot(int filterPid)
        {
            List<FlowRecord> snapshot = new List<FlowRecord>();
            lock (sync)
            {
                foreach (FlowRecord f in flowBytes.Values)
                {
                    if (filterPid > 0 && f.Pid != filterPid) continue;
                    snapshot.Add(new FlowRecord
                    {
                        Key = f.Key,
                        Protocol = f.Protocol,
                        Pid = f.Pid,
                        LocalEndpoint = f.LocalEndpoint,
                        RemoteEndpoint = f.RemoteEndpoint,
                        State = f.State,
                        RecvBytes = f.RecvBytes,
                        SentBytes = f.SentBytes
                    });
                }
            }
            return snapshot;
        }

        public void GetPidTotals(int pid, out long recvBytes, out long sentBytes)
        {
            lock (sync)
            {
                long[] counters;
                if (pidBytes.TryGetValue(pid, out counters))
                {
                    recvBytes = counters[0];
                    sentBytes = counters[1];
                }
                else
                {
                    recvBytes = 0;
                    sentBytes = 0;
                }
            }
        }

        public void Dispose()
        {
            IsRunning = false;
            if (traceHandle != ulong.MaxValue && traceHandle != 0)
            {
                try { CloseTrace(traceHandle); } catch { }
                traceHandle = ulong.MaxValue;
            }
            if (startedOwnSession && sessionHandle != 0)
            {
                int propsSize = Marshal.SizeOf(typeof(EVENT_TRACE_PROPERTIES));
                int totalSize = propsSize + 256;
                IntPtr propsPtr = Marshal.AllocHGlobal(totalSize);
                try
                {
                    byte[] zeros = new byte[totalSize];
                    Marshal.Copy(zeros, 0, propsPtr, totalSize);
                    EVENT_TRACE_PROPERTIES props = new EVENT_TRACE_PROPERTIES();
                    props.Wnode.BufferSize = (uint)totalSize;
                    props.Wnode.Guid = sessionGuid;
                    Marshal.StructureToPtr(props, propsPtr, false);
                    ControlTrace(sessionHandle, sessionName, propsPtr, EVENT_TRACE_CONTROL_STOP);
                }
                catch { }
                finally { Marshal.FreeHGlobal(propsPtr); }
                sessionHandle = 0;
            }
        }
    }

    public class GraphPanel : Panel
    {
        private readonly double[] downHistory;
        private readonly double[] upHistory;
        private readonly Color downColor;
        private readonly Color upColor;
        private readonly Color bgColor;
        private readonly Color borderColor;
        private readonly Color textColor;
        private readonly Func<double, string> rateFormatter;

        public GraphPanel(
            double[] downHistory,
            double[] upHistory,
            Color downColor,
            Color upColor,
            Color bgColor,
            Color borderColor,
            Color textColor,
            Func<double, string> rateFormatter)
        {
            this.downHistory = downHistory;
            this.upHistory = upHistory;
            this.downColor = downColor;
            this.upColor = upColor;
            this.bgColor = bgColor;
            this.borderColor = borderColor;
            this.textColor = textColor;
            this.rateFormatter = rateFormatter;

            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = bgColor;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (SolidBrush bg = new SolidBrush(bgColor))
            {
                g.FillRectangle(bg, rect);
            }
            using (Pen border = new Pen(borderColor, 1))
            {
                g.DrawRectangle(border, rect);
            }

            int padLeft = 10;
            int padRight = 10;
            int padTop = 26;
            int padBottom = 14;
            int plotW = Math.Max(10, Width - padLeft - padRight);
            int plotH = Math.Max(10, Height - padTop - padBottom);

            double maxVal = 10240.0;
            for (int i = 0; i < downHistory.Length; i++)
            {
                if (downHistory[i] > maxVal) maxVal = downHistory[i];
                if (upHistory[i] > maxVal) maxVal = upHistory[i];
            }
            maxVal *= 1.15;

            using (Pen gridPen = new Pen(Color.FromArgb(45, 45, 52), 1))
            using (Font labelFont = new Font("Segoe UI", 8f))
            using (SolidBrush labelBrush = new SolidBrush(textColor))
            {
                gridPen.DashStyle = DashStyle.Dot;
                for (int i = 0; i <= 4; i++)
                {
                    int y = padTop + (int)(plotH * (i / 4.0));
                    g.DrawLine(gridPen, padLeft, y, padLeft + plotW, y);
                }

                g.DrawString("60s Real-Time Throughput (Max: " + rateFormatter(maxVal) + ")",
                    labelFont, labelBrush, padLeft, 6);
            }

            DrawSeries(g, upHistory, upColor, padLeft, padTop, plotW, plotH, maxVal);
            DrawSeries(g, downHistory, downColor, padLeft, padTop, plotW, plotH, maxVal);
        }

        private static void DrawSeries(
            Graphics g,
            double[] data,
            Color color,
            int left,
            int top,
            int width,
            int height,
            double maxVal)
        {
            int count = data.Length;
            if (count < 2) return;

            PointF[] points = new PointF[count];
            for (int i = 0; i < count; i++)
            {
                float x = left + (float)i / (count - 1) * width;
                double ratio = Math.Min(1.0, Math.Max(0.0, data[i] / maxVal));
                float y = top + height - (float)(ratio * height);
                points[i] = new PointF(x, y);
            }

            using (GraphicsPath fillPath = new GraphicsPath())
            {
                fillPath.AddLines(points);
                fillPath.AddLine(points[count - 1].X, top + height, points[0].X, top + height);
                fillPath.CloseFigure();

                using (LinearGradientBrush fillBrush = new LinearGradientBrush(
                    new Point(0, top),
                    new Point(0, top + height),
                    Color.FromArgb(65, color),
                    Color.FromArgb(5, color)))
                {
                    g.FillPath(fillBrush, fillPath);
                }
            }

            using (Pen linePen = new Pen(color, 2f))
            {
                linePen.LineJoin = LineJoin.Round;
                g.DrawLines(linePen, points);
            }
        }
    }
}
