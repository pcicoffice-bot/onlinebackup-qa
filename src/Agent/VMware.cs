using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// VMW-010: a small client of the vSphere Web Services API (SOAP, vim25) — ESXi hosts and vCenter alike, with no
    /// VMware library to install. Sign-in with a user of the host / vCenter (its password stays on this computer); a
    /// self-signed certificate is trusted by its SHA-256 only (the set's VM_THUMBPRINT).
    /// </summary>
    public sealed class VSphere : IDisposable
    {
        static readonly XNamespace V = "urn:vim25";
        readonly string host; readonly CookieContainer cookies = new CookieContainer();
        public string RootFolder, PropertyCollector, ViewManager, SessionManager, ApiType = "", Version = "";
        public string Datacenter = "ha-datacenter";

        public VSphere(string host, string thumbprint)
        {
            this.host = host;
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch (NotSupportedException) { }
            Client.TrustHost(new Uri(Base).Host, thumbprint);
            var sc = Call("<RetrieveServiceContent xmlns=\"urn:vim25\"><_this type=\"ServiceInstance\">ServiceInstance</_this></RetrieveServiceContent>").Element(V + "returnval");
            RootFolder = (string)sc.Element(V + "rootFolder"); PropertyCollector = (string)sc.Element(V + "propertyCollector");
            ViewManager = (string)sc.Element(V + "viewManager"); SessionManager = (string)sc.Element(V + "sessionManager");
            var about = sc.Element(V + "about");
            if (about != null) { ApiType = (string)about.Element(V + "apiType") ?? ""; Version = (string)about.Element(V + "version") ?? ""; }
        }

        /// <summary>https://host (an address with its scheme is taken as it is: tests).</summary>
        public string Base { get { return host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? host.TrimEnd('/') : "https://" + host; } }

        static string X(string s) { return SecurityElement.Escape(s ?? ""); }

        /// <summary>One SOAP call; a fault becomes an exception with vSphere's own message (never the request: it may hold the password).</summary>
        public XElement Call(string body)
        {
            var env = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><soapenv:Body>" + body + "</soapenv:Body></soapenv:Envelope>";
            var b = Encoding.UTF8.GetBytes(env);
            for (int attempt = 0; ; attempt++)
            {
                var r = (HttpWebRequest)WebRequest.Create(Base + "/sdk");
                r.Method = "POST"; r.ContentType = "text/xml; charset=utf-8"; r.Headers["SOAPAction"] = "urn:vim25/6.7"; r.CookieContainer = cookies; r.Timeout = 300000;
                r.ContentLength = b.Length;
                try
                {
                    using (var s = r.GetRequestStream()) s.Write(b, 0, b.Length);
                    using (var resp = (HttpWebResponse)r.GetResponse()) using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        return XDocument.Parse(rd.ReadToEnd()).Root.Descendants().First(e => e.Name.LocalName == "Body").Elements().First();
                }
                catch (WebException e)
                {
                    var resp = e.Response as HttpWebResponse;
                    if (resp == null) { if (attempt < 2) { Thread.Sleep(3000); continue; } throw new AgentException(0, "VMWARE", "vSphere " + host + ": " + e.Message); }
                    string msg = "HTTP " + (int)resp.StatusCode;
                    try
                    {
                        using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        {
                            var fault = XDocument.Parse(rd.ReadToEnd()).Descendants().FirstOrDefault(x => x.Name.LocalName == "faultstring");
                            if (fault != null) msg = fault.Value;
                        }
                    }
                    catch (Exception) { }
                    throw new AgentException(0, "VMWARE", "vSphere: " + msg);
                }
            }
        }

        public void Login(string user, string password)
        {
            Call("<Login xmlns=\"urn:vim25\"><_this type=\"SessionManager\">" + X(SessionManager) + "</_this><userName>" + X(user) + "</userName><password>" + X(password) + "</password></Login>");
        }

        public void Dispose()
        {
            try { Call("<Logout xmlns=\"urn:vim25\"><_this type=\"SessionManager\">" + X(SessionManager) + "</_this></Logout>"); } catch (Exception) { }
        }

        /// <summary>Properties of every object of a type (a container view over the whole inventory).</summary>
        public List<KeyValuePair<string, XElement>> All(string type, params string[] props)
        {
            var view = (string)Call("<CreateContainerView xmlns=\"urn:vim25\"><_this type=\"ViewManager\">" + X(ViewManager) + "</_this><container type=\"Folder\">" + X(RootFolder) + "</container><type>" + type + "</type><recursive>true</recursive></CreateContainerView>").Element(V + "returnval");
            try
            {
                var spec = "<specSet><propSet><type>" + type + "</type>" + string.Concat(props.Select(p => "<pathSet>" + p + "</pathSet>").ToArray()) + "</propSet>"
                    + "<objectSet><obj type=\"ContainerView\">" + X(view) + "</obj><skip>true</skip><selectSet xsi:type=\"TraversalSpec\"><name>v</name><type>ContainerView</type><path>view</path><skip>false</skip></selectSet></objectSet></specSet>";
                return Retrieve(spec);
            }
            finally { try { Call("<DestroyView xmlns=\"urn:vim25\"><_this type=\"ContainerView\">" + X(view) + "</_this></DestroyView>"); } catch (Exception) { } }
        }

        /// <summary>Properties of one object.</summary>
        public XElement Get(string type, string moref, params string[] props)
        {
            var spec = "<specSet><propSet><type>" + type + "</type>" + string.Concat(props.Select(p => "<pathSet>" + p + "</pathSet>").ToArray()) + "</propSet><objectSet><obj type=\"" + type + "\">" + X(moref) + "</obj></objectSet></specSet>";
            var l = Retrieve(spec);
            return l.Count == 0 ? new XElement("none") : l[0].Value;
        }

        List<KeyValuePair<string, XElement>> Retrieve(string spec)
        {
            var list = new List<KeyValuePair<string, XElement>>();
            var res = Call("<RetrievePropertiesEx xmlns=\"urn:vim25\"><_this type=\"PropertyCollector\">" + X(PropertyCollector) + "</_this>" + spec + "<options/></RetrievePropertiesEx>").Element(V + "returnval");
            while (res != null)
            {
                foreach (var o in res.Elements(V + "objects")) list.Add(new KeyValuePair<string, XElement>((string)o.Element(V + "obj"), o));
                var token = (string)res.Element(V + "token");
                if (token == null) break;
                res = Call("<ContinueRetrievePropertiesEx xmlns=\"urn:vim25\"><_this type=\"PropertyCollector\">" + X(PropertyCollector) + "</_this><token>" + X(token) + "</token></ContinueRetrievePropertiesEx>").Element(V + "returnval");
            }
            return list;
        }

        public static XElement Prop(XElement obj, string name)
        {
            var p = obj.Elements(V + "propSet").FirstOrDefault(x => (string)x.Element(V + "name") == name);
            return p == null ? null : p.Element(V + "val");
        }

        /// <summary>Waits for a task; returns its result element (a snapshot, a registered VM) or throws vSphere's error.</summary>
        public XElement Wait(string task, int minutes = 120)
        {
            var until = SystemClock.UtcNow.AddMinutes(minutes);
            while (true)
            {
                var info = Prop(Get("Task", task, "info"), "info");
                var state = info == null ? null : (string)info.Element(V + "state");
                if (state == "success") return info.Element(V + "result");
                if (state == "error")
                {
                    var err = info.Element(V + "error");
                    var m = err == null ? "error" : ((string)err.Element(V + "localizedMessage") ?? err.Value);
                    throw new AgentException(0, "VMWARE", "vSphere task failed: " + m);
                }
                if (SystemClock.UtcNow > until) throw new AgentException(0, "VMWARE", "vSphere task did not finish in " + minutes + " minutes");
                Thread.Sleep(Environment.GetEnvironmentVariable("OB_VSPHERE_FAST") != null ? 50 : 2000);
            }
        }

        public string Task(string body) { return (string)Call(body).Element(V + "returnval"); }

        // ---------------------------------------------------------------- datastore files (the host's /folder interface)

        /// <summary>"[datastore1] vm1/vm1.vmx" → datastore, path.</summary>
        public static KeyValuePair<string, string> DsPath(string dsPath)
        {
            int a = dsPath.IndexOf('['), b = dsPath.IndexOf(']');
            if (a != 0 || b < 0) throw new AgentException(0, "VMWARE", "not a datastore path: " + dsPath);
            return new KeyValuePair<string, string>(dsPath.Substring(1, b - 1), dsPath.Substring(b + 1).Trim());
        }

        string FolderUrl(string ds, string path)
        {
            return Base + "/folder/" + string.Join("/", path.Split('/').Select(Uri.EscapeDataString).ToArray()) + "?dcPath=" + Uri.EscapeDataString(Datacenter) + "&dsName=" + Uri.EscapeDataString(ds);
        }

        public long Download(string dsPath, string toFile)
        {
            var p = DsPath(dsPath);
            var r = (HttpWebRequest)WebRequest.Create(FolderUrl(p.Key, p.Value));
            r.CookieContainer = cookies; r.Timeout = 300000; r.ReadWriteTimeout = 1800000;
            Directory.CreateDirectory(Path.GetDirectoryName(toFile));
            try
            {
                using (var resp = (HttpWebResponse)r.GetResponse()) using (var s = resp.GetResponseStream()) using (var f = File.Create(toFile)) s.CopyTo(f, 1 << 20);
            }
            catch (WebException e) { throw new AgentException(0, "VMWARE", "download " + dsPath + ": " + e.Message); }
            return new FileInfo(toFile).Length;
        }

        public void Upload(string file, string ds, string path)
        {
            var r = (HttpWebRequest)WebRequest.Create(FolderUrl(ds, path));
            r.Method = "PUT"; r.CookieContainer = cookies; r.Timeout = 300000; r.ReadWriteTimeout = 1800000;
            r.ContentType = "application/octet-stream"; r.ContentLength = new FileInfo(file).Length; r.AllowWriteStreamBuffering = false;
            try
            {
                using (var s = r.GetRequestStream()) using (var f = File.OpenRead(file)) f.CopyTo(s, 1 << 20);
                using (r.GetResponse()) { }
            }
            catch (WebException e) { throw new AgentException(0, "VMWARE", "upload " + path + ": " + e.Message); }
        }
    }

    /// <summary>
    /// VMW-020..040: VMware ESXi / vCenter virtual machines, agentless (as Ahsay's VMware module without CBT).
    /// Per VM: leftovers of an earlier interrupted run are removed, a quiesced snapshot is taken (VMware Tools; without
    /// them a crash-consistent one, with a warning), the VM's files as they were at the snapshot — .vmx, .nvram and every
    /// virtual disk (descriptor + data) — are copied from the datastore into &lt;staging&gt;\&lt;VM&gt;.new, the snapshot is
    /// removed (always, also after a failure), and the copy replaces &lt;staging&gt;\&lt;VM&gt;. Stable paths: restic stores
    /// only the changed parts of the disks (every run still reads each disk in full — no changed-block tracking).
    /// The free ESXi licence has a read-only API: snapshots need a licensed host (vSphere Essentials and up).
    /// Restore: the VM's files are uploaded into a new folder of a datastore and registered as a new VM.
    /// Sources: VM names (none / "*" = every VM). Host: DB_HOST, user: ADMIN_USERNAME, password: sql-password.
    /// </summary>
    public static class VMware
    {
        public const string Type = "VMWARE";
        const string SnapName = "OnlineBackup";
        static readonly XNamespace V = "urn:vim25";
        static readonly string[] Kept = { "config", "nvram", "diskDescriptor", "diskExtent" };

        public static string Staging(AgentHome home, BackupSetInfo set)
        {
            return string.IsNullOrEmpty(set.WorkingDir) ? Path.Combine(home.SetDir(set.Id), "vmware") : Path.Combine(set.WorkingDir, "vmware-" + set.Id);
        }

        public static VSphere Connect(BackupSetInfo set, string password)
        {
            if (string.IsNullOrEmpty(set.DbHost)) throw new AgentException(0, "VMWARE", "No ESXi / vCenter address");
            if (string.IsNullOrEmpty(set.SqlUser) || string.IsNullOrEmpty(password)) throw new AgentException(0, "VMWARE", "The vSphere user or its password is not set on this computer (sql-password)");
            var v = new VSphere(set.DbHost, set.VmThumbprint);
            v.Login(set.SqlUser, password);
            if (!string.IsNullOrEmpty(set.VmDatacenter)) v.Datacenter = set.VmDatacenter;
            else if (v.ApiType == "VirtualCenter")
            {
                var dcs = v.All("Datacenter", "name").Select(d => (string)VSphere.Prop(d.Value, "name")).ToList();
                if (dcs.Count != 1) { v.Dispose(); throw new AgentException(0, "VMWARE", "vCenter has " + dcs.Count + " datacenters: choose one in the set (" + string.Join(", ", dcs.ToArray()) + ")"); }
                v.Datacenter = dcs[0];
            }
            return v;
        }

        static IEnumerable<KeyValuePair<string, string>> Snapshots(XElement list)
        {
            if (list == null) yield break;
            foreach (var t in list.Elements().Where(e => e.Name.LocalName == "ManagedObjectReference" || e.Name.LocalName == "VirtualMachineSnapshotTree" || e.Element(V + "snapshot") != null))
            {
                yield return new KeyValuePair<string, string>((string)t.Element(V + "name"), (string)t.Element(V + "snapshot"));
                foreach (var c in Snapshots(new XElement("x", t.Elements(V + "childSnapshotList")))) yield return c;
            }
        }

        /// <summary>VMW-020: backs up the chosen VMs; returns the staging folder. A VM that fails keeps its last copy.</summary>
        public static string Backup(AgentHome home, BackupSetInfo set, string password, Action<string> info, Action<string> warn)
        {
            var staging = Staging(home, set);
            Directory.CreateDirectory(staging);
            using (var v = Connect(set, password))
            {
                info("[VMware] " + v.ApiType + " " + v.Version + " at " + set.DbHost + ", datacenter " + v.Datacenter);
                var vms = v.All("VirtualMachine", "name", "layoutEx.file", "snapshot.rootSnapshotList", "config.template");
                var wanted = set.Sources.Where(s => !string.IsNullOrWhiteSpace(s) && s != "*").Select(s => s.Trim()).ToList();
                var chosen = vms.Where(x => (string)VSphere.Prop(x.Value, "config.template") != "true"
                    && (wanted.Count == 0 || wanted.Any(w => string.Equals(w, (string)VSphere.Prop(x.Value, "name"), StringComparison.OrdinalIgnoreCase)))).ToList();
                foreach (var w in wanted.Where(w => !vms.Any(x => string.Equals(w, (string)VSphere.Prop(x.Value, "name"), StringComparison.OrdinalIgnoreCase)))) warn("[VMware] " + w + ": no such virtual machine");
                if (chosen.Count == 0) throw new AgentException(0, "VMWARE", "No virtual machine to back up");
                int ok = 0;
                foreach (var vm in chosen)
                {
                    var moref = vm.Key; var vmName = (string)VSphere.Prop(vm.Value, "name");
                    var name = M365Sync.Safe(vmName, 100);
                    var final = Path.Combine(staging, name); var tmp = Path.Combine(staging, name + ".new");
                    string snap = null;
                    try
                    {
                        // a snapshot left by an interrupted run goes first (it would grow forever)
                        foreach (var old in Snapshots(VSphere.Prop(vm.Value, "snapshot.rootSnapshotList")).Where(s => s.Key == SnapName).ToList())
                        {
                            v.Wait(v.Task("<RemoveSnapshot_Task xmlns=\"urn:vim25\"><_this type=\"VirtualMachineSnapshot\">" + SecurityElement.Escape(old.Value) + "</_this><removeChildren>false</removeChildren><consolidate>true</consolidate></RemoveSnapshot_Task>"));
                            warn("[VMware] " + vmName + ": a snapshot of an interrupted run was removed");
                        }
                        var files = VSphere.Prop(v.Get("VirtualMachine", moref, "layoutEx.file"), "layoutEx.file");
                        var keep = files == null ? new List<string>() : files.Elements().Where(f => Kept.Contains((string)f.Element(V + "type"))).Select(f => (string)f.Element(V + "name")).Distinct().ToList();
                        if (!keep.Any(f => f.EndsWith(".vmx", StringComparison.OrdinalIgnoreCase))) throw new AgentException(0, "VMWARE", "the VM's files were not found");
                        var t0 = SystemClock.UtcNow;
                        var create = "<CreateSnapshot_Task xmlns=\"urn:vim25\"><_this type=\"VirtualMachine\">" + SecurityElement.Escape(moref) + "</_this><name>" + SnapName + "</name><description>Online backup in progress</description><memory>false</memory><quiesce>{Q}</quiesce></CreateSnapshot_Task>";
                        try { snap = (string)v.Wait(v.Task(create.Replace("{Q}", "true"))); }
                        catch (AgentException e)
                        {
                            warn("[VMware] " + vmName + ": quiesced snapshot not possible (" + e.Message + "): crash-consistent copy");
                            snap = (string)v.Wait(v.Task(create.Replace("{Q}", "false")));
                        }
                        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                        Directory.CreateDirectory(tmp);
                        long bytes = 0;
                        foreach (var f in keep) bytes += v.Download(f, Path.Combine(tmp, M365Sync.Safe(Path.GetFileName(VSphere.DsPath(f).Value), 150)));
                        var vmx = keep.First(f => f.EndsWith(".vmx", StringComparison.OrdinalIgnoreCase));
                        File.WriteAllText(Path.Combine(tmp, "vm-info.txt"), "name=" + vmName + "\r\nvmx=" + vmx + "\r\nhost=" + set.DbHost + "\r\ndatacenter=" + v.Datacenter + "\r\nfiles=" + string.Join(";", keep.ToArray()) + "\r\n", Encoding.UTF8);
                        if (Directory.Exists(final)) Directory.Delete(final, true);
                        Directory.Move(tmp, final);
                        ok++;
                        info("[VMware] " + vmName + ": " + keep.Count + " files, " + bytes + " bytes in " + (int)(SystemClock.UtcNow - t0).TotalSeconds + "s");
                    }
                    catch (Exception e)
                    {
                        try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch (IOException) { }
                        warn("[VMware] " + vmName + " not backed up (its last copy is kept): " + e.Message);
                    }
                    finally
                    {
                        if (snap != null)
                            try { v.Wait(v.Task("<RemoveSnapshot_Task xmlns=\"urn:vim25\"><_this type=\"VirtualMachineSnapshot\">" + SecurityElement.Escape(snap) + "</_this><removeChildren>false</removeChildren><consolidate>true</consolidate></RemoveSnapshot_Task>")); }
                            catch (Exception e) { warn("[VMware] " + vmName + ": the backup snapshot could not be removed — remove \"" + SnapName + "\" in vSphere: " + e.Message); }
                    }
                }
                if (ok == 0) throw new AgentException(0, "VMWARE", "No virtual machine was backed up");
            }
            return staging;
        }

        /// <summary>VMW-040: a restored VM folder → a new folder on a datastore, registered as a new VM (the original is never touched).</summary>
        public static string Restore(BackupSetInfo set, string password, string folder, string datastore, string newName, Action<string> info)
        {
            var vmx = Directory.GetFiles(folder, "*.vmx").FirstOrDefault();
            if (vmx == null) throw new AgentException(0, "VMWARE", "no .vmx in " + folder);
            using (var v = Connect(set, password))
            {
                var dc = v.All("Datacenter", "name", "vmFolder").FirstOrDefault(d => (string)VSphere.Prop(d.Value, "name") == v.Datacenter || v.Datacenter == "ha-datacenter");
                if (dc.Key == null) throw new AgentException(0, "VMWARE", "datacenter " + v.Datacenter + " not found");
                var vmFolder = (string)VSphere.Prop(dc.Value, "vmFolder");
                var pool = v.All("ComputeResource", "resourcePool").Select(c => (string)VSphere.Prop(c.Value, "resourcePool")).FirstOrDefault();
                if (pool == null) throw new AgentException(0, "VMWARE", "no resource pool");
                var dir = M365Sync.Safe(newName, 80);
                foreach (var f in Directory.GetFiles(folder).Where(f => !f.EndsWith("vm-info.txt", StringComparison.OrdinalIgnoreCase)))
                {
                    v.Upload(f, datastore, dir + "/" + Path.GetFileName(f));
                    info("uploaded " + Path.GetFileName(f));
                }
                var result = v.Wait(v.Task("<RegisterVM_Task xmlns=\"urn:vim25\"><_this type=\"Folder\">" + SecurityElement.Escape(vmFolder) + "</_this><path>[" + SecurityElement.Escape(datastore) + "] " + SecurityElement.Escape(dir + "/" + Path.GetFileName(vmx)) + "</path><name>" + SecurityElement.Escape(newName) + "</name><asTemplate>false</asTemplate><pool type=\"ResourcePool\">" + SecurityElement.Escape(pool) + "</pool></RegisterVM_Task>"));
                info("registered " + newName + " (" + (string)result + ") — when it starts, answer \"I copied it\"");
                return (string)result;
            }
        }
    }
}
