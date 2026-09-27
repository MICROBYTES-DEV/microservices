using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace FrontEnd.Services;

public sealed class AppwriteClient
{
    private readonly HttpClient httpClient;
    private readonly string endpoint;
    private readonly string projectId;
    private readonly string databaseId;

    public AppwriteClient(HttpClient httpClient, IConfiguration configuration)
    {
        this.httpClient = httpClient;
        endpoint = configuration["Appwrite:Endpoint"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("Appwrite:Endpoint is not configured.");
        projectId = configuration["Appwrite:ProjectId"]
            ?? throw new InvalidOperationException("Appwrite:ProjectId is not configured.");
        databaseId = configuration["Appwrite:DatabaseId"]
            ?? throw new InvalidOperationException("Appwrite:DatabaseId is not configured.");
    }

    public async Task<bool> SignInAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "/account/sessions/email");
        request.Content = JsonContent.Create(new { email, password });
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

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{endpoint}{path}");
        request.Headers.Add("X-Appwrite-Project", projectId);
        return request;
    }
}

public sealed record AppwriteDocument(
    [property: JsonPropertyName("$id")] string Id,
    [property: JsonPropertyName("$createdAt")] string CreatedAt,
    [property: JsonPropertyName("data")] Dictionary<string, object?> Data);

internal sealed record AppwriteDocumentList(
    [property: JsonPropertyName("documents")] List<AppwriteDocument> Documents);