using System.Drawing;
using System.Windows.Forms;

namespace FeigeP2P;

public sealed class MainForm : Form
{
    private readonly AppConfig _config;
    private readonly DiscoveryService _discovery;
    private readonly TransferService _transfer;

    private readonly Label _nameLabel = new();
    private readonly TextBox _nameBox = new();
    private readonly Label _localInfo = new();
    private readonly Label _onlineDot = new();
    private readonly ListView _devices = new();
    private readonly Label _emptyHint = new();
    private readonly RichTextBox _log = new();
    private readonly Button _clearLogBtn = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _progressLabel = new();
    private readonly ContextMenuStrip _deviceMenu = new();
    private readonly ToolTip _tip = new();
    private readonly ToolStripMenuItem _menuSend;
    private readonly ToolStripMenuItem _menuCopyIp;
    private readonly ToolStripMenuItem _menuRefresh;

    private Peer? _menuTarget;
    private string _lastLog = "";
    private DateTime _lastLogTime = DateTime.MinValue;

    public MainForm()
    {
        _config = AppConfig.Load();
        _discovery = new DiscoveryService(_config);
        _transfer = new TransferService(_config);

        _menuSend = new ToolStripMenuItem("发送文件…");
        _menuCopyIp = new ToolStripMenuItem("复制 IP 地址");
        _menuRefresh = new ToolStripMenuItem("刷新设备");

        BuildUi();
        WireEvents();

        _transfer.Start();
        _discovery.Start();
        AppendLog($"已启动 · 本机 {_config.DeviceName} ({_discovery.LocalIp}:{_config.TcpPort})", Color.FromArgb(25, 100, 180));
        AppendLog("右键在线设备即可发送文件；文件保存在程序目录 received/", Color.Gray);
    }

    // ---------------- UI 构建 ----------------

    private void BuildUi()
    {
        Text = "飞鸽 P2P · 局域网互传";
        Size = new Size(1040, 680);
        MinimumSize = new Size(820, 540);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(245, 246, 248);
        Font = new Font("微软雅黑", 9F);

        // 根容器：3 行（顶部信息 / 主体 / 底部进度），用百分比+绝对值，稳定不重叠
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62F));
        Controls.Add(root);

        root.Controls.Add(BuildTop(), 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);
        root.Controls.Add(BuildBottom(), 0, 2);
    }

    private Control BuildTop()
    {
        var top = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(20, 0, 20, 0),
            Margin = Padding.Empty
        };

        // 本机名（可点击改）
        _nameLabel.Text = _config.DeviceName;
        _nameLabel.Font = new Font("微软雅黑", 15F, FontStyle.Bold);
        _nameLabel.ForeColor = Color.FromArgb(28, 32, 38);
        _nameLabel.Location = new Point(20, 9);
        _nameLabel.AutoSize = true;
        _nameLabel.Cursor = Cursors.Hand;
        _tip.SetToolTip(_nameLabel, "点击修改本机设备名");
        top.Controls.Add(_nameLabel);

        _nameBox.Location = new Point(18, 7);
        _nameBox.Size = new Size(260, 30);
        _nameBox.Font = new Font("微软雅黑", 12F);
        _nameBox.Visible = false;
        top.Controls.Add(_nameBox);

        // IP / 端口 / 协议
        _localInfo.Text = $"{_discovery.LocalIp}:{_config.TcpPort}   ·   协议 v1.0   ·   点击名称可修改";
        _localInfo.ForeColor = Color.FromArgb(130, 138, 148);
        _localInfo.Font = new Font("微软雅黑", 9F);
        _localInfo.Location = new Point(21, 38);
        _localInfo.AutoSize = true;
        top.Controls.Add(_localInfo);

        // 右上角在线状态（用 Anchor 靠右，不设 Dock 避免抢布局）
        _onlineDot.Text = "●  在线";
        _onlineDot.ForeColor = Color.FromArgb(32, 168, 72);
        _onlineDot.Font = new Font("微软雅黑", 11F, FontStyle.Bold);
        _onlineDot.AutoSize = true;
        _onlineDot.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _onlineDot.Location = new Point(top.Width - 100, 20);
        top.Controls.Add(_onlineDot);

        // 底部分隔线
        var line = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 1,
            BackColor = Color.FromArgb(224, 228, 233),
            Margin = Padding.Empty
        };
        top.Controls.Add(line);
        return top;
    }

    private Control BuildBody()
    {
        // 左右两栏：左 56% / 右 44%
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(12, 10, 12, 8),
            Padding = Padding.Empty
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56F));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44F));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        body.Controls.Add(BuildLeft(), 0, 0);
        body.Controls.Add(BuildRight(), 1, 0);
        return body;
    }

    private Control BuildLeft()
    {
        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0, 0, 6, 0),
            Padding = Padding.Empty,
            BackColor = Color.White
        };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));

        // 标题
        var deviceTitle = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = Padding.Empty, Padding = new Padding(14, 0, 0, 0) };
        var titleLbl = new Label
        {
            Text = "在线设备",
            Font = new Font("微软雅黑", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 46, 54),
            Location = new Point(14, 8),
            AutoSize = true
        };
        var subLbl = new Label
        {
            Text = "右键设备发送文件",
            Font = new Font("微软雅黑", 8.5F),
            ForeColor = Color.FromArgb(150, 156, 164),
            AutoSize = true
        };
        subLbl.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        subLbl.Location = new Point(deviceTitle.Width - 130, 11);
        deviceTitle.Controls.Add(titleLbl);
        deviceTitle.Controls.Add(subLbl);
        left.Controls.Add(deviceTitle, 0, 0);

        // 设备列表
        _devices.View = View.Details;
        _devices.FullRowSelect = true;
        _devices.MultiSelect = false;
        _devices.Dock = DockStyle.Fill;
        _devices.BorderStyle = BorderStyle.None;
        _devices.BackColor = Color.White;
        _devices.Font = new Font("微软雅黑", 9.5F);
        _devices.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _devices.ContextMenuStrip = _deviceMenu;
        _devices.Columns.Add("设备名", 180);
        _devices.Columns.Add("IP 地址", 140);
        _devices.Columns.Add("端口", 70);
        _deviceMenu.Items.AddRange(new ToolStripItem[] { _menuSend, _menuCopyIp, new ToolStripSeparator(), _menuRefresh });
        _deviceMenu.Opening += DeviceMenu_Opening;

        _emptyHint.Text = "暂无在线设备\n\n启动手机 App 或另一台电脑，稍候将自动出现在此处";
        _emptyHint.TextAlign = ContentAlignment.MiddleCenter;
        _emptyHint.ForeColor = Color.FromArgb(165, 170, 178);
        _emptyHint.Font = new Font("微软雅黑", 10F);
        _emptyHint.Dock = DockStyle.Fill;
        _emptyHint.Visible = true;

        var listHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Color.White };
        listHost.Controls.Add(_devices);
        listHost.Controls.Add(_emptyHint);
        _devices.BringToFront();
        left.Controls.Add(listHost, 0, 1);

        // 打开收件夹
        var folderBar = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Color.White, Padding = new Padding(12, 7, 12, 7) };
        var openFolder = new Button
        {
            Text = "打开收件夹",
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(246, 249, 253),
            ForeColor = Color.FromArgb(40, 96, 168),
            Font = new Font("微软雅黑", 9.5F),
            Cursor = Cursors.Hand
        };
        openFolder.FlatAppearance.BorderColor = Color.FromArgb(206, 218, 234);
        openFolder.Click += (_, _) => OpenReceiveFolder();
        folderBar.Controls.Add(openFolder);
        left.Controls.Add(folderBar, 0, 2);

        // 窗口缩放时按比例调整列宽
        Resize += (_, _) =>
        {
            int w = Math.Max(_devices.ClientSize.Width - 4, 200);
            _devices.Columns[0].Width = (int)(w * 0.48);
            _devices.Columns[1].Width = (int)(w * 0.36);
            _devices.Columns[2].Width = w - _devices.Columns[0].Width - _devices.Columns[1].Width;
        };

        return left;
    }

    private Control BuildRight()
    {
        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(6, 0, 0, 0),
            Padding = Padding.Empty,
            BackColor = Color.White
        };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var logHeader = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Color.White };
        var logTitle = new Label
        {
            Text = "传输日志",
            Font = new Font("微软雅黑", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 46, 54),
            Location = new Point(14, 8),
            AutoSize = true
        };
        _clearLogBtn.Text = "清空";
        _clearLogBtn.Font = new Font("微软雅黑", 8.5F);
        _clearLogBtn.ForeColor = Color.FromArgb(120, 128, 138);
        _clearLogBtn.FlatStyle = FlatStyle.Flat;
        _clearLogBtn.FlatAppearance.BorderSize = 0;
        _clearLogBtn.BackColor = Color.Transparent;
        _clearLogBtn.AutoSize = true;
        _clearLogBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _clearLogBtn.Location = new Point(logHeader.Width - 50, 9);
        _clearLogBtn.Click += (_, _) => _log.Clear();
        logHeader.Controls.Add(logTitle);
        logHeader.Controls.Add(_clearLogBtn);
        right.Controls.Add(logHeader, 0, 0);

        _log.Dock = DockStyle.Fill;
        _log.ReadOnly = true;
        _log.BorderStyle = BorderStyle.None;
        _log.BackColor = Color.White;
        _log.Font = new Font("Consolas", 9.5F);
        _log.Margin = new Padding(10, 0, 10, 8);
        right.Controls.Add(_log, 0, 1);
        return right;
    }

    private Control BuildBottom()
    {
        var bottom = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Margin = Padding.Empty,
            Padding = new Padding(20, 10, 20, 8)
        };

        _progressLabel.Dock = DockStyle.Fill;
        _progressLabel.Text = "就绪  ·  右键在线设备即可发送文件";
        _progressLabel.ForeColor = Color.FromArgb(110, 118, 128);
        _progressLabel.Font = new Font("微软雅黑", 9F);

        _progress.Dock = DockStyle.Bottom;
        _progress.Height = 8;
        _progress.Minimum = 0;
        _progress.Maximum = 1000;
        _progress.Value = 0;
        _progress.Style = ProgressBarStyle.Continuous;

        bottom.Controls.Add(_progressLabel);
        bottom.Controls.Add(_progress);

        var line = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = Color.FromArgb(224, 228, 233),
            Margin = Padding.Empty
        };
        bottom.Controls.Add(line);
        return bottom;
    }

    private void WireEvents()
    {
        _nameLabel.Click += (_, _) => BeginEditName();
        _nameBox.KeyDown += NameBox_KeyDown;
        _nameBox.LostFocus += (_, _) => CommitNameIfVisible();

        _menuSend.Click += async (_, _) => await SendFromMenuAsync();
        _menuCopyIp.Click += (_, _) =>
        {
            if (_menuTarget != null)
            {
                Clipboard.SetText(_menuTarget.Ip);
                AppendLog($"已复制 IP：{_menuTarget.Ip}", Color.Gray);
            }
        };
        _menuRefresh.Click += (_, _) =>
        {
            _discovery.BroadcastOnce();
            AppendLog("已重新广播本机信息", Color.FromArgb(180, 130, 20));
        };

        _discovery.PeerChanged += (peer, added) => Ui(() =>
        {
            if (added)
            {
                var existing = FindItem(peer.Key);
                if (existing == null)
                {
                    var item = new ListViewItem(new[] { peer.Name, peer.Ip, peer.TcpPort.ToString() }) { Tag = peer };
                    _devices.Items.Add(item);
                    AppendLog($"发现设备：{peer.Name} ({peer.Ip})", Color.FromArgb(180, 130, 20));
                }
                else
                {
                    existing.SubItems[0].Text = peer.Name;
                    existing.Tag = peer;
                }
            }
            else
            {
                var item = FindItem(peer.Key);
                if (item != null)
                {
                    _devices.Items.Remove(item);
                    AppendLog($"设备离线：{peer.Name}", Color.FromArgb(180, 130, 20));
                }
            }
            _emptyHint.Visible = _devices.Items.Count == 0;
        });

        _transfer.Log += msg => Ui(() => AppendLog(msg, Color.FromArgb(40, 40, 40)));
        _transfer.Progress += (dir, name, done, total) => Ui(() =>
        {
            var pct = total > 0 ? (int)(1000.0 * done / total) : 0;
            _progress.Value = Math.Min(1000, Math.Max(0, pct));
            _progressLabel.Text = $"{dir} {name}  {TransferService.FormatSize(done)}/{TransferService.FormatSize(total)}  {pct / 10}%";
        });
        _transfer.TransferFinished += (dir, name, peer, size, ok, msg) => Ui(() =>
        {
            if (ok)
            {
                AppendLog($"✓ {dir} {name} 完成", Color.FromArgb(25, 140, 60));
                _progress.Value = 1000;
                _progressLabel.Text = $"{dir} {name} 完成 · {TransferService.FormatSize(size)}";
            }
            else
            {
                AppendLog($"✗ {dir} {name} 失败：{msg}", Color.FromArgb(190, 40, 40));
                _progressLabel.Text = $"{dir} {name} 失败";
            }
        });

        FormClosing += (_, _) =>
        {
            _discovery.Dispose();
            _transfer.Dispose();
        };
    }

    private void DeviceMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_devices.SelectedItems.Count == 0) { e.Cancel = true; return; }
        _menuTarget = _devices.SelectedItems[0].Tag as Peer;
        if (_menuTarget == null) { e.Cancel = true; return; }
        _menuSend.Text = $"发送文件到 {_menuTarget.Name}…";
    }

    private async Task SendFromMenuAsync()
    {
        if (_menuTarget == null) return;
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = $"发送文件到 {_menuTarget.Name}",
            Filter = "所有文件|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var target = _menuTarget;
        foreach (var file in dlg.FileNames)
        {
            _progress.Value = 0;
            AppendLog($"开始发送：{Path.GetFileName(file)} → {target.Name}", Color.FromArgb(30, 100, 180));
            var (ok, msg) = await _transfer.SendFileAsync(target, file);
            if (!ok) AppendLog(msg, Color.FromArgb(190, 40, 40));
        }
    }

    private void BeginEditName()
    {
        _nameLabel.Visible = false;
        _nameBox.Text = _config.DeviceName;
        _nameBox.Visible = true;
        _nameBox.BringToFront();
        _nameBox.Focus();
        _nameBox.SelectAll();
    }

    private void NameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) { CommitNameIfVisible(); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape) { _nameBox.Visible = false; _nameLabel.Visible = true; }
    }

    private void CommitNameIfVisible()
    {
        if (!_nameBox.Visible) return;
        var newName = _nameBox.Text.Trim();
        if (newName.Length == 0) newName = Environment.MachineName;
        _config.DeviceName = newName;
        _config.Save();
        _discovery.SetDeviceName(newName);
        _nameLabel.Text = newName;
        _nameBox.Visible = false;
        _nameLabel.Visible = true;
        AppendLog($"设备名已改为「{newName}」，已通知局域网", Color.FromArgb(180, 130, 20));
    }

    private ListViewItem? FindItem(string key) =>
        _devices.Items.Cast<ListViewItem>().FirstOrDefault(i => (i.Tag as Peer)?.Key == key);

    private void OpenReceiveFolder()
    {
        try
        {
            var dir = AppConfig.ReceiveDirectory;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch (Exception ex) { AppendLog($"打开收件夹失败：{ex.Message}", Color.FromArgb(190, 40, 40)); }
    }

    private void AppendLog(string msg, Color color)
    {
        Ui(() =>
        {
            // 同一条消息 500ms 内重复则跳过，避免设备闪烁刷屏
            var now = DateTime.Now;
            if (msg == _lastLog && (now - _lastLogTime).TotalMilliseconds < 500) return;
            _lastLog = msg;
            _lastLogTime = now;

            _log.SelectionStart = _log.TextLength;
            _log.SelectionColor = color;
            _log.AppendText($"[{now:HH:mm:ss}] {msg}{Environment.NewLine}");
            _log.ScrollToCaret();
        });
    }

    private void Ui(Action action)
    {
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }
}
