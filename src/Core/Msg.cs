using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace OnlineBackup.Core
{
    /// <summary>
    /// The agent ↔ server message: a flat set of fields plus lists of child messages, as small XML.
    /// XML (not JSON) because .NET 4.0 on Windows 2003 has it built in.
    /// </summary>
    public sealed class Msg
    {
        readonly Dictionary<string, string> f = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, List<Msg>> lists = new Dictionary<string, List<Msg>>(StringComparer.Ordinal);

        public string this[string key]
        {
            get { string v; return f.TryGetValue(key, out v) ? v : null; }
            set { if (value == null) f.Remove(key); else f[key] = value; }
        }

        public Msg Set(string key, object value)
        {
            this[key] = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            return this;
        }

        public long Long(string key, long def = 0) { long v; return long.TryParse(this[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def; }
        public int Int(string key, int def = 0) { int v; return int.TryParse(this[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def; }
        public bool Bool(string key) { var v = this[key]; return v == "1" || v == "Y" || v == "true"; }
        public IEnumerable<string> Keys { get { return f.Keys; } }

        public List<Msg> List(string name)
        {
            List<Msg> l;
            if (!lists.TryGetValue(name, out l)) { l = new List<Msg>(); lists[name] = l; }
            return l;
        }

        public Msg Add(string listName, Msg item) { List(listName).Add(item); return this; }

        public XElement ToXml(string name = "m")
        {
            var e = new XElement(name);
            foreach (var kv in f) e.Add(new XElement("f", new XAttribute("n", kv.Key), kv.Value));
            foreach (var kv in lists)
            {
                var le = new XElement("l", new XAttribute("n", kv.Key));
                foreach (var item in kv.Value) le.Add(item.ToXml("i"));
                e.Add(le);
            }
            return e;
        }

        public static Msg FromXml(XElement e)
        {
            var m = new Msg();
            foreach (var fe in e.Elements("f")) m[(string)fe.Attribute("n")] = fe.Value;
            foreach (var le in e.Elements("l"))
            {
                var l = m.List((string)le.Attribute("n"));
                foreach (var ie in le.Elements("i")) l.Add(FromXml(ie));
            }
            return m;
        }

        public byte[] ToBytes() { return Encoding.UTF8.GetBytes(ToXml().ToString(SaveOptions.DisableFormatting)); }
        public override string ToString() { return ToXml().ToString(SaveOptions.DisableFormatting); }

        public static Msg Parse(byte[] data) { return data == null || data.Length == 0 ? new Msg() : Parse(Encoding.UTF8.GetString(data)); }

        /// <summary>FUZZ-010: a message is plain XML — no DOCTYPE, no entities (no file of this computer can be pulled in).</summary>
        public static Msg Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return new Msg();
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null };
            using (var r = System.Xml.XmlReader.Create(new System.IO.StringReader(text), settings)) return FromXml(XElement.Load(r));
        }

        public static Msg Read(Stream s)
        {
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return Parse(ms.ToArray());
        }
    }
}
