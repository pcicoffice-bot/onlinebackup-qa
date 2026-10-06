using System; using System.Collections.Generic; using System.Linq; using System.Reflection; using System.Threading; using System.Windows.Forms; using OnlineBackup.Agent; using OnlineBackup.Core;
class Fake : IClientApi {
  public int n;
  public Msg Call(string op, Msg body, IDictionary<string,string> q) {
    if (op == "state") return new Msg().Set("registered", 1).Set("computer", "PC1").Set("login", "dana").Set("product", "GOLAN").Set("session", 1)
      .Add("sets", new Msg().Set("id", "1").Set("name", "Docs").Set("mine", 1).Set("result", "").Set("last", "").Set("hour", "22:00"));
    if (op == "jobs") { n++; return new Msg().Add("jobs", new Msg().Set("id", "j" + n).Set("set", "1").Set("state", n % 2 == 0 ? "running" : "ok")); }   // changes every poll
    if (op == "update") throw new AgentException(0, "X", "no server");
    return new Msg();
  }
}
static class P {
  [STAThread] static void Main(string[] a) {
    Application.EnableVisualStyles();
    var f = new ClientForm(new Fake());
    var content = (Panel)typeof(ClientForm).GetField("content", BindingFlags.NonPublic|BindingFlags.Instance).GetValue(f);
    var pageF = typeof(ClientForm).GetField("page", BindingFlags.NonPublic|BindingFlags.Instance);
    var render = typeof(ClientForm).GetMethod("Render", BindingFlags.NonPublic|BindingFlags.Instance);
    var pages = new[] { "help", "new", "security", "restore", "status" }; int pi = 0, step = 0; List<TextBox> boxes = new List<TextBox>(); bool ok = true;
    var t = new System.Windows.Forms.Timer { Interval = 1000 };
    t.Tick += (s, e) => {
      step++;
      if (step % 10 == 1) { if (pi >= pages.Length) { t.Stop(); Console.WriteLine(ok ? "ALL KEPT" : "LOST"); Application.Exit(); return; }
        pageF.SetValue(f, pages[pi]); render.Invoke(f, null); boxes = content.Controls.OfType<TextBox>().Where(b => !b.ReadOnly).ToList(); foreach (var b in boxes) { b.Focus(); b.Text = "typed"; } }
      if (step % 10 == 9) { int lost = boxes.Count(b => b.IsDisposed || b.Text != "typed"); Console.WriteLine(pages[pi] + ": " + (boxes.Count == 0 ? "no field" : lost == 0 ? "kept (" + boxes.Count + " fields)" : "LOST " + lost + " of " + boxes.Count)); ok &= lost == 0; pi++; }
    };
    f.Shown += (s, e) => t.Start();
    Application.Run(f);
  }
}
