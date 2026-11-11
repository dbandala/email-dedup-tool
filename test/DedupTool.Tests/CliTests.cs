using System.Diagnostics;
using System.Text.Json;
using DedupTool;
using Xunit;

namespace DedupTool.Tests;

public class CliTests
{
    [Fact]
    public void MixedCustodianFamily_ProducesWarningAndCompleteOutput()
    {
        WithFiles((input, output) =>
        {
            var batch = new Batch
            {
                BatchId = "batch",
                Documents = new()
                {
                    new Document
                    {
                        DocId = "A", DocType = "email", Custodian = "c1", FileName = "a.eml",
                        Sha256 = "h1", SentDate = "2024-03-05T19:22:00Z", From = "a",
                        To = new(), Subject = "", Body = ""
                    },
                    new Document
                    {
                        DocId = "B", ParentId = "A", DocType = "attachment", Custodian = "c2",
                        FileName = "b", Sha256 = "h2"
                    }
                }
            };
            File.WriteAllText(input, JsonSerializer.Serialize(batch));
            var (exit, stdout, stderr) = RunCli(input, output);
            Assert.Equal(0, exit);
            Assert.Contains("2 documents in, 2 out, 0 suppressed", stdout);
            Assert.Contains("1 mixed-custodian", stderr);
            Assert.Contains("B,A,A,c2,attachment", File.ReadAllText(output));
        });
    }

    [Theory]
    [InlineData("{\"batchId\":\"batch\"}")]
    [InlineData("{\"batchId\":\"batch\",\"documents\":[{\"docId\":\"A\"}]}")]
    [InlineData("{\"batchId\":\"batch\",\"documents\":null}")]
    [InlineData("{\"batchId\":\"\",\"documents\":[]}")]
    public void InvalidInput_FailsWithoutReplacingDestination(string json)
    {
        WithFiles((input, output) =>
        {
            File.WriteAllText(input, json);
            File.WriteAllText(output, "existing");
            var (exit, stdout, stderr) = RunCli(input, output);
            Assert.Equal(1, exit);
            Assert.Empty(stdout);
            Assert.Contains("Deduplication failed:", stderr);
            Assert.Equal("existing", File.ReadAllText(output));
        });
    }

    private static (int Exit, string Stdout, string Stderr) RunCli(string input, string output)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(Deduper).Assembly.Location);
        start.ArgumentList.Add(input);
        start.ArgumentList.Add(output);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    private static void WithFiles(Action<string, string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "deduptool-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "input.json");
        var output = Path.Combine(directory, "output.csv");
        try
        {
            action(input, output);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
