using B1DataImporter.Api.Services;
using B1DataImporter.Api.Services.Ai;

namespace B1DataImporter.Api.Api;

/// <summary>Body for updating AI settings. Blank keys mean "keep the current one".</summary>
public record SettingsInput(
    string? Provider, string? Model, string? OpenRouterApiKey, string? AnthropicApiKey);

public static class SettingsEndpoints
{
    public static void MapSettings(this WebApplication app)
    {
        // Never returns the keys themselves — only whether each is set and the effective status.
        app.MapGet("/api/settings", async (SettingsStore store, AiMappingService ai) =>
        {
            var (configured, provider) = await ai.GetStatusAsync();
            return Results.Ok(new
            {
                provider = await store.GetAsync(SettingsStore.AiProvider) ?? "auto",
                model = await store.GetAsync(SettingsStore.OpenRouterModel) ?? "",
                hasOpenRouterKey = await store.HasAsync(SettingsStore.OpenRouterApiKey),
                hasAnthropicKey = await store.HasAsync(SettingsStore.AnthropicApiKey),
                aiConfigured = configured,
                activeProvider = provider,
            });
        });

        app.MapPut("/api/settings", async (SettingsInput input, SettingsStore store) =>
        {
            if (input.Provider != null)
                await store.SetAsync(SettingsStore.AiProvider, input.Provider);
            if (input.Model != null)
                // Blank clears it so the default model applies again.
                await store.SetAsync(SettingsStore.OpenRouterModel,
                    string.IsNullOrWhiteSpace(input.Model) ? null : input.Model.Trim());
            // Secrets: blank = keep existing.
            await store.SetSecretAsync(SettingsStore.OpenRouterApiKey, input.OpenRouterApiKey);
            await store.SetSecretAsync(SettingsStore.AnthropicApiKey, input.AnthropicApiKey);
            return Results.Ok(new { ok = true });
        });

        // Validate the saved AI key/provider with a lightweight live check.
        app.MapPost("/api/settings/test-ai", async (AiMappingService ai) =>
        {
            var (ok, message) = await ai.TestAsync();
            return ok ? Results.Ok(new { ok, message }) : Results.BadRequest(new { ok, message });
        });
    }
}
