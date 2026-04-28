using System.Runtime.CompilerServices;

namespace BuildingBlocks.Helper;

public static class AsyncEnumerableExtensions
{
    public static IAsyncEnumerable<T> ToAsyncEnumerable<T>(this ICollection<T> source, CancellationToken cancellationToken = default)
    {
        return ToAsyncEnumerable(source.AsEnumerable(), cancellationToken);
    }

    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> source, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield(); // This ensures the method is truly asynchronous
            yield return item;
        }
    }
}
