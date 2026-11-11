using System.Text.Json;
using DedupTool;
using Microsoft.Data.Sqlite;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: DedupTool <batch.json> <loadfile.csv>");
            return 2;
        }

        try
        {
            using var job = new DiskJob(args[0], args[1]);
            (string BatchId, int Input, int Suppressed, int Mixed) result;
            using (var pipeline = new DiskPipeline(job))
            {
                result = pipeline.Run();
                File.Delete(job.OutputTemporary);
                LoadFileWriter.Write(job.Destination, job.OutputTemporary, pipeline.Rows());
            }
            if (result.Mixed > 0)
                Console.Error.WriteLine($"Warning: {result.Mixed} mixed-custodian families cannot be independently severed without provenance.");
            try { job.Cleanup(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Warning: output was published but sensitive job scratch could not be removed: {exception.Message}");
            }
            Console.WriteLine($"{result.BatchId}: {result.Input} documents in, {result.Input - result.Suppressed} out, {result.Suppressed} suppressed as duplicates.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or SqliteException)
        {
            Console.Error.WriteLine($"Deduplication failed: {exception.Message}");
            return 1;
        }
    }
}
