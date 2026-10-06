using OnlineBackup.Agent;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// The folders ticked in "New backup" (FolderPicker's selection, without the window) — component contract (UX-27,
    /// found by the UX review in the code: the tree's "No access to this folder." line and the "…" placeholder have no
    /// folder; ticking one after a real folder threw NullReferenceException in the window — .NET's crash dialog):
    ///   input    tick C:\Data; tick a line with no folder; tick C:\Data\Old inside it and untick it; untick C:\Data
    ///   expected the line with no folder is refused (nothing changes, no exception); a sub-folder of a ticked folder
    ///            unticked becomes an exclusion; unticking the parent clears it
    /// </summary>
    public class FolderSelectionComponentTests
    {
        [Fact]
        public void ALineWithNoFolder_IsRefused_ATickedFolderStays()
        {
            var s = new FolderSelection();
            Assert.True(s.Toggle(@"C:\Data", true));
            Assert.False(s.Toggle(null, true));           // "No access to this folder." / "…"
            Assert.False(s.Toggle("", true));
            Assert.Equal(new[] { @"C:\Data" }, s.Included.ToArray());
            Assert.Empty(s.Excluded);
        }

        [Fact]
        public void ASubFolderUnticked_IsAnExclusion_AndTheParentUntickedClearsAll()
        {
            var s = new FolderSelection();
            s.Toggle(@"C:\Data", true);
            s.Toggle(@"C:\Data\Old", false);
            Assert.Equal(new[] { @"C:\Data\Old" }, s.Excluded.ToArray());
            s.Toggle(@"c:\data\old", true);               // ticked again (another case): the exclusion goes
            Assert.Empty(s.Excluded);
            Assert.Equal(new[] { @"C:\Data" }, s.Included.ToArray());   // inside a ticked folder: not added twice
            s.Toggle(@"C:\Data", false);
            Assert.Empty(s.Included);
        }
    }
}
