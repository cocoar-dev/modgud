namespace BuildingBlocks.Helper;

public static class ArrayHelper
{
    public static T[] WrapInArray<T>(T? value)
    {
        if (value is null)
        {
            return [];
        }

        return [value];
    }
}
