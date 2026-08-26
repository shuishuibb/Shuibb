using System;
using System.Runtime.InteropServices;
using Xunit;
using Assert = Xunit.Assert;

namespace MapleLib.Tests;

/// <summary>
/// Documents the exact Marshal.StringToHGlobalAnsi contract that
/// HaSharedLibrary\SharpApng\SharpApngBasicWrapper.cs's MarshalString now relies on for its fix:
/// allocation includes the null terminator, no out-of-bounds write, an empty string doesn't
/// crash, nothing indexes source[0], and non-ASCII paths survive instead of collapsing to '?'.
///
/// This can't call SharpApngBasicWrapper.MarshalString directly: that type's static constructor
/// LoadLibrary()s apng32/64.dll, which this environment doesn't ship (a pre-existing,
/// out-of-scope gap - the whole SharpApng export feature is currently unreachable here,
/// unrelated to this round's four bugs), so touching any static member of that class throws
/// before MarshalString's body would ever run. Testing the exact BCL primitive the fix was
/// rewritten to use is the closest available regression.
/// </summary>
public sealed class MarshalStringAnsiRoundTripTests
{
    [Fact]
    public void StringToHGlobalAnsi_EmptyString_DoesNotThrowAndNullTerminatesImmediately()
    {
        IntPtr ptr = Marshal.StringToHGlobalAnsi(string.Empty);
        try
        {
            Assert.NotEqual(IntPtr.Zero, ptr);
            Assert.Equal(0, Marshal.ReadByte(ptr)); // terminator right at offset 0, nothing to read past it
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void StringToHGlobalAnsi_AsciiString_RoundTripsExactlyAndIsNullTerminated()
    {
        const string source = "C:\\output\\frame.png";
        IntPtr ptr = Marshal.StringToHGlobalAnsi(source);
        try
        {
            Assert.Equal(source, Marshal.PtrToStringAnsi(ptr));
            Assert.Equal(0, Marshal.ReadByte(ptr, source.Length)); // terminator right after the content, not past uninitialised bytes
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void StringToHGlobalAnsi_NonAsciiPath_SurvivesInsteadOfCollapsingToQuestionMarks()
    {
        // A Traditional-Chinese path segment, the kind this project's own test fixtures use
        // (Data\Lang\zh_TW\...). Under the old Encoding.ASCII.GetBytes, every one of these
        // characters would have collapsed to '?', and '?' is not a legal Windows file name
        // character - the native fopen() would have silently failed.
        const string source = "輸出\\畫面.png";
        IntPtr ptr = Marshal.StringToHGlobalAnsi(source);
        try
        {
            string? roundTripped = Marshal.PtrToStringAnsi(ptr);
            Assert.NotNull(roundTripped);
            Assert.DoesNotContain('?', roundTripped);
            Assert.Equal(source, roundTripped);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
