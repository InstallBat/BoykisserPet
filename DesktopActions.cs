using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BoykisserPet;

/// <summary>Explicit, local-only desktop actions requested through the pet chat.</summary>
internal static class DesktopActions
{
    private static readonly object ForegroundLock = new();
    private static (string ProcessName, string WindowTitle) _lastExternalApp = (string.Empty, string.Empty);

    public static void ObserveForeground(string processName, string title)
    {
        if (string.IsNullOrWhiteSpace(processName) || processName.Equals("BoykisserPet", StringComparison.OrdinalIgnoreCase)) return;
        lock (ForegroundLock) _lastExternalApp = (processName, title);
    }

    public static (string ProcessName, string WindowTitle) LastForegroundApp
    {
        get { lock (ForegroundLock) return _lastExternalApp; }
    }

    public static bool TryHandle(string input, out string reply)
    {
        string text = input.Trim().ToLowerInvariant();
        reply = string.Empty;
        if (text.Length == 0) return false;

        // Safe single-operation calculator.
        var calculation = System.Text.RegularExpressions.Regex.Match(input,
            @"(?<![\w.])(-?\d+(?:[.,]\d+)?)\s*([+\-*/:])\s*(-?\d+(?:[.,]\d+)?)(?![\w.])");
        if (calculation.Success &&
            double.TryParse(calculation.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double left) &&
            double.TryParse(calculation.Groups[3].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double right))
        {
            char op = calculation.Groups[2].Value[0];
            if ((op == '/' || op == ':') && right == 0)
            { reply = "На ноль делить нельзя, даже если очень хочется."; return true; }
            double result = op switch { '+' => left + right, '-' => left - right, '*' => left * right, '/' or ':' => left / right, _ => double.NaN };
            if (double.IsFinite(result))
            {
                string a = FormatNumber(left), b = FormatNumber(right), r = FormatNumber(result);
                string action = op switch { '+' => "прибавить", '-' => "вычесть", '*' => "умножить на", '/' or ':' => "разделить на", _ => "посчитать" };
                reply = $"{a} {action} {b} будет {r}."; return true;
            }
        }

        if (ContainsAny(text, "отмени выключение", "отмена выключения", "cancel shutdown", "abort shutdown"))
        {
            Run("shutdown.exe", "/a"); reply = "Отменил запланированное выключение, если оно было."; return true;
        }

        // Flexible natural-language intent: verbs need not be at the beginning of the sentence.
        bool shutdownIntent = ContainsWord(text, "выключи", "выключить", "выруби", "вырубить", "shutdown", "shut", "turnoff", "poweroff") &&
            ContainsAny(text, "компьютер", "комп", "пк", "computer", "pc", "laptop", "ноутбук", "систему");
        if (shutdownIntent)
        {
            if (MessageBox.Show("Выключить компьютер через 30 секунд? Можно отменить командой «отмени выключение».", "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                reply = "Окей, выключение отменено.";
            else { Run("shutdown.exe", "/s /t 30"); reply = "Компьютер выключится через 30 секунд. Скажи «отмени выключение», если передумал."; }
            return true;
        }

        if (ContainsWord(text, "громкость", "звук", "volume", "loudness"))
        {
            int? percent = ParsePercentNearKeyword(text, "громкость", "звук", "volume", "loudness");
            if (percent.HasValue)
            {
                try { SetVolume(percent.Value / 100f); reply = $"Громкость выставлена примерно на {percent.Value}%."; }
                catch { reply = "Не получилось изменить системную громкость на этом устройстве."; }
                return true;
            }
        }
        if (ContainsWord(text, "яркость", "brightness", "bright"))
        {
            int? percent = ParsePercentNearKeyword(text, "яркость", "brightness", "bright");
            if (percent.HasValue)
            {
                try
                {
                    string script = "$b=" + percent.Value + "; Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods -ErrorAction Stop | ForEach-Object { Invoke-CimMethod -InputObject $_ -MethodName WmiSetBrightness -Arguments @{Timeout=1;Brightness=$b} | Out-Null }";
                    RunPowerShell(script);
                    reply = $"Отправил запрос установить яркость на {percent.Value}%. Если это внешний монитор, он может не поддерживать управление через Windows.";
                }
                catch { reply = "Не удалось изменить яркость. Некоторые мониторы не предоставляют Windows такой возможности."; }
                return true;
            }
        }

        if (ContainsWord(text, "блютуз", "блютус", "bluetooth", "bluetooth-а", "bluetoothа"))
        {
            Run("cmd.exe", "/c start ms-settings:bluetooth");
            reply = ContainsWord(text, "выключи", "выключить", "выруби", "отключи", "отключить", "off", "disable")
                ? "Открыл настройки Bluetooth — переключи тумблер там. Прямое выключение зависит от драйвера и разрешений Windows."
                : "Открыл настройки Bluetooth, чтобы можно было включить его. Прямое переключение не поддерживается одинаково на всех устройствах.";
            return true;
        }

        // If the message contains an action verb, scan every word for a running process name.
        // We only offer to close a match; the user must explicitly confirm before anything is closed.
        bool closeIntent = ContainsWord(text,
            "убей", "убить", "убейте", "убивай", "закрой", "закрыть", "закройте", "закроешь", "закрою", "закроем", "закроет", "закрывай", "закрываешь", "закрыл", "закрыла", "закрыли", "убери", "убрать", "уберите", "уберешь", "уберёшь", "убирай", "убираешь",
            "выруби", "вырубить", "вырубите", "вырубишь", "вырубай", "вырубаешь", "грохни", "заверши", "завершить", "завершишь", "останови", "остановить", "остановишь",
            "kill", "close", "quit", "exit", "terminate", "stop", "remove", "delete", "end", "forceclose", "force-quit", "turn", "off");
        if (closeIntent)
        {
            string[] ignored = { "убей", "убить", "убейте", "убивай", "закрой", "закрыть", "закройте", "закроешь", "закрою", "закроем", "закроет", "закрывай", "закрываешь", "закрыл", "закрыла", "закрыли", "убери", "убрать", "уберите", "уберешь", "уберёшь", "убирай", "убираешь", "выруби", "вырубить", "вырубите", "вырубишь", "вырубай", "вырубаешь", "грохни", "заверши", "завершить", "завершишь", "останови", "остановить", "остановишь", "kill", "close", "quit", "exit", "terminate", "stop", "remove", "delete", "end", "forceclose", "force-quit", "turn", "off", "программу", "приложение", "процесс", "программы", "приложения", "процессы", "пожалуйста", "плиз", "please", "the", "app", "program", "process", "application", "на", "из", "мне", "пж" };
            // Common Russian names/transliterations map to executable process names.
            // Match whole words so a casual mention does not become a substring guess.
            var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["дискорд"] = new[] { "discord" }, ["дискорде"] = new[] { "discord" }, ["дискорда"] = new[] { "discord" },
                ["хром"] = new[] { "chrome" }, ["гуглхром"] = new[] { "chrome" },
                ["телеграм"] = new[] { "telegram" }, ["телега"] = new[] { "telegram" }, ["телеграмм"] = new[] { "telegram" },
                ["аюграм"] = new[] { "ayugram" }, ["айюграм"] = new[] { "ayugram" }, ["стим"] = new[] { "steam" },
                ["фаерфокс"] = new[] { "firefox" }, ["файрфокс"] = new[] { "firefox" }, ["мозилла"] = new[] { "firefox" },
                ["эдж"] = new[] { "msedge" }, ["опера"] = new[] { "opera" }, ["яндекс"] = new[] { "yandex" }, ["яндексбраузер"] = new[] { "yandex" },
                ["роблокс"] = new[] { "roblox" }, ["фотошоп"] = new[] { "photoshop" }, ["капкат"] = new[] { "capcut" },
                ["вс код"] = new[] { "code" }, ["вскод"] = new[] { "code" }, ["вскоде"] = new[] { "code" }, ["визуалстудиокод"] = new[] { "code" },
                ["терминал"] = new[] { "windowsterminal", "wt" }, ["повершелл"] = new[] { "powershell", "pwsh" }, ["команднаястрока"] = new[] { "cmd" }, ["консоль"] = new[] { "cmd", "windowsterminal", "powershell" },
                ["торрент"] = new[] { "utorrent", "bittorrent", "qbittorrent" }, ["юторрент"] = new[] { "utorrent" }, ["биторрент"] = new[] { "bittorrent" }, ["кбитторрент"] = new[] { "qbittorrent" },
                ["спотифай"] = new[] { "spotify" }, ["влс"] = new[] { "vlc" }, ["зум"] = new[] { "zoom" }, ["скайп"] = new[] { "skype" },
                ["ватсап"] = new[] { "whatsapp" }, ["вотсап"] = new[] { "whatsapp" }, ["слак"] = new[] { "slack" }, ["тимс"] = new[] { "teams" },
                ["обс"] = new[] { "obs64", "obs" }, ["роблоксстудио"] = new[] { "robloxstudio" }, ["майнкрафт"] = new[] { "minecraft", "javaw" },
                ["эксплорер"] = new[] { "explorer" }, ["проводник"] = new[] { "explorer" }, ["блокнот"] = new[] { "notepad" }, ["паинт"] = new[] { "mspaint" }, ["пейнт"] = new[] { "mspaint" },
                ["ворд"] = new[] { "winword" }, ["эксель"] = new[] { "excel" }, ["пауэрпоинт"] = new[] { "powerpnt" }, ["аутлук"] = new[] { "outlook" },
                ["винрар"] = new[] { "winrar" }, ["7зип"] = new[] { "7zfm" }, ["эпик"] = new[] { "epicgameslauncher" }, ["эпикгеймс"] = new[] { "epicgameslauncher" },
                ["батлнет"] = new[] { "battle.net", "battle.net.exe", "agent" }, ["риот"] = new[] { "riotclientservices", "riotclient" },
                ["кэпчер"] = new[] { "sharex" }, ["шерикс"] = new[] { "sharex" }, ["гитхабдесктоп"] = new[] { "githubdesktop" },
                ["фигма"] = new[] { "figma" }, ["блендер"] = new[] { "blender" }, ["давинчи"] = new[] { "resolve" }, ["премьера"] = new[] { "adobe premiere pro", "premiere" }
            };
            string[] rawWords = System.Text.RegularExpressions.Regex.Matches(input.ToLowerInvariant(), @"[a-zа-яё0-9_.-]+")
                .Select(m => m.Value.Trim('.', '_', '-'))
                .Where(w => w.Length >= 3 && !ignored.Contains(w, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] words = rawWords.SelectMany(w =>
                {
                    if (aliases.TryGetValue(w, out string[]? mapped)) return mapped;
                    string stem = NormalizeRussianCase(w);
                    if (stem != w && aliases.TryGetValue(stem, out mapped)) return mapped;
                    return new[] { w };
                })
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var matched = new List<(string Name, Process[] Processes)>();
            foreach (string word in words)
            {
                string candidate = word.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? word[..^4] : word;
                if (IsProtected(candidate)) continue;
                Process[] found = FindProcesses(candidate);
                if (found.Length > 0) matched.Add((candidate, found));
            }
            // Prefer the narrowest match to avoid asking about unrelated processes for common words.
            var best = matched.OrderByDescending(m => m.Name.Length).FirstOrDefault();
            foreach (var item in matched.Where(m => !ReferenceEquals(m.Processes, best.Processes)))
                foreach (var proc in item.Processes) proc.Dispose();
            if (best.Processes == null || best.Processes.Length == 0)
            {
                reply = "Понял, что ты хочешь закрыть программу, но не нашёл совпадений среди запущенных процессов. Попробуй написать её имя, например Discord или chrome.exe.";
                return true;
            }
            string display = string.Join(", ", best.Processes.Take(5).Select(p => { try { return p.ProcessName; } catch { return best.Name; } }));
            if (MessageBox.Show($"Нашёл совпадение по слову «{best.Name}»: {display}. Закрыть? Несохранённые данные могут потеряться.", "Подтверждение закрытия", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            { foreach (Process process in best.Processes) process.Dispose(); reply = "Хорошо, не закрываю."; return true; }
            int closed = 0;
            foreach (Process process in best.Processes)
            {
                try { if (process.CloseMainWindow()) { if (process.WaitForExit(1800)) closed++; } else { process.Kill(); closed++; } }
                catch { }
                finally { process.Dispose(); }
            }
            reply = closed > 0 ? $"Закрыл {closed} процесс(а) по запросу." : "Не получилось закрыть программу; возможно, нужны права или у неё нет обычного окна.";
            return true;
        }

        return false;
    }

    public static string CurrentAppComment(string process, string title)
    {
        string p = process.ToLowerInvariant();
        string t = title.ToLowerInvariant();
        if (p.Contains("utorrent") || p.Contains("bittorrent")) return "*косится на окно* uTorrent? Напоминаю: пиратить нехорошо. Я ничего не видел. Пока.";
        if (t.Contains("youtube")) return "YouTube снова затянул тебя? Я могу засечь, сколько длится «последнее видео».";
        if (p.Contains("chrome") || p.Contains("msedge") || p.Contains("firefox") || p.Contains("opera") || p.Contains("brave") || p.Contains("vivaldi") || p.Contains("browser") || p.Contains("yandex")) return "Опять браузер. Надеюсь, это исследование, а не ещё 47 открытых вкладок.";
        if (p.Contains("telegram") || p.Contains("ayugram")) return "Сообщения пришли. Я не подглядываю. Я просто нахожусь рядом с окном.";
        if (p.Contains("steam")) return "Steam открыт. Это рабочая необходимость или перерыв с очень хорошей графикой?";
        if (p.Contains("discord")) return "Discord detected. Голосовой чат, мемы или обсуждение, которое могло быть одним сообщением?";
        if (p.Contains("roblox")) return "Roblox? Ладно, только не строй меня в обби.";
        if (p.Contains("photoshop")) return "Photoshop открыт. Пожалуйста, не ретушируй мои пиксели без согласия.";
        if (p.Contains("capcut")) return "CapCut? Если я попаду в монтаж, требую титры и оплату мемами.";
        if (p.Contains("powershell") || p.Contains("pwsh") || p.Contains("windowsterminal") || p == "cmd" || p.Contains("conhost")) return "Терминал открыт. Один неверный символ — и начинается новая сюжетная арка.";
        return string.Empty;
    }

    private static string NormalizeRussianCase(string word)
    {
        string[] endings = { "иями", "ями", "ами", "ого", "ему", "ому", "ыми", "ими", "иях", "ах", "ях", "ом", "ем", "ём", "ой", "ей", "ам", "ям", "ов", "ев", "у", "ю", "а", "я", "е", "ы", "и" };
        foreach (string ending in endings.OrderByDescending(e => e.Length))
            if (word.Length - ending.Length >= 4 && word.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
                return word[..^ending.Length];
        return word;
    }

    private static string FormatNumber(double value) => value.ToString("0.##########", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

    private static int? ParsePercentNearKeyword(string text, params string[] keywords)
    {
        var words = System.Text.RegularExpressions.Regex.Matches(text, @"[a-zа-яё0-9%]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => m.Value).ToArray();
        for (int i = 0; i < words.Length; i++)
        {
            if (!keywords.Contains(words[i], StringComparer.OrdinalIgnoreCase)) continue;
            // Number immediately after the setting word, or with one intervening word ("громкость на 40").
            for (int offset = 1; offset <= 2 && i + offset < words.Length; offset++)
            {
                if (int.TryParse(words[i + offset].TrimEnd('%'), out int value))
                    return value >= 1 && value <= 100 ? value : null;
                if (offset == 1 && (words[i + offset] == "на" || words[i + offset] == "до" || words[i + offset] == "to" || words[i + offset] == "at" || words[i + offset] == "на")) continue;
                break;
            }
        }
        return null;
    }
    private static bool ContainsAny(string value, params string[] needles) => needles.Any(n => value.Contains(n, StringComparison.OrdinalIgnoreCase));
    private static bool ContainsWord(string value, params string[] words) =>
        System.Text.RegularExpressions.Regex.Matches(value.ToLowerInvariant(), @"[a-zа-яё0-9_-]+")
            .Select(m => m.Value).Any(token => words.Contains(token, StringComparer.OrdinalIgnoreCase)) ||
        words.Any(w => w.Contains('-') && value.Contains(w, StringComparison.OrdinalIgnoreCase));
    private static void Run(string file, string args) => Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true, CreateNoWindow = true });
    private static void RunPowerShell(string script)
    {
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "`\"") + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException();
        if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { } throw new TimeoutException(); }
        if (p.ExitCode != 0) throw new InvalidOperationException(p.StandardError.ReadToEnd());
    }
    private static string ExtractAppName(string input)
    {
        string value = input.Trim();
        string[] prefixes = { "закрой программу", "закрой приложение", "закрой процесс", "close app", "close program", "закрой", "close" };
        foreach (string prefix in prefixes) { int i = value.IndexOf(prefix, StringComparison.OrdinalIgnoreCase); if (i >= 0) { value = value[(i + prefix.Length)..].Trim(' ', ':', '"', '\''); break; } }
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
    }
    private static bool IsProtected(string name) => new[] { "system", "csrss", "wininit", "services", "lsass", "smss", "winlogon", "dwm", "registry", "idle", "boykisserpet" }.Contains(name, StringComparer.OrdinalIgnoreCase);
    private static Process[] FindProcesses(string name)
    {
        string key = Path.GetFileNameWithoutExtension(name);
        var matches = new List<Process>();
        foreach (Process process in Process.GetProcesses())
        {
            bool match;
            try { match = process.ProcessName.Equals(key, StringComparison.OrdinalIgnoreCase) || process.ProcessName.Contains(key, StringComparison.OrdinalIgnoreCase); }
            catch { match = false; }
            if (match) matches.Add(process); else process.Dispose();
        }
        return matches.ToArray();
    }

    private static void SetVolume(float scalar)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out IMMDevice device);
        Guid iid = typeof(IAudioEndpointVolume).GUID;
        device.Activate(ref iid, 23, IntPtr.Zero, out object endpointObject);
        var endpoint = (IAudioEndpointVolume)endpointObject;
        Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar(Math.Clamp(scalar, 0f, 1f), Guid.Empty));
        Marshal.ReleaseComObject(endpoint); Marshal.ReleaseComObject(device); Marshal.ReleaseComObject(enumerator);
    }

    private enum EDataFlow { eRender, eCapture, eAll }
    private enum ERole { eConsole, eMultimedia, eCommunications }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    private interface IMMDeviceEnumerator { int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out object devices); int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint); }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    private interface IMMDevice { int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer); }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify); int UnregisterControlChangeNotify(IntPtr notify); int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float level, Guid eventContext); int SetMasterVolumeLevelScalar(float level, Guid eventContext);
        int GetMasterVolumeLevel(out float level); int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float level, Guid eventContext); int SetChannelVolumeLevelScalar(uint channel, float level, Guid eventContext);
        int GetChannelVolumeLevel(uint channel, out float level); int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, Guid eventContext); int GetMute(out bool mute);
        int GetVolumeStepInfo(out uint step, out uint stepCount); int VolumeStepUp(Guid eventContext); int VolumeStepDown(Guid eventContext);
        int QueryHardwareSupport(out uint hardwareSupportMask); int GetVolumeRange(out float volumeMin, out float volumeMax, out float volumeIncrement);
    }
}
