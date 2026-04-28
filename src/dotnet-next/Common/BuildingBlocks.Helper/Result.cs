namespace BuildingBlocks.Helper;

public abstract class Result
{
    public bool IsSuccess { get; }
    public abstract object? GetValue();

    public ErrorResult? Error { get; }

    protected Result(bool isSuccess, ErrorResult? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

}
public class Result<T> : Result
{
    public T? Value { get; }
    protected Result(T? value, bool isSuccess, ErrorResult? error) : base(isSuccess, error)
    {
        Value = value;
    }


    public TReturn Match<TReturn>(Func<T, TReturn> ok, Func<ErrorResult, TReturn> error, Action<ErrorResultHandler<TReturn>>? other = null)
    {
        if (IsSuccess)
        {
            return ok(Value!);
        }

        if (other != null)
        {
            var eh = new ErrorResultHandler<TReturn>();
            other(eh);
            var errorType = Error!.GetType();

            if (eh.Handlers.TryGetValue(errorType, out var handler))
            {
                return handler(Error!);
            }
        }


        return error(Error!);
    }


    public override object? GetValue()
    {
        return Value;
    }

    public static Result<T> Success(T value) => new(value, true, null);
    public static Result<T> NotFound() => new(default, false, new NotFoundError());
    public static Result<T> Failure(string message) => new(default, false, new ErrorResult(message));
    public static Result<T> Failure(Exception exception) => new(default, false, new ExceptionError(exception));


}

public class ErrorResultHandler<TResult>
{
    internal Dictionary<Type, Func<ErrorResult, TResult>> Handlers = new();

    public ErrorResultHandler<TResult> Handle<TError>(Func<TError, TResult> handler) where TError : ErrorResult
    {
        Handlers[typeof(TError)] = err => handler((TError)err);
        return this;
    }
}

public record ErrorResult(string Message);
public record NotFoundError(string? Message = null) : ErrorResult(Message ?? "");
public record ExceptionError(Exception Exception) : ErrorResult(Exception.GetBaseException().Message);
