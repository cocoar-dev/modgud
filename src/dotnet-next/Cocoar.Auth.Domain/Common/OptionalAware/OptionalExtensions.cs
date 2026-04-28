namespace Cocoar.Auth.Domain.Common;

public static class OptionalExtensions
{
    /// <summary>
    /// Synchronous map: if <paramref name="opt"/> has a value, applies <paramref name="map"/> and wraps the result;
    /// otherwise returns None.
    /// </summary>
    public static Optional<TResult> Map<T, TResult>(
        this Optional<T> opt,
        Func<T, TResult> map)
    {
        if (!opt.HasValue)
            return Optional<TResult>.None;

        return new Optional<TResult>(map(opt.Value!));
    }

    /// <summary>
    /// Asynchronous map: if <paramref name="opt"/> has a value, awaits <paramref name="mapAsync"/>
    /// and wraps the result; otherwise returns None.
    /// </summary>
    public static async Task<Optional<TResult>> MapAsync<T, TResult>(
        this Optional<T> opt,
        Func<T, Task<TResult>> mapAsync)
    {
        if (!opt.HasValue)
            return Optional<TResult>.None;

        var result = await mapAsync(opt.Value!);
        return new Optional<TResult>(result);
    }

    /// <summary>
    /// Synchronous flat-map (bind): if <paramref name="opt"/> has a value, applies <paramref name="binder"/>
    /// which itself returns an Optional; otherwise returns None.
    /// </summary>
    public static Optional<TResult> FlatMap<T, TResult>(
        this Optional<T> opt,
        Func<T, Optional<TResult>> binder)
    {
        if (!opt.HasValue)
            return Optional<TResult>.None;

        return binder(opt.Value!);
    }

    /// <summary>
    /// Asynchronous flat-map: if <paramref name="opt"/> has a value, awaits <paramref name="binderAsync"/>
    /// which itself returns an Optional; otherwise returns None.
    /// </summary>
    public static async Task<Optional<TResult>> FlatMapAsync<T, TResult>(
        this Optional<T> opt,
        Func<T, Task<Optional<TResult>>> binderAsync)
    {
        if (!opt.HasValue)
            return Optional<TResult>.None;

        return await binderAsync(opt.Value!);
    }
}
