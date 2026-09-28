using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace FrontEnd.Services;

public sealed class AppwriteClient
{
    private readonly HttpClient httpClient;
    private readonly string endpoint;
    private readonly string projectId;
    private readonly string databaseId;
    private readonly string papersCollectionId;
    private readonly string activitiesCollectionId;
    private readonly string storageBucketId;
    private readonly IJSRuntime jsRuntime;
    private string? session;
    public string? LastError { get; private set; }

    public AppwriteClient(HttpClient httpClient, IConfiguration configuration, IJSRuntime jsRuntime)
    {
        this.httpClient = httpClient;
        this.jsRuntime = jsRuntime;
        endpoint = configuration["Appwrite:Endpoint"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("Appwrite:Endpoint is not configured.");
        projectId = configuration["Appwrite:ProjectId"] ?? string.Empty;
        databaseId = configuration["Appwrite:DatabaseId"] ?? string.Empty;
        papersCollectionId = configuration["Appwrite:PapersCollectionId"] ?? "papers";
        activitiesCollectionId = configuration["Appwrite:ActivitiesCollectionId"] ?? "activities";
        storageBucketId = configuration["Appwrite:StorageBucketId"] ?? "pyq-pdfs";
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(projectId)
        && !projectId.StartsWith("REPLACE_", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(databaseId)
        && !databaseId.StartsWith("REPLACE_", StringComparison.OrdinalIgnoreCase);

    public string PapersCollectionId => papersCollectionId;
    public string ActivitiesCollectionId => activitiesCollectionId;
    public string StorageBucketId => storageBucketId;

    public async Task InitializeAsync()
    {
        try
        {
            session = await jsRuntime.InvokeAsync<string?>("localStorage.getItem", "preptube.appwrite.session");
        }
        catch (JSException)
        {
            session = null;
        }
    }

    public async Task<bool> SignInAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "/account/sessions/email");
        request.Content = JsonContent.Create(new { email, password });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            LastError = null;
            var sessionResponse = await response.Content.ReadFromJsonAsync<AppwriteSession>(cancellationToken);
            session = sessionResponse?.Id ?? (response.Headers.TryGetValues("X-Appwrite-Session", out var values) ? values.FirstOrDefault() : null);
            if (!string.IsNullOrWhiteSpace(session))
            {
                await jsRuntime.InvokeVoidAsync("localStorage.setItem", "preptube.appwrite.session", session);
            }
        }
        else
        {
            LastError = await ReadErrorAsync(response, cancellationToken);
        }
        return response.IsSuccessStatusCode;
    }

    public async Task<AppwriteUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "/account");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            await ClearSessionAsync();
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
        if (!response.IsSuccessStatusCode)
        {
            LastError = await ReadErrorAsync(response, cancellationToken);
        }
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> SignOutAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, "/account/sessions/current");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        session = null;
        await jsRuntime.InvokeVoidAsync("localStorage.removeItem", "preptube.appwrite.session");
        return response.IsSuccessStatusCode;
    }

    private async Task ClearSessionAsync()
    {
        session = null;
        try
        {
            await jsRuntime.InvokeVoidAsync("localStorage.removeItem", "preptube.appwrite.session");
        }
        catch (JSException)
        {
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<AppwriteError>(cancellationToken);
            return error?.Message ?? $"Appwrite request failed ({(int)response.StatusCode}).";
        }
        catch (JsonException)
        {
            return $"Appwrite request failed ({(int)response.StatusCode}).";
        }
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

    public async Task<AppwriteFile?> UploadPdfAsync(IBrowserFile file, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        await using var stream = file.OpenReadStream(50 * 1024 * 1024, timeout.Token);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("unique()"), "fileId");
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", file.Name);

        using var request = CreateRequest(HttpMethod.Post, $"/storage/buckets/{storageBucketId}/files");
        request.Content = content;
        using var response = await httpClient.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AppwriteFile>(cancellationToken);
    }

    public string GetFileViewUrl(string fileId)
        => $"{endpoint}/storage/buckets/{storageBucketId}/files/{fileId}/view?project={projectId}";

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{endpoint}{path}");
        request.Headers.Add("X-Appwrite-Project", projectId);
        if (!string.IsNullOrWhiteSpace(session))
        {
            request.Headers.Add("X-Appwrite-Session", session);
        }
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

public sealed record AppwriteFile(
    [property: JsonPropertyName("$id")] string Id,
    [property: JsonPropertyName("name")] string Name);

public sealed record AppwriteSession(
    [property: JsonPropertyName("$id")] string? Id,
    [property: JsonPropertyName("secret")] string? Secret);

public sealed record AppwriteError(
    [property: JsonPropertyName("message")] string Message);

internal sealed record AppwriteDocumentList(
    [property: JsonPropertyName("documents")] List<AppwriteDocument> Documents);