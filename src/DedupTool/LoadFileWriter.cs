using System.Text;

namespace DedupTool;

public static class LoadFileWriter
{
    public const string Header = "DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash";

    public static string ToCsv(IEnumerable<LoadFileRow> rows)
    {
        using var writer = new StringWriter();
        WriteRows(writer, rows);
        return writer.ToString();
    }

    public static void Write(string path, IEnumerable<LoadFileRow> rows)
    {
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        Write(destination, temporary, rows);
    }

    internal static void Write(string destination, string temporary, IEnumerable<LoadFileRow> rows)
    {
        var created = false;
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                created = true;
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                WriteRows(writer, rows);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (created && File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void WriteRows(TextWriter writer, IEnumerable<LoadFileRow> rows)
    {
        writer.Write(Header);
        writer.Write('\n');
        foreach (var row in rows)
        {
            string[] fields =
            [
                row.DocId, row.FamilyId, row.ParentId, row.Custodian,
                row.DocType, row.FileName, row.SentDate, row.Hash
            ];
            for (var i = 0; i < fields.Length; i++)
            {
                if (i > 0)
                    writer.Write(',');
                WriteField(writer, fields[i]);
            }
            writer.Write('\n');
        }
    }

    private static void WriteField(TextWriter writer, string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            writer.Write(value);
            return;
        }

        writer.Write('"');
        foreach (var character in value)
        {
            if (character == '"')
                writer.Write('"');
            writer.Write(character);
        }
        writer.Write('"');
    }
}
