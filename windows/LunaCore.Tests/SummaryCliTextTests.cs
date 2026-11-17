using System.Linq;
using System.Text.Json.Nodes;
using LunaCore;
using Xunit;

namespace LunaCore.Tests;

/// <summary>Textlogik der Anbieter-CLIs (Fehlerkern, TOML für codex, NDJSON für agy) – Ausgaben von agy 1.5.10 und codex-cli 0.169.0.</summary>
public class SummaryCliTextTests
{
    [Fact(DisplayName = "Fehlerkern: letzte error-Zeile statt Vorspann oder Modellliste")]
    public void Essence()
    {
        var codex = string.Join("\n",
            "Reading additional input from stdin...",
            "OpenAI Codex v0.169.0",
            "--------",
            "workdir: C:\\Temp\\LunaTranscribe-codex-1",
            "model: gpt-6.1-sol",
            "2026-11-17T15:45:05.576104Z ERROR codex_models_manager::manager: failed to refresh available models: request timed out",
            "ERROR: The 'gibts-nicht-9' model is not supported when using Codex with a ChatGPT account.");
        Assert.Equal("The 'gibts-nicht-9' model is not supported when using Codex with a ChatGPT account.", SummaryCliText.Essence(codex));

        var agy = "error: invalid model selection (--model \"x\" --effort \"\"): model x is not recognized\r\nAvailable models:\r\n  Gemini 3.8 Flash (High)\r\n  Gemini 3.8 Flash (Low)";
        Assert.Equal("invalid model selection (--model \"x\" --effort \"\"): model x is not recognized", SummaryCliText.Essence(agy));

        Assert.Equal("Not logged in", SummaryCliText.Essence("Not logged in\n"));
        Assert.Equal("c d", SummaryCliText.Essence("a\nb\nc\nd\nd"));
        Assert.Equal("", SummaryCliText.Essence("  \n"));
    }

    [Fact(DisplayName = "TOML-String: einzeilig, maskiert, ohne % für cmd.exe, zurücklesbar")]
    public void TomlString()
    {
        var text = "Zeile 1\n\"Zitat\" C:\\Pfad\t50 % mehr\u0001 – äöü 🙂";
        var toml = SummaryCliText.TomlString(text);
        Assert.DoesNotContain("\n", toml);
        Assert.DoesNotContain("%", toml);
        Assert.StartsWith("\"Zeile 1\\n\\\"Zitat\\\" C:\\\\Pfad\\t50 \\u0025 mehr\\u0001 – äöü 🙂", toml);
        // Ein einfacher TOML-String mit diesen Escapes ist auch ein gültiger JSON-String.
        Assert.Equal(text, JsonNode.Parse(toml)!.GetValue<string>());
    }

    [Fact(DisplayName = "agy: Prompt mit Anweisung und Transkript in Tags, NDJSON-Eingabe einzeilig")]
    public void AntigravityInput()
    {
        var prompt = SummaryCliText.AntigravityPrompt("Fasse zusammen.", "[00:00] Anna: Hallo\n[00:05] Markus: Hi");
        Assert.StartsWith("Fasse zusammen.\n\nBenutze keine Werkzeuge", prompt);
        Assert.EndsWith("<transcript>\n[00:00] Anna: Hallo\n[00:05] Markus: Hi\n</transcript>", prompt);

        var line = SummaryCliText.AntigravityStreamInput(prompt);
        Assert.EndsWith("\n", line);
        Assert.Single(line.TrimEnd('\n').Split('\n'));
        var node = JsonNode.Parse(line)!;
        Assert.Equal("user", node["event"]!.GetValue<string>());
        Assert.Equal(prompt, node["message"]!["content"]!.GetValue<string>());
    }

    [Fact(DisplayName = "agy: Antwort aus dem result-Ereignis, Fehler, Warnungen und fehlendes Ergebnis")]
    public void AntigravityResult()
    {
        var success = string.Join("\n",
            """{"event":"init","conversation_id":"c1","init":{"cwd":"C:\\Temp","tools":["view_file"],"permission_mode":"request-review"}}""",
            "warning: something harmless",
            """{"event":"step_update","step_update":{"conversation_id":"c1","step_index":1,"state":"ACTIVE","step_type":"agent_response","text_delta":"## Kurz"}}""",
            """{"event":"result","result":{"conversation_id":"c1","status":"SUCCESS","response":"## Kurzfassung\nAlles gut.\n","duration_seconds":3.7,"num_turns":1}}""");
        var ok = SummaryCliText.ParseAntigravityResult(success)!;
        Assert.True(ok.Success);
        Assert.Equal("## Kurzfassung\nAlles gut.", ok.Response);
        Assert.Null(ok.Error);

        var failed = SummaryCliText.ParseAntigravityResult(
            """{"event":"result","result":{"conversation_id":"","status":"ERROR","response":"","error":"stream input message is missing the \"event\" field","finished_reason":"error"}}""")!;
        Assert.False(failed.Success);
        Assert.Equal("stream input message is missing the \"event\" field", failed.Error);

        Assert.Equal("ERROR", SummaryCliText.ParseAntigravityResult("""{"event":"result","result":{"status":"ERROR"}}""")!.Error);
        Assert.Null(SummaryCliText.ParseAntigravityResult("Authentication required. Please visit the URL to log in:\n  https://accounts.google.com/…"));
        Assert.Null(SummaryCliText.ParseAntigravityResult("""{"event":"result","result":"kaputt"}"""));
        Assert.Null(SummaryCliText.ParseAntigravityResult("{kein json"));
    }
}
