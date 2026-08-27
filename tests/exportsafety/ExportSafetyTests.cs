using System;
using System.Diagnostics;
using System.IO;
using HaRepacker.GUI;
using Xunit;
using Assert = Xunit.Assert;

namespace ExportSafetyTests;

/// <summary>
/// Targeted regression for the export crash family (audit P1): a denied subdirectory aborting the
/// whole Data folder scan with an exception that closed the app, and export worker threads with
/// no outer catch.
///
/// SCOPE - not covered here: the MainForm wiring itself (the click handler's outer try/catch, the
/// workers calling ExportWorkerBoundary.Run, progress-bar restoration). That is by code reading
/// and manual test - no GUI is driven here.
/// </summary>
public sealed class ExportSafetyTests : IDisposable
{
    private readonly string root;

    public ExportSafetyTests()
    {
        root = Path.Combine(Path.GetTempPath(), "ShuibbFixValidation", "scan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }

    private string MakeShard(params string[] relative)
    {
        string path = Path.Combine(root, Path.Combine(relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, new byte[] { 1 });
        return path;
    }

    private static void Deny(string dir) => RunIcacls($"\"{dir}\" /deny \"{Environment.UserName}:(OI)(CI)R\"");
    private static void Undeny(string dir) => RunIcacls($"\"{dir}\" /remove:d \"{Environment.UserName}\"");

    // ---- scanner resilience --------------------------------------------------------------------

    [Fact]
    public void NormalNestedData_FindsEveryShard()
    {
        MakeShard("Data", "Item", "Consume", "Consume_000.wz");
        MakeShard("Data", "Character", "Weapon", "Weapon_000.wz");

        var scan = DataFolderWzScanner.ScanWithReport(Path.Combine(root, "Data"));

        Assert.Null(scan.RootError);
        Assert.Equal(2, scan.Shards.Count);
        Assert.Empty(scan.SkippedDirectories);
    }

    [Fact]
    public void DeniedChildDirectory_IsSkipped_AndTheRestIsStillScanned()
    {
        MakeShard("Data", "Item", "Item_000.wz");
        MakeShard("Data", "Mob", "Mob_000.wz");
        string denied = Path.Combine(root, "Data", "Locked");
        Directory.CreateDirectory(denied);

        Deny(denied);
        try
        {
            var ex = Record.Exception(() =>
            {
                var scan = DataFolderWzScanner.ScanWithReport(Path.Combine(root, "Data"));

                // The two readable categories survive; the denied one is reported, not fatal.
                Assert.Null(scan.RootError);
                Assert.Equal(2, scan.Shards.Count);
                Assert.Single(scan.SkippedDirectories);
                Assert.Contains("Locked", scan.SkippedDirectories[0]);
                Assert.False(string.IsNullOrEmpty(scan.FirstSkipReason));
            });
            Assert.Null(ex); // this exact situation used to close the whole app
        }
        finally
        {
            Undeny(denied);
        }
    }

    [Fact]
    public void DeniedRoot_IsAControlledFailure_NotAnException()
    {
        string dataRoot = Path.Combine(root, "Data");
        MakeShard("Data", "Item", "Item_000.wz");

        Deny(dataRoot);
        try
        {
            var ex = Record.Exception(() =>
            {
                var scan = DataFolderWzScanner.ScanWithReport(dataRoot);
                Assert.NotNull(scan.RootError);
                Assert.Empty(scan.Shards);
            });
            Assert.Null(ex);
        }
        finally
        {
            Undeny(dataRoot);
        }
    }

    [Fact]
    public void MissingRoot_ReportsRootErrorInsteadOfThrowing()
    {
        var scan = DataFolderWzScanner.ScanWithReport(Path.Combine(root, "does-not-exist"));
        Assert.NotNull(scan.RootError);
        Assert.Empty(scan.Shards);
    }

    [Fact]
    public void PlainScan_StillReturnsTheSameShards()
    {
        MakeShard("Data", "Item", "Item_000.wz");
        var shards = DataFolderWzScanner.Scan(Path.Combine(root, "Data"));
        Assert.Single(shards);
    }

    // ---- worker boundary -----------------------------------------------------------------------

    [Fact]
    public void IOException_BecomesAFailureResult()
    {
        string failure = ExportWorkerBoundary.Run(() => throw new IOException("磁碟空間不足"));
        Assert.Equal("磁碟空間不足", failure);
    }

    [Fact]
    public void UnauthorizedAccess_BecomesAFailureResult()
    {
        string failure = ExportWorkerBoundary.Run(() => throw new UnauthorizedAccessException("denied"));
        Assert.Equal("denied", failure);
    }

    [Fact]
    public void AnyOtherException_IsStillContained()
    {
        string failure = ExportWorkerBoundary.Run(() => throw new InvalidOperationException("boom"));
        Assert.Equal("boom", failure);
    }

    [Fact]
    public void Success_ReportsNothing()
    {
        Assert.Null(ExportWorkerBoundary.Run(() => { }));
    }

    [Fact]
    public void UserAbort_IsAnOutcomeNotAFailure()
    {
        Assert.Null(ExportWorkerBoundary.Run(() => throw new OperationCanceledException()));
    }

    /// <summary>
    /// The environment failures the audit called out, run through the boundary the way a worker
    /// does: an output directory that vanished before the export, and a locked output file.
    /// </summary>
    [Fact]
    public void EnvironmentFailures_NeverEscapeTheBoundary()
    {
        string gone = Path.Combine(root, "vanished-output", "sub");
        string failure1 = ExportWorkerBoundary.Run(() =>
        {
            // Simulates CreateDirectory/write into an output location that no longer exists
            // (drive unplugged, folder deleted between picking it and exporting).
            File.WriteAllText(Path.Combine(gone, "a.xml"), "x");
        });
        Assert.NotNull(failure1);

        string locked = Path.Combine(root, "locked.txt");
        File.WriteAllText(locked, "log");
        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Simulates ErrorLogger.SaveToFile hitting a locked log file at the end of a run.
            string failure2 = ExportWorkerBoundary.Run(() => File.AppendAllText(locked, "more"));
            Assert.NotNull(failure2);
        }
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
