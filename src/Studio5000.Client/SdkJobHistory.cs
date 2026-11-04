using System.Text.Json;

namespace Studio5000.Client;

public sealed record SdkJobSummary(string Id, string Status, DateTimeOffset CreatedAt, string[] Methods,
    string? SourceSha256, string? ControllerTargetId, bool? ControllerEffectsPossible, bool? HasError);

public sealed record SdkJobHistoryPage(SdkJobSummary[] Items, int Offset, int Limit, int? NextOffset,
    bool HasMore, int SourceWindowLimit, bool SourceMayHaveMore, int ResponseBytesRead, int MaximumResponseBytes,
    bool SummariesOnly = true);

// Transport projection shared by clients, not a second SDK operation. Only a
// bounded prefix is observed; full results and all journals remain service-owned.
public static class SdkJobHistory
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    public const int SourceWindowLimit = 100;
    private const int InitialBufferBytes = 64 * 1024;

    public static void ValidatePage(int offset, int limit)
    {
        if (offset is < 0 or >= SourceWindowLimit || limit is < 1 or > 20)
            throw Error("sdk_job_history_page_invalid", "Use offset 0..99 and limit 1..20 (default 5).", 400);
    }

    public static async Task<SdkJobHistoryPage> ReadAsync(Stream source, int offset = 0, int limit = 5,
        int maximumResponseBytes = MaximumBytes, CancellationToken ct = default)
    {
        ValidatePage(offset, limit);
        if (maximumResponseBytes is < 1 or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        // Completed tokens live in JsonReaderState, not a whole-job DOM. Only
        // an incomplete token can require a larger contiguous input buffer.
        var buffer = new byte[Math.Min(InitialBufferBytes, maximumResponseBytes)];
        var parser = new Parser(offset, limit); var buffered = 0; var total = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (total == maximumResponseBytes)
                    throw Error("sdk_job_history_limit", "The SDK history prefix reached the response byte limit before a complete page and lookahead. Use a smaller/earlier page or studio_sdk_get_job with a known ID. Deeper history requires a future service summary API; no jobs were changed.", 413);
                // Grow only when the unconsumed tail fills the buffer. The
                // independent total still counts every byte read, including
                // completed tokens that have already been compacted away.
                if (buffered == buffer.Length)
                    Array.Resize(ref buffer, Math.Min(buffer.Length * 2, maximumResponseBytes));
                var read = await source.ReadAsync(buffer.AsMemory(buffered,
                    Math.Min(InitialBufferBytes, Math.Min(buffer.Length - buffered, maximumResponseBytes - total))), ct);
                buffered += read; total += read;
                var consumed = parser.Consume(buffer.AsSpan(0, buffered), read == 0, ct);
                if (parser.Done)
                    return new(parser.Items.ToArray(), offset, limit, parser.HasMore ? offset + parser.Items.Count : null,
                        parser.HasMore, SourceWindowLimit, parser.HasMore || parser.Observed >= SourceWindowLimit,
                        total, maximumResponseBytes);
                buffer.AsSpan(consumed, buffered - consumed).CopyTo(buffer);
                buffered -= consumed;
            }
        }
        catch (JsonException)
        { throw Error("sdk_job_history_invalid", "The SDK history response prefix is not a valid job array. Inspect service diagnostics; no jobs were changed.", 502); }
    }

    private static ServiceErrorException Error(string code, string message, int status) => new(new(code, message, status, []));

    private sealed class Parser(int offset, int limit)
    {
        private JsonReaderState state;
        private bool started, ended;
        private Summary? job;
        public List<SdkJobSummary> Items { get; } = [];
        public int Observed { get; private set; }
        public bool HasMore { get; private set; }
        public bool Done => ended || HasMore;

        public int Consume(ReadOnlySpan<byte> bytes, bool final, CancellationToken ct)
        {
            var reader = new Utf8JsonReader(bytes, final, state);
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (!started)
                {
                    if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException();
                    started = true; continue;
                }
                if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.StartObject)
                {
                    if (Observed >= SourceWindowLimit) throw new JsonException();
                    if (Items.Count == limit) { HasMore = true; break; }
                    job = Observed >= offset ? new() : null;
                }
                else if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.EndObject)
                {
                    if (job is not null) Items.Add(job.Finish());
                    job = null; Observed++;
                }
                else if (reader.CurrentDepth == 0 && reader.TokenType == JsonTokenType.EndArray) ended = true;
                else if (reader.CurrentDepth == 1) throw new JsonException();
                else job?.Read(ref reader);
            }
            state = reader.CurrentState;
            if (final && !Done) throw new JsonException();
            return checked((int)reader.BytesConsumed);
        }
    }

    private sealed class Summary
    {
        private enum Field { None, Id, Status, CreatedAt, Methods, SourceSha256, ControllerTargetId, ControllerEffectsPossible, Error }
        private Field field;
        private int seen;
        private string id = "", status = "";
        private DateTimeOffset createdAt;
        private readonly List<string> methods = [];
        private string? sourceSha256, controllerTargetId;
        private bool? effects, hasError;
        private bool inMethods;

        public void Read(ref Utf8JsonReader reader)
        {
            if (reader.CurrentDepth == 2 && reader.TokenType == JsonTokenType.PropertyName)
            {
                field = reader.ValueTextEquals("id"u8) ? Field.Id : reader.ValueTextEquals("status"u8) ? Field.Status :
                    reader.ValueTextEquals("createdAt"u8) ? Field.CreatedAt : reader.ValueTextEquals("methods"u8) ? Field.Methods :
                    reader.ValueTextEquals("sourceSha256"u8) ? Field.SourceSha256 : reader.ValueTextEquals("controllerTargetId"u8) ? Field.ControllerTargetId :
                    reader.ValueTextEquals("controllerEffectsPossible"u8) ? Field.ControllerEffectsPossible : reader.ValueTextEquals("error"u8) ? Field.Error : Field.None;
                if (field != Field.None)
                {
                    var flag = 1 << (int)field;
                    if ((seen & flag) != 0) throw new JsonException();
                    seen |= flag;
                }
            }
            else if (inMethods && reader.CurrentDepth == 3)
            {
                if (methods.Count == 256) throw new JsonException();
                methods.Add(Text(ref reader, 256));
            }
            else if (reader.CurrentDepth == 2)
            {
                switch (field)
                {
                    case Field.Id: id = Text(ref reader, 128); break;
                    case Field.Status: status = Text(ref reader, 64); break;
                    case Field.CreatedAt:
                        if (reader.TokenType != JsonTokenType.String || !reader.TryGetDateTimeOffset(out createdAt)) throw new JsonException();
                        break;
                    case Field.Methods:
                        if (reader.TokenType == JsonTokenType.StartArray) inMethods = true;
                        else if (reader.TokenType == JsonTokenType.EndArray && inMethods) inMethods = false;
                        else throw new JsonException();
                        break;
                    case Field.SourceSha256: sourceSha256 = reader.TokenType == JsonTokenType.Null ? null : Text(ref reader, 128); break;
                    case Field.ControllerTargetId: controllerTargetId = reader.TokenType == JsonTokenType.Null ? null : Text(ref reader, 4096); break;
                    case Field.ControllerEffectsPossible:
                        effects = reader.TokenType switch { JsonTokenType.True => true, JsonTokenType.False => false, JsonTokenType.Null => null, _ => throw new JsonException() }; break;
                    case Field.Error:
                        hasError = reader.TokenType switch { JsonTokenType.String => true, JsonTokenType.Null => false, _ => throw new JsonException() }; break;
                }
            }
        }

        private static string Text(ref Utf8JsonReader reader, int maximum)
        {
            if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length > maximum) throw new JsonException();
            return reader.GetString()!;
        }

        public SdkJobSummary Finish()
        {
            const int required = (1 << (int)Field.Id) | (1 << (int)Field.Status) | (1 << (int)Field.CreatedAt) | (1 << (int)Field.Methods);
            if ((seen & required) != required || id.Length == 0 || status.Length == 0) throw new JsonException();
            return new(id, status, createdAt, methods.ToArray(), sourceSha256, controllerTargetId, effects, hasError);
        }
    }
}
