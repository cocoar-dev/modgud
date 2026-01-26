using System.Net.Http.Json;
using Cocoar.Auth.Web.Models;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Cocoar.Auth.Web.Services;

public interface IRoleService
{
    Task<List<RoleDto>> GetRolesAsync();
    Task<RoleDto?> GetRoleAsync(Guid id);
    Task<RoleDto?> CreateRoleAsync(CreateRoleRequest request);
    Task<RoleDto?> UpdateRoleAsync(Guid id, UpdateRoleRequest request);
    Task<bool> DeleteRoleAsync(Guid id);
    Task<bool> AddClaimAsync(Guid roleId, AddClaimRequest request);
    Task<bool> RemoveClaimAsync(Guid roleId, string claimType, string claimValue);
}

public class RoleService : IRoleService
{
    private readonly HttpClient _http;

    public RoleService(HttpClient http)
    {
        _http = http;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        return request;
    }

    public async Task<List<RoleDto>> GetRolesAsync()
    {
        try
        {
            var request = CreateRequest(HttpMethod.Get, "/api/admin/roles");
            var response = await _http.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<List<RoleDto>>() ?? [];
            }
            return [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<RoleDto?> GetRoleAsync(Guid id)
    {
        try
        {
            var request = CreateRequest(HttpMethod.Get, $"/api/admin/roles/{id}");
            var response = await _http.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<RoleDto>();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<RoleDto?> CreateRoleAsync(CreateRoleRequest request)
    {
        try
        {
            var httpRequest = CreateRequest(HttpMethod.Post, "/api/admin/roles");
            httpRequest.Content = JsonContent.Create(request);
            
            var response = await _http.SendAsync(httpRequest);
            
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<RoleDto>();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<RoleDto?> UpdateRoleAsync(Guid id, UpdateRoleRequest request)
    {
        try
        {
            var httpRequest = CreateRequest(HttpMethod.Put, $"/api/admin/roles/{id}");
            httpRequest.Content = JsonContent.Create(request);
            
            var response = await _http.SendAsync(httpRequest);
            
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<RoleDto>();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> DeleteRoleAsync(Guid id)
    {
        var request = CreateRequest(HttpMethod.Delete, $"/api/admin/roles/{id}");
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> AddClaimAsync(Guid roleId, AddClaimRequest request)
    {
        var httpRequest = CreateRequest(HttpMethod.Post, $"/api/admin/roles/{roleId}/claims");
        httpRequest.Content = JsonContent.Create(request);
        
        var response = await _http.SendAsync(httpRequest);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> RemoveClaimAsync(Guid roleId, string claimType, string claimValue)
    {
        var url = $"/api/admin/roles/{roleId}/claims?type={Uri.EscapeDataString(claimType)}&value={Uri.EscapeDataString(claimValue)}";
        var request = CreateRequest(HttpMethod.Delete, url);
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }
}
