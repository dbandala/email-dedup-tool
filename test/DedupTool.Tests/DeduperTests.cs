using DedupTool;
using Xunit;

namespace DedupTool.Tests;

public class DeduperTests
{
    private static Document Loose(string id, string custodian, string hash) => new()
    {
        DocId = id, Custodian = custodian, DocType = "loose", FileName = id + ".txt", Sha256 = hash, SizeBytes = 10,
    };

    [Fact]
    public void IdenticalLooseDocuments_SecondIsSuppressed()
    {
        var batch = new Batch
        {
            Documents = new() { Loose("A", "c1", "h1"), Loose("B", "c1", "h1") },
        };

        var result = Deduper.Run(batch);

        Assert.Single(result.Rows);
        Assert.Equal("A", result.Rows[0].DocId);
        Assert.Equal(1, result.Suppressed);
    }

    [Fact]
    public void DistinctDocuments_AllKept()
    {
        var batch = new Batch
        {
            Documents = new() { Loose("A", "c1", "h1"), Loose("B", "c1", "h2") },
        };

        var result = Deduper.Run(batch);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public void LoadFile_StartsWithFixedHeader()
    {
        var csv = LoadFileWriter.ToCsv(Array.Empty<LoadFileRow>());

        Assert.StartsWith("DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash", csv);
    }
}
