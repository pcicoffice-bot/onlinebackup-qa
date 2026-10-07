using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// UI-07 (pilot: Hebrew and English) — COMPONENT layer, the messages the server and the agent send to the pilot's
    /// screens. The screens show every such message through tr() (app.js api(), client.html api(), setup-client.html), so a
    /// message without a Hebrew entry is shown in English on a Hebrew screen. Contract (specs.py UI-07): no missing keys.
    /// </summary>
    public class PilotI18nFailingComponentTests
    {
        static readonly Regex CommandLineFlag = new Regex(@"--[a-z][a-z-]*");   // a command-line option the user types as it is (--accept-contract) is a name, not a sentence
        static readonly Regex EnglishWord = new Regex(@"\b(?!restic\b)[a-z]{3,}\b");   // a lowercase English word (names are capitalised; restic is a name)

        /// <summary>
        /// UI-07 boundary: the messages a technician or a customer meets on the pilot's own screens — the admin sign-in,
        /// the set editor's refusals, the pilot's refusals (each reason), the client window, the installation wizard —
        /// arrive in Hebrew: L.Tr("he", message) leaves no English word. Every message that does not is listed.
        /// </summary>
        [Fact]
        public void UI07_TheMessagesOfThePilotScreens_ArriveInHebrew_NotInEnglish()
        {
            var messages = new List<string>
            {
                // the admin site's sign-in (Staff.Check, Api.Admin)
                "The account is locked after wrong passwords. Try again later or ask the main administrator.",
                "Set up two-step verification first.",
                // the set editor's refusals (SetControl.Save)
                "Give the set a name.", "The time is not valid.", "At most 12 times a day.", "The maximum duration is 1 to 168 hours, or no limit.",
                "Choose how long to keep versions — unlimited is not allowed.", "The backup set was not found.",
                // the pilot's refusals, as the admin site, the client window and the wizard receive them (Core.Scope / Server.PilotScope)
                Scope.Refusal(new BackupSetInfo { Type = "MSSQL" }), Scope.Refusal(new BackupSetInfo { Type = "SYSTEMSTATE" }),
                Scope.Refused("The restic engine"), Scope.Refused("Running commands before or after the backup"), Scope.Refused("A local copy (local disk or network folder)"),
                Scope.Refused("Opening a new account from the client software"), Scope.Refused("Moving a computer to another customer"), Scope.OldWindowsMessage,
                // the installation wizard (Setup.Connect, SetupUi.CheckServer)
                "The agreement was not accepted — the installation stopped. (Unattended: --accept-contract)",
                "The server's certificate is not the one this software was made for. Ask your IT company.",
            };
            var english = messages.Select(m => new { m, he = L.Tr("he", m) }).Where(x => EnglishWord.IsMatch(CommandLineFlag.Replace(x.he, ""))).Select(x => "\"" + x.m + "\" → \"" + x.he + "\"").ToList();
            Assert.True(english.Count == 0, english.Count + " of " + messages.Count + " messages of the pilot's screens are shown in English on a Hebrew screen:\n" + string.Join("\n", english));
        }
    }
}
