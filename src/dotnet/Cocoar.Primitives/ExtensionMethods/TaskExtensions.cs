namespace Cocoar.Primitives.ExtensionMethods
{
    public static class TaskExtensions
    {
        public static Task<List<T>> ToListAsync<T>(this Task<IReadOnlyList<T>> task)
        {
            return task.ContinueWith(t => t.Result.ToList());
        }
    }
}
