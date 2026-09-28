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
    private string? fallbackCookies;
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
            fallbackCookies = await jsRuntime.InvokeAsync<string?>("localStorage.getItem", "cookieFallback");
            // Clean up legacy session key if present
            await jsRuntime.InvokeVoidAsync("localStorage.removeItem", "preptube.appwrite.session");
        }
        catch (JSException)
        {
            fallbackCookies = null;
        }
    }

    public async Task<bool> SignInAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        try
        {
            using var delRequest = CreateRequest(HttpMethod.Delete, "/account/sessions/current");
            await httpClient.SendAsync(delRequest, cancellationToken);
        }
        catch
        {
            // Ignore if no active session
        }

        await ClearSessionAsync();

        using var request = CreateRequest(HttpMethod.Post, "/account/sessions/email");
        request.Content = JsonContent.Create(new { email, password });
        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            LastError = null;
            await CaptureFallbackCookiesAsync(response);
            return true;
        }
        else
        {
            LastError = await ReadErrorAsync(response, cancellationToken);
            if (LastError.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                var currentUser = await GetCurrentUserAsync(cancellationToken);
                if (currentUser is not null)
                {
                    LastError = null;
                    return true;
                }
            }
            return false;
        }
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

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await CaptureFallbackCookiesAsync(response);
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
            return false;
        }
        await CaptureFallbackCookiesAsync(response);
        LastError = null;
        return true;
    }

    public async Task<bool> SignOutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Delete, "/account/sessions/current");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            await ClearSessionAsync();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            await ClearSessionAsync();
            return true;
        }
    }

    private async Task ClearSessionAsync()
    {
        fallbackCookies = null;
        try
        {
            await jsRuntime.InvokeVoidAsync("localStorage.removeItem", "cookieFallback");
            await jsRuntime.InvokeVoidAsync("localStorage.removeItem", "preptube.appwrite.session");
        }
        catch (JSException)
        {
        }
    }

    private async Task CaptureFallbackCookiesAsync(HttpResponseMessage response)
    {
        IEnumerable<string>? values = null;
        if (response.Headers.TryGetValues("X-Fallback-Cookies", out values) ||
            response.Headers.TryGetValues("x-fallback-cookies", out values) ||
            (response.Content?.Headers.TryGetValues("X-Fallback-Cookies", out values) ?? false) ||
            (response.Content?.Headers.TryGetValues("x-fallback-cookies", out values) ?? false))
        {
            var cookies = values?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(cookies))
            {
                fallbackCookies = cookies;
                try
                {
                    await jsRuntime.InvokeVoidAsync("localStorage.setItem", "cookieFallback", cookies);
                    Console.WriteLine("[Appwrite] Successfully saved X-Fallback-Cookies to localStorage.");
                }
                catch (JSException)
                {
                }
            }
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
        await CaptureFallbackCookiesAsync(response);
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
        if (!response.IsSuccessStatusCode)
        {
            LastError = await ReadErrorAsync(response, cancellationToken);
            return false;
        }
        await CaptureFallbackCookiesAsync(response);
        LastError = null;
        return true;
    }

    public async Task<AppwriteFile?> UploadPdfAsync(IBrowserFile file, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await using var stream = file.OpenReadStream(50 * 1024 * 1024, timeout.Token);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("unique()"), "fileId");
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", file.Name);

        using var request = CreateRequest(HttpMethod.Post, $"/storage/buckets/{storageBucketId}/files");
        request.Content = content;
        using var response = await httpClient.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            LastError = await ReadErrorAsync(response, timeout.Token);
            return null;
        }
        await CaptureFallbackCookiesAsync(response);
        LastError = null;
        return await response.Content.ReadFromJsonAsync<AppwriteFile>(cancellationToken);
    }

    public string GetFileViewUrl(string fileId)
        => $"{endpoint}/storage/buckets/{storageBucketId}/files/{fileId}/view?project={projectId}";

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{endpoint}{path}");
        request.Headers.Add("X-Appwrite-Project", projectId);
        if (!string.IsNullOrWhiteSpace(fallbackCookies))
        {
            request.Headers.Add("X-Fallback-Cookies", fallbackCookies);
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