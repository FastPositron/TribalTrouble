using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

// Чтение и правка строковых (UTF8) констант в .class-файле Java.
// На смещения внутри constant pool ничего в классе не ссылается,
// поэтому длину строки можно менять свободно.
static class ClassPool
{
    class Utf8 { public int Pos; public int Len; public string Value; }

    static List<Utf8> Parse(byte[] b)
    {
        if (b.Length < 10 || b[0] != 0xCA || b[1] != 0xFE || b[2] != 0xBA || b[3] != 0xBE)
            throw new InvalidDataException("Это не class-файл Java.");
        var list = new List<Utf8>();
        int count = (b[8] << 8) | b[9];
        int pos = 10;
        for (int i = 1; i < count; i++)
        {
            byte tag = b[pos];
            switch (tag)
            {
                case 1:
                    int len = (b[pos + 1] << 8) | b[pos + 2];
                    list.Add(new Utf8 { Pos = pos, Len = len, Value = Encoding.UTF8.GetString(b, pos + 3, len) });
                    pos += 3 + len;
                    break;
                case 3: case 4: pos += 5; break;
                case 5: case 6: pos += 9; i++; break;
                case 7: case 8: case 16: case 19: case 20: pos += 3; break;
                case 9: case 10: case 11: case 12: case 17: case 18: pos += 5; break;
                case 15: pos += 4; break;
                default: throw new InvalidDataException("Неизвестный тег constant pool: " + tag);
            }
        }
        return list;
    }

    public static bool Has(byte[] b, string value)
    {
        foreach (var e in Parse(b)) if (e.Value == value) return true;
        return false;
    }

    public static byte[] Replace(byte[] b, Dictionary<string, string> map)
    {
        var entries = Parse(b);
        var hits = new List<KeyValuePair<Utf8, string>>();
        foreach (var kv in map)
        {
            Utf8 found = null;
            int n = 0;
            foreach (var e in entries) if (e.Value == kv.Key) { found = e; n++; }
            if (n != 1) throw new InvalidDataException("Строка \"" + kv.Key + "\" найдена " + n + " раз.");
            hits.Add(new KeyValuePair<Utf8, string>(found, kv.Value));
        }
        hits.Sort((x, y) => x.Key.Pos.CompareTo(y.Key.Pos));

        var outp = new MemoryStream();
        int cursor = 0;
        foreach (var h in hits)
        {
            outp.Write(b, cursor, h.Key.Pos - cursor);
            var nb = Encoding.UTF8.GetBytes(h.Value);
            outp.WriteByte(1);
            outp.WriteByte((byte)(nb.Length >> 8));
            outp.WriteByte((byte)(nb.Length & 0xFF));
            outp.Write(nb, 0, nb.Length);
            cursor = h.Key.Pos + 3 + h.Key.Len;
        }
        outp.Write(b, cursor, b.Length - cursor);
        return outp.ToArray();
    }
}

static class Installer
{
    const string AppId = "3945720";
    const string Marker = "META-INF/tt-ru-patch.txt";
    const string LanguagesClass = "com/oddlabs/tt/gui/Languages.class";
    const string Skin = "gui/gui_skin.xml";
    const string BackupSuffix = ".ru-backup";

    // Папка, где лежат TribalTrouble.exe и app\tt-*.jar.
    // Принимаем и папку Steam ("Tribal Trouble Resurrected"), и вложенную "TribalTrouble".
    public static string GameRoot(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        try
        {
            foreach (var d in new[] { dir, Path.Combine(dir, "TribalTrouble") })
                if (File.Exists(Path.Combine(d, "TribalTrouble.exe")) && FindJar(d) != null) return d;
        }
        catch (Exception) { }
        return null;
    }

    static string FindJar(string root)
    {
        var app = Path.Combine(root, "app");
        if (!Directory.Exists(app)) return null;
        string jar = null;
        foreach (var f in Directory.GetFiles(app, "tt-*.jar"))
        {
            if (!f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
            if (jar != null) return null;
            jar = f;
        }
        return jar;
    }

    public static bool IsInstalled(string root)
    {
        try { return ReadMarker(FindJar(root)) != null; }
        catch (Exception) { return false; }
    }

    public static bool GameRunning()
    {
        return Process.GetProcessesByName("TribalTrouble").Length > 0;
    }

    public static string FindGame()
    {
        var libs = new List<string>();
        foreach (var steam in SteamRoots())
        {
            if (!libs.Contains(steam)) libs.Add(steam);
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
            {
                var p = m.Groups[1].Value.Replace("\\\\", "\\");
                if (!libs.Contains(p)) libs.Add(p);
            }
        }

        foreach (var lib in libs)
        {
            var manifest = Path.Combine(lib, "steamapps", "appmanifest_" + AppId + ".acf");
            if (!File.Exists(manifest)) continue;
            var m = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
            if (!m.Success) continue;
            var dir = Path.Combine(lib, "steamapps", "common", m.Groups[1].Value);
            if (GameRoot(dir) != null) return dir;
        }
        return "";
    }

    static IEnumerable<string> SteamRoots()
    {
        var found = new List<string>();
        string[][] keys =
        {
            new[] { "HKEY_CURRENT_USER\\Software\\Valve\\Steam", "SteamPath" },
            new[] { "HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Valve\\Steam", "InstallPath" },
            new[] { "HKEY_LOCAL_MACHINE\\SOFTWARE\\Valve\\Steam", "InstallPath" },
        };
        foreach (var k in keys)
        {
            try
            {
                var v = Registry.GetValue(k[0], k[1], null) as string;
                if (string.IsNullOrEmpty(v)) continue;
                v = Path.GetFullPath(v.Replace('/', '\\'));
                if (Directory.Exists(v) && !found.Contains(v)) found.Add(v);
            }
            catch (Exception) { }
        }
        return found;
    }

    static byte[] ReadEntry(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name);
        if (e == null) return null;
        using (var s = e.Open())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    static void WriteEntry(ZipArchive zip, string name, byte[] data)
    {
        var old = zip.GetEntry(name);
        if (old != null) old.Delete();
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using (var s = e.Open()) s.Write(data, 0, data.Length);
    }

    static string ReadMarker(string jar)
    {
        using (var zip = ZipFile.OpenRead(jar))
        {
            var b = ReadEntry(zip, Marker);
            return b == null ? null : Encoding.UTF8.GetString(b);
        }
    }

    static string Sha256(string file)
    {
        using (var sha = SHA256.Create())
        using (var f = File.OpenRead(file))
            return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "");
    }

    // В .properties всё, что не ASCII, пишем как \uXXXX — так файл читается при любой кодировке.
    static string ToAscii(string s)
    {
        var sb = new StringBuilder(s.Length * 2);
        foreach (char c in s)
        {
            if (c > 126) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // Ключи .properties с учётом переноса строки через обратный слеш.
    static HashSet<string> Keys(byte[] data)
    {
        var keys = new HashSet<string>();
        if (data == null) return keys;
        bool cont = false;
        foreach (var raw in Encoding.UTF8.GetString(data).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            bool wasCont = cont;
            int slashes = 0;
            for (int i = line.Length - 1; i >= 0 && line[i] == '\\'; i--) slashes++;
            cont = slashes % 2 == 1;
            if (wasCont) continue;
            var m = Regex.Match(line, "^\\s*([^#!\\s][^=:\\s]*)\\s*[=:]");
            if (m.Success) keys.Add(m.Groups[1].Value);
        }
        return keys;
    }

    // Возвращает: [строк перевода, строк игры без перевода].
    public static int[] Install(string dir)
    {
        var root = GameRoot(dir);
        if (root == null) throw new InvalidOperationException("В этой папке нет Tribal Trouble: Resurrected.");
        var jar = FindJar(root);
        var backup = jar + BackupSuffix;

        string origHash;
        var marker = ReadMarker(jar);
        if (marker == null)
        {
            origHash = Sha256(jar);
            File.Copy(jar, backup, true);
        }
        else
        {
            origHash = Regex.Match(marker, "orig_sha256=(\\w+)").Groups[1].Value;
        }

        var tmp = jar + ".ru-tmp";
        File.Copy(jar, tmp, true);
        int lines = 0, missing = 0;
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Update))
            {
                // 1. Список языков: датский -> русский.
                var cls = ReadEntry(zip, LanguagesClass);
                if (cls == null) throw new InvalidOperationException("Неизвестная версия игры: нет списка языков.");
                if (!ClassPool.Has(cls, "ru"))
                {
                    if (!ClassPool.Has(cls, "da") || !ClassPool.Has(cls, "Dansk"))
                        throw new InvalidOperationException("Неизвестная версия игры: в списке языков нет датского.");
                    var map = new Dictionary<string, string>();
                    map["da"] = "ru";
                    map["Dansk"] = "Русский";
                    WriteEntry(zip, LanguagesClass, ClassPool.Replace(cls, map));
                }

                // 2. Скин: шрифт заголовков с кириллицей, нейтральная иконка вместо датского флага.
                var utf8 = new UTF8Encoding(false);
                var skinBytes = ReadEntry(zip, Skin);
                if (skinBytes != null)
                {
                    var skin = utf8.GetString(skinBytes);
                    var skin2 = skin.Replace("<headlinefont>/font/impact_24.font</headlinefont>",
                                             "<headlinefont>/font/intertight-black_28.font</headlinefont>");
                    var def = Regex.Match(skin2, "<flag_default>\\s*(<quad [^>]*/>)");
                    if (def.Success)
                        skin2 = Regex.Replace(skin2, "(<flag_da>\\s*)<quad [^>]*/>", m => m.Groups[1].Value + def.Groups[1].Value);
                    if (skin2 != skin) WriteEntry(zip, Skin, utf8.GetBytes(skin2));
                }

                // 3. Файлы перевода.
                using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
                using (var payload = new ZipArchive(res, ZipArchiveMode.Read))
                {
                    foreach (var entry in payload.Entries)
                    {
                        if (!entry.FullName.EndsWith("_ru.properties", StringComparison.Ordinal)) continue;
                        string text;
                        using (var r = new StreamReader(entry.Open(), utf8, true)) text = r.ReadToEnd();
                        WriteEntry(zip, entry.FullName, Encoding.ASCII.GetBytes(ToAscii(text)));
                    }
                }

                WriteEntry(zip, Marker, Encoding.UTF8.GetBytes(
                    "Russian translation patch\norig_sha256=" + origHash + "\ndate=" + DateTime.Now.ToString("s") + "\n"));

                // 4. Что осталось без перевода (если игра обновилась и добавила текст).
                var names = new List<string>();
                foreach (var e in zip.Entries) names.Add(e.FullName);
                foreach (var name in names)
                {
                    if (!name.StartsWith("com/") || !name.EndsWith(".properties")) continue;
                    if (Regex.IsMatch(name, "_[a-z]{2}\\.properties$")) continue;
                    var en = Keys(ReadEntry(zip, name));
                    var ru = Keys(ReadEntry(zip, name.Substring(0, name.Length - 11) + "_ru.properties"));
                    foreach (var k in en) { if (ru.Contains(k)) lines++; else missing++; }
                }
            }
            File.Copy(tmp, jar, true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        return new[] { lines, missing };
    }

    public static bool Remove(string dir)
    {
        var root = GameRoot(dir);
        if (root == null) throw new InvalidOperationException("В этой папке нет Tribal Trouble: Resurrected.");
        var jar = FindJar(root);
        var marker = ReadMarker(jar);
        if (marker == null) return false;

        var origHash = Regex.Match(marker, "orig_sha256=(\\w+)").Groups[1].Value;
        var backup = jar + BackupSuffix;
        if (!File.Exists(backup) || !string.Equals(Sha256(backup), origHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Нет подходящей копии оригинала. Восстановите игру через Steam: "
                + "Свойства → Установленные файлы → Проверить целостность файлов игры.");
        File.Copy(backup, jar, true);
        File.Delete(backup);
        return true;
    }
}

class MainForm : Form
{
    readonly TextBox path = new TextBox();
    readonly Label status = new Label();
    readonly Button install = new Button();
    readonly Button remove = new Button();

    public MainForm()
    {
        Text = "Русификатор Tribal Trouble: Resurrected";
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(560, 250);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var intro = new Label
        {
            Text = "Русский перевод всей игры: меню, обучение, обе кампании, сетевая игра.\n"
                 + "Русский встаёт в настройках игры на место датского.",
            Location = new Point(16, 14),
            Size = new Size(528, 40),
        };

        var pathLabel = new Label { Text = "Папка с игрой:", Location = new Point(16, 66), AutoSize = true };
        path.Location = new Point(16, 88);
        path.Size = new Size(430, 25);
        path.TextChanged += delegate { Refresh2(); };

        var browse = new Button { Text = "Обзор...", Location = new Point(454, 86), Size = new Size(90, 28) };
        browse.Click += delegate
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Укажите папку игры, в которой лежит TribalTrouble.exe";
                dlg.SelectedPath = path.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK) path.Text = dlg.SelectedPath;
            }
        };

        status.Location = new Point(16, 124);
        status.Size = new Size(528, 44);

        install.Location = new Point(16, 176);
        install.Size = new Size(260, 36);
        install.Click += delegate { Run(true); };

        remove.Text = "Удалить русификатор";
        remove.Location = new Point(284, 176);
        remove.Size = new Size(260, 36);
        remove.Click += delegate { Run(false); };

        var credit = new Label
        {
            Text = "Перевод: FastPositron. Игра — открытый проект под GPL-2.0.",
            Location = new Point(16, 222),
            Size = new Size(528, 20),
            ForeColor = SystemColors.GrayText,
        };

        Controls.AddRange(new Control[] { intro, pathLabel, path, browse, status, install, remove, credit });
        path.Text = Installer.FindGame();
        Refresh2();
    }

    void Refresh2()
    {
        var dir = path.Text.Trim();
        var root = Installer.GameRoot(dir);
        var ok = root != null;
        var has = ok && Installer.IsInstalled(root);

        install.Enabled = ok;
        remove.Enabled = has;
        install.Text = has ? "Переустановить русификатор" : "Установить русификатор";

        if (dir.Length == 0)
            status.Text = "Игра не найдена автоматически. Укажите папку с TribalTrouble.exe.";
        else if (!ok)
            status.Text = "В этой папке нет игры: нужны TribalTrouble.exe и app\\tt-*.jar.";
        else if (has)
            status.Text = "Игра найдена. Русификатор уже установлен.\n"
                        + "После обновления игры в Steam нажмите «Переустановить».";
        else
            status.Text = "Игра найдена. Русификатор не установлен.";
    }

    void Run(bool doInstall)
    {
        var dir = path.Text.Trim();
        if (Installer.GameRunning())
        {
            MessageBox.Show(this, "Сначала закройте игру.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            if (doInstall)
            {
                var r = Installer.Install(dir);
                var msg = "Готово, переведено строк: " + r[0] + ".\n\n"
                        + "В игре: Options → Language → Русский, затем перезапустите игру.";
                if (r[1] > 0)
                    msg += "\n\nВ этой версии игры есть новые строки без перевода (" + r[1] + "), они останутся на английском.";
                MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                Installer.Remove(dir);
                MessageBox.Show(this, "Русификатор удалён, оригинальные файлы восстановлены.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Нет прав на запись в папку игры. Запустите русификатор от имени администратора.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "Не получилось: " + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        Refresh2();
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Тихий режим: TribalTrouble_RUS.exe /install "папка игры"  или  /remove "папка игры"
        if (args.Length == 2 && (args[0] == "/install" || args[0] == "/remove"))
        {
            try
            {
                if (args[0] == "/install") Installer.Install(args[1]);
                else Installer.Remove(args[1]);
                return 0;
            }
            catch (Exception)
            {
                return 1;
            }
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }
}
