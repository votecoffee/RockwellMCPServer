using System.Text;

namespace Studio5000.Client;

public sealed partial class ServiceClient
{
    // The pinned service has no summary/paging endpoint. Bound this read of its
    // legacy array locally; never send unsupported query parameters or read journals.
    public async Task<SdkJobHistoryPage> ListSdkJobsAsync(int offset = 0, int limit = 5, CancellationToken ct = default)
    {
        try
        {
            SdkJobHistory.ValidatePage(offset, limit);
            var connection = await Ready(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(connection.Timeout); // ResponseHeadersRead alone does not time out the body.
            using var response = await connection.GetAsync("/api/sdk/jobs", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var bytes = new byte[16384]; var count = 0;
                while (count < bytes.Length)
                {
                    var read = await body.ReadAsync(bytes.AsMemory(count), timeout.Token);
                    if (read == 0) break;
                    count += read;
                }
                // An overlong/unknown envelope gets the existing generic HTTP error.
                var text = count == bytes.Length ? "" : Encoding.UTF8.GetString(bytes, 0, count);
                throw new ServiceErrorException(ServiceError.Parse(response.StatusCode, text));
            }
            return await SdkJobHistory.ReadAsync(body, offset, limit,
                Math.Min(maximumResponseBytes ?? SdkJobHistory.MaximumBytes, SdkJobHistory.MaximumBytes), timeout.Token);
        }
        catch (ServiceErrorException error) when (errorTranslator is not null)
        { throw errorTranslator(error); }
    }
}
