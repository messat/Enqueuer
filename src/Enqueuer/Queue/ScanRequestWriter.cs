using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Enqueuer.Queue
{
    // Writes a scan request as the JSON the scanner reads: plain text, not Base64, with the six fields in the
    // example's names and order and no indentation (Issue 3 design, decisions 2 and 3)
    public static class ScanRequestWriter
    {
        public static string Write(ScanRequest request)
        {
            ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>();
            using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
            {
                // The names are written out here, not taken from the record's properties, so renaming a property
                // cannot change what is sent (decision 3)
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", ScanRequest.SchemaVersion);
                // The Guid overloads write the lower-case form with hyphens, as the example has it
                writer.WriteString("correlationId", request.CorrelationId);
                writer.WriteString("repositoryId", request.RepositoryId);
                writer.WriteString("projectName", request.ProjectName);
                writer.WriteString("repositoryName", request.RepositoryName);
                writer.WriteString("defaultBranch", request.DefaultBranch);
                writer.WriteEndObject();
                writer.Flush();
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }
}
