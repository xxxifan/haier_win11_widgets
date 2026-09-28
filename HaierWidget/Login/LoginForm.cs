using System.Drawing;
using System.Windows.Forms;
using HaierWidget.Haier;
using HaierWidget.Util;

namespace HaierWidget.Login;

/// <summary>登录 / 账号管理窗口（WinForms，尽量贴近 Win11 风格）。</summary>
public sealed class LoginForm : Form
{
    private readonly Label _title = new();
    private readonly Label _status = new();
    private readonly TextBox _phone = new();
    private readonly TextBox _password = new();
    private readonly Button _login = new();
    private readonly Button _logout = new();
    private readonly Label _hint = new();
    private readonly Label _message = new();

    public LoginForm()
    {
        Text = "海尔智家小组件";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(380, 340);
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.White;
        TryLoadIcon();

        _title.Text = "登录海尔智家";
        _title.Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
        _title.AutoSize = true;
        _title.Location = new Point(28, 24);

        _status.AutoSize = true;
        _status.ForeColor = Color.FromArgb(96, 96, 96);
        _status.Location = new Point(30, 62);

        var phoneLabel = MakeLabel("手机号", 30, 100);
        _phone.Location = new Point(30, 122);
        _phone.Size = new Size(320, 30);
        _phone.PlaceholderText = "11 位手机号";
        _phone.MaxLength = 20;

        var pwdLabel = MakeLabel("密码", 30, 162);
        _password.Location = new Point(30, 184);
        _password.Size = new Size(320, 30);
        _password.UseSystemPasswordChar = true;
        _password.PlaceholderText = "海尔智家 App 登录密码";

        _hint.Text = "暂不支持短信验证码登录（海尔未公开该接口）。";
        _hint.AutoSize = true;
        _hint.Font = new Font("Microsoft YaHei UI", 8.5F);
        _hint.ForeColor = Color.FromArgb(120, 120, 120);
        _hint.Location = new Point(30, 220);

        _login.Text = "登录";
        _login.Size = new Size(150, 36);
        _login.Location = new Point(30, 256);
        _login.FlatStyle = FlatStyle.Flat;
        _login.FlatAppearance.BorderSize = 0;
        _login.BackColor = Color.FromArgb(0, 103, 192);
        _login.ForeColor = Color.White;
        _login.Click += async (_, _) => await DoLoginAsync();

        _logout.Text = "退出登录";
        _logout.Size = new Size(150, 36);
        _logout.Location = new Point(200, 256);
        _logout.FlatStyle = FlatStyle.Flat;
        _logout.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        _logout.Click += (_, _) => DoLogout();

        _message.AutoSize = false;
        _message.Size = new Size(320, 36);
        _message.Location = new Point(30, 298);
        _message.ForeColor = Color.FromArgb(196, 43, 28);
        _message.Font = new Font("Microsoft YaHei UI", 8.5F);

        Controls.AddRange(new System.Windows.Forms.Control[]
        {
            _title, _status, phoneLabel, _phone, pwdLabel, _password, _hint, _login, _logout, _message,
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
        if (session is null)
        {
            _status.Text = "当前未登录。登录后即可在小组件面板添加“海尔智家设备”。";
            _logout.Enabled = false;
        }
        else
        {
            _status.Text = $"已登录：{session.MaskedAccount}（重新登录可切换账号）";
            _logout.Enabled = true;
            _phone.Text = session.ClientId;
        }
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
        try
        {
            var session = await HaierSession.LoginAsync(phone, pwd, Paths.SessionFile);
            _message.ForeColor = Color.FromArgb(16, 124, 16);
            _message.Text = $"登录成功：{session.MaskedAccount}。现在可以去小组件面板添加设备了。";
            _password.Clear();
            RefreshState();
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
        _login.Enabled = !busy;
        _logout.Enabled = !busy && File.Exists(Paths.SessionFile);
        _phone.Enabled = !busy;
        _password.Enabled = !busy;
        UseWaitCursor = busy;
        if (text is not null)
        {
            _message.ForeColor = Color.FromArgb(96, 96, 96);
            _message.Text = text;
        }
    }
}
