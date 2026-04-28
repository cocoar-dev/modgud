using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace BuildingBlocks.Helper;

public static class ObservableExtensions
{
    /// <summary>
    /// Defers the streaming of live updates until after some initial data is provided via a function that returns an observable.
    /// The initial data is sent first, followed by the buffered updates and live events.
    /// </summary>
    /// <typeparam name="T">The type of data being streamed.</typeparam>
    /// <param name="liveStream">The observable stream of live updates.</param>
    /// <param name="initialProvider">A function that returns an observable providing the initial data.</param>
    /// <returns>An observable that first emits the initial data, followed by the live updates.</returns>
    public static IObservable<T> DeferUntil<T>(
        this IObservable<T> liveStream,
        Func<IObservable<T>> initialProvider)
    {
        // Use the initial provider to get the observable and then defer the stream.
        return liveStream.DeferUntil(initialProvider());
    }

    /// <summary>
    /// Defers the streaming of live updates until after some initial data is provided via an observable.
    /// The initial data is sent first, followed by the buffered updates and live events.
    /// </summary>
    /// <typeparam name="T">The type of data being streamed.</typeparam>
    /// <param name="liveStream">The observable stream of live updates.</param>
    /// <param name="initialObservable">An observable that emits the initial data.</param>
    /// <returns>An observable that first emits the initial data, followed by the live updates.</returns>
    public static IObservable<T> DeferUntil<T>(
        this IObservable<T> liveStream,
        IObservable<T> initialObservable)
    {
        var buffer = new ReplaySubject<T>();
        liveStream.Subscribe(buffer);
        return initialObservable.Concat(buffer).Concat(liveStream);
    }

    /// <summary>
    /// Defers the streaming of live updates until after some initial data is provided via a function that returns a single value.
    /// The initial data is sent first, followed by the buffered updates and live events.
    /// </summary>
    /// <typeparam name="T">The type of data being streamed.</typeparam>
    /// <param name="liveStream">The observable stream of live updates.</param>
    /// <param name="initialValueProvider">A function that returns the initial value.</param>
    /// <returns>An observable that first emits the initial value, followed by the live updates.</returns>
    public static IObservable<T> DeferUntil<T>(
        this IObservable<T> liveStream,
        Func<T> initialValueProvider)
    {
        return liveStream.DeferUntil(Observable.Return(initialValueProvider()));
    }

    /// <summary>
    /// Defers the streaming of live updates until after some initial data is provided as a static value.
    /// The initial data is sent first, followed by the buffered updates and live events.
    /// </summary>
    /// <typeparam name="T">The type of data being streamed.</typeparam>
    /// <param name="liveStream">The observable stream of live updates.</param>
    /// <param name="initialValue">The initial value to be emitted first.</param>
    /// <returns>An observable that first emits the initial value, followed by the live updates.</returns>
    public static IObservable<T> DeferUntil<T>(
        this IObservable<T> liveStream,
        T initialValue)
    {
        return liveStream.DeferUntil(Observable.Return(initialValue));
    }

    // Async overloads

    /// <summary>
    /// Defers the streaming of live updates until after some initial data is provided via an async function that returns an observable.
    /// The initial data is sent first, followed by the buffered updates and live events.
    /// </summary>
    /// <typeparam name="T">The type of data being streamed.</typeparam>
    /// <param name="liveStream">The observable stream of live updates.</param>
    /// <param name="initialProviderAsync">An async function that returns an observable providing the initial data.</param>
    /// <returns>An observable that first emits the initial data, followed by the live updates.</returns>
    public static IObservable<T> DeferUntil<T>(
        this IObservable<T> liveStream,
        Func<Task<IObservable<T>>> initialProviderAsync)
    {
        var buffer = new ReplaySubject<T>();
        liveStream.Subscribe(buffer);

        return Observable.FromAsync(initialProviderAsync)
            .SelectMany(initialObservable => initialObservable.Concat(buffer).Concat(liveStream));
    }

    /// <summary>
    /// Defers the streaming of live updates until after some initial data is provided via an async function that returns a single value.
    /// The initial data is sent first, followed by the buffered updates and live events.
    /// </summary>
    /// <typeparam name="T">The type of data being streamed.</typeparam>
    /// <param name="liveStream">The observable stream of live updates.</param>
    /// <param name="initialValueProviderAsync">An async function that returns the initial value.</param>
    /// <returns>An observable that first emits the initial value, followed by the live updates.</returns>
    public static IObservable<T> DeferUntil<T>(
        this IObservable<T> liveStream,
        Func<Task<T>> initialValueProviderAsync)
    {
        return liveStream.DeferUntil(() => Observable.FromAsync(initialValueProviderAsync));
    }

    /// <summary>
    /// Defers the streaming of live updates until after some initial data is provided via an async Task that returns an observable.
    /// The initial data is sent first, followed by the buffered updates and live events.
    /// </summary>
    /// <typeparam name="T">The type of data being streamed.</typeparam>
    /// <param name="liveStream">The observable stream of live updates.</param>
    /// <param name="initialTask">An async Task that returns an observable providing the initial data.</param>
    /// <returns>An observable that first emits the initial data, followed by the live updates.</returns>
    public static IObservable<T> DeferUntil<T>(
        this IObservable<T> liveStream,
        Task<IObservable<T>> initialTask)
    {
        return liveStream.DeferUntil(() => initialTask);
    }

    /// <summary>
    /// Defers the streaming of live updates until after some initial data is provided via an async Task that returns a single value.
    /// The initial data is sent first, followed by the buffered updates and live events.
    /// </summary>
    /// <typeparam name="T">The type of data being streamed.</typeparam>
    /// <param name="liveStream">The observable stream of live updates.</param>
    /// <param name="initialTask">An async Task that returns the initial value.</param>
    /// <returns>An observable that first emits the initial value, followed by the live updates.</returns>
    public static IObservable<T> DeferUntil<T>(
        this IObservable<T> liveStream,
        Task<T> initialTask)
    {
        return liveStream.DeferUntil(() => Observable.FromAsync(() => initialTask));
    }

    /// <summary>
    /// Converts a <see cref="CancellationToken"/> into an observable sequence that emits a single value when the token is canceled.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to convert.</param>
    /// <returns>
    /// An observable sequence that emits a <see cref="Unit"/> value when the cancellation token is canceled.
    /// </returns>
    /// <remarks>
    /// The returned observable can be used with operators like <c>TakeUntil</c> to cancel an observable sequence when the cancellation token is triggered.
    /// </remarks>
    public static IObservable<Unit> ToObservable(this CancellationToken cancellationToken)
    {
        return Observable.Create<Unit>(observer =>
        {
            var registration = cancellationToken.Register(() =>
            {
                observer.OnNext(Unit.Default);
                observer.OnCompleted();
            });

            return registration;
        });
    }

    /// <summary>
    /// Executes an asynchronous action for each element in the source sequence, with support for cancellation.
    /// </summary>
    /// <typeparam name="T">The type of elements in the source sequence.</typeparam>
    /// <param name="source">The source observable sequence.</param>
    /// <param name="asyncAction">An asynchronous function to execute for each element, which accepts a <see cref="CancellationToken"/>.</param>
    /// <param name="cancellationToken">A cancellation token to signal cancellation. Defaults to <see cref="CancellationToken.None"/>.</param>
    /// <returns>
    /// An <see cref="IDisposable"/> representing the subscription to the sequence.
    /// </returns>
    /// <remarks>
    /// This method subscribes to the source sequence and executes the specified asynchronous action for each element.
    /// If the cancellation token is canceled, the sequence is terminated, and any ongoing asynchronous operations receive the cancellation request.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="source"/> or <paramref name="asyncAction"/> is <c>null</c>.
    /// </exception>
    public static IDisposable ExecuteAsync<T>(
        this IObservable<T> source,
        Func<T, CancellationToken, Task> asyncAction,
        CancellationToken cancellationToken = default)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        if (asyncAction == null)
            throw new ArgumentNullException(nameof(asyncAction));

        return source
            .SelectMany(item => Observable.FromAsync(ct => asyncAction(item, ct)))
            .TakeUntil(cancellationToken.ToObservable())
            .Catch<Unit, OperationCanceledException>(_ => Observable.Empty<Unit>())
            .Subscribe();
    }

    /// <summary>
    /// Executes an asynchronous action for each element in the source sequence, with support for cancellation.
    /// </summary>
    /// <typeparam name="T">The type of elements in the source sequence.</typeparam>
    /// <param name="source">The source observable sequence.</param>
    /// <param name="asyncAction">An asynchronous function to execute for each element.</param>
    /// <param name="cancellationToken">A cancellation token to signal cancellation. Defaults to <see cref="CancellationToken.None"/>.</param>
    /// <returns>
    /// An <see cref="IDisposable"/> representing the subscription to the sequence.
    /// </returns>
    /// <remarks>
    /// This method subscribes to the source sequence and executes the specified asynchronous action for each element.
    /// If the cancellation token is canceled, the sequence is terminated. Note that the asynchronous action does not receive the cancellation token,
    /// so any ongoing operations may continue unless they handle cancellation internally.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="source"/> or <paramref name="asyncAction"/> is <c>null</c>.
    /// </exception>
    public static IDisposable ExecuteAsync<T>(
        this IObservable<T> source,
        Func<T, Task> asyncAction,
        CancellationToken cancellationToken = default)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        if (asyncAction == null)
            throw new ArgumentNullException(nameof(asyncAction));

        return source
            .SelectMany(item => Observable.FromAsync(() => asyncAction(item)))
            .TakeUntil(cancellationToken.ToObservable())
            .Catch<Unit, OperationCanceledException>(_ => Observable.Empty<Unit>())
            .Subscribe();
    }
}
