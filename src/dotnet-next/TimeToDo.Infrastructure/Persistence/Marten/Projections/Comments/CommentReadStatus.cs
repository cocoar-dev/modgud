using System.Security.Cryptography;
using Marten.Schema;

namespace TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;

[DocumentAlias("comment_read_status")]
public record CommentReadStatus
{
    public Guid Id { get; init; }
    public Guid CommentId { get; init; }
    public Guid UserId { get; init; }
    public DateTime ReadAt { get; init; }

    public static Guid DeterministicId(Guid commentId, Guid userId)
    {
        Span<byte> input = stackalloc byte[32];
        commentId.TryWriteBytes(input);
        userId.TryWriteBytes(input[16..]);
        var hash = SHA256.HashData(input);
        return new Guid(hash.AsSpan(0, 16));
    }
}
