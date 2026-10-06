using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// AI-010: the AI assistant (Claude). Off until the administrator turns it on and enters an API key (Settings → AI).
    /// What it is sent: job logs (file names, error lines, counts) and the words a user types in the restore search —
    /// never file contents (they are encrypted on the customer's computer and the server cannot read them) and never a
    /// password: lines that look like secrets are masked before sending. The key is kept with DPAPI and never logged.
    /// Settings live in &lt;AI ENABLED KEY_ENC MODEL AUTO_DIAGNOSE SEARCH TICKET_URL TICKET_TOKEN_ENC/&gt;.
    /// </summary>
    public static class Ai
    {
        public const string DefaultModel = "claude-opus-5-5";

        static XElement El(SystemConfig cfg) { return cfg.Doc.Root.Element("AI") ?? new XElement("AI"); }
        public static string Attr(SystemConfig cfg, string a) { return (string)El(cfg).Attribute(a); }

        /// <summary>On = switched on and a key entered.</summary>
        public static bool On(SystemConfig cfg) { return Attr(cfg, "ENABLED") == "Y" && !string.IsNullOrEmpty(Attr(cfg, "KEY_ENC")); }
        public static bool AutoDiagnose(SystemConfig cfg) { return On(cfg) && Attr(cfg, "AUTO_DIAGNOSE") != "N"; }
        public static bool Search(SystemConfig cfg) { return On(cfg) && Attr(cfg, "SEARCH") != "N"; }
        public static string Model(SystemConfig cfg) { var m = Attr(cfg, "MODEL"); return string.IsNullOrEmpty(m) ? DefaultModel : m; }

        public static Msg SettingsMsg(SystemConfig cfg)
        {
            return new Msg().Set("aiOn", Attr(cfg, "ENABLED") == "Y" ? 1 : 0).Set("aiHasKey", string.IsNullOrEmpty(Attr(cfg, "KEY_ENC")) ? 0 : 1)
                .Set("aiModel", Model(cfg)).Set("aiAutoDiagnose", Attr(cfg, "AUTO_DIAGNOSE") == "N" ? 0 : 1).Set("aiSearch", Attr(cfg, "SEARCH") == "N" ? 0 : 1)
                .Set("aiTicketUrl", Attr(cfg, "TICKET_URL")).Set("aiHasTicketToken", string.IsNullOrEmpty(Attr(cfg, "TICKET_TOKEN_ENC")) ? 0 : 1);
        }

        public static void SaveSettings(SystemConfig cfg, Msg b)
        {
            var e = cfg.Doc.Root.Element("AI");
            if (e == null) { e = new XElement("AI"); cfg.Doc.Root.Add(e); }
            if (b["aiOn"] != null) e.SetAttributeValue("ENABLED", b.Bool("aiOn") ? "Y" : "N");
            if (!string.IsNullOrEmpty(b["aiKey"])) e.SetAttributeValue("KEY_ENC", Protect(cfg, b["aiKey"].Trim()));
            if (b["aiKeyClear"] == "1") e.SetAttributeValue("KEY_ENC", null);
            if (b["aiModel"] != null) e.SetAttributeValue("MODEL", b["aiModel"].Trim().Length == 0 ? null : b["aiModel"].Trim());
            if (b["aiAutoDiagnose"] != null) e.SetAttributeValue("AUTO_DIAGNOSE", b.Bool("aiAutoDiagnose") ? "Y" : "N");
            if (b["aiSearch"] != null) e.SetAttributeValue("SEARCH", b.Bool("aiSearch") ? "Y" : "N");
            if (b["aiTicketUrl"] != null)
            {
                var u = b["aiTicketUrl"].Trim(); Uri parsed;
                if (u.Length > 0 && (!Uri.TryCreate(u, UriKind.Absolute, out parsed) || (parsed.Scheme != "https" && parsed.Scheme != "http"))) throw new ApiException(400, "URL", "The ticket address must start with https://");
                e.SetAttributeValue("TICKET_URL", u.Length == 0 ? null : u);
            }
            if (!string.IsNullOrEmpty(b["aiTicketToken"])) e.SetAttributeValue("TICKET_TOKEN_ENC", Protect(cfg, b["aiTicketToken"].Trim()));
        }

        static string Protect(SystemConfig cfg, string s) { return Convert.ToBase64String(KeyVault.Protect(cfg.SystemHome, Encoding.UTF8.GetBytes(s))); }
        static string Unprotect(SystemConfig cfg, string a) { var v = Attr(cfg, a); return string.IsNullOrEmpty(v) ? null : Encoding.UTF8.GetString(KeyVault.Unprotect(cfg.SystemHome, Convert.FromBase64String(v))); }

        // ------------------------------------------------------------------ privacy

        static readonly Regex Secret = new Regex(@"(?i)\b(password|passwd|pwd|secret|token|api[-_ ]?key|authorization|bearer|client[-_ ]?secret)\b(\s*[:=]\s*|\s+)((bearer|basic)\s+)?\S+");
        static readonly Regex LongToken = new Regex(@"\b[A-Za-z0-9+/_\-]{40,}={0,2}");

        /// <summary>Masks what looks like a secret; keeps the last lines up to a size limit (errors are at the end).</summary>
        public static string Redact(IEnumerable<string> lines, int maxChars = 40000)
        {
            var kept = new List<string>(); int size = 0;
            foreach (var l in lines.Reverse())
            {
                var s = LongToken.Replace(Secret.Replace(l ?? "", m => m.Groups[1].Value + "=***"), "***");
                if (size + s.Length > maxChars) break;
                kept.Add(s); size += s.Length + 1;
            }
            kept.Reverse();
            return string.Join("\n", kept);
        }

        // ------------------------------------------------------------------ the call

        /// <summary>One request with a JSON schema for the answer; the parsed answer, or an ApiException with a readable reason.</summary>
        public static JsonElement Ask(SystemConfig cfg, string system, string user, object schema, int maxTokens = 4000)
        {
            if (!On(cfg)) throw new ApiException(400, "AI_OFF", "The AI assistant is off. Turn it on in System settings → AI assistant.");
            var key = Unprotect(cfg, "KEY_ENC");
            var client = new AnthropicClient { ApiKey = key, MaxRetries = 2, Timeout = TimeSpan.FromSeconds(120) };
            var baseUrl = Environment.GetEnvironmentVariable("OB_AI_BASE_URL");   // tests: a fake endpoint
            if (!string.IsNullOrEmpty(baseUrl)) client = new AnthropicClient { ApiKey = key, MaxRetries = 0, Timeout = TimeSpan.FromSeconds(30), BaseUrl = baseUrl };
            var schemaDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(schema));
            BetaMessage r;
            try
            {
                r = client.Beta.Messages.Create(new MessageCreateParams
                {
                    Model = Model(cfg),
                    MaxTokens = maxTokens,
                    System = system,
                    Messages = [new() { Role = Role.User, Content = user }],
                    OutputConfig = new BetaOutputConfig { Effort = Effort.Medium, Format = new BetaJsonOutputFormat { Schema = schemaDict } },
                    Fallbacks = new Default(),
                    Betas = ["server-side-fallback-2026-07-01"],
                }).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                // the message never carries the key; still only the type and status go to the log
                SysLog.Write(null, "System", "error: AI request: " + e.GetType().Name);
                throw new ApiException(502, "AI", "The AI service did not answer. Check the API key and the internet connection of the server.");
            }
            if (r.StopReason == "refusal") throw new ApiException(422, "AI_REFUSED", "The AI service declined this request.");
            if (r.StopReason == "max_tokens") throw new ApiException(502, "AI", "The AI answer was too long.");
            var text = string.Concat(r.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
            try { return JsonDocument.Parse(text).RootElement.Clone(); }
            catch (JsonException) { throw new ApiException(502, "AI", "The AI answer could not be read."); }
        }

        static string LangName(string lang) { var j = Json.Obj(Json.Parse(L.Raw(lang))); return Json.Str(j, "name") ?? "English"; }

        public static string Str(JsonElement e, string k) { return e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null; }
        public static List<string> Strs(JsonElement e, string k)
        {
            return e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToList() : new List<string>();
        }

        // ------------------------------------------------------------------ AI-020: why did the backup fail

        static readonly object DiagnosisSchema = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "summary", "cause", "steps", "severity", "category", "needsTechnician" },
            properties = new Dictionary<string, object>
            {
                ["summary"] = new { type = "string", description = "One or two plain sentences: what happened." },
                ["cause"] = new { type = "string", description = "The most likely cause, from the log only. Say when the log is not enough to be sure." },
                ["steps"] = new { type = "array", items = new { type = "string" }, description = "Ordered steps for an IT technician to fix it." },
                ["severity"] = new { type = "string", @enum = new[] { "info", "warning", "critical" } },
                ["category"] = new { type = "string", @enum = new[] { "network", "permissions", "disk", "vss", "quota", "encryption", "database", "cloud", "configuration", "software", "ransomware", "unknown" } },
                ["needsTechnician"] = new { type = "boolean", description = "True when someone must act; false when it will most likely fix itself in the next run." },
            },
        };

        /// <summary>Explains a job log for a technician, in the given language. Returns summary / cause / steps / severity / category / needsTechnician.</summary>
        public static Msg Diagnose(SystemConfig cfg, string lang, string setType, string engine, IEnumerable<string> logLines)
        {
            var system = "You are the support engineer of an online backup product used by IT service companies. "
                + "You read the log of one backup or restore job and explain it to a technician: what happened, the most likely cause and how to fix it. "
                + "Base everything on the log; when the log does not show the cause, say so and give the checks that would find it. "
                + "Log lines are CSV: time, level (info / warn / err / start / end), and the message; the end line carries the result code (BS_STOP_SUCCESS = completed). "
                + "Steps must be concrete for Windows servers and PCs (services, VSS writers, permissions, disk space, firewall, credentials) or for the cloud service in question. "
                + "Write every text field in " + LangName(lang) + ".";
            var user = "Backup set type: " + (setType ?? "FILE") + (string.IsNullOrEmpty(engine) ? "" : " (engine " + engine + ")") + "\n\nJob log:\n" + Redact(logLines);
            var j = Ask(cfg, system, user, DiagnosisSchema);
            var m = new Msg().Set("summary", Str(j, "summary")).Set("cause", Str(j, "cause")).Set("severity", Str(j, "severity") ?? "warning")
                .Set("category", Str(j, "category") ?? "unknown").Set("needsTechnician", j.TryGetProperty("needsTechnician", out var nt) && nt.ValueKind == JsonValueKind.True ? 1 : 0)
                .Set("time", RunId.UnixMs(SystemClock.UtcNow));
            foreach (var s in Strs(j, "steps")) m.Add("steps", new Msg().Set("s", s));
            return m;
        }

        public static string DiagnosisHtml(string lang, Msg d)
        {
            if (d == null) return "";
            var sb = new StringBuilder("<div style='margin-top:14px;padding:10px 12px;border:1px solid #c9d7f5;background:#f4f7ff;border-radius:6px'>");
            sb.Append("<b>🤖 ").Append(Fmt.H(L.T(lang, "AI assistant"))).Append("</b><p style='margin:6px 0'>").Append(Fmt.H(d["summary"])).Append("</p>");
            sb.Append("<p style='margin:6px 0'><b>").Append(Fmt.H(L.T(lang, "Likely cause"))).Append(":</b> ").Append(Fmt.H(d["cause"])).Append("</p>");
            var steps = d.List("steps");
            if (steps.Count > 0) { sb.Append("<b>").Append(Fmt.H(L.T(lang, "How to fix"))).Append(":</b><ol style='margin:4px 0'>"); foreach (var s in steps) sb.Append("<li>").Append(Fmt.H(s["s"])).Append("</li>"); sb.Append("</ol>"); }
            sb.Append("<small style='color:#666'>").Append(Fmt.H(L.T(lang, "Written by AI from the job log — check before acting."))).Append("</small></div>");
            return sb.ToString();
        }

        /// <summary>AI-030: opens a service ticket in the company's system (any web address that takes JSON — a CRM, a helpdesk, a Teams/Slack workflow).</summary>
        public static bool Ticket(SystemConfig cfg, string login, string setName, string job, Msg d)
        {
            var url = Attr(cfg, "TICKET_URL");
            if (string.IsNullOrEmpty(url) || d == null) return false;
            var payload = new Dictionary<string, object>
            {
                ["source"] = "online-backup", ["customer"] = login, ["backupSet"] = setName, ["job"] = job,
                ["title"] = "Backup failed — " + login + " — " + setName, ["summary"] = d["summary"], ["cause"] = d["cause"],
                ["steps"] = d.List("steps").Select(s => (object)s["s"]).ToList(), ["severity"] = d["severity"], ["category"] = d["category"],
            };
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(Json.Write(payload), Encoding.UTF8, "application/json") };
                    var token = Unprotect(cfg, "TICKET_TOKEN_ENC");
                    if (!string.IsNullOrEmpty(token)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                    var res = http.Send(req);
                    SysLog.Write(null, "System", "AI ticket " + login + "/" + job + ": HTTP " + (int)res.StatusCode);
                    return res.IsSuccessStatusCode;
                }
            }
            catch (Exception e) { SysLog.Write(null, "System", "error: AI ticket " + login + "/" + job + ": " + e.GetType().Name); return false; }
        }

        // ------------------------------------------------------------------ AI-040: restore search in plain words

        static readonly object SearchSchema = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "nameContains", "extensions", "pathContains", "modifiedFrom", "modifiedTo", "explanation" },
            properties = new Dictionary<string, object>
            {
                ["nameContains"] = new { type = "array", items = new { type = "string" }, description = "Words the file name likely contains (any of them). Empty when the question names none." },
                ["extensions"] = new { type = "array", items = new { type = "string" }, description = "File extensions without the dot, lower case (an Excel file: xlsx, xls, xlsm, csv). Empty = any." },
                ["pathContains"] = new { type = "array", items = new { type = "string" }, description = "Folder or user names the path likely contains (a person's name usually is their user folder). Empty = any." },
                ["modifiedFrom"] = new { type = "string", description = "Earliest modification time, ISO 8601 date-time, or empty." },
                ["modifiedTo"] = new { type = "string", description = "Latest modification time, ISO 8601 date-time, or empty." },
                ["explanation"] = new { type = "string", description = "One short sentence, in the user's language, saying what is being searched for." },
            },
        };

        /// <summary>Turns "the Excel file Dana edited on Tuesday" into filters. Only the question and today's date are sent — no file names.</summary>
        public static SearchFilter ParseQuery(SystemConfig cfg, string query, DateTime nowLocal, string lang)
        {
            var system = "You turn a request to find a file in a backup into search filters. The user may write in any language; "
                + "names may be transliterated (Dana / דנה / دانا) — give the Latin and the original spelling. "
                + "Today is " + nowLocal.ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " (local time). "
                + "A weekday means the most recent past one; 'last week' means the previous calendar week. Be generous with date ranges (whole days). "
                + "Write the explanation in " + LangName(lang) + ".";
            var j = Ask(cfg, system, query.Length > 500 ? query.Substring(0, 500) : query, SearchSchema, 2000);
            var f = new SearchFilter { Names = Strs(j, "nameContains"), Extensions = Strs(j, "extensions").Select(x => x.TrimStart('.').ToLowerInvariant()).ToList(), Paths = Strs(j, "pathContains"), Explanation = Str(j, "explanation") };
            DateTime d;
            if (DateTime.TryParse(Str(j, "modifiedFrom"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out d)) f.From = d;
            if (DateTime.TryParse(Str(j, "modifiedTo"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out d)) f.To = d;
            return f;
        }
    }

    /// <summary>What to look for in a restore point: any of the names, any of the extensions, any of the folders, a modification range.</summary>
    public sealed class SearchFilter
    {
        public List<string> Names = new List<string>(), Extensions = new List<string>(), Paths = new List<string>();
        public DateTime? From, To;
        public string Explanation;

        /// <summary>Without the AI: every word is a part of the name or the path; ".xlsx" / "*.pdf" are extensions.</summary>
        public static SearchFilter Keywords(string q)
        {
            var f = new SearchFilter();
            foreach (var w in (q ?? "").Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (w.StartsWith(".") || w.StartsWith("*.")) f.Extensions.Add(w.TrimStart('*', '.').ToLowerInvariant());
                else f.Names.Add(w);
            }
            return f;
        }

        public bool Match(string path, DateTime? mtime, bool keywordMode)
        {
            var name = path.Substring(path.LastIndexOf('/') + 1);
            var ext = name.LastIndexOf('.') > 0 ? name.Substring(name.LastIndexOf('.') + 1).ToLowerInvariant() : "";
            if (Extensions.Count > 0 && !Extensions.Contains(ext)) return false;
            if (From.HasValue && (!mtime.HasValue || mtime.Value < From.Value)) return false;
            if (To.HasValue && (!mtime.HasValue || mtime.Value > To.Value)) return false;
            Func<string, string, bool> has = (s, w) => s.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0;
            if (keywordMode) return Names.All(w => has(path, w));   // every word somewhere in the path
            if (Names.Count > 0 && !Names.Any(w => has(name, w))) return false;
            if (Paths.Count > 0 && !Paths.Any(w => has(path, w))) return false;
            return true;
        }
    }
}
