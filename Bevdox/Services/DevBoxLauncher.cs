using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Bevdox.Auth;
using Bevdox.Models;

namespace Bevdox.Services;

public sealed class DevBoxLauncher(AuthService auth)
{
    private static readonly HttpClient Http = new();

    public async Task LaunchAsync(DevBoxInstance instance, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(instance.DevCenterUri))
            throw new InvalidOperationException($"No DevCenter URI available for '{instance.EffectiveName}'.");

        var token = await auth.GetDevCenterTokenAsync(ct);

        // Extract user ID from the resource ID: .../users/{userId}/devboxes/{name}
        var parts = instance.UniqueId.Split('/');
        var userIndex = Array.IndexOf(parts, "users");
        var userId = userIndex >= 0 && userIndex + 1 < parts.Length ? parts[userIndex + 1] : "me";

        var url = $"{instance.DevCenterUri}/projects/{instance.ProjectName}" +
                  $"/users/{userId}/devboxes/{instance.OriginalName}" +
                  "/remoteConnection?api-version=2025-02-01";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        // Prefer rdpConnectionUrl (ms-avd protocol) over webUrl (browser)
        var connectionUrl = (doc.RootElement.TryGetProperty("rdpConnectionUrl", out var rdp) ? rdp.GetString() : null)
            ?? throw new InvalidOperationException("Remote connection URL not available.");

        Console.WriteLine($"Launching {instance.EffectiveName}...");
        Process.Start(new ProcessStartInfo(connectionUrl) { UseShellExecute = true });
    }
}
