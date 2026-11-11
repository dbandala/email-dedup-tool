using DedupTool;
using Xunit;

namespace DedupTool.Tests;

public class LoadFileWriterTests
{
    [Fact]
    public void Csv_EscapesFieldsAndUsesFixedHeaderAndLf()
    {
        var csv = LoadFileWriter.ToCsv(new[]
        {
            new LoadFileRow { DocId = "A", FileName = "a,\"b\"\r\nc", FamilyId = "A" }
        });

        Assert.Equal(LoadFileWriter.Header + "\nA,A,,,,\"a,\"\"b\"\"\r\nc\",,\n", csv);
    }

    [Fact]
    public void Write_WhenRowsFail_PreservesExistingDestinationAndCleansTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "deduptool-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "output.csv");
        try
        {
            File.WriteAllText(path, "existing");
            Assert.Throws<InvalidOperationException>(() => LoadFileWriter.Write(path, FailingRows()));
            Assert.Equal("existing", File.ReadAllText(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static IEnumerable<LoadFileRow> FailingRows()
    {
        yield return new LoadFileRow { DocId = "A" };
        throw new InvalidOperationException("test failure");
    }
}
