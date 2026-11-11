using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DedupTool;

internal sealed class DiskPipeline : IDisposable
{
    private const int TransactionSize = 1000;
    private readonly DiskJob job;
    private readonly SqliteConnection connection;
    private readonly Func<byte[], byte[]>? digestOverride;

    internal DiskPipeline(DiskJob job, Func<byte[], byte[]>? digestOverride = null)
    {
        this.job = job;
        this.digestOverride = digestOverride;
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = job.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            Initialize();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void Initialize()
    {
        Execute($"PRAGMA cache_size=-{job.CacheKiB}; PRAGMA temp_store=FILE; PRAGMA synchronous=FULL; PRAGMA journal_mode=DELETE;");
        Execute("""
            CREATE TABLE IF NOT EXISTS meta (name TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS nodes (
                id TEXT PRIMARY KEY, sort_key BLOB NOT NULL, parent TEXT, type TEXT NOT NULL,
                custodian TEXT NOT NULL, filename TEXT NOT NULL, hash TEXT NOT NULL,
                email_key TEXT, date TEXT, remaining INTEGER, shape INTEGER, root TEXT, keep INTEGER
            );
            CREATE INDEX IF NOT EXISTS nodes_parent ON nodes(parent);
            CREATE INDEX IF NOT EXISTS nodes_ready ON nodes(shape, remaining);
            CREATE INDEX IF NOT EXISTS nodes_order ON nodes(sort_key);
            CREATE INDEX IF NOT EXISTS nodes_root ON nodes(root);
            CREATE INDEX IF NOT EXISTS nodes_winner ON nodes(parent, shape, sort_key);
            CREATE TABLE IF NOT EXISTS shapes (
                id INTEGER PRIMARY KEY, digest BLOB NOT NULL, a TEXT NOT NULL, b TEXT NOT NULL,
                c TEXT NOT NULL, d TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS shapes_digest ON shapes(digest);
            CREATE TABLE IF NOT EXISTS shape_children (
                shape INTEGER NOT NULL, child INTEGER NOT NULL, quantity INTEGER NOT NULL,
                PRIMARY KEY(shape, child)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS queue (id TEXT PRIMARY KEY, root TEXT NOT NULL, keep INTEGER NOT NULL);
            """);
    }

    internal (string BatchId, int Input, int Suppressed, int Mixed) Run()
    {
        RunStage("staged", "staging", Stage);
        RunStage("shaped", "tree indexing", Shape);
        RunStage("assigned", "family assignment", Assign);

        var input = Count("SELECT COUNT(*) FROM nodes");
        var suppressed = Count("SELECT COUNT(*) FROM nodes WHERE keep=0");
        var mixed = Count("""
            SELECT COUNT(*) FROM nodes AS root
            WHERE root.parent IS NULL AND EXISTS (
                SELECT 1 FROM nodes AS child
                WHERE child.root=root.id AND child.custodian<>root.custodian
            )
            """);
        return (GetMeta("batch")!, input, suppressed, mixed);
    }

    private void RunStage(string marker, string name, Action action)
    {
        var trace = Environment.GetEnvironmentVariable("DEDUP_TRACE") == "1";
        if (GetMeta(marker) == "1")
        {
            if (trace) Console.Error.WriteLine($"Stage {name}: resumed from checkpoint.");
            return;
        }
        var timer = Stopwatch.StartNew();
        action();
        if (trace) Console.Error.WriteLine($"Stage {name}: {timer.Elapsed.TotalSeconds:F2}s.");
    }

    private void Stage()
    {
        Execute("BEGIN IMMEDIATE; DELETE FROM nodes; DELETE FROM meta; COMMIT;");
        Execute("BEGIN IMMEDIATE;");
        var inserted = 0;
        InvalidDataException? validationError = null;
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO nodes(id,sort_key,parent,type,custodian,filename,hash,email_key,date)
                VALUES ($id,$sort,$parent,$type,$custodian,$filename,$hash,$email,$date)
                """;
            foreach (var parameter in new[] { "$id", "$sort", "$parent", "$type", "$custodian", "$filename", "$hash", "$email", "$date" })
                command.Parameters.Add(new SqliteParameter(parameter, DBNull.Value));
            using var input = File.OpenRead(job.SnapshotPath);
            var batch = StreamingBatchReader.Read(input, job.DocumentLimit,
                () => { Execute("DELETE FROM nodes"); inserted = 0; validationError = null; },
                document =>
                {
                    if (validationError is not null)
                        return;
                    try { Deduper.ValidateDocument(document); }
                    catch (InvalidDataException exception) { validationError = exception; return; }
                    var item = document!;
                    Deduper.EmailIdentity? email;
                    try { email = item.DocType == "email" ? Deduper.EmailIdentity.Create(item) : null; }
                    catch (InvalidDataException exception) { validationError = exception; return; }
                    Set(command, "$id", item.DocId);
                    Set(command, "$sort", OrdinalKey(item.DocId));
                    Set(command, "$parent", item.ParentId);
                    Set(command, "$type", item.DocType);
                    Set(command, "$custodian", item.Custodian);
                    Set(command, "$filename", item.FileName);
                    Set(command, "$hash", item.Sha256);
                    Set(command, "$email", email is null ? null : JsonSerializer.Serialize(email));
                    Set(command, "$date", email?.Instant.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                    try { command.ExecuteNonQuery(); }
                    catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
                    {
                        validationError = new InvalidDataException($"Duplicate document ID '{item.DocId}'.", exception);
                        return;
                    }
                    if (++inserted % TransactionSize == 0)
                        Execute("COMMIT; BEGIN IMMEDIATE;");
                });
            if (validationError is not null)
                throw validationError;
            using (var missing = Command("""
                SELECT child.id,child.parent FROM nodes child LEFT JOIN nodes parent ON parent.id=child.parent
                WHERE child.parent IS NOT NULL AND parent.id IS NULL LIMIT 1
                """))
            using (var reader = missing.ExecuteReader())
                if (reader.Read())
                    throw new InvalidDataException($"Document '{reader.GetString(0)}' has missing parent '{reader.GetString(1)}'.");
            SetMeta("batch", batch);
            SetMeta("staged", "1");
            Execute("COMMIT;");
        }
        catch
        {
            Rollback();
            throw;
        }
    }

    private void Shape()
    {
        Execute("""
            BEGIN IMMEDIATE;
            DELETE FROM shape_children; DELETE FROM shapes;
            UPDATE nodes SET remaining=(SELECT COUNT(*) FROM nodes c WHERE c.parent=nodes.id),shape=NULL,root=NULL,keep=NULL;
            COMMIT;
            BEGIN IMMEDIATE;
            """);
        var processed = 0;
        try
        {
            while (true)
            {
                string id, type, custodian, filename, hash;
                string? parent, email;
                using (var command = Command("""
                    SELECT id,parent,type,custodian,filename,hash,email_key
                    FROM nodes WHERE shape IS NULL AND remaining=0 LIMIT 1
                    """))
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        break;
                    id = reader.GetString(0);
                    parent = reader.IsDBNull(1) ? null : reader.GetString(1);
                    type = reader.GetString(2);
                    custodian = reader.GetString(3);
                    filename = reader.GetString(4);
                    hash = reader.GetString(5);
                    email = reader.IsDBNull(6) ? null : reader.GetString(6);
                }

                var a = type;
                var b = parent is not null ? hash.ToUpperInvariant() : type == "loose" ? custodian : "";
                var c = parent is not null ? filename : type == "loose" ? hash.ToUpperInvariant() : "";
                var d = parent is null && type == "email" ? email! : "";
                var digest = ShapeDigest(id, a, b, c, d);
                long? shape = null;
                using (var candidates = Command("SELECT id FROM shapes WHERE digest=$digest AND a=$a AND b=$b AND c=$c AND d=$d"))
                {
                    Add(candidates, "$digest", digest); Add(candidates, "$a", a); Add(candidates, "$b", b);
                    Add(candidates, "$c", c); Add(candidates, "$d", d);
                    using var reader = candidates.ExecuteReader();
                    while (reader.Read())
                    {
                        var candidate = reader.GetInt64(0);
                        if (ChildrenEqual(id, candidate))
                        {
                            shape = candidate;
                            break;
                        }
                    }
                }
                if (shape is null)
                {
                    using var insert = Command("INSERT INTO shapes(digest,a,b,c,d) VALUES($digest,$a,$b,$c,$d); SELECT last_insert_rowid();");
                    Add(insert, "$digest", digest); Add(insert, "$a", a); Add(insert, "$b", b);
                    Add(insert, "$c", c); Add(insert, "$d", d);
                    shape = (long)insert.ExecuteScalar()!;
                    using var children = Command("""
                        INSERT INTO shape_children(shape,child,quantity)
                        SELECT $shape,shape,COUNT(*) FROM nodes WHERE parent=$id GROUP BY shape
                        """);
                    Add(children, "$shape", shape.Value); Add(children, "$id", id);
                    children.ExecuteNonQuery();
                }
                using (var update = Command("UPDATE nodes SET shape=$shape WHERE id=$id"))
                {
                    Add(update, "$shape", shape.Value); Add(update, "$id", id); update.ExecuteNonQuery();
                }
                if (parent is not null)
                {
                    using var update = Command("UPDATE nodes SET remaining=remaining-1 WHERE id=$parent");
                    Add(update, "$parent", parent); update.ExecuteNonQuery();
                }
                if (++processed % TransactionSize == 0)
                    Execute("COMMIT; BEGIN IMMEDIATE;");
            }
            if (processed != Count("SELECT COUNT(*) FROM nodes"))
                throw new InvalidDataException($"Document parent relationships contain a cycle involving '{Scalar("SELECT id FROM nodes WHERE shape IS NULL LIMIT 1")}'.");
            SetMeta("shaped", "1");
            Execute("COMMIT;");
        }
        catch
        {
            Rollback();
            throw;
        }
    }

    private byte[] ShapeDigest(string id, params string[] fields)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> number = stackalloc byte[8];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt64BigEndian(number, bytes.Length);
            digest.AppendData(number);
            digest.AppendData(bytes);
        }
        using var command = Command("SELECT shape,COUNT(*) FROM nodes WHERE parent=$id GROUP BY shape ORDER BY shape");
        Add(command, "$id", id);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            BinaryPrimitives.WriteInt64BigEndian(number, reader.GetInt64(0));
            digest.AppendData(number);
            BinaryPrimitives.WriteInt64BigEndian(number, reader.GetInt64(1));
            digest.AppendData(number);
        }
        var result = digest.GetHashAndReset();
        return digestOverride is null ? result : digestOverride(result);
    }

    private bool ChildrenEqual(string id, long shape)
    {
        using var command = Command("""
            SELECT
              NOT EXISTS (
                SELECT shape,COUNT(*) FROM nodes WHERE parent=$id GROUP BY shape
                EXCEPT SELECT child,quantity FROM shape_children WHERE shape=$shape
              )
              AND NOT EXISTS (
                SELECT child,quantity FROM shape_children WHERE shape=$shape
                EXCEPT SELECT shape,COUNT(*) FROM nodes WHERE parent=$id GROUP BY shape
              )
            """);
        Add(command, "$id", id); Add(command, "$shape", shape);
        return (long)command.ExecuteScalar()! == 1;
    }

    private void Assign()
    {
        Execute("""
            BEGIN IMMEDIATE;
            DELETE FROM queue; UPDATE nodes SET root=NULL,keep=NULL;
            INSERT INTO queue(id,root,keep)
            SELECT r.id,r.id,CASE WHEN r.id=(
              SELECT w.id FROM nodes w WHERE w.parent IS NULL AND w.shape=r.shape ORDER BY w.sort_key LIMIT 1
            ) THEN 1 ELSE 0 END FROM nodes r WHERE r.parent IS NULL;
            COMMIT;
            BEGIN IMMEDIATE;
            """);
        var count = 0;
        try
        {
            while (true)
            {
                string id, root;
                long keep;
                using (var command = Command("SELECT id,root,keep FROM queue LIMIT 1"))
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) break;
                    id = reader.GetString(0); root = reader.GetString(1); keep = reader.GetInt64(2);
                }
                using (var command = Command("UPDATE nodes SET root=$root,keep=$keep WHERE id=$id"))
                {
                    Add(command, "$root", root); Add(command, "$keep", keep); Add(command, "$id", id);
                    command.ExecuteNonQuery();
                }
                using (var command = Command("INSERT INTO queue SELECT id,$root,$keep FROM nodes WHERE parent=$id"))
                {
                    Add(command, "$root", root); Add(command, "$keep", keep); Add(command, "$id", id);
                    command.ExecuteNonQuery();
                }
                using (var command = Command("DELETE FROM queue WHERE id=$id"))
                {
                    Add(command, "$id", id); command.ExecuteNonQuery();
                }
                if (++count % TransactionSize == 0)
                    Execute("COMMIT; BEGIN IMMEDIATE;");
            }
            SetMeta("assigned", "1");
            Execute("COMMIT;");
        }
        catch
        {
            Rollback();
            throw;
        }
    }

    internal IEnumerable<LoadFileRow> Rows()
    {
        using var command = Command("""
            SELECT n.id,n.root,n.parent,n.custodian,n.type,n.filename,r.date,n.hash
            FROM nodes n JOIN nodes r ON r.id=n.root WHERE n.keep=1 ORDER BY n.sort_key
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            yield return new LoadFileRow
            {
                DocId = reader.GetString(0), FamilyId = reader.GetString(1),
                ParentId = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Custodian = reader.GetString(3), DocType = reader.GetString(4),
                FileName = reader.GetString(5), SentDate = reader.IsDBNull(6) ? "" : reader.GetString(6),
                Hash = reader.GetString(7)
            };
    }

    private static byte[] OrdinalKey(string text)
    {
        var bytes = new byte[checked(text.Length * 2)];
        for (var i = 0; i < text.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2, 2), text[i]);
        return bytes;
    }

    private SqliteCommand Command(string sql) { var command = connection.CreateCommand(); command.CommandText = sql; return command; }
    private void Execute(string sql) { using var command = Command(sql); command.ExecuteNonQuery(); }
    private void Rollback()
    {
        try { Execute("ROLLBACK;"); }
        catch (SqliteException exception)
        {
            Console.Error.WriteLine($"Warning: could not roll back the current SQLite transaction: {exception.Message}");
        }
    }
    private object? Scalar(string sql) { using var command = Command(sql); return command.ExecuteScalar(); }
    private int Count(string sql) => checked((int)(long)Scalar(sql)!);
    private string? GetMeta(string key)
    {
        using var command = Command("SELECT value FROM meta WHERE name=$key");
        Add(command, "$key", key);
        return command.ExecuteScalar() as string;
    }
    private void SetMeta(string key, string value)
    {
        using var command = Command("INSERT OR REPLACE INTO meta(name,value) VALUES($key,$value)");
        Add(command, "$key", key); Add(command, "$value", value); command.ExecuteNonQuery();
    }
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static void Set(SqliteCommand command, string name, object? value) => command.Parameters[name].Value = value ?? DBNull.Value;
    public void Dispose() => connection.Dispose();
}
