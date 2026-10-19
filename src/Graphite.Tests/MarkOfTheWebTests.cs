using Graphite.Core;
using Xunit;

namespace Graphite.Tests;

public class MarkOfTheWebTests
{
    private static string NewTempFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "graphite-motw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Mark(string file) =>
        File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");

    [Fact]
    public void RemovesTheMarkFromFilesInTheFolderAndSubfolders()
    {
        if (!OperatingSystem.IsWindows()) return; // alternate data streams are a Windows/NTFS thing
        string dir = NewTempFolder();
        try
        {
            string a = Path.Combine(dir, "Graphite.exe");
            string sub = Path.Combine(dir, "x64");
            Directory.CreateDirectory(sub);
            string b = Path.Combine(sub, "tesseract50.dll");
            string clean = Path.Combine(dir, "readme.txt");
            File.WriteAllText(a, "a");
            File.WriteAllText(b, "b");
            File.WriteAllText(clean, "c");
            Mark(a);
            Mark(b);

            Assert.Equal(2, MarkOfTheWeb.UnblockFolder(dir));

            Assert.False(File.Exists(a + ":Zone.Identifier"));
            Assert.False(File.Exists(b + ":Zone.Identifier"));
            Assert.Equal("a", File.ReadAllText(a)); // the file itself is untouched
            Assert.Equal("b", File.ReadAllText(b));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SecondRunFindsNothingToDo()
    {
        if (!OperatingSystem.IsWindows()) return;
        string dir = NewTempFolder();
        try
        {
            string a = Path.Combine(dir, "Graphite.exe");
            File.WriteAllText(a, "a");
            Mark(a);
            Assert.Equal(1, MarkOfTheWeb.UnblockFolder(dir));
            Assert.Equal(0, MarkOfTheWeb.UnblockFolder(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void MissingOrEmptyFolderIsIgnored()
    {
        Assert.Equal(0, MarkOfTheWeb.UnblockFolder(""));
        Assert.Equal(0, MarkOfTheWeb.UnblockFolder(Path.Combine(Path.GetTempPath(), "graphite-does-not-exist-" + Guid.NewGuid().ToString("N"))));
    }
}
