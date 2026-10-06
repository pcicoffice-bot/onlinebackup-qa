// UI-030: the client's sign-in screen keeps what is typed (the poll must not draw it again). mono: mcs -r:OnlineBackup.Agent.exe -r:OnlineBackup.Core.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll typing-check.cs; xvfb-run mono typing-check.exe
using System; using System.Collections.Generic; using System.Linq; using System.Reflection; using System.Threading; using System.Windows.Forms; using OnlineBackup.Agent; using OnlineBackup.Core;
class Fake : IClientApi {
  public int calls;
  public Msg Call(string op, Msg body, IDictionary<string,string> q) {
    Interlocked.Increment(ref calls);
    if (op == "state") return new Msg().Set("registered", 0).Set("computer", "PC1").Set("login", "").Set("product", "GOLAN").Set("defaultServer", "https://backup.example.com:8443").Set("session", 0);
    if (op == "jobs") return new Msg();
    if (op == "update") throw new AgentException(0, "X", "no server");
    return new Msg();
  }
}
static class P {
  [STAThread] static void Main() {
    Application.EnableVisualStyles();
    var api = new Fake(); var f = new ClientForm(api);
    var content = (Panel)typeof(ClientForm).GetField("content", BindingFlags.NonPublic|BindingFlags.Instance).GetValue(f);
    TextBox box = null; List<TextBox> all = new List<TextBox>(); int step = 0;
    var t = new System.Windows.Forms.Timer { Interval = 1000 };
    t.Tick += (s, e) => {
      step++;
      if (step == 3) {
        // the "new customer" radio
        var rb = content.Controls.OfType<RadioButton>().ToList();
        Console.WriteLine("radios " + rb.Count); if (rb.Count > 1) rb[1].Checked = true;
      }
      if (step == 5) { all = content.Controls.OfType<TextBox>().ToList(); foreach (var b in all) { b.Focus(); b.Text = "Acme"; } box = all.First(); Console.WriteLine("typed into " + all.Count + " fields"); }
      if (step > 5 && step % 3 == 0) {
        int lost = all.Count(b => b.IsDisposed || b.Text != "Acme");
        Console.WriteLine("t=" + step + " fields=" + all.Count + " lost=" + lost + " disposed=" + (lost > 0) + " calls=" + api.calls);
      }
      if (step == 20) { t.Stop(); Application.Exit(); }
    };
    f.Shown += (s, e) => t.Start();
    Application.Run(f);
  }
}
