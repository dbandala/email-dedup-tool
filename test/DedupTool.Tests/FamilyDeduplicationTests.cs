using System.Text.Json;
using DedupTool;
using Xunit;

namespace DedupTool.Tests;

public class FamilyDeduplicationTests
{
    private static Document Email(string id, string custodian = "c1") => new()
    {
        DocId = id, Custodian = custodian, DocType = "email", FileName = id + ".eml",
        Sha256 = id + "-hash", SentDate = "2024-03-05T14:22:00-05:00",
        From = "John@example.com", To = new() { "maria@example.com", "ray@example.com" },
        Subject = " Subject ", Body = " First\r\nSecond \r\n",
    };

    private static Document Attachment(string id, string parent, string hash = "hash",
        string fileName = "attachment.pdf", string custodian = "c1") => new()
    {
        DocId = id, ParentId = parent, Custodian = custodian, DocType = "attachment",
        FileName = fileName, Sha256 = hash,
    };

    private static Document Loose(string id, string custodian = "c1", string hash = "hash") => new()
    {
        DocId = id, Custodian = custodian, DocType = "loose", FileName = id + ".txt", Sha256 = hash,
    };

    private static DedupResult Run(params Document[] documents) =>
        Deduper.Run(new Batch { Documents = documents.ToList() });

    [Fact]
    public void SampleBatch_ProducesCompleteFamiliesAndSuppressesWholeDuplicate()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../data/batch-sample.json"));
        var batch = JsonSerializer.Deserialize<Batch>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var result = Deduper.Run(batch);
        var ids = result.Rows.Select(row => row.DocId).ToArray();

        Assert.Equal(16, batch.Documents.Count);
        Assert.Equal(13, result.Rows.Count);
        Assert.Equal(3, result.Suppressed);
        Assert.Contains("DOC-000005", ids);
        Assert.DoesNotContain("DOC-000009", ids);
        Assert.DoesNotContain("DOC-000010", ids);
        Assert.DoesNotContain("DOC-000011", ids);
        Assert.Contains("DOC-000013", ids);
        Assert.Equal("2024-03-05T19:22:00Z", result.Rows.Single(row => row.DocId == "DOC-000002").SentDate);
    }

    [Fact]
    public void SharedAttachmentAndRepeatedSiblings_AreNotSuppressedFromRetainedFamilies()
    {
        var second = Email("B");
        second.Subject = "Different";
        var result = Run(Email("A"), Attachment("A1", "A"), Attachment("A2", "A"),
            second, Attachment("B1", "B"));

        Assert.Equal(5, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public void MatchingTrees_AreSuppressedAsWholeFamiliesAcrossCustodians()
    {
        var result = Run(Attachment("B2", "B1", "nested", "nested.txt", "c2"),
            Email("B", "c2"), Attachment("A1", "A"), Email("A"),
            Attachment("B1", "B", custodian: "c2"), Attachment("A2", "A1", "nested", "nested.txt"));

        Assert.Equal(new[] { "A", "A1", "A2" }, result.Rows.Select(row => row.DocId));
        Assert.Equal(3, result.Suppressed);
        var nested = result.Rows.Single(row => row.DocId == "A2");
        Assert.Equal("A", nested.FamilyId);
        Assert.Equal("A1", nested.ParentId);
        Assert.Equal("2024-03-05T19:22:00Z", nested.SentDate);
    }

    [Fact]
    public void LooseFiles_AreScopedByCustodian_AndDoNotCompeteWithAttachments()
    {
        var email = Email("E");
        email.Sha256 = "hash";
        var result = Run(Loose("A"), Loose("B", "c2", "HASH"), Loose("C", hash: "HASH"),
            email, Attachment("E1", "E"));

        Assert.Equal(new[] { "A", "B", "E", "E1" }, result.Rows.Select(row => row.DocId));
        Assert.Equal(1, result.Suppressed);
        Assert.Equal("", result.Rows.Single(row => row.DocId == "A").SentDate);
    }

    [Fact]
    public void LooseRootsWithDifferentTrees_AreNotSuppressed()
    {
        var result = Run(Loose("A"), Attachment("A1", "A"), Loose("B"));
        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("filename")]
    [InlineData("multiplicity")]
    [InlineData("nesting")]
    public void DifferentAttachmentTrees_AreNotSuppressed(string difference)
    {
        var documents = new List<Document>
        {
            Email("A"), Attachment("A1", "A"), Email("B"),
            Attachment("B1", "B", difference == "content" ? "other" : "hash",
                difference == "filename" ? "other.pdf" : "attachment.pdf")
        };
        if (difference == "multiplicity")
            documents.Add(Attachment("B2", "B"));
        if (difference == "nesting")
            documents.Add(Attachment("B2", "B1"));

        var result = Run(documents.ToArray());
        Assert.Equal(documents.Count, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public void SameAttachmentOccurrencesWithDifferentNesting_AreNotSuppressed()
    {
        var result = Run(Email("A"), Attachment("A1", "A"), Attachment("A2", "A1"),
            Email("B"), Attachment("B1", "B"), Attachment("B2", "B"));
        Assert.Equal(6, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public void LooseRootTrees_MustAlsoMatchAndRemainWithinCustodian()
    {
        var result = Run(Loose("B"), Attachment("B1", "B"), Loose("A"),
            Attachment("A1", "A"), Loose("C", "c2"), Attachment("C1", "C", custodian: "c2"));
        Assert.Equal(new[] { "A", "A1", "C", "C1" }, result.Rows.Select(row => row.DocId));
        Assert.Equal(2, result.Suppressed);
    }

    [Fact]
    public void ChildOrderAndInputOrder_DoNotChangeOutput()
    {
        var documents = new[]
        {
            Email("B"), Attachment("B1", "B", "one", "one"),
            Attachment("B2", "B", "two", "two"), Email("A"),
            Attachment("A2", "A", "two", "two"), Attachment("A1", "A", "one", "one"),
        };
        var expected = LoadFileWriter.ToCsv(Run(documents).Rows);
        var random = new Random(123);
        for (var i = 0; i < 20; i++)
        {
            var shuffled = documents.OrderBy(_ => random.Next()).ToArray();
            var result = Run(shuffled);
            Assert.Equal(expected, LoadFileWriter.ToCsv(result.Rows));
            Assert.Equal(new[] { "A", "A1", "A2" }, result.Rows.Select(row => row.DocId));
            Assert.Equal(3, result.Suppressed);
        }
    }

    [Fact]
    public void EmailNormalization_MatchesAllAgreedVariances()
    {
        var email = Email("B", "c2");
        email.SentDate = "2024-03-05T19:22:00Z";
        email.From = "JOHN@EXAMPLE.COM";
        email.To = new() { "RAY@example.com", "MARIA@example.com", "ray@example.com" };
        email.Subject = "Subject";
        email.Body = "First\nSecond";
        var result = Run(email, Email("A"));
        Assert.Equal("A", Assert.Single(result.Rows).DocId);
        Assert.Equal(1, result.Suppressed);
    }

    [Fact]
    public void EmailDates_PreserveFractionalPrecisionAndNormalizeEquivalentOffsets()
    {
        var first = Email("A");
        first.SentDate = "2024-03-05T14:22:00.123456789-0500";
        var same = Email("B");
        same.SentDate = "2024-03-05t19:22:00,1234567890z";
        var distinct = Email("C");
        distinct.SentDate = "2024-03-05T19:22:00.123456788Z";
        var result = Run(distinct, same, first);
        Assert.Equal(new[] { "A", "C" }, result.Rows.Select(row => row.DocId));
        Assert.Equal(1, result.Suppressed);
        Assert.All(result.Rows, row => Assert.Equal("2024-03-05T19:22:00Z", row.SentDate));
    }

    [Fact]
    public void EmailDates_MinutePrecisionMatchesAnExactMinute()
    {
        var second = Email("B");
        second.SentDate = "2024-03-05T19:22Z";
        Assert.Single(Run(Email("A"), second).Rows);
    }

    [Fact]
    public void EmailBody_BareCarriageReturnsAreNormalized()
    {
        var second = Email("B");
        second.Body = "First\rSecond";
        Assert.Single(Run(Email("A"), second).Rows);
    }

    [Theory]
    [InlineData("date")]
    [InlineData("from")]
    [InlineData("to")]
    [InlineData("subject")]
    [InlineData("body")]
    public void DifferentEmailKeyComponents_KeepBothEmails(string component)
    {
        var email = Email("B");
        switch (component)
        {
            case "date": email.SentDate = "2024-03-05T19:22:01Z"; break;
            case "from": email.From = "other@example.com"; break;
            case "to": email.To = new() { "other@example.com" }; break;
            case "subject": email.Subject = "Other"; break;
            case "body": email.Body = "Other"; break;
        }
        Assert.Equal(2, Run(Email("A"), email).Rows.Count);
    }

    [Fact]
    public void MatchingEmailTuple_IsAHeuristicEvenWhenRawHashesDiffer()
    {
        var result = Run(Email("A"), Email("B"));
        Assert.Equal("A", Assert.Single(result.Rows).DocId);
        Assert.Equal(1, result.Suppressed);
    }

    [Fact]
    public void ExplicitlyEmptySubjectBodyAndRecipients_AreValid()
    {
        var email = Email("A");
        email.To = new();
        email.Subject = "";
        email.Body = "";
        Assert.Single(Run(email).Rows);
    }

    [Theory]
    [InlineData("date")]
    [InlineData("offset")]
    [InlineData("from")]
    [InlineData("to")]
    [InlineData("recipient")]
    [InlineData("subject")]
    [InlineData("body")]
    public void IncompleteEmailIdentity_FailsWithDocumentId(string missing)
    {
        var email = Email("A");
        switch (missing)
        {
            case "date": email.SentDate = "not-a-date"; break;
            case "offset": email.SentDate = "2024-03-05T19:22:00"; break;
            case "from": email.From = null; break;
            case "to": email.To = null; break;
            case "recipient": email.To = new() { "" }; break;
            case "subject": email.Subject = null; break;
            case "body": email.Body = null; break;
        }
        Assert.Contains("A", Assert.Throws<InvalidDataException>(() => Run(email)).Message);
    }

    [Fact]
    public void InvalidGraph_FailsExplicitly()
    {
        Assert.Contains("Duplicate", Assert.Throws<InvalidDataException>(() => Run(Loose("A"), Loose("A"))).Message);
        Assert.Contains("missing parent", Assert.Throws<InvalidDataException>(() => Run(Attachment("A", "missing"))).Message);
        Assert.Contains("cycle", Assert.Throws<InvalidDataException>(() => Run(Attachment("A", "A"))).Message);
        Assert.Contains("cycle", Assert.Throws<InvalidDataException>(() => Run(Attachment("A", "B"), Attachment("B", "A"))).Message);
        Assert.Contains("relationship", Assert.Throws<InvalidDataException>(() => Run(new Document
        {
            DocId = "A", DocType = "attachment", Custodian = "c", Sha256 = "h"
        })).Message);
        var nonAttachmentChild = Email("B");
        nonAttachmentChild.ParentId = "A";
        Assert.Contains("relationship", Assert.Throws<InvalidDataException>(() => Run(Email("A"), nonAttachmentChild)).Message);
        var unknown = Loose("A");
        unknown.DocType = "unknown";
        Assert.Contains("unsupported", Assert.Throws<InvalidDataException>(() => Run(unknown)).Message);
    }

    [Fact]
    public void DeepFamily_ResolvesWithoutRecursion()
    {
        var documents = new List<Document> { Email("root") };
        var parent = "root";
        for (var i = 0; i < 10000; i++)
        {
            var id = $"child-{i:D5}";
            documents.Add(Attachment(id, parent));
            parent = id;
        }
        var result = Run(documents.ToArray());
        Assert.Equal(documents.Count, result.Rows.Count);
        Assert.Equal("root", result.Rows.Single(row => row.DocId == parent).FamilyId);
    }

    [Fact]
    public void MixedCustodianFamily_PreservesMemberCustodians()
    {
        var result = Run(Email("A"), Attachment("A1", "A", custodian: "c2"));
        Assert.Equal("c2", result.Rows.Single(row => row.DocId == "A1").Custodian);
        Assert.Equal("A", result.Rows.Single(row => row.DocId == "A1").FamilyId);
    }
}
