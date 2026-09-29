using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HaierWidget.Haier;
using HaierWidget.Util;

namespace HaierWidget.Login;

/// <summary>登录 / 账号管理窗口（WinForms，尽量贴近 Win11 风格）。</summary>
public sealed class LoginForm : Form
{
    /// <summary>左右留白与内容宽度：所有控件都按这两个值对齐，文字超长时靠固定宽度自动折行。</summary>
    private const int Pad = 30;
    private const int ContentWidth = 320;

    /// <summary>未登录时的按钮行 / 消息行 / 窗口高度，以及已登录时收起「密码」那一块省下的高度。</summary>
    private const int ButtonsTop = 278;
    private const int MessageTop = 322;
    private const int FormHeight = 386;
    private const int PasswordBlockHeight = 82;

    private readonly Label _title = new();
    private readonly Label _status = new();
    private readonly HintTextBox _phone = new();
    private readonly Label _pwdLabel = MakeLabel("密码", Pad, 180);
    private readonly HintTextBox _password = new();
    private readonly Button _login = new();
    private readonly Button _logout = new();
    private readonly Label _hint = new();
    private readonly Label _message = new();
    private bool _loggedIn;

    public LoginForm()
    {
        Text = "海尔智家小组件";
        // 面板是 TOPMOST 的，从小组件点“登录”进来时不置顶就会被它压住（面板自己不收起）
        TopMost = WidgetBoard.IsOpen();
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(Pad * 2 + ContentWidth, FormHeight);
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.White;
        TryLoadIcon();

        _title.Text = "登录海尔智家";
        _title.Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
        _title.AutoSize = true;
        _title.Location = new Point(Pad, 24);

        // 定宽 + AutoSize=false，登录状态那行文字才会在框内折行而不是顶出窗口
        _status.AutoSize = false;
        _status.Size = new Size(ContentWidth, 52);
        _status.ForeColor = Color.FromArgb(96, 96, 96);
        _status.Location = new Point(Pad, 58);

        var phoneLabel = MakeLabel("手机号", Pad, 120);
        _phone.Location = new Point(Pad, 142);
        _phone.Size = new Size(ContentWidth, 30);
        _phone.Hint = "11 位手机号";
        _phone.MaxLength = 20;

        _password.Location = new Point(Pad, 202);
        _password.Size = new Size(ContentWidth, 30);
        _password.UseSystemPasswordChar = true;
        _password.Hint = "海尔智家 App 登录密码";

        _hint.Text = "暂不支持短信验证码登录（海尔未公开该接口）。";
        _hint.AutoSize = false;
        _hint.Size = new Size(ContentWidth, 32);
        _hint.Font = new Font("Microsoft YaHei UI", 8.5F);
        _hint.ForeColor = Color.FromArgb(120, 120, 120);
        _hint.Location = new Point(Pad, 240);

        _login.Text = "登录";
        _login.Size = new Size(150, 36);
        _login.Location = new Point(Pad, ButtonsTop);
        _login.FlatStyle = FlatStyle.Flat;
        _login.FlatAppearance.BorderSize = 0;
        _login.Click += async (_, _) => await DoLoginAsync();

        _logout.Text = "退出登录";
        _logout.Size = new Size(150, 36);
        _logout.Location = new Point(Pad + ContentWidth - 150, ButtonsTop);
        _logout.FlatStyle = FlatStyle.Flat;
        _logout.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        _logout.Click += (_, _) => DoLogout();

        // 接口报错的文案可能很长：给三行的高度，再长就省略号，不撑破窗口
        _message.AutoSize = false;
        _message.Size = new Size(ContentWidth, 48);
        _message.Location = new Point(Pad, MessageTop);
        _message.AutoEllipsis = true;
        _message.ForeColor = Color.FromArgb(196, 43, 28);
        _message.Font = new Font("Microsoft YaHei UI", 8.5F);

        Controls.AddRange(new System.Windows.Forms.Control[]
        {
            _title, _status, phoneLabel, _phone, _pwdLabel, _password, _hint, _login, _logout, _message,
        });
        AcceptButton = _login;

        RefreshState();
    }

    private static Label MakeLabel(string text, int x, int y) => new()
    {
        Text = text,
        AutoSize = true,
        Location = new Point(x, y),
        ForeColor = Color.FromArgb(50, 50, 50),
    };

    private void TryLoadIcon()
    {
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(ico))
            {
                Icon = new Icon(ico);
            }
        }
        catch
        {
        }
    }

    private void RefreshState()
    {
        var session = HaierSession.Load(Paths.SessionFile);
        _loggedIn = session is not null;
        if (session is null)
        {
            _status.Text = "当前未登录。登录后即可在小组件面板添加“海尔智家设备”。";
        }
        else
        {
            _status.Text = $"已登录：{session.MaskedAccount}。要换账号请先退出登录。";
            _phone.Text = session.ClientId;
        }

        // 已登录就只剩「退出登录」可点：手机号只读、密码那一块整体收起，窗口跟着变矮
        _phone.ReadOnly = _loggedIn;
        _phone.BackColor = _loggedIn ? Color.FromArgb(246, 246, 246) : Color.White;
        _pwdLabel.Visible = !_loggedIn;
        _password.Visible = !_loggedIn;
        _hint.Visible = !_loggedIn;
        _password.Clear();

        // 只读的手机号框别拿焦点，否则一进来整串号码是选中反白的，像能改一样
        ActiveControl = _loggedIn ? _logout : _phone;

        var shift = _loggedIn ? PasswordBlockHeight : 0;
        _login.Top = ButtonsTop - shift;
        _logout.Top = ButtonsTop - shift;
        _message.Top = MessageTop - shift;
        ClientSize = new Size(Pad * 2 + ContentWidth, FormHeight - shift);

        SetBusy(false);
    }

    private async Task DoLoginAsync()
    {
        var phone = _phone.Text.Trim();
        var pwd = _password.Text;
        if (phone.Length == 0 || pwd.Length == 0)
        {
            _message.Text = "请输入手机号和密码。";
            return;
        }
        SetBusy(true, "正在登录…");
        var ok = false;
        try
        {
            var session = await HaierSession.LoginAsync(phone, pwd, Paths.SessionFile);
            _message.ForeColor = Color.FromArgb(16, 124, 16);
            _message.Text = $"登录成功：{session.MaskedAccount}。正在打开小组件面板…";
            _password.Clear();
            RefreshState();
            ok = true;
        }
        catch (Exception ex)
        {
            _message.ForeColor = Color.FromArgb(196, 43, 28);
            _message.Text = "登录失败：" + ex.Message;
            Log.Error("登录失败", ex);
        }
        finally
        {
            SetBusy(false);
        }

        if (ok && !IsDisposed && !Disposing)
        {
            // 登录成功：拉起小组件面板，自己让位并退出
            Hide();
            WidgetBoard.Open();
            await Task.Delay(200); // 给 shell 一点时间把面板拉起来，别赶在它前面退进程
            Close();
        }
    }

    private void DoLogout()
    {
        try
        {
            File.Delete(Paths.SessionFile);
        }
        catch (Exception ex)
        {
            _message.Text = "退出失败：" + ex.Message;
            return;
        }
        _message.ForeColor = Color.FromArgb(96, 96, 96);
        _message.Text = "已退出登录。";
        RefreshState();
    }

    private void SetBusy(bool busy, string? text = null)
    {
        // 已登录时「登录」一直是禁用的（要换账号先退出登录）
        var canLogin = !busy && !_loggedIn;
        _login.Enabled = canLogin;
        _login.BackColor = canLogin ? Color.FromArgb(0, 103, 192) : Color.FromArgb(230, 230, 230);
        _login.ForeColor = canLogin ? Color.White : Color.FromArgb(140, 140, 140);
        _logout.Enabled = !busy && _loggedIn;
        _phone.Enabled = !busy;
        _password.Enabled = !busy;
        UseWaitCursor = busy;
        if (text is not null)
        {
            _message.ForeColor = Color.FromArgb(96, 96, 96);
            _message.Text = text;
        }
    }

    /// <summary>
    /// 提示文字走原生 EM_SETCUEBANNER，由 edit 控件自己画。
    /// WinForms 的 TextBox.PlaceholderText 是在控件画完之后再补一笔（dotnet/winforms#4089），
    /// 鼠标划过触发重绘时提示文字会被擦掉再补上，看起来就是一直闪。
    /// </summary>
    private sealed class HintTextBox : TextBox
    {
        private const int EmSetCueBanner = 0x1501;
        private string _hint = "";

        public string Hint
        {
            get => _hint;
            set
            {
                _hint = value;
                if (IsHandleCreated)
                {
                    Apply();
                }
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Apply(); // 句柄被重建过（比如改 UseSystemPasswordChar）也要补回来
        }

        private void Apply() => SendMessage(Handle, EmSetCueBanner, IntPtr.Zero, _hint);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
    }
}
