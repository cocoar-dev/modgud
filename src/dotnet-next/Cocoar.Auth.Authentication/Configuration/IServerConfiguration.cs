namespace Cocoar.Auth.Authentication;

public interface IServerConfiguration
{
    string AppUrl { get; }
    string? PublicUrl { get; }
}
