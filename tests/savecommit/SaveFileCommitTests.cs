using System;
using System.Diagnostics;
using System.IO;
using HaRepacker.GUI;
using Xunit;
using Assert = Xunit.Assert;

namespace SaveCommitTests;

/// <summary>
/// Targeted regression for SaveForm's file replacement (audit P1-3).
///
/// The old flow was Delete(original) then Move(temp) - a failure between the two left nothing at
/// the target - and a failed .img copy was swallowed while the tree node was deleted anyway.
/// SaveFileCommit.Replace is the rule both paths now go through: the original is moved aside
/// first, and every failure reports where the surviving copies are instead of throwing.
///
/// SCOPE - not covered here: the SaveForm dialog itself (its wiring to this helper - abort on
/// failure, node kept, reload on success - is by code reading and manual test).
/// </summary>
public sealed class SaveFileCommitTests : IDisposable
{
    private readonly string root;

    public SaveFileCommitTests()
    {
        root = Path.Combine(Path.GetTempPath(), "ShuibbFixValidation", "commit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ReplacingAnExistingTarget_PutsTheNewBytesThere_AndLeavesNoBackup()
    {
        string temp = Write("new$tmp", "NEW");
        string target = Write("target.wz", "OLD");

        var result = SaveFileCommit.Replace(temp, target);

        Assert.True(result.Success);
        Assert.Equal("NEW", File.ReadAllText(target));
        Assert.False(File.Exists(temp));
        Assert.Empty(Directory.GetFiles(root, "*.bak*"));
    }

    [Fact]
    public void SavingToAFreshPath_JustMovesTheTempThere()
    {
        string temp = Write("new$tmp", "NEW");
        string target = Path.Combine(root, "fresh.wz");

        var result = SaveFileCommit.Replace(temp, target);

        Assert.True(result.Success);
        Assert.Equal("NEW", File.ReadAllText(target));
    }

    [Fact]
    public void MissingTemp_FailsWithoutTouchingTheTarget()
    {
        string target = Write("target.wz", "OLD");

        var result = SaveFileCommit.Replace(Path.Combine(root, "never-written$tmp"), target);

        Assert.False(result.Success);
        Assert.Equal("OLD", File.ReadAllText(target));
    }

    [Fact]
    public void TargetLockedByAnotherProcess_Fails_OriginalIntact_TempKept()
    {
        string temp = Write("new$tmp", "NEW");
        string target = Write("target.wz", "OLD");

        // The game client - or another editor - holding the file open.
        using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = SaveFileCommit.Replace(temp, target);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            Assert.Equal(temp, result.TempPathKept);
        }
        Assert.Equal("OLD", File.ReadAllText(target)); // original never moved
        Assert.Equal("NEW", File.ReadAllText(temp));   // finished bytes still recoverable
    }

    [Fact]
    public void AccessDeniedTargetDirectory_Fails_WithoutThrowing()
    {
        // A directory the user may not write into - the Program Files case. Simulated with an
        // ACL deny on a temp folder so no real Data directory is ever touched.
        string denied = Path.Combine(root, "denied");
        Directory.CreateDirectory(denied);
        string target = Path.Combine(denied, "target.wz");
        string temp = Write("new$tmp", "NEW");

        RunIcacls($"\"{denied}\" /deny \"{Environment.UserName}:(OI)(CI)W\"");
        try
        {
            var ex = Record.Exception(() =>
            {
                var result = SaveFileCommit.Replace(temp, target);
                Assert.False(result.Success);
                Assert.NotNull(result.Error);
            });
            Assert.Null(ex); // a permission problem must come back as a result, never as a throw
            Assert.True(File.Exists(temp));
        }
        finally
        {
            RunIcacls($"\"{denied}\" /remove:d \"{Environment.UserName}\"");
        }
    }

    /// <summary>
    /// The .img decision this helper feeds: only Success may lead to DeleteWzNode. A failed
    /// commit returns Success=false with the temp still on disk - the caller keeps the node.
    /// </summary>
    [Fact]
    public void FailedCommit_LeavesEverythingRecoverable_SoTheCallerKeepsTheNode()
    {
        string temp = Write("img$tmp", "EDITED-IMG");
        string target = Write("0002000.img", "ORIGINAL-IMG");

        using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = SaveFileCommit.Replace(temp, target);
            Assert.False(result.Success);
        }

        Assert.Equal("ORIGINAL-IMG", File.ReadAllText(target));
        Assert.Equal("EDITED-IMG", File.ReadAllText(temp));
    }

    private static void RunIcacls(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("icacls", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        p.WaitForExit(15000);
    }
}
