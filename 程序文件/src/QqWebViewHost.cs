using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HarmonicaStudio
{
    // Optional SDK loading keeps anonymous song search independent of the browser runtime.
    internal sealed class QqWebViewHost : IDisposable
    {
        private readonly Panel container;
        private readonly Action<string> status;
        private readonly List<Control> views = new List<Control>();
        private object environment, activeCore;
        private Type viewType;
        private bool disposed;
        private static IntPtr nativeLoader;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string filename);

        internal QqWebViewHost(Panel container, Action<string> status)
        { this.container = container; this.status = status; container.Resize += delegate { FitOfficialPage(); }; }
        internal bool IsReady { get { return !disposed && activeCore != null; } }

        internal async Task InitializeAsync()
        {
            string sdk = Path.Combine(AppPaths.ContentDirectory, "runtime", "webview2");
            string coreFile = Path.Combine(sdk, "Microsoft.Web.WebView2.Core.dll");
            string controlFile = Path.Combine(sdk, "Microsoft.Web.WebView2.WinForms.dll");
            string nativeFile = Path.Combine(sdk, "runtimes", IntPtr.Size == 8 ? "win-x64" : "win-x86", "native", "WebView2Loader.dll");
            if (!File.Exists(coreFile) || !File.Exists(controlFile) || !File.Exists(nativeFile))
                throw new FileNotFoundException("软件内登录组件缺失，请重新复制完整软件。");
            if (nativeLoader == IntPtr.Zero) nativeLoader = LoadLibrary(nativeFile);
            if (nativeLoader == IntPtr.Zero) throw new InvalidOperationException("软件内登录组件无法加载。");
            Assembly coreAssembly = Assembly.LoadFrom(coreFile), formsAssembly = Assembly.LoadFrom(controlFile);
            Type environmentType = coreAssembly.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Environment", true);
            viewType = formsAssembly.GetType("Microsoft.Web.WebView2.WinForms.WebView2", true);
            Directory.CreateDirectory(QqAccountStore.ProfileDirectory);
            object result = environmentType.GetMethod("CreateAsync").Invoke(null, new object[] { null, QqAccountStore.ProfileDirectory, null });
            await (Task)result;
            if (disposed) return;
            environment = result.GetType().GetProperty("Result").GetValue(result, null);
            object core = await CreateViewAsync();
            if (disposed) return;
            if (File.Exists(QqAccountStore.LogoutMarker))
            {
                await ClearBrowsingAsync(core);
                if (disposed) return;
                File.Delete(QqAccountStore.LogoutMarker);
            }
            activeCore = core;
            Navigate(QqMusicCatalog.OfficialLoginUrl);
        }

        private async Task<object> CreateViewAsync()
        {
            var view = (Control)Activator.CreateInstance(viewType);
            views.Add(view); view.Dock = DockStyle.Fill; container.Controls.Add(view); view.BringToFront();
            object task = viewType.GetMethod("EnsureCoreWebView2Async", new Type[] { environment.GetType() }).Invoke(view, new object[] { environment });
            await (Task)task;
            if (disposed) return null;
            object core = Property(view, "CoreWebView2");
            FitOfficialPage();
            object settings = Property(core, "Settings");
            SetOptional(settings, "AreDevToolsEnabled", false);
            SetOptional(settings, "IsStatusBarEnabled", false);
            SetOptional(settings, "AreDefaultContextMenusEnabled", false);
            object profile = Property(core, "Profile");
            SetOptional(profile, "IsPasswordAutosaveEnabled", false);
            SetOptional(profile, "IsGeneralAutofillEnabled", false);
            Bind(core, "NavigationStarting", NavigationStarting);
            Bind(core, "NavigationCompleted", delegate(object sender, object args)
            {
                if (disposed) return;
                bool success = (bool)Property(args, "IsSuccess");
                status(success ? "在下方官方页面点“登录”，选择 QQ 或微信。完成后点“使用此账号”。" :
                    "官方页面加载失败。请检查网络后点“重新打开官方页”。");
            });
            Bind(core, "NewWindowRequested", NewWindowRequested);
            Bind(core, "WindowCloseRequested", delegate(object sender, object args)
            {
                if (disposed || views.Count <= 1) return;
                Control popup = views[views.Count - 1]; views.RemoveAt(views.Count - 1); popup.Dispose();
                Control original = views[views.Count - 1]; original.Visible = true; original.BringToFront();
                activeCore = Property(original, "CoreWebView2");
            });
            return core;
        }

        internal static bool IsOfficialNavigation(string address)
        {
            Uri uri;
            if (!Uri.TryCreate(address, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !uri.IsDefaultPort || !String.IsNullOrEmpty(uri.UserInfo)) return false;
            string host = uri.Host;
            return host == "y.qq.com" || host.EndsWith(".y.qq.com", StringComparison.OrdinalIgnoreCase) ||
                host == "graph.qq.com" || host == "ptlogin2.qq.com" || host.EndsWith(".ptlogin2.qq.com", StringComparison.OrdinalIgnoreCase) ||
                host == "ptlogin2.graph.qq.com" || host.EndsWith(".ptlogin2.graph.qq.com", StringComparison.OrdinalIgnoreCase) ||
                host == "open.weixin.qq.com" || host == "wx.qq.com" || host == "ssl.captcha.qq.com" || host == "t.captcha.qq.com";
        }
        private void NavigationStarting(object sender, object args)
        {
            if (disposed || !IsOfficialNavigation(Convert.ToString(Property(args, "Uri"))))
            { SetOptional(args, "Cancel", true); if (!disposed) status("登录窗口仅打开 QQ 音乐及其官方账号授权页；请使用页面中的扫码登录。"); }
        }
        private async void NewWindowRequested(object sender, object args)
        {
            object deferral = null;
            try
            {
                SetOptional(args, "Handled", true);
                if (disposed || !IsOfficialNavigation(Convert.ToString(Property(args, "Uri")))) return;
                deferral = args.GetType().GetMethod("GetDeferral").Invoke(args, null);
                if (views.Count >= 3) { status("请先完成当前官方授权页面。关闭后可以重新登录。"); return; }
                Control previous = views.Count == 0 ? null : views[views.Count - 1];
                object popup = await CreateViewAsync();
                if (disposed || popup == null) return;
                args.GetType().GetProperty("NewWindow").SetValue(args, popup, null);
                activeCore = popup;
                if (previous != null) previous.Visible = false;
            }
            catch (Exception) { if (!disposed) status("官方授权页无法打开，请重新打开官方页再试。"); }
            finally { if (deferral != null) deferral.GetType().GetMethod("Complete").Invoke(deferral, null); }
        }
        internal void Navigate(string address)
        {
            if (!IsReady || !IsOfficialNavigation(address)) return;
            activeCore.GetType().GetMethod("Navigate").Invoke(activeCore, new object[] { address });
        }
        private void FitOfficialPage()
        {
            if (disposed) return;
            double zoom = Math.Max(0.5, Math.Min(1.0, Math.Min(container.ClientSize.Width / 1280.0, container.ClientSize.Height / 580.0)));
            foreach (Control view in views) if (!view.IsDisposed && Property(view, "CoreWebView2") != null) SetOptional(view, "ZoomFactor", zoom);
        }
        internal async Task<bool> OpenOfficialLoginAsync()
        {
            if (!IsReady || views.Count == 0) return false;
            object core = Property(views[0], "CoreWebView2");
            Uri source;
            if (!Uri.TryCreate(Convert.ToString(Property(core, "Source")), UriKind.Absolute, out source) || source.Host != "y.qq.com") return false;
            // Trigger only the website's existing, currently verified login link.
            // No custom OAuth address, partner app ID or password fields are used.
            const string script = "(()=>{const e=Array.from(document.querySelectorAll('.top_login__link')).find(x=>x.textContent.trim()==='登录');if(!e)return false;e.click();return true;})()";
            object task = core.GetType().GetMethod("ExecuteScriptAsync").Invoke(core, new object[] { script });
            await (Task)task;
            if (disposed) return false;
            return String.Equals(Convert.ToString(task.GetType().GetProperty("Result").GetValue(task, null)), "true", StringComparison.Ordinal);
        }
        internal async Task<List<QqMusicCookie>> GetMusicCookiesAsync()
        {
            var result = new List<QqMusicCookie>();
            if (!IsReady) return result;
            object manager = Property(activeCore, "CookieManager");
            object task = manager.GetType().GetMethod("GetCookiesAsync").Invoke(manager, new object[] { "https://y.qq.com/" });
            await (Task)task;
            if (disposed) return result;
            IEnumerable cookies = task.GetType().GetProperty("Result").GetValue(task, null) as IEnumerable;
            if (cookies != null) foreach (object cookie in cookies)
                result.Add(new QqMusicCookie { Name = Convert.ToString(Property(cookie, "Name")), Value = Convert.ToString(Property(cookie, "Value")),
                    Domain = Convert.ToString(Property(cookie, "Domain")), ExpiresUtc = ((DateTime)Property(cookie, "Expires")).ToUniversalTime(),
                    IsSession = (bool)Property(cookie, "IsSession") });
            return result;
        }
        internal async Task ClearProfileAsync()
        {
            if (!IsReady) return;
            await ClearBrowsingAsync(activeCore);
            if (disposed) return;
            if (File.Exists(QqAccountStore.LogoutMarker)) File.Delete(QqAccountStore.LogoutMarker);
            if (views.Count > 0)
            {
                while (views.Count > 1) { Control last = views[views.Count - 1]; views.RemoveAt(views.Count - 1); last.Dispose(); }
                views[0].Visible = true; activeCore = Property(views[0], "CoreWebView2");
            }
            Navigate(QqMusicCatalog.OfficialLoginUrl);
        }
        private static async Task ClearBrowsingAsync(object core)
        {
            object cookies = Property(core, "CookieManager");
            cookies.GetType().GetMethod("DeleteAllCookies").Invoke(cookies, null);
            object profile = Property(core, "Profile");
            await (Task)profile.GetType().GetMethod("ClearBrowsingDataAsync", Type.EmptyTypes).Invoke(profile, null);
        }
        private static object Property(object target, string name)
        { return target.GetType().GetProperty(name).GetValue(target, null); }
        private static void SetOptional(object target, string name, object value)
        { PropertyInfo property = target.GetType().GetProperty(name); if (property != null && property.CanWrite) property.SetValue(target, value, null); }
        private static void Bind(object target, string name, Action<object, object> callback)
        {
            EventInfo info = target.GetType().GetEvent(name);
            if (info == null) return;
            ParameterInfo[] parameters = info.EventHandlerType.GetMethod("Invoke").GetParameters();
            var sender = Expression.Parameter(parameters[0].ParameterType, "sender");
            var args = Expression.Parameter(parameters[1].ParameterType, "args");
            Delegate handler = Expression.Lambda(info.EventHandlerType,
                Expression.Invoke(Expression.Constant(callback), Expression.Convert(sender, typeof(object)), Expression.Convert(args, typeof(object))), sender, args).Compile();
            info.AddEventHandler(target, handler);
        }
        public void Dispose()
        {
            disposed = true; activeCore = null;
            foreach (Control view in views) view.Dispose(); views.Clear();
        }
    }
}
