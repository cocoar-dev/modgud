using Cocoar.Auth.Application;
using Cocoar.Auth.Infrastructure;
using Cocoar.Primitives;
using Cocoar.Primitives.OptionalAware;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddInfrastructure(connectionString);
builder.Services.AddIdentityWithMarten();
builder.Services.AddApplication();

// Configure Wolverine
builder.Host.UseWolverine(opts =>
{
    // Discover handlers in the Application assembly
    opts.Discovery.IncludeAssembly(typeof(Cocoar.Auth.Application.DependencyInjection).Assembly);

    // Use local, in-memory queue (no external message transport needed)
    opts.Durability.Mode = DurabilityMode.Solo;
});

// Configure authentication
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
    options.LoginPath = "/api/auth/login";
    options.LogoutPath = "/api/auth/logout";
    options.AccessDeniedPath = "/api/auth/access-denied";

    // Return 401/403 for API instead of redirects
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new OptionalJsonConverterFactory());
        options.JsonSerializerOptions.Converters.Add(new ShortGuidJsonConverter());
        options.JsonSerializerOptions.TypeInfoResolver = new OptionalAwareTypeInfoResolver();
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add CORS for Blazor UI
builder.Services.AddCors(options =>
{
    options.AddPolicy("BlazorUI", policy =>
    {
        policy.WithOrigins(
                "https://localhost:7054",
                "http://localhost:5128")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseCors("BlazorUI");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

// Make the implicit Program class public for integration tests
public partial class Program { }
