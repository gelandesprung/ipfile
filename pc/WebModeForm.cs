using System.Drawing;
using System.Windows.Forms;

namespace FeigeP2P;

/// <summary>
/// 网页模式窗口（Host 端）：地址族切换 + 二维码 + PIN + 在线访客 + 群发。
/// 关窗口只是收起，服务继续跑；要真正停服点「停止服务」（决策：服务与窗口生命周期分离）。
/// </summary>
internal sealed class WebModeForm : Form
{
    private readonly WebHostService _web;
    private readonly AppConfig _config;
    private readonly Action _onStateChanged;

    private readonly Label _status = new();
    private readonly Button _toggleBtn = new();
    private readonly Button _v4 = new();
    private readonly Button _v6 = new();
    private readonly PictureBox _qr = new();
    private readonly Label _url = new();
    private readonly Button _copy = new();
    private readonly Label _pin = new();
    private readonly Button _pinRefresh = new();
    private readonly Label _guestsTitle = new();
    private readonly Button _group = new();
    private readonly ListView _list = new();
    private readonly Label _empty = new();
    private readonly Button _portBtn = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _menuSend = new("发送文件…");
    private readonly ToolStripMenuItem _menuCopyIp = new("复制 IP 地址");
    private readonly ToolStripMenuItem _menuKick = new("断开此访客");
    private readonly ToolTip _tip = new();

    private string _family = "v4";
    private bool _allowClose;

    public WebModeForm(WebHostService web, AppConfig config, Action onStateChanged)
    {
        _web = web;
        _config = config;
        _onStateChanged = onStateChanged;

        BuildUi();
        WireEvents();
        UpdateStatus();
        RefreshGuests();
    }

    private int D(int design) => (int)Math.Round(design * DeviceDpi / 96.0);

    private void Ui(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    // ---------------- 构建 ----------------

    private void BuildUi()
    {
        Text = "网页模式 · 让朋友用浏览器收文件";
        Size = new Size(720, 640);
        MinimumSize = new Size(620, 540);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(245, 246, 248);
        Font = new Font("微软雅黑", 9F);
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
            Padding = Padding.Empty, Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 250F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildAddressCard(), 0, 1);
        root.Controls.Add(BuildGuests(), 0, 2);
        root.Controls.Add(BuildFooter(), 0, 3);

        var scale = DeviceDpi / 96f;
        if (scale > 1.01f) Scale(new SizeF(scale, scale));
        root.PerformLayout();
    }

    private Control BuildHeader()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = Padding.Empty };
        var title = new Label
        {
            Text = "网页模式",
            Font = new Font("微软雅黑", 15F, FontStyle.Bold),
            ForeColor = Color.FromArgb(28, 32, 38),
            AutoSize = true
        };

        _status.AutoSize = true;
        _status.Font = new Font("微软雅黑", 9F);
        _status.ForeColor = Color.FromArgb(130, 138, 148);

        _toggleBtn.Text = "停止服务";
        _toggleBtn.AutoSize = true;
        _toggleBtn.FlatStyle = FlatStyle.Flat;
        _toggleBtn.FlatAppearance.BorderSize = 0;
        _toggleBtn.BackColor = Color.Transparent;
        _toggleBtn.ForeColor = Color.FromArgb(198, 40, 40);
        _toggleBtn.Font = new Font("微软雅黑", 9F);
        _toggleBtn.Cursor = Cursors.Hand;

        host.Controls.Add(title);
        host.Controls.Add(_status);
        host.Controls.Add(_toggleBtn);
        host.Controls.Add(new Panel
        {
            Dock = DockStyle.Bottom, Height = 1, BackColor = Color.FromArgb(224, 228, 233)
        });

        host.Layout += (_, _) =>
        {
            var padX = D(22);
            title.Location = new Point(padX, D(9));
            _status.Location = new Point(padX, title.Bottom + D(1));
            _toggleBtn.Location = new Point(
                Math.Max(padX, host.ClientSize.Width - padX - _toggleBtn.Width),
                D(20));
        };
        return host;
    }

    private Control BuildAddressCard()
    {
        var wrap = new Panel
        {
            Dock = DockStyle.Fill, BackColor = Color.FromArgb(245, 246, 248),
            Padding = new Padding(22, 12, 22, 12), Margin = Padding.Empty
        };

        var card = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            BackColor = Color.White, Margin = Padding.Empty, Padding = new Padding(16, 12, 16, 12)
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156F));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _qr.Dock = DockStyle.Fill;
        _qr.SizeMode = PictureBoxSizeMode.Zoom;
        _qr.BackColor = Color.White;
        _qr.BorderStyle = BorderStyle.FixedSingle;
        _qr.Margin = new Padding(0, 0, 14, 0);
        card.Controls.Add(_qr, 0, 0);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.White
        };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));   // 地址族
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));   // URL + 复制
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));   // 复制按钮
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));   // PIN
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 说明

        // 地址族分段控件
        var seg = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty
        };
        StyleSegment(_v4, "IPv4", true);
        StyleSegment(_v6, "IPv6", false);
        seg.Controls.Add(_v4);
        seg.Controls.Add(_v6);
        right.Controls.Add(seg, 0, 0);

        var urlRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _url.Dock = DockStyle.Fill;
        _url.Font = new Font("Consolas", 10F);
        _url.ForeColor = Color.FromArgb(30, 111, 217);
        _url.TextAlign = ContentAlignment.MiddleLeft;
        _url.AutoEllipsis = true;
        _url.Cursor = Cursors.Hand;
        urlRow.Controls.Add(_url, 0, 0);
        right.Controls.Add(urlRow, 0, 1);

        _copy.Text = "复制链接";
        _copy.AutoSize = true;
        _copy.FlatStyle = FlatStyle.Flat;
        _copy.FlatAppearance.BorderColor = Color.FromArgb(210, 217, 227);
        _copy.FlatAppearance.BorderSize = 1;
        _copy.BackColor = Color.White;
        _copy.ForeColor = Color.FromArgb(30, 111, 217);
        _copy.Font = new Font("微软雅黑", 8.5F);
        _copy.Padding = new Padding(10, 3, 10, 3);
        _copy.Cursor = Cursors.Hand;
        _copy.Anchor = AnchorStyles.Left;
        right.Controls.Add(_copy, 0, 2);

        var pinRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        pinRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pinRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        pinRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pinRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        pinRow.Controls.Add(new Label
        {
            Text = "访问 PIN", AutoSize = true, Anchor = AnchorStyles.Left,
            ForeColor = Color.FromArgb(130, 138, 148), Font = new Font("微软雅黑", 9F),
            Margin = new Padding(0, 0, 12, 0)
        }, 0, 0);

        _pin.Dock = DockStyle.Fill;
        _pin.Font = new Font("Consolas", 17F, FontStyle.Bold);
        _pin.ForeColor = Color.FromArgb(28, 32, 38);
        _pin.TextAlign = ContentAlignment.MiddleLeft;
        pinRow.Controls.Add(_pin, 1, 0);

        _pinRefresh.Text = "刷新 PIN";
        _pinRefresh.AutoSize = true;
        _pinRefresh.FlatStyle = FlatStyle.Flat;
        _pinRefresh.FlatAppearance.BorderSize = 0;
        _pinRefresh.BackColor = Color.Transparent;
        _pinRefresh.ForeColor = Color.FromArgb(30, 111, 217);
        _pinRefresh.Font = new Font("微软雅黑", 8.5F);
        _pinRefresh.Cursor = Cursors.Hand;
        _pinRefresh.Anchor = AnchorStyles.Right;
        pinRow.Controls.Add(_pinRefresh, 2, 0);
        right.Controls.Add(pinRow, 0, 3);

        right.Controls.Add(new Label
        {
            Text = "让朋友用浏览器扫上面的码，或手输地址 + PIN 加入。\r\n切换地址族只影响新扫码的人：PIN 不变、已在线的访客不受影响。",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(150, 156, 164),
            Font = new Font("微软雅黑", 8.5F),
            AutoSize = false
        }, 0, 4);

        card.Controls.Add(right, 1, 0);
        wrap.Controls.Add(card);
        return wrap;
    }

    /// <summary>分段按钮只改配色，字体复用同一个实例，避免每次刷新状态都新建 GDI 字体。</summary>
    private static readonly Font SegmentFont = new("微软雅黑", 9F);

    private static void StyleSegment(Button b, string text, bool on)
    {
        b.Text = text;
        b.AutoSize = false;
        b.Size = new Size(76, 26);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = on ? Color.FromArgb(30, 111, 217) : Color.FromArgb(214, 220, 228);
        b.BackColor = on ? Color.FromArgb(30, 111, 217) : Color.White;
        b.ForeColor = on ? Color.White : Color.FromArgb(110, 118, 128);
        b.Font = SegmentFont;
        b.Cursor = Cursors.Hand;
        b.Margin = new Padding(0, 0, 6, 0);
    }

    private Control BuildGuests()
    {
        var box = new Panel
        {
            Dock = DockStyle.Fill, BackColor = Color.White,
            Padding = new Padding(22, 8, 22, 10), Margin = new Padding(0, 0, 0, 0)
        };

        var head = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.White };
        _guestsTitle.Text = "在线访客";
        _guestsTitle.Font = new Font("微软雅黑", 11F, FontStyle.Bold);
        _guestsTitle.ForeColor = Color.FromArgb(40, 46, 54);
        _guestsTitle.AutoSize = true;

        _group.Text = "群发";
        _group.AutoSize = true;
        _group.FlatStyle = FlatStyle.Flat;
        _group.FlatAppearance.BorderSize = 0;
        _group.BackColor = Color.FromArgb(30, 111, 217);
        _group.ForeColor = Color.White;
        _group.Font = new Font("微软雅黑", 9F);
        _group.Padding = new Padding(14, 4, 14, 4);
        _group.Cursor = Cursors.Hand;

        head.Controls.Add(_guestsTitle);
        head.Controls.Add(_group);
        head.Layout += (_, _) =>
        {
            _guestsTitle.Location = new Point(0, D(10));
            _group.Location = new Point(Math.Max(0, head.ClientSize.Width - _group.Width), D(6));
        };

        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.BorderStyle = BorderStyle.None;
        _list.Dock = DockStyle.Fill;
        _list.BackColor = Color.White;
        _list.Font = new Font("微软雅黑", 9.5F);
        _list.ShowItemToolTips = true;
        _list.ContextMenuStrip = _menu;
        _list.Columns.Add("昵称", 140);
        _list.Columns.Add("IP 地址", 150);
        _list.Columns.Add("状态", 200);

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _menuSend, _menuCopyIp, new ToolStripSeparator(), _menuKick
        });
        _menu.Opening += Menu_Opening;

        _empty.Text = "还没有访客\r\n\r\n让朋友连你的热点或同一 WiFi，用浏览器打开上面的地址，输入 PIN 加入";
        _empty.TextAlign = ContentAlignment.MiddleCenter;
        _empty.ForeColor = Color.FromArgb(165, 170, 178);
        _empty.Font = new Font("微软雅黑", 9.5F);
        _empty.Dock = DockStyle.Fill;

        var listHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        listHost.Controls.Add(_list);
        listHost.Controls.Add(_empty);
        _empty.BringToFront();
        listHost.ContextMenuStrip = _menu;

        box.Controls.Add(listHost);
        box.Controls.Add(head);

        void FitColumns()
        {
            var w = Math.Max(_list.ClientSize.Width - 4, 240);
            var ipW = D(150);
            var stateW = D(200);
            _list.Columns[1].Width = ipW;
            _list.Columns[2].Width = Math.Max(D(120), Math.Min(stateW, w - D(140) - ipW));
            _list.Columns[0].Width = Math.Max(D(90), w - _list.Columns[1].Width - _list.Columns[2].Width);
        }
        _list.SizeChanged += (_, _) => FitColumns();
        FitColumns();

        return box;
    }

    private Control BuildFooter()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill, BackColor = Color.White,
            Padding = new Padding(22, 8, 22, 8), Margin = Padding.Empty
        };

        var hint = new Label
        {
            Text = "关掉这个窗口服务仍在运行 · 收文件时会弹确认框，与手机互传用的是同一套确认与落盘逻辑",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(150, 156, 164),
            Font = new Font("微软雅黑", 8.5F),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };

        _portBtn.Text = "改端口";
        _portBtn.AutoSize = true;
        _portBtn.FlatStyle = FlatStyle.Flat;
        _portBtn.FlatAppearance.BorderSize = 0;
        _portBtn.BackColor = Color.Transparent;
        _portBtn.ForeColor = Color.FromArgb(120, 128, 138);
        _portBtn.Font = new Font("微软雅黑", 8.5F);
        _portBtn.Cursor = Cursors.Hand;
        _portBtn.Dock = DockStyle.Right;

        host.Controls.Add(hint);
        host.Controls.Add(_portBtn);
        host.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(224, 228, 233) });
        return host;
    }

    private void WireEvents()
    {
        _toggleBtn.Click += (_, _) => ToggleService();
        _v4.Click += (_, _) => SetFamily("v4");
        _v6.Click += (_, _) => SetFamily("v6");
        _copy.Click += (_, _) => CopyUrl();
        _url.Click += (_, _) => CopyUrl();
        _pinRefresh.Click += (_, _) =>
        {
            _web.RefreshPin();
            _pin.Text = _web.Pin;
        };
        _portBtn.Click += (_, _) => ChangePort();
        _group.Click += (_, _) => GroupSendAsync().FireAndForget();
        _menuSend.Click += (_, _) => SendToSelectedAsync().FireAndForget();
        _menuCopyIp.Click += (_, _) =>
        {
            var guest = SelectedGuest();
            if (guest != null) Clipboard.SetText(guest.RemoteIp);
        };
        _menuKick.Click += (_, _) =>
        {
            var guest = SelectedGuest();
            if (guest != null) _web.DisconnectGuest(guest.Id);
        };
        _list.DoubleClick += (_, _) => SendToSelectedAsync().FireAndForget();

        _web.GuestsChanged += () => Ui(() =>
        {
            RefreshGuests();
            UpdateStatus();
        });

        FormClosing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>真正关闭（程序退出时由 MainForm 调用）。</summary>
    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    // ---------------- 状态与地址 ----------------

    private void UpdateStatus()
    {
        var running = _web.Running;
        _status.Text = running
            ? $"● 服务运行中 · 端口 {_web.Port} · 在线访客 {_web.SnapshotGuests().Count} 人"
            : "服务已停止（访客全部断开）";
        _status.ForeColor = running ? Color.FromArgb(32, 168, 72) : Color.FromArgb(150, 156, 164);
        _toggleBtn.Text = running ? "停止服务" : "启动服务";
        _toggleBtn.ForeColor = running ? Color.FromArgb(198, 40, 40) : Color.FromArgb(30, 111, 217);
        _pin.Text = _web.Pin;
        _pin.Enabled = running;
        _pinRefresh.Enabled = running;
        _copy.Enabled = running;
        _group.Enabled = running;

        var v6 = AppConfig.GetLocalIpV6Only();
        var v4 = AppConfig.GetLocalIpV4Only();
        _v6.Enabled = v6 != null;
        _v4.Enabled = v4 != null;

        // 当前选中的地址族取不到地址时自动回退，避免显示一个连不上的链接
        if (_family == "v6" && v6 == null) _family = v4 != null ? "v4" : "v6";
        if (_family == "v4" && v4 == null) _family = "v6";

        StyleSegment(_v4, "IPv4", _family == "v4");
        StyleSegment(_v6, "IPv6", _family == "v6");

        var url = CurrentUrl();
        _url.Text = url.Length > 0 ? url : "（本机没有可用地址）";
        _url.ForeColor = url.Length > 0 ? Color.FromArgb(30, 111, 217) : Color.FromArgb(150, 156, 164);
        var tip = url.Length > 0 ? url : "请检查网络连接";
        _url.Tag = url;
        _tip.SetToolTip(_url, tip);
        RenderQr(url);
        _onStateChanged();
    }

    private string CurrentUrl()
    {
        if (!_web.Running) return "";
        var ip = _family == "v6" ? AppConfig.GetLocalIpV6Only() : AppConfig.GetLocalIpV4Only();
        if (ip == null) return "";
        var host = ip.Contains(':') ? $"[{ip}]" : ip;
        return $"http://{host}:{_web.Port}/";
    }

    private void RenderQr(string url)
    {
        var old = _qr.Image;
        if (url.Length == 0)
        {
            _qr.Image = null;
            old?.Dispose();
            return;
        }
        var png = QrCode.RenderPng(url);
        if (png == null) return;
        using var ms = new MemoryStream(png);
        using var tmp = Image.FromStream(ms);
        _qr.Image = new Bitmap(tmp);
        old?.Dispose();
    }

    private void SetFamily(string family)
    {
        if (_family == family) return;
        _family = family;
        UpdateStatus();
    }

    private void CopyUrl()
    {
        if (_url.Tag is not string url || url.Length == 0) return;
        try { Clipboard.SetText(url); } catch { }
    }

    private void ToggleService()
    {
        if (_web.Running)
        {
            _web.Stop();
        }
        else
        {
            var (ok, msg) = _web.Start(_config.WebPort);
            if (!ok)
            {
                MessageBox.Show(this, msg, "网页模式", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _config.WebPort = _web.Port;
            _config.Save();
        }
        UpdateStatus();
        RefreshGuests();
    }

    private void ChangePort()
    {
        using var dlg = new InputDialog("修改端口", "网页模式端口（建议 8080–9000，改完会重启服务）：", _config.WebPort.ToString())
        { Owner = this };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (!int.TryParse(dlg.Value.Trim(), out var port) || port < 1024 || port > 65535)
        {
            MessageBox.Show(this, "端口需为 1024–65535 之间的数字", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var wasRunning = _web.Running;
        if (wasRunning) _web.Stop();
        _config.WebPort = port;
        _config.Save();
        if (wasRunning)
        {
            var (ok, msg) = _web.Start(port);
            if (!ok)
            {
                MessageBox.Show(this, msg, "网页模式", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (_web.Port != port)
            {
                _config.WebPort = _web.Port;
                _config.Save();
            }
        }
        UpdateStatus();
        RefreshGuests();
    }

    // ---------------- 访客列表 ----------------

    private WebGuest? SelectedGuest() =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as WebGuest : null;

    private void Menu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var guest = SelectedGuest();
        _menuSend.Enabled = guest != null;
        _menuCopyIp.Enabled = guest != null;
        _menuKick.Enabled = guest != null;
        _menuSend.Text = guest != null ? $"发送文件到 {guest.Nickname}…" : "发送文件（先选中访客）";
        _menuKick.Text = guest != null ? $"断开 {guest.Nickname}" : "断开此访客";
    }

    private void RefreshGuests()
    {
        var guests = _web.SnapshotGuests();
        var selectedId = SelectedGuest()?.Id;

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var g in guests)
            {
                var item = new ListViewItem(new[] { g.Nickname, g.RemoteIp, g.Status })
                {
                    Tag = g,
                    ToolTipText = $"{g.Nickname}\n{g.RemoteIp}\n加入于 {g.JoinedAt:HH:mm:ss}"
                };
                item.ForeColor = g.Status == "空闲"
                    ? Color.FromArgb(40, 46, 54)
                    : Color.FromArgb(30, 100, 180);
                _list.Items.Add(item);
                if (g.Id == selectedId) item.Selected = true;
            }
        }
        finally { _list.EndUpdate(); }

        _guestsTitle.Text = guests.Count > 0 ? $"在线访客 {guests.Count}" : "在线访客";
        _empty.Visible = _list.Items.Count == 0;
        _list.Visible = !_empty.Visible;
    }

    private async Task SendToSelectedAsync()
    {
        var guest = SelectedGuest();
        if (guest == null) return;
        if (!_web.Running)
        {
            MessageBox.Show(this, "服务已停止，先启动服务再发送", "网页模式", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = $"发送文件到 {guest.Nickname}",
            Filter = "所有文件|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await _web.SendFilesAsync(new[] { guest.Id }, dlg.FileNames);
    }

    private async Task GroupSendAsync()
    {
        var guests = _web.SnapshotGuests();
        if (guests.Count == 0)
        {
            MessageBox.Show(this, "还没有访客加入", "网页模式", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!_web.Running)
        {
            MessageBox.Show(this, "服务已停止，先启动服务再发送", "网页模式", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var pick = new GroupSendDialog(guests) { Owner = this };
        if (pick.ShowDialog(this) != DialogResult.OK || pick.SelectedIds.Count == 0) return;

        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = $"群发给 {pick.SelectedIds.Count} 位访客",
            Filter = "所有文件|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await _web.SendFilesAsync(pick.SelectedIds, dlg.FileNames);
    }
}

/// <summary>群发：多选访客（默认全不选，防误发），确认后再选文件。</summary>
internal sealed class GroupSendDialog : Form
{
    private readonly CheckedListBox _box = new();
    public List<string> SelectedIds { get; } = new();

    public GroupSendDialog(IReadOnlyList<WebGuest> guests)
    {
        Text = "群发文件";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;
        Font = new Font("微软雅黑", 9.5F);
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(360, 360);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Padding = new Padding(18), BackColor = Color.White
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));

        root.Controls.Add(new Label
        {
            Text = "选择要发送的访客（可多选）",
            AutoSize = true,
            ForeColor = Color.FromArgb(110, 118, 128),
            Margin = new Padding(0, 0, 0, 8)
        }, 0, 0);

        _box.Dock = DockStyle.Fill;
        _box.BorderStyle = BorderStyle.FixedSingle;
        _box.CheckOnClick = true;
        _box.Font = new Font("微软雅黑", 10F);
        foreach (var g in guests) _box.Items.Add(g, false);
        root.Controls.Add(_box, 0, 1);

        var btns = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false
        };
        var ok = new Button
        {
            Text = "下一步：选文件",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 111, 217),
            ForeColor = Color.White,
            Padding = new Padding(14, 6, 14, 6),
            Margin = new Padding(8, 8, 0, 0)
        };
        ok.FlatAppearance.BorderSize = 0;
        ok.Click += (_, _) =>
        {
            SelectedIds.Clear();
            foreach (var item in _box.CheckedItems)
                if (item is WebGuest g) SelectedIds.Add(g.Id);
            if (SelectedIds.Count == 0)
            {
                MessageBox.Show(this, "请至少选择一位访客", "群发", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };

        var cancel = new Button
        {
            Text = "取消",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(245, 246, 248),
            ForeColor = Color.FromArgb(70, 78, 88),
            Padding = new Padding(14, 6, 14, 6),
            Margin = new Padding(8, 8, 0, 0)
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(214, 220, 228);

        btns.Controls.Add(ok);
        btns.Controls.Add(cancel);
        root.Controls.Add(btns, 0, 2);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
