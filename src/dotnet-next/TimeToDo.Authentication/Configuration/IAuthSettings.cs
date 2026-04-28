namespace TimeToDo.Authentication;

public interface IAuthSettings
{
    int AuthenticationMinimumLevel { get; }
    bool MagicLinkSelfService { get; }
    int TwoFactorGracePeriodDays { get; }
}
