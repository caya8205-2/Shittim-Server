using System.Text.Json;
using Schale.MX.GameLogic.DBModel;

namespace Shittim_Server.Services;

// Diagnostic helper: writes the opponent payload the server actually sends, so a rendering problem can be
// told apart from a payload problem. Temporarily on by default while the arena result screen is
// being diagnosed; set SHITTIM_DUMP_ARENA=0 to silence it.
public static class ArenaDump
{
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHITTIM_DUMP_ARENA") != "0";

    public static void Write<T>(string tag, T? payload)
    {
        if (!Enabled || payload == null)
            return;

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, $"arena-{tag}.json");
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, options));
        }
        catch
        {
            // Diagnostics must never break the protocol response.
        }
    }
}
