using System.Net.Http.Json;
using Cocoar.Auth.Web.Models;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Cocoar.Auth.Web.Services;

public interface IUserService
{
    Task<List<UserDto>> GetUsersAsync();
    Task<UserDto?> GetUserAsync(Guid id);
    Task<UserDto?> CreateUserAsync(CreateUserRequest request);
    Task<UserDto?> UpdateUserAsync(Guid id, UpdateUserRequest request);
    Task<bool> DeleteUserAsync(Guid id);
    Task<bool> LockUserAsync(Guid id);
    Task<bool> UnlockUserAsync(Guid id);
    Task<bool> ResetUserPasswordAsync(Guid id, string newPassword);
    Task<bool> AddClaimAsync(Guid userId, AddClaimRequest request);
    Task<bool> RemoveClaimAsync(Guid userId, string claimType, string claimValue);

    // GDPR Admin Operations
    Task<bool> SoftDeleteUserAsync(Guid userId, string? reason);
    Task<bool> RestoreUserAsync(Guid userId, string? reason);
    Task<bool> PermanentlyEraseUserAsync(Guid userId, string reason);
    Task<DeletionStatusResponse?> GetUserDeletionStatusAsync(Guid userId);

    // Session Management
    Task<SessionListResponse?> GetUserSessionsAsync(Guid userId);
    Task<bool> RevokeAllUserSessionsAsync(Guid userId);
}

public class UserService : IUserService
{
    private readonly HttpClient _http;

    public UserService(HttpClient http)
    {
        _http = http;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        return request;
    }

    public async Task<List<UserDto>> GetUsersAsync()
    {
        try
        {
            var request = CreateRequest(HttpMethod.Get, "/api/admin/users");
            var response = await _http.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<List<UserDto>>() ?? [];
            }
            return [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<UserDto?> GetUserAsync(Guid id)
    {
        try
        {
            var request = CreateRequest(HttpMethod.Get, $"/api/admin/users/{id}");
            var response = await _http.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<UserDto>();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<UserDto?> CreateUserAsync(CreateUserRequest request)
    {
        try
        {
            var httpRequest = CreateRequest(HttpMethod.Post, "/api/admin/users");
            httpRequest.Content = JsonContent.Create(request);
            
            var response = await _http.SendAsync(httpRequest);
            
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<UserDto>();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<UserDto?> UpdateUserAsync(Guid id, UpdateUserRequest request)
    {
        try
        {
            var httpRequest = CreateRequest(HttpMethod.Put, $"/api/admin/users/{id}");
            httpRequest.Content = JsonContent.Create(request);
            
            var response = await _http.SendAsync(httpRequest);
            
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<UserDto>();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> DeleteUserAsync(Guid id)
    {
        var request = CreateRequest(HttpMethod.Delete, $"/api/admin/users/{id}");
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> LockUserAsync(Guid id)
    {
        var request = CreateRequest(HttpMethod.Post, $"/api/admin/users/{id}/lock");
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UnlockUserAsync(Guid id)
    {
        var request = CreateRequest(HttpMethod.Post, $"/api/admin/users/{id}/unlock");
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ResetUserPasswordAsync(Guid id, string newPassword)
    {
        var httpRequest = CreateRequest(HttpMethod.Post, $"/api/admin/users/{id}/reset-password");
        httpRequest.Content = JsonContent.Create(new { NewPassword = newPassword });
        
        var response = await _http.SendAsync(httpRequest);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> AddClaimAsync(Guid userId, AddClaimRequest request)
    {
        var httpRequest = CreateRequest(HttpMethod.Post, $"/api/admin/users/{userId}/claims");
        httpRequest.Content = JsonContent.Create(request);
        
        var response = await _http.SendAsync(httpRequest);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> RemoveClaimAsync(Guid userId, string claimType, string claimValue)
    {
        var url = $"/api/admin/users/{userId}/claims?type={Uri.EscapeDataString(claimType)}&value={Uri.EscapeDataString(claimValue)}";
        var request = CreateRequest(HttpMethod.Delete, url);
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // GDPR ADMIN OPERATIONS
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<bool> SoftDeleteUserAsync(Guid userId, string? reason)
    {
        var request = CreateRequest(HttpMethod.Post, $"/api/admin/users/{userId}/soft-delete");
        request.Content = JsonContent.Create(new SoftDeleteUserRequest(reason));
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> RestoreUserAsync(Guid userId, string? reason)
    {
        var request = CreateRequest(HttpMethod.Post, $"/api/admin/users/{userId}/restore");
        request.Content = JsonContent.Create(new RestoreUserRequest(reason));
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> PermanentlyEraseUserAsync(Guid userId, string reason)
    {
        var request = CreateRequest(HttpMethod.Delete, $"/api/admin/users/{userId}/permanent");
        request.Content = JsonContent.Create(new PermanentEraseUserRequest(reason));
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<DeletionStatusResponse?> GetUserDeletionStatusAsync(Guid userId)
    {
        try
        {
            var request = CreateRequest(HttpMethod.Get, $"/api/admin/users/{userId}/deletion-status");
            var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<DeletionStatusResponse>();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SESSION MANAGEMENT
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<SessionListResponse?> GetUserSessionsAsync(Guid userId)
    {
        try
        {
            var request = CreateRequest(HttpMethod.Get, $"/api/admin/users/{userId}/sessions");
            var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var sessions = await response.Content.ReadFromJsonAsync<List<SessionDto>>();
                return new SessionListResponse(sessions ?? []);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> RevokeAllUserSessionsAsync(Guid userId)
    {
        var request = CreateRequest(HttpMethod.Delete, $"/api/admin/users/{userId}/sessions");
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }
}
