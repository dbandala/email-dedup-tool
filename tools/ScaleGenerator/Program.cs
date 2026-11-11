using System.Text.Json;

if (args.Length is < 2 or > 3 || !int.TryParse(args[0], out var count) || count < 0 ||
    (args.Length == 3 && args[2] is not ("nested" or "wide" or "unique" or "large-body")))
{
    Console.Error.WriteLine("usage: ScaleGenerator <document-count> <output.json> [nested|wide|unique|large-body]");
    return 2;
}

var profile = args.Length == 3 ? args[2] : "nested";
var body = profile == "large-body" ? new string('x', 128 * 1024) : "Synthetic message body.";
var groupSize = profile == "wide" ? 1000 : 3;
using var stream = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.None);
using var writer = new StreamWriter(stream);
writer.Write("{\"batchId\":\"synthetic\",\"documents\":[");
for (var i = 0; i < count; i++)
{
    if (i > 0) writer.Write(',');
    var group = i / groupSize;
    var position = i % groupSize;
    object document = profile == "unique"
        ? new
        {
            docId = $"loose-{i:D10}", custodian = $"custodian-{i % 20}", docType = "loose",
            parentId = (string?)null, fileName = "file.bin", sha256 = $"hash-{i}", sizeBytes = 100
        }
        : position == 0
        ? new
        {
            docId = $"email-{group:D10}", custodian = $"custodian-{group % 20}", docType = "email",
            parentId = (string?)null, fileName = "mail.eml", sha256 = $"raw-{group}", sizeBytes = 100,
            sentDate = "2024-01-01T00:00:00Z", from = "sender@example.com",
            to = new[] { "recipient@example.com" }, subject = $"subject-{group / 2}",
            body
        }
        : new
        {
            docId = $"attachment-{group:D10}-{position}", custodian = $"custodian-{group % 20}",
            docType = "attachment", parentId = position == 1 || profile == "wide"
                ? $"email-{group:D10}" : $"attachment-{group:D10}-1",
            fileName = $"attachment-{position}.bin", sha256 = $"hash-{group / 2}-{position}", sizeBytes = 100
        };
    writer.Write(JsonSerializer.Serialize(document));
}
writer.Write("]}");
return 0;
