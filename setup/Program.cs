using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

// BgFree installer: same steps as install.ps1, as a double-clickable exe.
// Close HDT -> download latest BgFree.dll -> register + enable in plugins.xml -> disable HDT's own Tier7 overlay -> relaunch HDT.
static class Program
{
    const string Repo = "Heinul/Hearthstone_Deck_Tracker_Battlegrounds_Plugin";

    static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Title = "BgFree 설치";
        try
        {
            Run().GetAwaiter().GetResult();
            Console.WriteLine();
            Console.WriteLine("설치 완료. HDT가 다시 시작됩니다. 전장 큐를 돌리면 오버레이가 표시됩니다.");
        }
        catch (Exception e)
        {
            Console.WriteLine();
            Console.WriteLine("실패: " + e.Message);
            Console.WriteLine("이 창 내용을 그대로 전달해 주세요.");
        }
        Console.WriteLine();
        Console.Write("아무 키나 누르면 닫힙니다...");
        try { Console.ReadKey(true); } catch (InvalidOperationException) { /* no console (redirected input) */ }
        return 0;
    }

    static async Task Run()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HearthstoneDeckTracker");
        var localApp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HearthstoneDeckTracker");

        var exe = Directory.Exists(localApp)
            ? Directory.GetDirectories(localApp, "app-*").OrderByDescending(d => d).Select(d => Path.Combine(d, "HearthstoneDeckTracker.exe")).FirstOrDefault(File.Exists)
            : null;
        if (exe == null)
            throw new Exception("Hearthstone Deck Tracker가 설치되어 있지 않습니다. https://hsreplay.net/downloads/ 에서 먼저 설치하고 한 번 실행하세요.");

        // 1. Close HDT (its loaded plugin copy is locked while it runs).
        var running = Process.GetProcessesByName("HearthstoneDeckTracker");
        if (running.Length > 0)
        {
            Console.WriteLine("HDT 종료 중...");
            foreach (var p in running) { try { p.CloseMainWindow(); } catch { } }
            foreach (var p in running) { if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } } }
            await Task.Delay(1000);
        }

        // 2. Latest BgFree.dll from GitHub releases.
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BgFree-setup");
        Console.WriteLine("최신 버전 확인 중...");
        var release = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        var tag = Regex.Match(release, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
        var url = Regex.Match(release, "\"browser_download_url\"\\s*:\\s*\"([^\"]+/BgFree\\.dll)\"").Groups[1].Value;
        if (url == "") throw new Exception("릴리스에 BgFree.dll이 없습니다.");
        var pluginDir = Path.Combine(appData, "Plugins", "BgFree");
        Directory.CreateDirectory(pluginDir);
        Console.WriteLine($"BgFree {tag} 다운로드 중...");
        File.WriteAllBytes(Path.Combine(pluginDir, "BgFree.dll"), await http.GetByteArrayAsync(url));

        // 3. Register + enable in plugins.xml.
        var pluginsXml = Path.Combine(appData, "plugins.xml");
        XNamespace xsd = "http://www.w3.org/2001/XMLSchema", xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var doc = File.Exists(pluginsXml)
            ? XDocument.Load(pluginsXml)
            : new XDocument(new XElement("ArrayOfPluginSettings", new XAttribute(XNamespace.Xmlns + "xsd", xsd), new XAttribute(XNamespace.Xmlns + "xsi", xsi)));
        var root = doc.Root ?? throw new Exception("plugins.xml 형식을 읽을 수 없습니다.");
        var entry = root.Elements("PluginSettings").FirstOrDefault(e => (string?)e.Element("FileName") == "Plugins/BgFree/BgFree.dll");
        if (entry == null)
            root.Add(new XElement("PluginSettings", new XElement("FileName", "Plugins/BgFree/BgFree.dll"), new XElement("IsEnabled", "true"), new XElement("Name", "BgFree")));
        else
            entry.SetElementValue("IsEnabled", "true");
        doc.Save(pluginsXml);
        Console.WriteLine("플러그인 활성화 등록 완료.");

        // 4. Turn off HDT's own Tier7 hero-picking overlay so the two do not overlap.
        var configXml = Path.Combine(appData, "config.xml");
        if (File.Exists(configXml))
        {
            var cfg = File.ReadAllText(configXml);
            var patched = Regex.Replace(cfg, "<EnableBattlegroundsTier7Overlay>[^<]*</EnableBattlegroundsTier7Overlay>", "<EnableBattlegroundsTier7Overlay>false</EnableBattlegroundsTier7Overlay>");
            if (patched != cfg) File.WriteAllText(configXml, patched);
        }

        // 5. Relaunch HDT.
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }
}
