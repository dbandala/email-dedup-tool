using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DedupTool;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DedupTool.Tests;

public class DiskPipelineTests
{
    [Fact]
    public void DigestCollisions_StillCompareCompleteTreeShapes()
    {
        WithDirectory(directory =>
        {
            var input = Path.Combine(directory, "input.json");
            var output = Path.Combine(directory, "output.csv");
            var documents = new List<Document>
            {
                new() { DocId = "A", DocType = "loose", Custodian = "c", FileName = "a", Sha256 = "root" },
                new() { DocId = "A1", ParentId = "A", DocType = "attachment", Custodian = "c", FileName = "a", Sha256 = "one" },
                new() { DocId = "B", DocType = "loose", Custodian = "c", FileName = "b", Sha256 = "root" },
                new() { DocId = "B1", ParentId = "B", DocType = "attachment", Custodian = "c", FileName = "a", Sha256 = "two" },
                new() { DocId = "C", DocType = "loose", Custodian = "c", FileName = "c", Sha256 = "root" },
                new() { DocId = "C1", ParentId = "C", DocType = "attachment", Custodian = "c", FileName = "a", Sha256 = "one" }
            };
            File.WriteAllText(input, JsonSerializer.Serialize(new Batch { BatchId = "b", Documents = documents }));
            using var job = new DiskJob(input, output);
            using (var pipeline = new DiskPipeline(job, _ => new byte[32]))
            {
                var result = pipeline.Run();
                Assert.Equal(2, result.Suppressed);
                Assert.Equal(LoadFileWriter.ToCsv(Deduper.Run(new Batch { Documents = documents }).Rows),
                    LoadFileWriter.ToCsv(pipeline.Rows()));
            }
            job.Cleanup();
        });
    }

    [Fact]
    public void IncompleteStage_IsRebuiltOnResume()
    {
        WithDirectory(directory =>
        {
            var input = Path.Combine(directory, "input.json");
            var output = Path.Combine(directory, "output.csv");
            var batch = new Batch
            {
                BatchId = "b", Documents = new()
                {
                    new Document { DocId = "a", Custodian = "c", DocType = "loose", FileName = "x", Sha256 = "h" },
                    new Document { DocId = "b", Custodian = "c", DocType = "loose", FileName = "y", Sha256 = "h" }
                }
            };
            File.WriteAllText(input, JsonSerializer.Serialize(batch));
            using (var job = new DiskJob(input, output))
            {
                using (var pipeline = new DiskPipeline(job))
                    Assert.Equal(1, pipeline.Run().Suppressed);
                using var connection = new SqliteConnection($"Data Source={job.DatabasePath};Pooling=False");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM meta WHERE name IN ('shaped','assigned'); UPDATE nodes SET shape=NULL,remaining=23,root=NULL,keep=NULL;";
                command.ExecuteNonQuery();
            }
            var resumed = Run(input, output);
            Assert.True(resumed.Exit == 0, resumed.Error);
            Assert.Equal(LoadFileWriter.ToCsv(Deduper.Run(batch).Rows), File.ReadAllText(output));
        });
    }

    [Fact]
    public void AlreadyPublishedOutput_IsSafelyRepublishedAfterInterruption()
    {
        WithDirectory(directory =>
        {
            var input = Path.Combine(directory, "input.json");
            var output = Path.Combine(directory, "output.csv");
            File.WriteAllText(input, "{\"batchId\":\"b\",\"documents\":[]}");
            using (var job = new DiskJob(input, output))
            using (var pipeline = new DiskPipeline(job))
            {
                pipeline.Run();
                LoadFileWriter.Write(job.Destination, job.OutputTemporary, pipeline.Rows());
            }
            Assert.Equal(LoadFileWriter.Header + "\n", File.ReadAllText(output));
            var resumed = Run(input, output);
            Assert.True(resumed.Exit == 0, resumed.Error);
            Assert.Equal(LoadFileWriter.Header + "\n", File.ReadAllText(output));
        });
    }

    [Fact]
    public void RandomizedNestedFamilies_MatchInMemoryOracle()
    {
        var documents = new List<Document>();
        for (var i = 0; i < 80; i++)
        {
            var root = $"r-{i:D3}";
            documents.Add(i % 2 == 0
                ? new Document
                {
                    DocId = root, Custodian = $"c-{i % 3}", DocType = "email", FileName = "mail", Sha256 = root,
                    SentDate = "2024-01-01T00:00:00Z", From = "A", To = new() { "B" },
                    Subject = $"subject-{i % 5}", Body = "body\r\nsecond"
                }
                : new Document { DocId = root, Custodian = $"c-{i % 3}", DocType = "loose", FileName = "loose", Sha256 = $"h-{i % 5}" });
            for (var j = 0; j < i % 4; j++)
                documents.Add(new Document
                {
                    DocId = $"{root}-{j}", ParentId = j == 2 ? $"{root}-0" : root, Custodian = $"c-{i % 3}",
                    DocType = "attachment", FileName = $"file-{j % 2}", Sha256 = $"child-{j % 2}"
                });
        }
        var expected = LoadFileWriter.ToCsv(Deduper.Run(new Batch { Documents = documents }).Rows);
        WithDirectory(directory =>
        {
            for (var seed = 0; seed < 8; seed++)
            {
                var input = Path.Combine(directory, "input.json");
                var output = Path.Combine(directory, $"output-{seed}.csv");
                var random = new Random(seed);
                File.WriteAllText(input, JsonSerializer.Serialize(new Batch
                {
                    BatchId = "b", Documents = documents.OrderBy(_ => random.Next()).ToList()
                }));
                var result = Run(input, output);
                Assert.True(result.Exit == 0, result.Error);
                Assert.Equal(expected, File.ReadAllText(output));
            }
        });
    }

    [Fact]
    public void ShuffledFamilies_MatchInMemoryOracleIncludingUnicodeOrdering()
    {
        var documents = new List<Document>();
        var ids = new[] { "a", "\uE000", "\U00010000", "z", "a\0b", "a\0c" };
        foreach (var id in ids)
        {
            documents.Add(new Document
            {
                DocId = id, DocType = "email", Custodian = "c", FileName = "mail", Sha256 = id,
                SentDate = "2024-01-01T00:00:00.123456789Z", From = "A", To = new() { "X", "x" },
                Subject = id == "z" ? "different" : "same", Body = "a\r\nb"
            });
            documents.Add(new Document
            {
                DocId = id + "-child", ParentId = id, DocType = "attachment", Custodian = "c",
                FileName = "a,\"b\"", Sha256 = "abc"
            });
        }
        documents.Add(new Document { DocId = "loose1", DocType = "loose", Custodian = "c", FileName = "x", Sha256 = "hash" });
        documents.Add(new Document { DocId = "loose2", DocType = "loose", Custodian = "c", FileName = "y", Sha256 = "HASH" });
        var expected = LoadFileWriter.ToCsv(Deduper.Run(new Batch { Documents = documents }).Rows);
        WithDirectory(directory =>
        {
            for (var seed = 0; seed < 4; seed++)
            {
                var random = new Random(seed);
                var batch = new Batch { BatchId = "b", Documents = documents.OrderBy(_ => random.Next()).ToList() };
                var input = Path.Combine(directory, "input.json");
                var output = Path.Combine(directory, $"output-{seed}.csv");
                File.WriteAllText(input, JsonSerializer.Serialize(batch));
                var result = Run(input, output);
                Assert.True(result.Exit == 0, result.Error);
                Assert.Equal(expected, File.ReadAllText(output));
            }
        });
    }

    [Fact]
    public void SqliteFull_PreservesDestinationAndCanResume()
    {
        WithDirectory(directory =>
        {
            var input = Path.Combine(directory, "input.json");
            var output = Path.Combine(directory, "output.csv");
            var documents = Enumerable.Range(0, 1000).Select(i => new Document
            {
                DocId = $"d-{i:D6}", Custodian = "c", DocType = "loose", FileName = "x", Sha256 = $"h-{i}"
            }).ToList();
            File.WriteAllText(input, JsonSerializer.Serialize(new Batch { BatchId = "b", Documents = documents }));
            File.WriteAllText(output, "previous");
            using (var job = new DiskJob(input, output))
            using (var pipeline = new DiskPipeline(job))
            {
                var field = typeof(DiskPipeline).GetField("connection", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var connection = (SqliteConnection)field.GetValue(pipeline)!;
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA max_page_count=40";
                command.ExecuteNonQuery();
                var error = Assert.Throws<SqliteException>(() => pipeline.Run());
                Assert.Equal(13, error.SqliteErrorCode);
                Assert.Equal("previous", File.ReadAllText(output));
            }
            var resumed = Run(input, output);
            Assert.True(resumed.Exit == 0, resumed.Error);
            Assert.Contains("1000 documents in, 1000 out, 0 suppressed", resumed.Output);
        });
    }

    [Fact]
    public void OutputIoFailure_PreservesDestinationAndCanResume()
    {
        WithDirectory(directory =>
        {
            var input = Path.Combine(directory, "input.json");
            var output = Path.Combine(directory, "output.csv");
            File.WriteAllText(input, "{\"batchId\":\"b\",\"documents\":[]}");
            File.WriteAllText(output, "previous");
            string temporary;
            using (var job = new DiskJob(input, output))
                temporary = job.OutputTemporary;
            Directory.CreateDirectory(temporary);
            var failed = Run(input, output);
            Assert.NotEqual(0, failed.Exit);
            Assert.Contains("Deduplication failed:", failed.Error);
            Assert.Equal("previous", File.ReadAllText(output));
            Directory.Delete(temporary);
            var resumed = Run(input, output);
            Assert.True(resumed.Exit == 0, resumed.Error);
            Assert.Equal(LoadFileWriter.Header + "\n", File.ReadAllText(output));
        });
    }

    [Fact]
    public void InterruptedPublication_ResumesAndRejectsChangedInput()
    {
        WithDirectory(directory =>
        {
            var input = Path.Combine(directory, "input.json");
            var output = Path.Combine(directory, "output.csv");
            var content = JsonSerializer.Serialize(new Batch
            {
                BatchId = "b",
                Documents = new() { new Document { DocId = "a", Custodian = "c", DocType = "loose", FileName = "x", Sha256 = "h" } }
            });
            File.WriteAllText(input, content);
            Directory.CreateDirectory(output);
            var first = Run(input, output);
            Assert.NotEqual(0, first.Exit);
            var job = Assert.Single(Directory.GetDirectories(directory, ".deduptool-*"));
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(job, "state.sqlite")};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM meta WHERE name='assigned'";
                Assert.Equal("1", command.ExecuteScalar());
            }
            File.WriteAllText(input, content + " ");
            var changed = Run(input, output);
            Assert.NotEqual(0, changed.Exit);
            Assert.Contains("does not match", changed.Error);
            Assert.True(Directory.Exists(output));
            File.WriteAllText(input, content);
            var changedLimit = Run(input, output, 1024);
            Assert.NotEqual(0, changedLimit.Exit);
            Assert.Contains("does not match", changedLimit.Error);
            Directory.Delete(output);
            var resumed = Run(input, output);
            Assert.True(resumed.Exit == 0, resumed.Error);
            Assert.Contains("1 documents in, 1 out, 0 suppressed", resumed.Output);
            Assert.Contains("a,a,,c,loose,x,,h", File.ReadAllText(output));
            Assert.False(File.Exists(Path.Combine(job, "input.json")));
            Assert.False(File.Exists(Path.Combine(job, "state.sqlite")));
        });
    }

    [Fact]
    public void DocumentSizeLimit_AcceptsTheBoundaryAndRejectsOneByteMore()
    {
        var document = new Document
        {
            DocId = "a", Custodian = "c", DocType = "loose", FileName = "", Sha256 = "h"
        };
        var baseline = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(document));
        document.FileName = new string('x', 1024 - baseline);
        var serialized = JsonSerializer.Serialize(document);
        Assert.Equal(1024, Encoding.UTF8.GetByteCount(serialized));
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"batchId\":\"b\",\"documents\":[{serialized}]}}")))
        {
            var count = 0;
            Assert.Equal("b", StreamingBatchReader.Read(stream, 1024, () => { }, _ => count++));
            Assert.Equal(1, count);
        }
        document.FileName += "x";
        serialized = JsonSerializer.Serialize(document);
        Assert.Equal(1025, Encoding.UTF8.GetByteCount(serialized));
        using var tooLarge = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"batchId\":\"b\",\"documents\":[{serialized}]}}"));
        Assert.Throws<InvalidDataException>(() => StreamingBatchReader.Read(tooLarge, 1024, () => { }, _ => { }));
    }

    [Fact]
    public void LargeDocumentLimit_FailsClearlyWithoutReplacingOutput()
    {
        WithDirectory(directory =>
        {
            var input = Path.Combine(directory, "input.json");
            var output = Path.Combine(directory, "output.csv");
            var doc = new Document
            {
                DocId = "a", Custodian = "c", DocType = "email", FileName = "x", Sha256 = "h",
                SentDate = "2024-01-01T00:00Z", From = "a", To = new(), Subject = "", Body = new string('x', 5000)
            };
            File.WriteAllText(input, JsonSerializer.Serialize(new Batch { BatchId = "b", Documents = new() { doc } }));
            File.WriteAllText(output, "previous");
            var result = Run(input, output, 1024);
            Assert.NotEqual(0, result.Exit);
            Assert.Contains("limit", result.Error);
            Assert.Equal("previous", File.ReadAllText(output));
        });
    }

    [Fact]
    public void RootPropertiesMayFollowDocuments_AndTruncatedInputFails()
    {
        WithDirectory(directory =>
        {
            var input = Path.Combine(directory, "input.json");
            var output = Path.Combine(directory, "output.csv");
            File.WriteAllText(input, "{\"documents\":[],\"unknown\":{\"a\":[1,2]},\"batchId\":\"b\"}\n");
            var result = Run(input, output);
            Assert.True(result.Exit == 0, result.Error);
            Assert.Equal(LoadFileWriter.Header + "\n", File.ReadAllText(output));
            File.WriteAllText(input, "{\"documents\":[");
            result = Run(input, output);
            Assert.NotEqual(0, result.Exit);
            Assert.Equal(LoadFileWriter.Header + "\n", File.ReadAllText(output));
        });
    }

    [Fact]
    public void Utf8Bom_IsAccepted()
    {
        WithDirectory(directory =>
        {
            var input = Path.Combine(directory, "input.json");
            var output = Path.Combine(directory, "output.csv");
            File.WriteAllText(input, "{\"batchId\":\"b\",\"documents\":[]}", new UTF8Encoding(true));
            var result = Run(input, output);
            Assert.True(result.Exit == 0, result.Error);
        });
    }

    [Fact]
    public void RepeatedDocumentsProperty_UsesTheLastArray()
    {
        WithDirectory(directory =>
        {
            var input = Path.Combine(directory, "input.json");
            var output = Path.Combine(directory, "output.csv");
            var document = "{\"docId\":\"a\",\"custodian\":\"c\",\"docType\":\"loose\",\"parentId\":null,\"fileName\":\"x\",\"sha256\":\"h\",\"sizeBytes\":1}";
            File.WriteAllText(input, $"{{\"batchId\":\"b\",\"documents\":[{document},{document}],\"documents\":null,\"documents\":[]}}");
            var result = Run(input, output);
            Assert.True(result.Exit == 0, result.Error);
            Assert.Contains("0 documents in, 0 out, 0 suppressed", result.Output);
            Assert.Equal(LoadFileWriter.Header + "\n", File.ReadAllText(output));
        });
    }

    [Fact]
    public void UnownedScratchDirectory_IsNeverCleaned()
    {
        WithDirectory(directory =>
        {
            var input = Path.Combine(directory, "input.json");
            var output = Path.Combine(directory, "output.csv");
            File.WriteAllText(input, "{\"batchId\":\"b\",\"documents\":[]}");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(output))))[..24];
            var scratch = Path.Combine(directory, ".deduptool-" + hash);
            Directory.CreateDirectory(scratch);
            var unrelated = Path.Combine(scratch, "input.json");
            File.WriteAllText(unrelated, "unrelated");
            var result = Run(input, output);
            Assert.NotEqual(0, result.Exit);
            Assert.Contains("not owned", result.Error);
            Assert.Equal("unrelated", File.ReadAllText(unrelated));
        });
    }

    [Fact]
    public void ConcurrentJob_FailsWithoutDisturbingOwner()
    {
        WithDirectory(directory =>
        {
            var input = Path.Combine(directory, "input.json");
            var output = Path.Combine(directory, "output.csv");
            File.WriteAllText(input, "{\"batchId\":\"b\",\"documents\":[]}");
            using (var job = new DiskJob(input, output))
            {
                var scratch = Path.Combine(directory, "other-scratch");
                Directory.CreateDirectory(scratch);
                var result = Run(input, output, scratch: scratch);
                Assert.NotEqual(0, result.Exit);
                Assert.Contains("Deduplication failed:", result.Error);
                Assert.True(File.Exists(job.SnapshotPath));
            }
            var resumed = Run(input, output);
            Assert.True(resumed.Exit == 0, resumed.Error);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidGraph_PreservesDestination(bool cycle)
    {
        WithDirectory(directory =>
        {
            var input = Path.Combine(directory, "input.json");
            var output = Path.Combine(directory, "output.csv");
            var batch = new Batch
            {
                BatchId = "b", Documents = new()
                {
                    new Document { DocId = "a", ParentId = cycle ? "a" : "missing", Custodian = "c",
                        DocType = "attachment", FileName = "x", Sha256 = "h" }
                }
            };
            File.WriteAllText(input, JsonSerializer.Serialize(batch));
            File.WriteAllText(output, "previous");
            var result = Run(input, output);
            Assert.NotEqual(0, result.Exit);
            Assert.Contains(cycle ? "cycle" : "missing parent", result.Error);
            Assert.Equal("previous", File.ReadAllText(output));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LargeFamilies_MatchOracleWithoutRecursion(bool deep)
    {
        WithDirectory(directory =>
        {
            var input = Path.Combine(directory, "input.json");
            var output = Path.Combine(directory, "output.csv");
            var batch = new Batch
            {
                BatchId = "b", Documents = new()
                {
                    new Document { DocId = "root", DocType = "loose", Custodian = "c", FileName = "r", Sha256 = "root" }
                }
            };
            for (var i = 0; i < 2500; i++)
                batch.Documents.Add(new Document
                {
                    DocId = $"child-{i:D5}", ParentId = deep && i > 0 ? $"child-{i - 1:D5}" : "root",
                    DocType = "attachment", Custodian = "c", FileName = "x", Sha256 = $"h-{i % 5}"
                });
            File.WriteAllText(input, JsonSerializer.Serialize(batch));
            var result = Run(input, output);
            Assert.True(result.Exit == 0, result.Error);
            Assert.Equal(LoadFileWriter.ToCsv(Deduper.Run(batch).Rows), File.ReadAllText(output));
        });
    }

    [Fact]
    public void ProcessTermination_LeavesResumableJob()
    {
        WithDirectory(directory =>
        {
            var input = Path.Combine(directory, "input.json");
            var output = Path.Combine(directory, "output.csv");
            var documents = Enumerable.Range(0, 20000).Select(i => new Document
            {
                DocId = $"doc-{i:D6}", DocType = "loose", Custodian = "c",
                FileName = "x", Sha256 = $"hash-{i / 2}"
            }).ToList();
            var batch = new Batch { BatchId = "b", Documents = documents };
            File.WriteAllText(input, JsonSerializer.Serialize(batch));
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(Deduper).Assembly.Location);
            start.ArgumentList.Add(input);
            start.ArgumentList.Add(output);
            using (var process = Process.Start(start)!)
            {
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < TimeSpan.FromSeconds(30) && !process.HasExited &&
                    !Directory.GetDirectories(directory, ".deduptool-*")
                        .Any(path => File.Exists(Path.Combine(path, "state.sqlite"))))
                    Thread.Sleep(10);
                Assert.False(process.HasExited, "The process finished before it could be interrupted.");
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), "The job did not reach SQLite staging.");
                process.Kill();
                process.WaitForExit();
            }
            Assert.False(File.Exists(output));
            var resumed = Run(input, output);
            Assert.True(resumed.Exit == 0, resumed.Error);
            Assert.Equal(LoadFileWriter.ToCsv(Deduper.Run(batch).Rows), File.ReadAllText(output));
        });
    }

    private static (int Exit, string Output, string Error) Run(string input, string output, int? limit = null, string? scratch = null)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Deduper).Assembly.Location);
        start.ArgumentList.Add(input);
        start.ArgumentList.Add(output);
        if (limit is not null)
            start.Environment["DEDUP_MAX_DOCUMENT_BYTES"] = limit.Value.ToString();
        if (scratch is not null)
            start.Environment["DEDUP_SCRATCH_ROOT"] = scratch;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    private static void WithDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "deduptool-disk-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
