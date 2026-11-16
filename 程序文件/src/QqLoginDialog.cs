using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HarmonicaStudio
{
    internal sealed class QqLoginDialog : Form
    {
        private readonly Label status;
        private readonly SoftButton useAccount, reopen, logout, installBrowser, openLogin;
        private readonly QqWebViewHost browser;
        private bool closing, working;
        internal QqLoginDialog()
        {
            Text = "QQ 音乐 · 软件内官方登录";
            AutoScaleMode = AutoScaleMode.None; ClientSize = new Size(880, 660); MinimumSize = new Size(700, 560);
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            MaximizeBox = MinimizeBox = false; Font = Theme.PixelFont(9); BackColor = Theme.Background; ForeColor = Theme.Text;
            var title = new Label { Text = "在 QQ 音乐官方页面登录 QQ 或微信", Font = Theme.PixelFont(14),
                Location = new Point(18, 12), Size = new Size(840, 30), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            status = new Label { Text = "正在打开官方页面…", Location = new Point(18, 47), Size = new Size(840, 52),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, ForeColor = Theme.Muted, UseMnemonic = false };
            var page = new Panel { Location = new Point(18, 105), Size = new Size(844, 440), BackColor = Theme.Input,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            var notice = new Label { Text = "密码和扫码均在官方页面操作。仅保存本软件的 QQ 音乐会话，并使用 Windows 加密。\n账号通过官方验证后才生效；仅转换平台提供的完整普通音频，会员或加密歌曲仍可能受限。",
                Location = new Point(18, 555), Size = new Size(844, 43), ForeColor = Theme.Muted, UseMnemonic = false,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            useAccount = MakeButton("使用此账号", 685, true); useAccount.Width = 177;
            reopen = MakeButton("重新打开官方页", 18, false); reopen.Width = 158;
            openLogin = MakeButton("打开官方登录", 186, false); openLogin.Width = 140;
            logout = MakeButton("退出登录", 336, false); logout.Width = 108;
            installBrowser = MakeButton("安装 WebView2 ↗", 18, false); installBrowser.Width = 177; installBrowser.Visible = false;
            useAccount.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.AddRange(new Control[] { title, status, page, notice, useAccount, reopen, logout, installBrowser, openLogin });
            browser = new QqWebViewHost(page, SetStatus);
            useAccount.Enabled = reopen.Enabled = openLogin.Enabled = false;
            useAccount.Click += async delegate { await ConfirmAccountAsync(); };
            reopen.Click += delegate { browser.Navigate(QqMusicCatalog.OfficialLoginUrl); };
            openLogin.Click += async delegate
            {
                if (working || closing || !browser.IsReady) return;
                SetWorking(true);
                try
                {
                    if (await browser.OpenOfficialLoginAsync()) SetStatus("在官方登录面板选择“QQ 登录”或“微信登录”，完成后点“使用此账号”。");
                    else SetStatus("官方登录入口尚未加载，或已登录。请稍候，已登录时可直接点“使用此账号”。");
                }
                catch (Exception) { SetStatus("官方登录入口暂不可用，请点“重新打开官方页”后重试。"); }
                finally { SetWorking(false); }
            };
            logout.Click += async delegate { await SignOutAsync(); };
            installBrowser.Click += delegate
            {
                try { Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true }); }
                catch (Exception) { SetStatus("无法打开浏览器，请访问微软 WebView2 官网安装 Evergreen Runtime。"); }
            };
            Shown += async delegate
            {
                try
                {
                    await browser.InitializeAsync();
                    if (!closing) { useAccount.Enabled = reopen.Enabled = openLogin.Enabled = browser.IsReady; installBrowser.Visible = false; }
                }
                catch (Exception error)
                {
                    if (closing) return;
                    SetStatus(error is FileNotFoundException ? "软件内登录组件缺失，请重新复制完整软件文件夹。匿名搜歌仍可用。" :
                        "官方登录页面无法启动。请确认已安装微软 Edge WebView2 Runtime，再重新打开此窗口。匿名搜歌仍可用。");
                    installBrowser.Visible = true; reopen.Visible = openLogin.Visible = false;
                }
            };
            FormClosing += delegate { closing = true; };
            FormClosed += delegate { browser.Dispose(); };
        }
        private SoftButton MakeButton(string text, int x, bool primary)
        { return new SoftButton(text, primary) { Location = new Point(x, 610), Anchor = AnchorStyles.Bottom | AnchorStyles.Left }; }
        private void SetStatus(string message)
        { if (!closing && !IsDisposed && !Disposing) status.Text = message; }
        private void SetWorking(bool value)
        {
            working = value;
            if (closing) return;
            useAccount.Enabled = reopen.Enabled = openLogin.Enabled = !value && browser.IsReady; logout.Enabled = !value;
        }
        private async Task ConfirmAccountAsync()
        {
            if (working || closing || !browser.IsReady) return;
            SetWorking(true); SetStatus("正在向 QQ 音乐确认当前账号；不会读取密码…");
            try
            {
                QqAccountSession session = QqAccountSession.FromBrowserCookies(await browser.GetMusicCookiesAsync());
                if (closing) return;
                if (session == null) { SetStatus(AccountConfirmationMessage(null)); return; }
                bool accepted = await Task.Run(delegate { return QqMusicCatalog.ValidateAccount(session); });
                if (closing) return;
                if (!accepted) { SetStatus(AccountConfirmationMessage(session)); return; }
                QqAccountStore.Set(session);
                DialogResult = DialogResult.OK;
            }
            catch (UnauthorizedAccessException) { SetStatus("会话保存目录没有写入权限。请将完整软件移到可写文件夹后重试。"); }
            catch (Exception error) { SetStatus(AccountConfirmationExceptionMessage(error)); }
            finally { SetWorking(false); }
        }
        internal static string AccountConfirmationMessage(QqAccountSession session)
        {
            if (session == null)
                return "本软件尚未取得可确认的 QQ 音乐会话。若官方页已显示账号头像，请稍候再点“使用此账号”；否则先完成官方登录。";
            switch (session.ValidationFailureKind)
            {
                case "Expired":
                    return "QQ 音乐会话已失效。请在下方官方页面重新登录，再点“使用此账号”确认；匿名搜索仍可用。";
                case "Network":
                    return "已读取网页音乐会话，但账号确认因网络中断或超时未完成。请检查网络后重试；当前页面会保留。";
                case "Format":
                    return "已读取网页音乐会话，但账号接口的响应格式暂不兼容。当前页面会保留，请稍候重试；匿名搜索仍可用。";
                case "Identity":
                    return "官方返回的账号身份与当前网页会话不一致，暂未启用此账号。请在官方页面退出后重新登录，再点“使用此账号”。";
                case "Permission":
                    return "已读取网页音乐会话，但 QQ 音乐账号接口未接受此会话。请稍候重试；当前页面会保留，匿名搜索仍可用。";
            }
            return "已读取官方页面的音乐会话，但 QQ 音乐账号接口尚未确认。请稍候重试；当前页面会保留，匿名搜索仍可用。";
        }
        internal static string AccountConfirmationExceptionMessage(Exception error)
        {
            if (error is WebException || error is TimeoutException)
                return "账号确认因网络中断或超时未完成。请检查网络后重试；当前官方页面会保留。";
            if (error is InvalidDataException || error is FormatException)
                return "账号确认接口的响应格式暂不兼容。请稍候重试；当前官方页面会保留。";
            return "当前网页账号尚未在本软件中完成确认。请稍候重试；当前官方页面会保留，匿名搜索仍可用。";
        }
        private async Task SignOutAsync()
        {
            if (working || closing) return;
            SetWorking(true);
            try
            {
                QqAccountStore.Forget();
                await browser.ClearProfileAsync();
                SetStatus("已退出本软件的 QQ 音乐账号，并清除本软件的登录记录。可在官方页面重新登录。");
            }
            catch (Exception) { SetStatus("账号已停用。登录记录清理未完成，下次打开此窗口会继续清理。"); }
            finally { SetWorking(false); }
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { closing = true; if (browser != null) browser.Dispose(); } base.Dispose(disposing); }
    }
}
