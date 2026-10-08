using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace OnlineBackup.ClientApp
{
    /// <summary>OnlineBackup.Client.exe [--tray]: the customer's screen of the backup service running on this computer.</summary>
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            var ui = OnlineBackup.Agent.ClientUi.UiFileIn(AppDomain.CurrentDomain.BaseDirectory);
            string url = null;
            for (int i = 0; i < 20 && url == null; i++)   // at Windows start-up the service may still be starting
            {
                try
                {
                    if (File.Exists(ui))
                    {
                        var u = OnlineBackup.Core.Atomic.ReadAllText(ui).Trim();
                        using (var t = new System.Net.Sockets.TcpClient()) t.Connect("127.0.0.1", new Uri(u).Port);
                        url = u;
                    }
                }
                catch (Exception) { }
                if (url == null) System.Threading.Thread.Sleep(args.Contains("--tray") ? 3000 : 500);
            }
            if (url == null)
            {
                MessageBox.Show("The backup service on this computer is not running. Start it in Windows Services, or run Setup again.", "Backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
            return OnlineBackup.Agent.ClientForm.Run(url, args.Contains("--tray"), () => OnlineBackup.Core.Atomic.ReadAllText(ui));
        }
    }
}
