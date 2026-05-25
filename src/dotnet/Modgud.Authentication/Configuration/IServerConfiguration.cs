namespace Modgud.Authentication;

public interface IServerConfiguration
{
    string AppUrl { get; }
    string? PublicUrl { get; }
}
