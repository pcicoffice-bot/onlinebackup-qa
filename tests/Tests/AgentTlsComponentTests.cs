using System.Net;
using OnlineBackup.Agent;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Bug 129 (research candidate (ח)): the agent's connection to the backup server allowed TLS 1.0 next to TLS 1.2
    /// (kept for Windows 2003/2008). The oldest supported system is now Windows Server 2012 (owner decision), which has
    /// TLS 1.2 - the agent offers TLS 1.2 (and what newer the system has), never TLS 1.0 or 1.1.
    /// ORACLE: the process-wide protocol set the agent's client leaves after it is created (the one HttpWebRequest uses).
    /// </summary>
    public class AgentTlsComponentTests
    {
        [Fact]
        public void TheAgentsClient_OffersTls12_NeverTls10Or11()
        {
            var before = ServicePointManager.SecurityProtocol;
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;   // as an old process might leave it
                new Client("https://backup.example.invalid");
                var p = ServicePointManager.SecurityProtocol;
                Assert.True((p & SecurityProtocolType.Tls12) != 0, "TLS 1.2 is not offered: " + p);
                Assert.True((p & (SecurityProtocolType.Tls | SecurityProtocolType.Tls11)) == 0, "the agent still offers an old TLS version: " + p);
            }
            finally { ServicePointManager.SecurityProtocol = before; }
        }
    }
}
