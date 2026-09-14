namespace Gelato.Services;

public static class CatalogSearch
{
    /// <summary>Preserves successful catalogs, but distinguishes a total outage from no matches.</summary>
    public static async Task<List<T>> CollectAsync<T>(
        IReadOnlyList<Func<CancellationToken, Task<IReadOnlyList<T>>>> searches,
        Action<int, Exception> onFailure, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = await Task.WhenAll(searches.Select(async (search, index) =>
        {
            try
            {
                return (Success: true, Items: await search(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                onFailure(index, error);
                return (Success: false, Items: (IReadOnlyList<T>)Array.Empty<T>());
            }
        })).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (results.Length > 0 && results.All(result => !result.Success))
            throw new HttpRequestException("All configured search catalogs failed");
        return results.SelectMany(result => result.Items).ToList();
    }
}
