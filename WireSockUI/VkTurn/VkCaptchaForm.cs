using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WireSockUI.VkTurn
{
    /// <summary>
    ///     Shows VK's "I'm not a robot" page in an embedded WebView2 (Edge) and captures
    ///     success_token from the captchaNotRobot.check API response. DialogResult is OK
    ///     and <see cref="SuccessToken" /> is set when the captcha was solved.
    /// </summary>
    internal sealed class VkCaptchaForm : Form
    {
        private const string CheckMethodMarker = "captchaNotRobot.check";

        private readonly string _captchaUrl;
        private readonly WebView2 _webView;
        private readonly Label _status;
        private bool _completed;

        public string SuccessToken { get; private set; }

        public VkCaptchaForm(string captchaUrl)
        {
            _captchaUrl = captchaUrl ?? throw new ArgumentNullException(nameof(captchaUrl));

            Text = "VK — подтвердите, что вы не робот";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(480, 640);
            MinimumSize = new Size(360, 480);
            ShowInTaskbar = true;
            TopMost = true;

            _status = new Label
            {
                Dock = DockStyle.Top,
                Height = 36,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "VK просит подтвердить, что вы не робот. Это нужно для подключения VPN."
            };
            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);
            Controls.Add(_status);

            Shown += async (s, e) => await InitializeAsync();
        }

        /// <summary>True when the Edge WebView2 runtime is installed (always on Windows 11, usually on 10).</summary>
        public static bool IsRuntimeAvailable()
        {
            try
            {
                return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch
            {
                return false;
            }
        }

        private async Task InitializeAsync()
        {
            try
            {
                // The app runs elevated from Program Files; WebView2 needs a writable profile folder.
                var userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "cronixWG", "WebView2");
                Directory.CreateDirectory(userData);
                var environment = await CoreWebView2Environment.CreateAsync(null, userData);
                await _webView.EnsureCoreWebView2Async(environment);

                var core = _webView.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.NewWindowRequested += (s, e) => e.Handled = true;
                core.WebResourceResponseReceived += OnWebResourceResponseReceived;
                core.Navigate(_captchaUrl);
            }
            catch (Exception ex)
            {
                _status.Text = "Не удалось открыть окно капчи: " + ex.Message;
            }
        }

        private async void OnWebResourceResponseReceived(object sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            if (_completed || e.Request.Uri.IndexOf(CheckMethodMarker, StringComparison.OrdinalIgnoreCase) < 0)
                return;

            try
            {
                using (var stream = await e.Response.GetContentAsync())
                {
                    if (stream == null) return;
                    string body;
                    using (var reader = new StreamReader(stream))
                        body = await reader.ReadToEndAsync();

                    var token = ExtractSuccessToken(body);
                    if (string.IsNullOrEmpty(token) || _completed)
                        return;

                    _completed = true;
                    SuccessToken = token;
                    _status.Text = "Готово, подключаемся…";
                    DialogResult = DialogResult.OK;
                    Close();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"VK captcha response could not be read: {ex.Message}");
            }
        }

        internal static string ExtractSuccessToken(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            try
            {
                var root = new JavaScriptSerializer().DeserializeObject(body) as System.Collections.Generic.Dictionary<string, object>;
                if (root != null && root.TryGetValue("response", out var resp) &&
                    resp is System.Collections.Generic.Dictionary<string, object> response &&
                    response.TryGetValue("success_token", out var token))
                    return token as string;
            }
            catch
            {
                // not JSON
            }

            return null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _webView?.Dispose();
            base.Dispose(disposing);
        }
    }
}
