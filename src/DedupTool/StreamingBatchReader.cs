using System.Text.Json;

namespace DedupTool;

internal static class StreamingBatchReader
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    internal static string Read(Stream stream, int limit, Action clearDocuments, Action<Document?> addDocument)
    {
        var buffer = new byte[Math.Min(16384, limit)];
        var length = 0;
        var final = false;
        var state = new JsonReaderState();
        var mode = Mode.Start;
        string? property = null;
        string? batchId = null;
        var hasBatchId = false;
        var hasDocuments = false;
        var documentsNull = false;

        while (length < 3)
        {
            var read = stream.Read(buffer, length, 3 - length);
            if (read == 0) { final = true; break; }
            length += read;
        }
        if (length >= 3 && buffer[0] == 0xef && buffer[1] == 0xbb && buffer[2] == 0xbf)
            length = 0;

        while (true)
        {
            if (!final && length < buffer.Length)
            {
                var read = stream.Read(buffer, length, buffer.Length - length);
                length += read;
                final = read == 0;
            }

            var reader = new Utf8JsonReader(buffer.AsSpan(0, length), final, state);
            var consumed = 0;
            var savedState = state;
            while (true)
            {
                var before = reader.BytesConsumed;
                var beforeState = reader.CurrentState;
                if (!reader.Read())
                {
                    consumed = checked((int)reader.BytesConsumed);
                    savedState = reader.CurrentState;
                    break;
                }

                if (mode == Mode.Documents && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (!JsonDocument.TryParseValue(ref reader, out var document))
                    {
                        consumed = checked((int)before);
                        savedState = beforeState;
                        goto Refill;
                    }
                    using (document)
                    {
                        if (reader.BytesConsumed - before > limit)
                            throw new InvalidDataException($"A JSON document exceeds the {limit}-byte limit.");
                        addDocument(document.RootElement.Deserialize<Document>(Options));
                    }
                }
                else if (mode == Mode.Start)
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                        throw new JsonException("Batch must be a JSON object.");
                    mode = Mode.Property;
                }
                else if (mode == Mode.Property)
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                        mode = Mode.Done;
                    else if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        property = reader.GetString();
                        mode = Mode.Value;
                    }
                    else
                        throw new JsonException("Expected a batch property.");
                }
                else if (mode == Mode.Value)
                {
                    if (string.Equals(property, "documents", StringComparison.OrdinalIgnoreCase))
                    {
                        hasDocuments = true;
                        clearDocuments();
                        documentsNull = reader.TokenType == JsonTokenType.Null;
                        if (documentsNull)
                            mode = Mode.Property;
                        else if (reader.TokenType == JsonTokenType.StartArray)
                            mode = Mode.Documents;
                        else
                            throw new JsonException("Batch documents must be an array.");
                    }
                    else if (string.Equals(property, "batchId", StringComparison.OrdinalIgnoreCase))
                    {
                        hasBatchId = true;
                        if (reader.TokenType != JsonTokenType.String && reader.TokenType != JsonTokenType.Null)
                            throw new JsonException("Batch ID must be a string.");
                        batchId = reader.GetString();
                        mode = Mode.Property;
                    }
                    else
                    {
                        if (!reader.TrySkip())
                        {
                            consumed = checked((int)before);
                            savedState = beforeState;
                            goto Refill;
                        }
                        mode = Mode.Property;
                    }
                }
                else if (mode == Mode.Documents)
                    mode = Mode.Property;
                else
                    throw new JsonException("Unexpected content after the batch.");

                consumed = checked((int)reader.BytesConsumed);
                savedState = reader.CurrentState;
            }

        Refill:
            if (consumed > 0)
            {
                buffer.AsSpan(consumed, length - consumed).CopyTo(buffer);
                length -= consumed;
                state = savedState;
            }
            if (final)
            {
                if (length != 0 || mode != Mode.Done)
                    throw new JsonException("Incomplete batch JSON.");
                break;
            }
            if (length == buffer.Length)
            {
                if (buffer.Length >= limit)
                    throw new InvalidDataException($"A JSON token or document exceeds the {limit}-byte limit.");
                Array.Resize(ref buffer, Math.Min(limit, checked(buffer.Length * 2)));
            }
        }

        if (!hasBatchId || string.IsNullOrWhiteSpace(batchId))
            throw new InvalidDataException("Batch ID is missing.");
        if (!hasDocuments || documentsNull)
            throw new InvalidDataException("Batch documents are missing.");
        return batchId;
    }

    private enum Mode { Start, Property, Value, Documents, Done }
}
