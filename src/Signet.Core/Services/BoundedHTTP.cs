namespace Signet.Services;

/// <summary>
/// The one way native code reads an HTTP reply from a node, an indexer, a faucet or a name
/// service (S19). The body is taken as a stream and dropped the moment it passes the caller's
/// byte cap, declared length or not, so a misbehaving or hostile server cannot make the app
/// accumulate an arbitrarily large reply before rejecting it; a JSON reply is also checked
/// against structure and string budgets (<c>JSONBudget</c>) before anything parses it. The bridge's
/// own fetches have the same protections in <c>TaquitoBridge</c>; this is for the readers outside it.
///
/// Windows: <c>Http.Get</c>/<c>PostJSON</c>/<c>Send</c> read through <c>Read</c> and take the limit and the
/// JSON budget as arguments (upstream's <c>data(for:session:limit:json:)</c>).
/// </summary>
public static class BoundedHTTP
{
    /// <summary>Structure budgets for a JSON body, checked before parsing.</summary>
    public sealed record JSONLimits(int Depth, int Nodes, int String)
    {
        /// <summary>A chain id, a balance, a block header, a name lookup: a handful of values.</summary>
        public static readonly JSONLimits Small = new(16, 2_000, 16 << 10);
        /// <summary>One account record, a faucet reply: a few hundred fields at most.</summary>
        public static readonly JSONLimits Record = new(32, 20_000, 64 << 10);
        /// <summary>
        /// A page of operations, tokens or bakers; contract parameters nest deeply and token
        /// metadata can carry long URIs.
        /// </summary>
        public static readonly JSONLimits Page = new(128, 250_000, 1 << 20);
        /// <summary>Not JSON, or not checked (a node probe reads whatever the node answers).</summary>
        public static readonly JSONLimits Unchecked = new(int.MaxValue, int.MaxValue, int.MaxValue);
    }

    public abstract class Failure(string message) : HttpRequestException(message)
    {
        public sealed class TooLarge(int limit) : Failure($"The server's reply was larger than the {limit / 1024} KB allowed.")
        {
            public int Limit { get; } = limit;
        }

        public sealed class OverBudget() : Failure("The server's reply was more complex than allowed.");
    }

    /// <summary>
    /// The body of <c>response</c>, read as a stream and refused as soon as it passes <c>limit</c>
    /// bytes (a declared length over it is refused before a byte is read), then checked against
    /// <c>json</c> when given.
    /// </summary>
    public static async Task<byte[]> Read(HttpResponseMessage response, int limit, JSONLimits? json, CancellationToken cancel)
    {
        if (limit < 0 || response.Content.Headers.ContentLength > limit) throw new Failure.TooLarge(limit);
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancel)) > 0)
        {
            if (read > limit - body.Length) throw new Failure.TooLarge(limit);
            body.Write(buffer, 0, read);
        }
        var data = body.ToArray();
        if (json is not null && !JSONBudget.Accepts(data, json.Depth, json.Nodes, json.String)) throw new Failure.OverBudget();
        return data;
    }
}
