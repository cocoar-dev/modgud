namespace Cocoar.Primitives
{
    public static class ShortGuidExtensions
    {
        public static ShortGuid ToShortGuid(this Guid source)
        {
            return new ShortGuid(source);
        }

        public static string ToShortGuidString(this Guid source)
        {
            return ShortGuid.Encode(source);
        }

        public static ShortGuid ToShortGuid(this string source)
        {
            return new ShortGuid(source);
        }

        public static Guid ToShortGuidGuid(this string source)
        {
            return ShortGuid.Decode(source);
        }
    }
}