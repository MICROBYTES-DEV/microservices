using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace FrontEnd.Services;

public sealed class AppwriteClient
{
    private readonly HttpClient httpClient;
    private readonly string endpoint;
    private readonly string projectId;
    private readonly string databaseId;
    private readonly string papersCollectionId;
    private readonly string activitiesCollectionId;

    public AppwriteClient(HttpClient httpClient, IConfiguration configuration)
    {
        this.httpClient = httpClient;
        endpoint = configuration["Appwrite:Endpoint"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("Appwrite:Endpoint is not configured.");
        projectId = configuration["Appwrite:ProjectId"] ?? string.Empty;
        databaseId = configuration["Appwrite:DatabaseId"] ?? string.Empty;
        papersCollectionId = configuration["Appwrite:PapersCollectionId"] ?? "papers";
        activitiesCollectionId = configuration["Appwrite:ActivitiesCollectionId"] ?? "activities";
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(projectId)
        && !projectId.StartsWith("REPLACE_", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(databaseId)
        && !databaseId.StartsWith("REPLACE_", StringComparison.OrdinalIgnoreCase);

    public string PapersCollectionId => papersCollectionId;
    public string ActivitiesCollectionId => activitiesCollectionId;

    public async Task<bool> SignInAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "/account/sessions/email");
        request.Content = JsonContent.Create(new { email, password });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<AppwriteUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "/account");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AppwriteUser>(cancellationToken);
    }

    public async Task<bool> CreateAccountAsync(string email, string password, string name, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "/account");
        request.Content = JsonContent.Create(new { userId = "unique()", email, password, name });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> SignOutAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, "/account/sessions/current");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<AppwriteDocument>> ListDocumentsAsync(
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"/databases/{databaseId}/collections/{collectionId}/documents");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AppwriteDocumentList>(cancellationToken);
        return result?.Documents ?? [];
    }

    public async Task<bool> CreateDocumentAsync(
        string collectionId,
        object data,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, $"/databases/{databaseId}/collections/{collectionId}/documents");
        request.Content = JsonContent.Create(new { documentId = "unique()", data });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{endpoint}{path}");
        request.Headers.Add("X-Appwrite-Project", projectId);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        return request;
    }
}

public sealed record AppwriteDocument(
    [property: JsonPropertyName("$id")] string Id,
    [property: JsonPropertyName("$createdAt")] string CreatedAt,
    [property: JsonPropertyName("data")] Dictionary<string, object?> Data);

public sealed record AppwriteUser(
    [property: JsonPropertyName("$id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("email")] string Email);

internal sealed record AppwriteDocumentList(
    [property: JsonPropertyName("documents")] List<AppwriteDocument> Documents);