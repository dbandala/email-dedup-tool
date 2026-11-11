using System.Globalization;
using System.Text.RegularExpressions;

namespace DedupTool;

public static class Deduper
{
    public static DedupResult Run(Batch batch)
    {
        var plan = Prepare(batch);
        return new DedupResult(plan.Rows().ToList(), plan.Suppressed);
    }

    internal static DedupPlan Prepare(Batch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Documents is null)
            throw new InvalidDataException("Batch documents are missing.");
        var (nodes, roots) = BuildGraph(batch.Documents);
        IndexTreeShapes(nodes);
        var winners = SelectWinners(roots);
        var (suppressed, mixedCustodianFamilies) = AssignFamilies(roots, winners);
        nodes.Sort((left, right) => StringComparer.Ordinal.Compare(left.Document.DocId, right.Document.DocId));
        return new DedupPlan(nodes, suppressed, mixedCustodianFamilies);
    }

    private static (List<FamilyNode> Nodes, List<FamilyNode> Roots) BuildGraph(List<Document> documents)
    {
        var nodes = new List<FamilyNode>(documents.Count);
        var byId = new Dictionary<string, FamilyNode>(StringComparer.Ordinal);

        foreach (var document in documents)
        {
            ValidateDocument(document);

            var node = new FamilyNode(document);
            if (!byId.TryAdd(document.DocId, node))
                throw new InvalidDataException($"Duplicate document ID '{document.DocId}'.");
            nodes.Add(node);
        }

        var roots = new List<FamilyNode>();
        foreach (var node in nodes)
        {
            var parentId = node.Document.ParentId;
            if (parentId is null)
            {
                if (node.Document.DocType == "email")
                    node.Email = EmailIdentity.Create(node.Document);
                roots.Add(node);
            }
            else
            {
                if (!byId.TryGetValue(parentId, out var parent))
                    throw new InvalidDataException($"Document '{node.Document.DocId}' has missing parent '{parentId}'.");
                node.Parent = parent;
                parent.Children.Add(node);
            }
        }
        return (nodes, roots);
    }

    internal static void ValidateDocument(Document? document)
    {
        if (document is null)
            throw new InvalidDataException("A document is null.");
        if (string.IsNullOrWhiteSpace(document.DocId) ||
            string.IsNullOrWhiteSpace(document.Custodian) ||
            string.IsNullOrWhiteSpace(document.Sha256) || document.FileName is null)
            throw new InvalidDataException($"Document '{document.DocId}' must have an ID, custodian, hash, and filename.");
        if (document.DocType is not ("email" or "attachment" or "loose"))
            throw new InvalidDataException($"Document '{document.DocId}' has unsupported type '{document.DocType}'.");
        if ((document.ParentId is null) == (document.DocType == "attachment"))
            throw new InvalidDataException($"Document '{document.DocId}' has an invalid type and parent relationship.");
    }

    private static void IndexTreeShapes(List<FamilyNode> nodes)
    {
        // Intern bottom-up tree shapes so even deeply nested families need no recursion.
        var shapes = new Dictionary<TreeShape, int>();
        var ready = new Queue<FamilyNode>();
        foreach (var node in nodes)
        {
            node.RemainingChildren = node.Children.Count;
            if (node.RemainingChildren == 0)
                ready.Enqueue(node);
        }

        var processed = 0;
        while (ready.TryDequeue(out var node))
        {
            processed++;
            var childShapes = node.Children.Select(child => child.ShapeId).ToArray();
            Array.Sort(childShapes);
            node.ChildShapes = childShapes;
            if (node.Parent is not null)
            {
                var shape = new TreeShape(node.Document.DocType, node.Document.Sha256.ToUpperInvariant(),
                    node.Document.FileName, childShapes);
                if (!shapes.TryGetValue(shape, out var shapeId))
                {
                    shapeId = shapes.Count + 1;
                    shapes.Add(shape, shapeId);
                }
                node.ShapeId = shapeId;
                if (--node.Parent.RemainingChildren == 0)
                    ready.Enqueue(node.Parent);
            }
        }
        if (processed != nodes.Count)
        {
            var document = nodes.First(node => node.RemainingChildren > 0).Document;
            throw new InvalidDataException($"Document parent relationships contain a cycle involving '{document.DocId}'.");
        }
    }

    private static HashSet<FamilyNode> SelectWinners(List<FamilyNode> roots)
    {
        var winners = new Dictionary<FamilyKey, FamilyNode>();
        foreach (var root in roots)
        {
            var document = root.Document;
            var key = new FamilyKey(document.DocType,
                document.DocType == "loose" ? document.Custodian : null,
                document.DocType == "loose" ? document.Sha256.ToUpperInvariant() : null,
                root.Email, root.ChildShapes);
            if (!winners.TryGetValue(key, out var winner) ||
                StringComparer.Ordinal.Compare(document.DocId, winner.Document.DocId) < 0)
                winners[key] = root;
        }
        return new HashSet<FamilyNode>(winners.Values);
    }

    private static (int Suppressed, int MixedCustodianFamilies) AssignFamilies(
        List<FamilyNode> roots, HashSet<FamilyNode> keptRoots)
    {
        var stack = new Stack<FamilyNode>();
        var suppressed = 0;
        var mixedCustodianFamilies = 0;
        foreach (var root in roots)
        {
            var keep = keptRoots.Contains(root);
            var mixed = false;
            stack.Push(root);
            while (stack.TryPop(out var node))
            {
                node.Root = root;
                node.Keep = keep;
                mixed |= !StringComparer.Ordinal.Equals(root.Document.Custodian, node.Document.Custodian);
                if (!keep)
                    suppressed++;
                foreach (var child in node.Children)
                    stack.Push(child);
            }
            if (mixed)
                mixedCustodianFamilies++;
        }
        return (suppressed, mixedCustodianFamilies);
    }

    private static readonly Regex IsoOffsetDate = new(
        @"^(?<date>\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}(?::\d{2}(?:[.,](?<fraction>\d+))?)?)(?<offset>[Zz]|[+-]\d{2}(?::?\d{2})?)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal sealed record EmailIdentity(DateTimeOffset Instant, string Fraction, string From, string Recipients,
        string Subject, string Body)
    {
        internal static EmailIdentity Create(Document document)
        {
            var match = document.SentDate is null ? Match.Empty : IsoOffsetDate.Match(document.SentDate);
            if (!match.Success)
                throw new InvalidDataException($"Email '{document.DocId}' requires a valid ISO 8601 sent date with offset.");
            var offset = match.Groups["offset"].Value;
            if (offset.Equals("Z", StringComparison.OrdinalIgnoreCase))
                offset = "+00:00";
            else if (offset.Length == 3)
                offset += ":00";
            else if (offset.Length == 5)
                offset = offset.Insert(3, ":");
            var date = match.Groups["date"].Value;
            var fractionStart = date.IndexOfAny(['.', ',']);
            if (fractionStart >= 0)
                date = date[..fractionStart];
            if (date.Length == 16)
                date += ":00";
            if (!DateTimeOffset.TryParseExact(date.ToUpperInvariant() + offset,
                    "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var instant))
                throw new InvalidDataException($"Email '{document.DocId}' requires a valid ISO 8601 sent date with offset.");
            if (string.IsNullOrWhiteSpace(document.From) || document.To is null ||
                document.To.Any(string.IsNullOrWhiteSpace) || document.Subject is null || document.Body is null)
                throw new InvalidDataException($"Email '{document.DocId}' has incomplete identity fields.");

            var recipients = document.To.Select(address => address.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            // Length prefixes make the sorted set unambiguous even if an address contains delimiters.
            var recipientKey = string.Concat(recipients.Select(address =>
                address.Length.ToString(CultureInfo.InvariantCulture) + ":" + address));
            return new EmailIdentity(instant.ToUniversalTime(),
                match.Groups["fraction"].Value.TrimEnd('0'), document.From.ToLowerInvariant(),
                recipientKey, document.Subject.Trim(),
                document.Body.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n').Trim());
        }
    }

    internal sealed class FamilyNode(Document document)
    {
        internal Document Document { get; } = document;
        internal FamilyNode? Parent { get; set; }
        internal List<FamilyNode> Children { get; } = new();
        internal int RemainingChildren { get; set; }
        internal int ShapeId { get; set; }
        internal int[] ChildShapes { get; set; } = Array.Empty<int>();
        internal FamilyNode Root { get; set; } = null!;
        internal bool Keep { get; set; }
        internal EmailIdentity? Email { get; set; }
    }

    private sealed class TreeShape(string type, string hash, string fileName, int[] children)
        : IEquatable<TreeShape>
    {
        public bool Equals(TreeShape? other) => other is not null &&
            StringComparer.Ordinal.Equals(type, other.Type) &&
            StringComparer.Ordinal.Equals(hash, other.Hash) &&
            StringComparer.Ordinal.Equals(fileName, other.FileName) &&
            children.AsSpan().SequenceEqual(other.Children);
        public override bool Equals(object? obj) => obj is TreeShape other && Equals(other);
        public override int GetHashCode() => ShapeHash(type, hash, fileName, children);
        private string Type => type;
        private string Hash => hash;
        private string FileName => fileName;
        private int[] Children => children;
    }

    private sealed class FamilyKey(string type, string? custodian, string? hash,
        EmailIdentity? email, int[] children) : IEquatable<FamilyKey>
    {
        public bool Equals(FamilyKey? other) => other is not null &&
            StringComparer.Ordinal.Equals(type, other.Type) &&
            StringComparer.Ordinal.Equals(custodian, other.Custodian) &&
            StringComparer.Ordinal.Equals(hash, other.Hash) &&
            Equals(email, other.Email) && children.AsSpan().SequenceEqual(other.Children);
        public override bool Equals(object? obj) => obj is FamilyKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(ShapeHash(type, custodian, hash, children), email);
        private string Type => type;
        private string? Custodian => custodian;
        private string? Hash => hash;
        private EmailIdentity? Email => email;
        private int[] Children => children;
    }

    private static int ShapeHash(string? first, string? second, string? third, int[] children)
    {
        var hash = new HashCode();
        hash.Add(first, StringComparer.Ordinal);
        hash.Add(second, StringComparer.Ordinal);
        hash.Add(third, StringComparer.Ordinal);
        foreach (var child in children)
            hash.Add(child);
        return hash.ToHashCode();
    }
}

internal sealed class DedupPlan(List<Deduper.FamilyNode> nodes, int suppressed, int mixedCustodianFamilies)
{
    internal int Suppressed { get; } = suppressed;
    internal int MixedCustodianFamilies { get; } = mixedCustodianFamilies;

    internal IEnumerable<LoadFileRow> Rows()
    {
        foreach (var node in nodes)
        {
            if (!node.Keep)
                continue;
            var document = node.Document;
            yield return new LoadFileRow
            {
                DocId = document.DocId,
                FamilyId = node.Root.Document.DocId,
                ParentId = document.ParentId ?? "",
                Custodian = document.Custodian,
                DocType = document.DocType,
                FileName = document.FileName,
                SentDate = node.Root.Email?.Instant.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "",
                Hash = document.Sha256,
            };
        }
    }
}
