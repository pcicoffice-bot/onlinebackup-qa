using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// SRC-030: the folders of each computer of a customer (names only), sent by the client software, for the tree in the
    /// admin site's set editor. &lt;user&gt;\folders\&lt;computer&gt;.txt — one folder per line. Opening a folder that is not
    /// there yet asks the computer for it: &lt;BROWSE COMPUTER PATH AT/&gt; in the profile; the computer answers within a minute.
    /// </summary>
    public static class FolderTree
    {
        public static string FileName(string computer)
        {
            var sb = new StringBuilder();
            foreach (var ch in (computer ?? "").Trim().ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.' ? ch : '_');
            if (sb.Length == 0 || sb.Length > 80 || sb.ToString().Trim('.').Length == 0) throw new ApiException(400, "COMPUTER", "Choose a computer.");
            return sb + ".txt";
        }

        static string FileOf(Users users, string login, string computer) { return Path.Combine(users.UserDir(login), "folders", FileName(computer)); }

        public static void Save(Users users, string login, Msg b)
        {
            var dirs = (b["dirs"] ?? "").Replace("\r", "").Split('\n').Where(x => x.Length > 0 && x.Length < 1000).Take(20000).ToArray();
            var f = FileOf(users, login, b["computer"]);
            Directory.CreateDirectory(Path.GetDirectoryName(f));
            Atomic.WriteText(f, (b["sep"] == "/" ? "/" : "\\") + "\n" + string.Join("\n", dirs));
        }

        public static Msg Get(Users users, string login, string computer)
        {
            var f = FileOf(users, login, computer);
            var m = new Msg().Set("computer", computer);
            if (!File.Exists(f)) return m.Set("dirs", "").Set("at", null);
            var lines = OnlineBackup.Core.Atomic.ReadAllText(f).Split('\n');
            m.Set("sep", lines[0]).Set("dirs", string.Join("\n", lines.Skip(1))).Set("at", RunId.UnixMs(File.GetLastWriteTimeUtc(f)));
            lock (users.ProfileLock)
            {
                var p = users.LoadProfile(login);
                foreach (var e in p.Root.Elements("BROWSE").Where(x => string.Equals((string)x.Attribute("COMPUTER"), computer, StringComparison.OrdinalIgnoreCase)))
                {
                    long at; long.TryParse((string)e.Attribute("AT"), out at);
                    if (at > m.Long("at")) m.Add("waiting", new Msg().Set("path", (string)e.Attribute("PATH")));
                }
            }
            return m;
        }

        /// <summary>Asks the computer for a folder's sub-folders (or, with no path, for a fresh list).</summary>
        public static void Request(Users users, string login, string computer, string path, DateTime nowUtc)
        {
            FileOf(users, login, computer);
            if ((path ?? "").Length > 1000) throw new ApiException(400, "PATH", "The path is too long.");
            lock (users.ProfileLock)
            {
                var p = users.LoadProfile(login);
                long now = RunId.UnixMs(nowUtc);
                foreach (var old in p.Root.Elements("BROWSE").ToList())
                {
                    long at; long.TryParse((string)old.Attribute("AT"), out at);
                    if (now - at > 24 * 3600 * 1000L) old.Remove();
                }
                p.Root.Add(new XElement("BROWSE", new XAttribute("COMPUTER", computer), new XAttribute("PATH", path ?? ""), new XAttribute("AT", now.ToString(CultureInfo.InvariantCulture))));
                users.SaveProfile(login, p);
            }
        }
    }
}
