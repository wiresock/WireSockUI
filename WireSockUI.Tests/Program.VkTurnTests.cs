using System;
using System.Collections.Generic;
using WireSockUI.VkTurn;

namespace WireSockUI.Tests
{
    internal static partial class Program
    {
        private const string VkProfile =
            "[Interface]\r\n" +
            "PrivateKey = AAAA\r\n" +
            "Address = 10.88.0.4/32\r\n" +
            "MTU = 1280\r\n" +
            "\r\n" +
            "[Peer]\r\n" +
            "PublicKey = BBBB\r\n" +
            "AllowedIPs = 0.0.0.0/0\r\n" +
            "Endpoint = 127.0.0.1:9000\r\n" +
            "#@ws:DisallowedApps = steam\r\n" +
            "\r\n" +
            "#@vk:Server = 45.196.97.204:57001\r\n" +
            "#@vk:Link = https://vk.com/call/join/AbCdEfGh123, IjKlMnOp456\r\n" +
            "#@vk:WrapKey = 00112233445566778899aabbccddeeff00112233445566778899AABBCCDDEEFF\r\n" +
            "#@vk:Streams = 6\r\n" +
            "#@vk:Udp = true\r\n" +
            "#@vk:Listen = 127.0.0.1:9100\r\n";

        internal static IEnumerable<KeyValuePair<string, Action>> VkTurnTests()
        {
            yield return new KeyValuePair<string, Action>("VK TURN settings parse profile directives", VkTurnSettingsParse);
            yield return new KeyValuePair<string, Action>("VK TURN ignores profiles without directives", VkTurnNoDirectives);
            yield return new KeyValuePair<string, Action>("VK TURN rejects invalid settings", VkTurnRejectsInvalid);
            yield return new KeyValuePair<string, Action>("VK TURN runtime config points WireSock at the core", VkTurnEffectiveConfig);
            yield return new KeyValuePair<string, Action>("VK TURN runtime config adds core exclusion when missing", VkTurnEffectiveConfigAddsExclusion);
            yield return new KeyValuePair<string, Action>("VK TURN respects AllowedApps allow-lists", VkTurnEffectiveConfigAllowList);
            yield return new KeyValuePair<string, Action>("VK TURN replaces user SOCKS5 settings", VkTurnEffectiveConfigReplacesUserSocks5);
            yield return new KeyValuePair<string, Action>("VK TURN core arguments", VkTurnCoreArguments);
            yield return new KeyValuePair<string, Action>("VK captcha token extraction", VkCaptchaTokenExtraction);
        }

        private static void VkTurnSettingsParse()
        {
            var s = VkTurnSettings.Parse(VkProfile);
            AssertTrue(s != null, "settings should be parsed");
            AssertEqual("45.196.97.204:57001", s.Server);
            AssertEqual(2, s.Links.Count);
            AssertEqual("https://vk.com/call/join/IjKlMnOp456", s.Links[1]);
            AssertEqual("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff", s.WrapKey);
            AssertEqual(6, s.Streams);
            AssertTrue(s.Udp, "udp");
            AssertTrue(s.ManualCaptchaOnly, "manual captcha is the default");
            AssertEqual("127.0.0.1:9100", s.Listen);
        }

        private static void VkTurnNoDirectives()
        {
            AssertTrue(VkTurnSettings.Parse("[Interface]\r\nPrivateKey = A\r\n") == null, "plain profile is not VK");
            AssertTrue(!VkTurnSettings.HasDirectives("# #@vk: in text\r\n"), "comment mentioning #@vk is not a directive");
        }

        private static void VkTurnRejectsInvalid()
        {
            AssertThrowsFormat(VkProfile + "#@vk:Streams = 0\r\n", "Streams");
            AssertThrowsFormat(VkProfile + "#@vk:WrapKey = xyz\r\n", "WrapKey");
            AssertThrowsFormat(VkProfile + "#@vk:Listen = 0.0.0.0:9000\r\n", "loopback");
            AssertThrowsFormat(VkProfile + "#@vk:Captcha = maybe\r\n", "Captcha");
            AssertThrowsFormat(VkProfile + "#@vk:Typo = 1\r\n", "Unknown");
            AssertThrowsFormat("#@vk:Link = AbCdEfGh123\r\n", "Server");
            AssertThrowsFormat("#@vk:Server = 1.2.3.4:5\r\n", "Link");
            AssertThrowsFormat("#@vk:Server = 1.2.3.4:5\r\n#@vk:Link = http://evil/\r\n", "Link");
        }

        private static void VkTurnEffectiveConfig()
        {
            var s = VkTurnSettings.Parse(VkProfile);
            var cfg = s.BuildEffectiveConfig(VkProfile);
            AssertTrue(cfg.Contains("Endpoint = 10.255.255.2:51820\r\n"), "endpoint is the fake peer behind the core's Wintun adapter: " + cfg);
            AssertTrue(!cfg.Contains("Endpoint = 127.0.0.1"), "loopback endpoint removed (WireSock rejects it)");
            AssertTrue(!cfg.Contains("Socks5Proxy"), "no SOCKS5 relay: " + cfg);
            AssertTrue(cfg.Contains("#@ws:DisallowedApps = steam, vkturn-core\r\n"), "core appended to DisallowedApps: " + cfg);
            AssertTrue(!cfg.Contains("#@vk:"), "VK secrets are not copied into the runtime config");
            AssertTrue(cfg.Contains("PrivateKey = AAAA"), "interface kept");
        }

        private static void VkTurnEffectiveConfigAddsExclusion()
        {
            var profile = VkProfile.Replace("#@ws:DisallowedApps = steam\r\n", string.Empty)
                .Replace("Endpoint = 127.0.0.1:9000\r\n", string.Empty);
            var cfg = VkTurnSettings.Parse(profile).BuildEffectiveConfig(profile);
            AssertTrue(cfg.Contains("Endpoint = 10.255.255.2:51820\r\n"), "missing endpoint added");
            AssertTrue(cfg.Contains("#@ws:DisallowedApps = vkturn-core\r\n"), "exclusion added: " + cfg);
        }

        private static void VkTurnEffectiveConfigReplacesUserSocks5()
        {
            var profile = VkProfile.Replace("#@ws:DisallowedApps = steam\r\n",
                "#@ws:Socks5Proxy = 10.0.0.1:1080\r\n#@ws:Socks5ProxyUsername = u\r\n#@ws:Socks5ProxyAllTraffic = false\r\n");
            var cfg = VkTurnSettings.Parse(profile).BuildEffectiveConfig(profile);
            AssertTrue(!cfg.Contains("10.0.0.1:1080") && !cfg.Contains("Socks5ProxyUsername") &&
                       !cfg.Contains("AllTraffic = false"), "user SOCKS5 settings replaced: " + cfg);
        }

        private static void VkTurnEffectiveConfigAllowList()
        {
            var profile = VkProfile.Replace("#@ws:DisallowedApps = steam", "#@ws:AllowedApps = telegram");
            var cfg = VkTurnSettings.Parse(profile).BuildEffectiveConfig(profile);
            AssertTrue(!cfg.Contains("vkturn-core"), "allow-list profiles already bypass the core");
            AssertTrue(cfg.Contains("#@ws:AllowedApps = telegram"), "allow-list kept");
        }

        private static void VkTurnCoreArguments()
        {
            var s = VkTurnSettings.Parse(VkProfile);
            var args = s.BuildCoreArguments(1, true, @"C:\ProgramData\Some Dir\creds.bin");
            AssertTrue(args.Contains("-peer 45.196.97.204:57001"), args);
            AssertTrue(args.Contains("-wintun 10.255.255.2:51820") && !args.Contains("-listen") && !args.Contains("-socks5"),
                "core serves WireSock through its Wintun adapter: " + args);
            AssertTrue(args.Contains("-vk-link https://vk.com/call/join/IjKlMnOp456"), "second link used: " + args);
            AssertTrue(args.Contains("-n 6") && args.Contains(" -udp") && args.Contains(" -wrap -wrap-key "), args);
            AssertTrue(args.Contains("-events-json") && args.Contains("-external-captcha") &&
                       args.Contains("-manual-captcha"), args);
            AssertTrue(args.Contains("-creds-cache \"C:\\ProgramData\\Some Dir\\creds.bin\""), "path quoted: " + args);
            AssertEqual("\"a \\\"b\\\"\"", VkTurnSettings.QuoteArgument("a \"b\""));
        }

        private static void VkCaptchaTokenExtraction()
        {
            AssertEqual("tok", VkCaptchaForm.ExtractSuccessToken("{\"response\":{\"success_token\":\"tok\",\"status\":\"OK\"}}"));
            AssertTrue(VkCaptchaForm.ExtractSuccessToken("{\"response\":{\"status\":\"BOT\"}}") == null, "no token");
            AssertTrue(VkCaptchaForm.ExtractSuccessToken("<html>") == null, "not json");
        }

        private static void AssertThrowsFormat(string profile, string messagePart)
        {
            try
            {
                VkTurnSettings.Parse(profile);
            }
            catch (FormatException ex)
            {
                AssertTrue(ex.Message.IndexOf(messagePart, StringComparison.OrdinalIgnoreCase) >= 0,
                    $"expected '{messagePart}' in: {ex.Message}");
                return;
            }

            throw new InvalidOperationException($"Expected FormatException mentioning '{messagePart}'.");
        }
    }
}
