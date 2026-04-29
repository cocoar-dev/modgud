namespace BuildingBlocks.Helper;

public static class ListExtensions
{
    public static List<string> SkipNullOrEmpty(this List<string?> list)
    {
        return list.SkipWhile(String.IsNullOrEmpty).Cast<string>().ToList();
    }

    public static List<string> SkipNullOrWhiteSpace(this List<string?> list)
    {
        return list.SkipWhile(String.IsNullOrWhiteSpace).Cast<string>().ToList();
    }

    public static List<T> TryAdd<T>(this List<T> list, T item)
    {
        if (!list.Contains(item))
        {
            list.Add(item);
        }

        return list;
    }

    public static async Task<List<TOut>> SelectAsync<T, TOut>(this Task<List<T>> list, Func<T, TOut> selector)
    {
        var items = await list;
        return items.Select(selector).ToList();
    }
}
