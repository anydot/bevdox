using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bevdox.Auth;
using Bevdox.Models;

namespace Bevdox.Services;

public sealed class DevBoxDiscovery(AuthService auth)
{
    private const string ArgEndpoint = "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01";

    private static readonly HttpClient Http = new();

    public async Task<List<DevBoxInstance>> DiscoverAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Authenticating...");
        var armToken = await auth.GetArmTokenAsync(ct);

        // Step 1: Query ARG for all DevCenter projects to get their endpoints
        progress?.Report("Querying projects...");
        var projects = await QueryArgAsync(armToken, """
            resources
            | where type =~ 'microsoft.devcenter/projects'
            | project projectName=name, devCenterUri=properties.devCenterUri, location
            """, ct);

        var projectInfos = new List<(string Name, string DevCenterUri, string Location)>();
        foreach (var row in projects)
        {
            var name = row.GetProperty("projectName").GetString();
            var uri = row.GetProperty("devCenterUri").GetString();
            var location = row.GetProperty("location").GetString() ?? "";
            if (name is not null && uri is not null)
                projectInfos.Add((name, uri.TrimEnd('/'), location));
        }

        // Step 2: Query each project's DevCenter data plane for dev boxes (in parallel, max 4)
        progress?.Report($"Querying 0/{projectInfos.Count} projects...");
        var devCenterToken = await auth.GetDevCenterTokenAsync(ct);

        int completed = 0;
        var allInstances = new List<DevBoxInstance>();
        var lockObj = new object();

        await Parallel.ForEachAsync(projectInfos,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (p, token) =>
            {
                var result = await ListDevBoxesForProjectAsync(devCenterToken, p.Name, p.DevCenterUri, p.Location, token);
                lock (lockObj)
                {
                    allInstances.AddRange(result);
                    completed++;
                    progress?.Report($"Querying {completed}/{projectInfos.Count} projects...");
                }
            });

        progress?.Report($"Done — found {allInstances.Count} dev box(es).");
        return allInstances;
    }

    private static async Task<List<DevBoxInstance>> ListDevBoxesForProjectAsync(
        string token, string projectName, string devCenterUri, string location, CancellationToken ct)
    {
        try
        {
            var url = $"{devCenterUri}/projects/{projectName}/users/me/devboxes?api-version=2025-02-01";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await Http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

        var instances = new List<DevBoxInstance>();
        if (doc.RootElement.TryGetProperty("value", out var value))
        {
            foreach (var box in value.EnumerateArray())
            {
                var name = box.GetProperty("name").GetString() ?? "unknown";
                var uri = box.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : "";
                var state = (box.TryGetProperty("powerState", out var ps) ? ps.GetString() : null)
                         ?? (box.TryGetProperty("provisioningState", out var prov) ? prov.GetString() : null)
                         ?? "Unknown";
                var poolName = box.TryGetProperty("poolName", out var pool) ? pool.GetString() : null;
                var boxLocation = box.TryGetProperty("location", out var loc) ? loc.GetString() ?? location : location;

                // Build a unique ID from the URI or construct one
                var uniqueId = !string.IsNullOrEmpty(uri) ? uri
                    : $"{devCenterUri}/projects/{projectName}/users/me/devboxes/{name}";

                instances.Add(new DevBoxInstance
                {
                    UniqueId = uniqueId,
                    OriginalName = name,
                    ProjectName = projectName,
                    DevCenterUri = devCenterUri,
                    State = state,
                    Location = boxLocation,
                    PoolName = poolName
                });
            }
        }

        return instances;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return [];
        }
    }

    private static async Task<List<JsonElement>> QueryArgAsync(string token, string query, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { query });
        using var request = new HttpRequestMessage(HttpMethod.Post, ArgEndpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await Http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(json);

        var results = new List<JsonElement>();
        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                    results.Add(item.Clone());
            }
            else if (data.TryGetProperty("rows", out var rows))
            {
                var columns = data.GetProperty("columns").EnumerateArray()
                    .Select(c => c.GetProperty("name").GetString()!)
                    .ToList();

                foreach (var row in rows.EnumerateArray())
                {
                    var dict = new Dictionary<string, JsonElement>();
                    for (int i = 0; i < columns.Count; i++)
                        dict[columns[i]] = row[i];

                    var rowJson = JsonSerializer.Serialize(dict);
                    using var rowDoc = JsonDocument.Parse(rowJson);
                    results.Add(rowDoc.RootElement.Clone());
                }
            }
        }

        return results;
    }
}
