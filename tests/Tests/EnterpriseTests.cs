using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// A stand-in for an ESXi host's vSphere API (SOAP /sdk) and datastore file access (/folder): sign-in with a session
    /// cookie, container views, properties, snapshot tasks (quiesce fails on a VM without VMware Tools), downloads, uploads, register.
    /// </summary>
    public sealed class FakeVSphere : IDisposable
    {
        readonly HttpListener l = new HttpListener();
        public string Url;
        public readonly object Gate = new object();
        public Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();          // "[datastore1] web01/web01.vmx" → content
        public Dictionary<string, string> VmNames = new Dictionary<string, string> { { "vm-1", "web01" }, { "vm-2", "db01" } };
        public Dictionary<string, List<string>> Snaps = new Dictionary<string, List<string>> { { "vm-1", new List<string>() }, { "vm-2", new List<string> { "snapshot-old:OnlineBackup" } } };
        public List<string> Ops = new List<string>(), DownloadsWithoutSnapshot = new List<string>(), Registered = new List<string>();
        readonly Dictionary<string, KeyValuePair<string, string>> tasks = new Dictionary<string, KeyValuePair<string, string>>();   // task → (result, error)
        readonly Dictionary<string, int> polls = new Dictionary<string, int>();
        int seq;

        public FakeVSphere()
        {
            var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); int port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
            Url = "http://localhost:" + port;
            l.Prefixes.Add(Url + "/"); l.Start();
            foreach (var vm in new[] { "web01", "db01" })
            {
                Files["[datastore1] " + vm + "/" + vm + ".vmx"] = Encoding.UTF8.GetBytes("displayName = \"" + vm + "\"\nscsi0:0.fileName = \"" + vm + ".vmdk\"\n");
                Files["[datastore1] " + vm + "/" + vm + ".nvram"] = new byte[8192];
                Files["[datastore1] " + vm + "/" + vm + ".vmdk"] = Encoding.UTF8.GetBytes("# Disk DescriptorFile\nRW 4096 VMFS \"" + vm + "-flat.vmdk\"\n");
                var disk = new byte[2 * 1024 * 1024]; new Random(vm.Length).NextBytes(disk);
                Files["[datastore1] " + vm + "/" + vm + "-flat.vmdk"] = disk;
                Files["[datastore1] " + vm + "/vmware.log"] = Encoding.UTF8.GetBytes("log");
            }
            new Thread(() => { while (l.IsListening) { try { var c = l.GetContext(); ThreadPool.QueueUserWorkItem(_ => Handle(c)); } catch { return; } } }) { IsBackground = true }.Start();
        }

        static string Esc(string s) { return System.Security.SecurityElement.Escape(s); }

        void Send(HttpListenerContext c, int st, string xml)
        {
            var b = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><soapenv:Body>" + xml + "</soapenv:Body></soapenv:Envelope>");
            c.Response.StatusCode = st; c.Response.ContentType = "text/xml"; c.Response.ContentLength64 = b.Length; c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close();
        }
        void Fault(HttpListenerContext c, string msg) { Send(c, 500, "<soapenv:Fault><faultcode>ServerFaultCode</faultcode><faultstring>" + Esc(msg) + "</faultstring></soapenv:Fault>"); }
        static string Ret(string op, string inner) { return "<" + op + "Response xmlns=\"urn:vim25\"><returnval>" + inner + "</returnval></" + op + "Response>"; }
        static string Prop(string name, string val) { return "<propSet><name>" + name + "</name><val>" + val + "</val></propSet>"; }

        string VmProps(string vm)
        {
            var name = VmNames[vm];
            var files = string.Concat(Files.Keys.Where(k => k.StartsWith("[datastore1] " + name + "/")).Select((k, i) =>
                "<VirtualMachineFileLayoutExFileInfo><key>" + i + "</key><name>" + Esc(k) + "</name><type>" + (k.EndsWith(".vmx") ? "config" : k.EndsWith(".nvram") ? "nvram" : k.EndsWith("-flat.vmdk") ? "diskExtent" : k.EndsWith(".vmdk") ? "diskDescriptor" : "log") + "</type><size>" + Files[k].Length + "</size></VirtualMachineFileLayoutExFileInfo>").ToArray());
            var snaps = string.Concat(Snaps[vm].Select(sn => "<VirtualMachineSnapshotTree><snapshot type=\"VirtualMachineSnapshot\">" + sn.Split(':')[0] + "</snapshot><vm type=\"VirtualMachine\">" + vm + "</vm><name>" + Esc(sn.Split(':')[1]) + "</name></VirtualMachineSnapshotTree>").ToArray());
            return "<objects><obj type=\"VirtualMachine\">" + vm + "</obj>" + Prop("name", Esc(name)) + Prop("layoutEx.file", files) + Prop("snapshot.rootSnapshotList", snaps) + Prop("config.template", "false") + "</objects>";
        }

        string NewTask(string result, string error = null) { var t = "task-" + (++seq); tasks[t] = new KeyValuePair<string, string>(result, error); return t; }

        void Handle(HttpListenerContext c)
        {
            try
            {
                var p = Uri.UnescapeDataString(c.Request.Url.AbsolutePath);
                var authed = c.Request.Headers["Cookie"] != null && c.Request.Headers["Cookie"].Contains("vmware_soap_session=s3cr3t");
                if (p.StartsWith("/folder/"))
                {
                    if (!authed) { c.Response.StatusCode = 401; c.Response.Close(); return; }
                    var key = "[" + c.Request.QueryString["dsName"] + "] " + p.Substring(8);
                    lock (Gate)
                    {
                        if (c.Request.HttpMethod == "PUT") { using (var ms = new MemoryStream()) { c.Request.InputStream.CopyTo(ms); Files[key] = ms.ToArray(); } Ops.Add("PUT " + key); c.Response.StatusCode = 201; c.Response.Close(); return; }
                        byte[] b; if (!Files.TryGetValue(key, out b)) { c.Response.StatusCode = 404; c.Response.Close(); return; }
                        var vm = VmNames.First(v => key.Contains("] " + v.Value + "/")).Key;
                        if (!Snaps[vm].Any(s => s.StartsWith("snapshot-") && !s.StartsWith("snapshot-old"))) DownloadsWithoutSnapshot.Add(key);
                        c.Response.StatusCode = 200; c.Response.ContentLength64 = b.Length; c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close(); return;
                    }
                }
                string body; using (var r = new StreamReader(c.Request.InputStream)) body = r.ReadToEnd();
                var x = XDocument.Parse(body).Root.Descendants().First(e => e.Name.LocalName == "Body").Elements().First();
                var op = x.Name.LocalName; Func<string, string> v = n => (string)x.Elements().FirstOrDefault(e => e.Name.LocalName == n);
                lock (Gate)
                {
                    Ops.Add(op);
                    if (op == "RetrieveServiceContent") { Send(c, 200, Ret(op, "<rootFolder type=\"Folder\">ha-folder-root</rootFolder><propertyCollector type=\"PropertyCollector\">ha-property-collector</propertyCollector><viewManager type=\"ViewManager\">ViewManager</viewManager><sessionManager type=\"SessionManager\">ha-sessionmgr</sessionManager><about><apiType>HostAgent</apiType><version>8.0.2</version></about>")); return; }
                    if (op == "Login")
                    {
                        if (v("userName") != "root" || v("password") != "Esxi-Pass-1") { Fault(c, "Cannot complete login due to an incorrect user name or password."); return; }
                        c.Response.AddHeader("Set-Cookie", "vmware_soap_session=s3cr3t; Path=/; HttpOnly"); Send(c, 200, Ret(op, "<key>x</key><userName>root</userName>")); return;
                    }
                    if (!authed) { Fault(c, "The session is not authenticated."); return; }
                    if (op == "Logout" || op == "DestroyView") { Send(c, 200, "<" + op + "Response xmlns=\"urn:vim25\"/>"); return; }
                    if (op == "CreateContainerView") { Send(c, 200, Ret(op, "session[1]view-" + v("type"))); return; }
                    if (op == "RetrievePropertiesEx")
                    {
                        var obj = x.Descendants().First(e => e.Name.LocalName == "obj"); var type = (string)obj.Attribute("type"); var id = obj.Value;
                        string objs;
                        if (type == "ContainerView" && id.EndsWith("VirtualMachine")) objs = string.Concat(VmNames.Keys.Select(VmProps).ToArray());
                        else if (type == "ContainerView" && id.EndsWith("Datacenter")) objs = "<objects><obj type=\"Datacenter\">ha-datacenter</obj>" + Prop("name", "ha-datacenter") + Prop("vmFolder", "ha-folder-vm") + "</objects>";
                        else if (type == "ContainerView" && id.EndsWith("ComputeResource")) objs = "<objects><obj type=\"ComputeResource\">ha-compute-res</obj>" + Prop("resourcePool", "ha-root-pool") + "</objects>";
                        else if (type == "VirtualMachine") objs = VmProps(id);
                        else if (type == "Task")
                        {
                            int n; polls.TryGetValue(id, out n); polls[id] = n + 1;
                            var t = tasks[id];
                            var info = n == 0 ? "<state>running</state>" : t.Value != null ? "<state>error</state><error><localizedMessage>" + Esc(t.Value) + "</localizedMessage></error>" : "<state>success</state><result>" + t.Key + "</result>";
                            objs = "<objects><obj type=\"Task\">" + id + "</obj>" + Prop("info", info) + "</objects>";
                        }
                        else objs = "";
                        Send(c, 200, Ret(op, objs)); return;
                    }
                    if (op == "CreateSnapshot_Task")
                    {
                        var vm = v("_this");
                        if (v("quiesce") == "true" && VmNames[vm] == "db01") { Send(c, 200, Ret(op, NewTask(null, "Cannot create a quiesced snapshot because VMware Tools is not running"))); return; }
                        var sn = "snapshot-" + (seq + 100); Snaps[vm].Add(sn + ":" + v("name"));
                        Send(c, 200, Ret(op, NewTask(sn))); return;
                    }
                    if (op == "RemoveSnapshot_Task") { var sn = v("_this"); foreach (var l2 in Snaps.Values) l2.RemoveAll(s => s.Split(':')[0] == sn); Send(c, 200, Ret(op, NewTask(""))); return; }
                    if (op == "RegisterVM_Task") { Registered.Add(v("path") + "|" + v("name") + "|" + v("pool") + "|" + v("_this")); Send(c, 200, Ret(op, NewTask("vm-99"))); return; }
                }
                Fault(c, "unknown method " + op);
            }
            catch (Exception e) { try { Fault(c, "fake: " + e.Message); } catch { } }
        }

        public void Dispose() { try { l.Stop(); l.Close(); } catch { } }
    }

    public class EnterpriseTests
    {
        static bool Have { get { var r = Environment.GetEnvironmentVariable("OB_RESTIC"); return !string.IsNullOrEmpty(r) && File.Exists(r); } }

        static string Script(Env env, string name, string body)
        {
            var f = Path.Combine(env.Dir("bin"), name);
            File.WriteAllText(f, "#!/bin/sh\n" + body);
            File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return f;
        }

        [Fact]
        public void VMware_SnapshotCopyOfEveryVm_SnapshotsAlwaysRemoved_RestoredAsANewVm()
        {
            if (!Have || OperatingSystem.IsWindows()) throw NotTested.Because("needs restic and Linux (the stand-in tools are shell scripts)");
            using (var esx = new FakeVSphere())
            using (var env = new Env())
            {
                Environment.SetEnvironmentVariable("OB_VSPHERE_FAST", "1");
                try
                {
                    env.CreateUser("vmcust", "Customer-Pass-1", 5);
                    var app = env.Agent("vmcust", "Customer-Pass-1");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1",
                        new BackupSetInfo { Name = "ESXi", Type = "VMWARE", Engine = "RESTIC", Vss = false, DbHost = esx.Url, SqlUser = "root" });
                    app.Home.SaveSecret(set.Id + "-sql", "wrong-pass");
                    var bad = app.Backup(set.Id);
                    Assert.NotEqual("BS_STOP_SUCCESS", bad.Result);
                    Assert.Contains(bad.LogLines, l => l.Contains("incorrect user name or password"));
                    Assert.DoesNotContain(bad.LogLines, l => l.Contains("wrong-pass"));

                    app.Home.SaveSecret(set.Id + "-sql", "Esxi-Pass-1");
                    var r1 = app.Backup(set.Id);
                    Assert.True(r1.Result.StartsWith("BS_STOP_SUCCESS"), string.Join("\n", r1.LogLines));
                    Assert.Contains(r1.LogLines, l => l.Contains("db01") && l.Contains("crash-consistent"));            // no VMware Tools
                    Assert.Contains(r1.LogLines, l => l.Contains("db01") && l.Contains("interrupted run was removed"));  // leftover snapshot
                    Assert.All(esx.Snaps.Values, s => Assert.Empty(s));                                                 // nothing left behind on the host
                    Assert.Empty(esx.DownloadsWithoutSnapshot);                                                         // every file was read under the snapshot
                    Assert.Contains("Logout", esx.Ops);

                    var s = app.Sets().First(x => x.Id == set.Id);
                    var rs = app.Restic(s, "Customer-Pass-1");
                    var files = rs.Ls(null).Where(f => f["type"] == "file").Select(f => f["path"]).ToList();
                    Assert.Contains(files, f => f.EndsWith("/web01/web01-flat.vmdk"));
                    Assert.Contains(files, f => f.EndsWith("/db01/db01.vmx"));
                    Assert.DoesNotContain(files, f => f.EndsWith("vmware.log"));                                         // logs are not part of the VM

                    // a few changed blocks: the next run sends only those
                    lock (esx.Gate) { var d = esx.Files["[datastore1] web01/web01-flat.vmdk"]; for (int i = 0; i < 4096; i++) d[1000000 + i] ^= 0x5a; }
                    var r2 = app.Backup(set.Id);
                    Assert.True(r2.Result.StartsWith("BS_STOP_SUCCESS"), string.Join("\n", r2.LogLines));

                    // restore web01 from the first point as a new VM on another datastore
                    var first = rs.SnapshotList().Last()["id"];
                    var tmp = env.Dir("vmrestore");
                    rs.RestoreMany(first, tmp, files.Where(f => f.Contains("/web01/")).ToList(), new List<string>());
                    var folder = Directory.GetDirectories(tmp, "web01", SearchOption.AllDirectories).First();
                    var lines = new List<string>();
                    Assert.Equal("vm-99", VMware.Restore(s, "Esxi-Pass-1", folder, "datastore2", "web01-restored", lines.Add));
                    var orig = new byte[2 * 1024 * 1024]; new Random("web01".Length).NextBytes(orig);
                    Assert.Equal(orig, esx.Files["[datastore2] web01-restored/web01-flat.vmdk"]);                      // the disk as it was at the first point
                    Assert.False(esx.Files.ContainsKey("[datastore2] web01-restored/vm-info.txt"));
                    Assert.Equal("[datastore2] web01-restored/web01.vmx|web01-restored|ha-root-pool|ha-folder-vm", esx.Registered.Single());
                }
                finally { Environment.SetEnvironmentVariable("OB_VSPHERE_FAST", null); }
            }
        }

        [Fact]
        public void Oracle_RmanOnlineBackup_PasswordOnlyOnStdin_FailedRunKeepsTheLastBackup()
        {
            if (!Have || OperatingSystem.IsWindows()) throw NotTested.Because("needs restic and Linux (the stand-in tools are shell scripts)");
            using (var env = new Env())
            {
                var log = Path.Combine(env.Dir("rmanlog"), "stdin.txt");
                var rman = Script(env, "rman",
                    "cat > '" + log + "'\necho \"args=$*\" >> '" + log + "'\necho \"sid=$ORACLE_SID\" >> '" + log + "'\n" +
                    "if [ -n \"$OB_FAKE_RMAN_FAIL\" ]; then echo 'ORA-19602: cannot backup or copy active file in NOARCHIVELOG mode'; echo 'RMAN-03009: failure of backup command'; exit 1; fi\n" +
                    "dir=$(grep -o \"FORMAT '[^']*'\" '" + log + "' | head -1 | sed \"s/FORMAT '//; s/'$//\" | xargs -0 dirname)\n" +
                    "head -c 400000 /dev/urandom > \"$dir/ORCL_20261004_01.bkp\"; head -c 20000 /dev/urandom > \"$dir/ctl_ORCL_20261004_02.bkp\"; head -c 3000 /dev/urandom > \"$dir/spfile_ORCL_20261004_03.bkp\"\n" +
                    "echo 'Recovery Manager complete.'\n");
                Environment.SetEnvironmentVariable("OB_RMAN", rman);
                try
                {
                    env.CreateUser("oracust", "Customer-Pass-1", 5);
                    var app = env.Agent("oracust", "Customer-Pass-1");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1",
                        new BackupSetInfo { Name = "Oracle", Type = "ORACLE", Engine = "RESTIC", Vss = false, SqlUser = "sys", Sources = { "ORCL" } });
                    app.Home.SaveSecret(set.Id + "-sql", "Ora-Sys-Pass-9");
                    var r1 = app.Backup(set.Id);
                    Assert.True(r1.Result.StartsWith("BS_STOP_SUCCESS"), string.Join("\n", r1.LogLines));
                    var stdin = File.ReadAllText(log);
                    Assert.Contains("CONNECT TARGET \"sys/\\\"Ora-Sys-Pass-9\\\" AS SYSDBA\"", stdin);            // the password reaches RMAN on its input…
                    Assert.Contains("args=\n", stdin);                                                               // …never on its command line
                    Assert.Contains("sid=ORCL", stdin);
                    Assert.Contains("PLUS ARCHIVELOG", stdin); Assert.Contains("FILESPERSET 1", stdin); Assert.DoesNotContain("COMPRESSED", stdin);
                    Assert.DoesNotContain(r1.LogLines, l => l.Contains("Ora-Sys-Pass-9"));
                    Assert.True(r1.LogLines.Any(l => l.Contains("[Oracle] ORCL: 3 pieces")), string.Join("\n", r1.LogLines));

                    // NOARCHIVELOG: the run fails with the reason, the previous backup stays in place
                    Environment.SetEnvironmentVariable("OB_FAKE_RMAN_FAIL", "1");
                    var r2 = app.Backup(set.Id);
                    Assert.NotEqual("BS_STOP_SUCCESS", r2.Result);
                    Assert.Contains(r2.LogLines, l => l.Contains("NOARCHIVELOG"));
                    Assert.Equal(3, Directory.GetFiles(Path.Combine(Oracle.Staging(app.Home, app.Sets().First(x => x.Id == set.Id)), "ORCL"), "*.bkp").Length);
                    Environment.SetEnvironmentVariable("OB_FAKE_RMAN_FAIL", null);

                    var rs = app.Restic(app.Sets().First(x => x.Id == set.Id), "Customer-Pass-1");
                    var files = rs.Ls(null).Where(f => f["type"] == "file").Select(f => f["path"]).ToList();
                    Assert.Contains(files, f => f.EndsWith("/ORCL/RESTORE-README.txt"));
                    Assert.Equal(3, files.Count(f => f.EndsWith(".bkp")));
                }
                finally { Environment.SetEnvironmentVariable("OB_RMAN", null); Environment.SetEnvironmentVariable("OB_FAKE_RMAN_FAIL", null); }
            }
        }

        [Fact]
        public void Domino_FoldersFromNotesIni_CacheFlushedBeforeTheCopy()
        {
            if (!Have || OperatingSystem.IsWindows()) throw NotTested.Because("needs restic and Linux (the stand-in tools are shell scripts)");
            using (var env = new Env())
            {
                var prog = env.Dir("domino-prog"); var data = env.Dir("notesdata"); var tlog = env.Dir("translog");
                Directory.CreateDirectory(Path.Combine(data, "mail"));
                File.WriteAllText(Path.Combine(data, "names.nsf"), "names"); File.WriteAllText(Path.Combine(data, "mail", "dana.nsf"), "dana's mail");
                File.WriteAllText(Path.Combine(tlog, "S0000001.TXN"), "txn");
                File.WriteAllText(Path.Combine(prog, "notes.ini"), "[Notes]\nDirectory=" + data + "\nTRANSLOG_Path=" + tlog + "\nKitType=2\n");
                var marker = Path.Combine(env.Dir("dom"), "console.txt");
                Environment.SetEnvironmentVariable("OB_DOMINO_INI", Path.Combine(prog, "notes.ini"));
                Environment.SetEnvironmentVariable("OB_DOMINO_CONSOLE", Script(env, "nserver", "echo \"$*\" >> '" + marker + "'\n"));
                try
                {
                    var src = Domino.DefaultSources();
                    Assert.Equal(new[] { data, tlog, Path.Combine(prog, "notes.ini") }, src.ToArray());
                    env.CreateUser("domcust", "Customer-Pass-1", 5);
                    var app = env.Agent("domcust", "Customer-Pass-1");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Domino", Type = "DOMINO", Engine = "RESTIC", Vss = true, Sources = src });
                    var r = app.Backup(set.Id);
                    Assert.True(r.Result.StartsWith("BS_STOP_SUCCESS"), string.Join("\n", r.LogLines));
                    Assert.Contains("-c dbcache flush", File.ReadAllText(marker));
                    Assert.Contains(r.LogLines, l => l.Contains("database cache flushed"));
                    var files = app.Restic(app.Sets().First(x => x.Id == set.Id), "Customer-Pass-1").Ls(null).Where(f => f["type"] == "file").Select(f => f["path"]).ToList();
                    Assert.Contains(files, f => f.EndsWith("/mail/dana.nsf")); Assert.Contains(files, f => f.EndsWith("/S0000001.TXN")); Assert.Contains(files, f => f.EndsWith("/notes.ini"));

                    // the own engine (Windows 2003+) takes the same folders
                    var own = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Domino2", Type = "DOMINO", Engine = "", Vss = true, Sources = src });
                    Assert.Equal("BS_STOP_SUCCESS", app.Backup(own.Id).Result);
                }
                finally { Environment.SetEnvironmentVariable("OB_DOMINO_INI", null); Environment.SetEnvironmentVariable("OB_DOMINO_CONSOLE", null); }
            }
        }
    }
}
