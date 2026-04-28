namespace TimeToDo.Api;

public class Status
{
    public string ServiceName { get; set; } = null!;
    public string Version { get; set; } = null!;
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CurrentDateTime { get; set; }
    public string User { get; set; } = null!;
    public string? Client { get; set; }
    public bool Authenticated { get; set; }
    public string HostName { get; set; } = null!;
    public string[] ProxyServers { get; set; } = [];
    public DateTime ServiceStart { get; set; }
    public TimeSpan ServiceRunningSince { get; set; }

    public string ContentRoot { get; set; } = null!;
    public string WebRoot { get; set; } = null!;

}