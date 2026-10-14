using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MlCpu.DesignModel;

/// <summary>
/// One chat message. Its text always has LF line ends: a C# raw string takes the line ends of its source file, and the working
/// copy checks out with CRLF (core.autocrlf), so a prompt's bytes depended on the checkout (the CAS runs of 2026-10-11 were LF).
/// </summary>
public sealed record ChatMessage(string Role, string Content)
{
    public string Content { get; init; } = Content.Replace("\r\n", "\n");
}

public sealed record ChatResult(string Content, string? Reasoning, int PromptTokens, int GeneratedTokens, double TokensPerSecond,
    double PromptTokensPerSecond, double Seconds, string Stop, string Backend);

public sealed record BackendOptions(string ModelPath, int Context, int MaxTokens, string Reasoning, double? Temperature, int Seed,
    bool MoeOnCpu, string LlamaDir, string? ServerUrl, int Port, string? ExtraServerArgs, string LogDir, bool AllowFallback,
    string? ExchangeDir = null);

/// <summary>The prompt is larger than the model's context: the step fails for this program, and the run goes on.</summary>
public sealed class PromptTooLargeException(string message) : Exception(message);

/// <summary>A chat model: messages in, one assistant reply out. The history is passed whole on every call.</summary>
public interface IChatBackend : IDisposable
{
    string Name { get; }
    ChatResult Complete(IReadOnlyList<ChatMessage> messages, int? maxTokens = null, string? reasoning = null);

    /// <summary>The number of tokens the model's tokenizer makes of <paramref name="text"/>; null when the backend cannot say.</summary>
    int? CountTokens(string text) => null;
}

/// <summary>
/// llama.cpp first; LM-Kit when llama-server cannot start or a request fails (the failed request is retried on LM-Kit and
/// every later request stays there).
/// </summary>
public sealed class FallbackBackend(BackendOptions o, string preferred) : IChatBackend
{
    IChatBackend? current;
    public readonly List<string> Events = [];

    public string Name => current?.Name ?? "(not started)";

    public int? CountTokens(string text)
    {
        current ??= Start();
        return current.CountTokens(text);
    }

    public ChatResult Complete(IReadOnlyList<ChatMessage> messages, int? maxTokens = null, string? reasoning = null)
    {
        current ??= Start();
        try { return current.Complete(messages, maxTokens, reasoning); }
        catch (Exception e) when (e.Message.Contains("exceed_context_size_error") || e.Message.Contains("exceeds the available context size"))
        {
            // The prompt does not fit the context: another backend with the same model and context cannot help.
            throw new PromptTooLargeException(e.Message);
        }
        catch (Exception e) when (current is LlamaServerBackend && o.AllowFallback)
        {
            Note($"llama.cpp request failed ({e.GetType().Name}: {e.Message}); falling back to LM-Kit");
            current.Dispose();
            current = new LmKitBackend(o);
            return current.Complete(messages, maxTokens, reasoning);
        }
    }

    IChatBackend Start()
    {
        if (preferred == "lmkit") return new LmKitBackend(o);
        if (preferred == "exchange") return new FileExchangeBackend(o.ExchangeDir ?? throw new ArgumentException("--exchange <dir> is required for --backend exchange"));
        if (preferred == "scripted") return new ScriptedBackend(o.ExchangeDir ?? throw new ArgumentException("--exchange <dir> is required for --backend scripted"));
        try { return new LlamaServerBackend(o); }
        catch (Exception e) when (o.AllowFallback)
        {
            Note($"llama.cpp unavailable ({e.Message}); falling back to LM-Kit");
            return new LmKitBackend(o);
        }
    }

    void Note(string m)
    {
        Events.Add($"{DateTime.Now:O} {m}");
        Program.Log(m);
    }

    public void Dispose() => current?.Dispose();
}

/// <summary>
/// llama-server (OpenAI-compatible /v1/chat/completions, streamed). Starts its own server unless --server is given; the
/// model is memory-mapped and MoE experts are placed by --fit (or kept on the CPU with --moe-cpu).
/// </summary>
public sealed class LlamaServerBackend : IChatBackend
{
    readonly BackendOptions o;
    readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    readonly Process? server;
    readonly string baseUrl;
    public string Name { get; }

    public LlamaServerBackend(BackendOptions options)
    {
        o = options;
        if (o.ServerUrl is { Length: > 0 } url)
        {
            baseUrl = url.TrimEnd('/');
            WaitHealthy(TimeSpan.FromMinutes(1), null);
        }
        else
        {
            var exe = Path.Combine(o.LlamaDir, "llama-server.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException($"llama-server not found at {exe}");
            baseUrl = $"http://127.0.0.1:{o.Port}";
            var args = new List<string> { "-m", o.ModelPath, "-c", o.Context.ToString(), "-np", "1", "--host", "127.0.0.1", "--port", o.Port.ToString(),
                "--jinja", "-fa", "on", "--no-webui", "-to", "14400", "--reasoning-format", "auto" };
            if (o.MoeOnCpu) args.AddRange(["--cpu-moe", "-ngl", "999"]);
            if (!string.IsNullOrWhiteSpace(o.ExtraServerArgs)) args.AddRange(SplitArgs(o.ExtraServerArgs));
            var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Directory.CreateDirectory(o.LogDir);
            var log = new StreamWriter(Path.Combine(o.LogDir, "llama-server.log"), append: true) { AutoFlush = true };
            log.WriteLine($"# {DateTime.Now:O} {exe} {string.Join(' ', args)}");
            server = Process.Start(psi) ?? throw new InvalidOperationException("llama-server did not start");
            server.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
            server.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();
            Program.Log($"llama-server pid {server.Id} loading {Path.GetFileName(o.ModelPath)} (ctx {o.Context}); log {Path.Combine(o.LogDir, "llama-server.log")}");
            var sw = Stopwatch.StartNew();
            try { WaitHealthy(TimeSpan.FromMinutes(45), server); }
            catch { Dispose(); throw; }
            Program.Log($"llama-server ready in {sw.Elapsed.TotalSeconds:N0}s");
        }
        Name = $"llama.cpp {Version()}";
    }

    void WaitHealthy(TimeSpan limit, Process? p)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            if (p is { HasExited: true }) throw new InvalidOperationException($"llama-server exited with code {p.ExitCode} while loading");
            try
            {
                using var r = http.GetAsync(baseUrl + "/health").GetAwaiter().GetResult();
                if (r.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            Thread.Sleep(2000);
        }
        throw new TimeoutException($"llama-server at {baseUrl} not healthy after {limit.TotalMinutes:N0} min");
    }

    string Version()
    {
        try
        {
            var props = JsonNode.Parse(http.GetStringAsync(baseUrl + "/props").GetAwaiter().GetResult());
            return props?["build_info"]?.ToString() ?? "server";
        }
        catch { return "server"; }
    }

    /// <summary>
    /// Reasoning tokens a reply may spend before it has written any content (MLCPU_REASONING_BUDGET; 0 or unset = no
    /// limit). Past it the stream is cut, which stops the server's generation, and the request is made once more with
    /// thinking off and a short "answer now" turn: a model that deliberates without converging (24 minutes, 34,000 tokens
    /// and no code in one Fineract attempt) then still answers, and the run records that it happened.
    /// </summary>
    /// <summary>llama-server's /tokenize: exact prompt sizes (packets and traced bytes run about 2 characters per token).</summary>
    public int? CountTokens(string text)
    {
        try
        {
            var body = new JsonObject { ["content"] = text, ["add_special"] = false };
            using var resp = http.PostAsync(baseUrl + "/tokenize", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"))
                .GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return null;
            return JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult())?["tokens"]?.AsArray().Count;
        }
        catch (Exception) { return null; }
    }

    static readonly int ReasoningBudget = int.TryParse(Environment.GetEnvironmentVariable("MLCPU_REASONING_BUDGET"), out var b) ? b : 0;

    const string AnswerNow = "You have reasoned at length without answering. Stop deliberating now: answer with your best complete " +
                             "solution in the required format, using what you have already worked out.";

    public ChatResult Complete(IReadOnlyList<ChatMessage> messages, int? maxTokens = null, string? reasoning = null)
    {
        var r = Stream(messages, maxTokens, reasoning, ReasoningBudget);
        if (r.Stop != "reasoning-budget") return r;
        Program.Log($"  reasoning budget of {ReasoningBudget:N0} tokens spent with no answer; asking again with thinking off");
        var again = Stream([.. messages, new ChatMessage("user", AnswerNow)], maxTokens, "none", 0);
        return again with
        {
            Reasoning = r.Reasoning, PromptTokens = r.PromptTokens + again.PromptTokens, GeneratedTokens = r.GeneratedTokens + again.GeneratedTokens,
            Seconds = r.Seconds + again.Seconds, Stop = again.Stop + " (after the reasoning budget)"
        };
    }

    ChatResult Stream(IReadOnlyList<ChatMessage> messages, int? maxTokens, string? reasoning, int budget)
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["max_tokens"] = maxTokens ?? o.MaxTokens,
            ["stream"] = true,
            ["seed"] = o.Seed,
            ["cache_prompt"] = true,
            ["timings_per_token"] = false,
            ["chat_template_kwargs"] = new JsonObject
            {
                ["reasoning_effort"] = reasoning ?? o.Reasoning,
                ["enable_thinking"] = (reasoning ?? o.Reasoning) != "none"
            },
        };
        if (o.Temperature is { } t) body["temperature"] = t;
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var sw = Stopwatch.StartNew();
        using var resp = http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"llama-server HTTP {(int)resp.StatusCode}: {resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()}");
        using var reader = new StreamReader(resp.Content.ReadAsStream());
        var content = new StringBuilder();
        var thinking = new StringBuilder();
        string stop = "unknown";
        int promptTokens = 0, generated = 0, chunks = 0, thinkingChunks = 0;
        double genRate = 0, promptRate = 0;
        var lastLog = Stopwatch.StartNew();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!line.StartsWith("data:")) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            var j = JsonNode.Parse(data);
            if (j?["error"] is { } err) throw new HttpRequestException($"llama-server error: {err.ToJsonString()}");
            var choice = j?["choices"]?[0];
            if (choice?["delta"]?["content"]?.GetValue<string>() is { } c) { content.Append(c); chunks++; }
            if (choice?["delta"]?["reasoning_content"]?.GetValue<string>() is { } rc) { thinking.Append(rc); chunks++; thinkingChunks++; }
            if (budget > 0 && thinkingChunks > budget && content.Length == 0)
            {
                stop = "reasoning-budget";
                generated = Math.Max(generated, chunks);
                break; // disposing the response closes the connection; llama-server cancels the slot's generation
            }
            if (choice?["finish_reason"]?.GetValue<string>() is { } fr) stop = fr;
            if (j?["timings"] is JsonObject tm)
            {
                promptTokens = tm["prompt_n"]?.GetValue<int>() ?? promptTokens;
                generated = tm["predicted_n"]?.GetValue<int>() ?? generated;
                genRate = tm["predicted_per_second"]?.GetValue<double>() ?? genRate;
                promptRate = tm["prompt_per_second"]?.GetValue<double>() ?? promptRate;
            }
            if (j?["usage"] is JsonObject u)
            {
                promptTokens = u["prompt_tokens"]?.GetValue<int>() ?? promptTokens;
                generated = u["completion_tokens"]?.GetValue<int>() ?? generated;
            }
            if (lastLog.Elapsed.TotalSeconds >= 60)
            {
                Program.Log($"  ... {sw.Elapsed.TotalMinutes:N1} min, ~{chunks} tokens streamed (reasoning {thinking.Length:N0} chars, content {content.Length:N0} chars)");
                lastLog.Restart();
            }
        }
        sw.Stop();
        return new ChatResult(content.ToString(), thinking.Length > 0 ? thinking.ToString() : null, promptTokens, generated, genRate, promptRate,
            sw.Elapsed.TotalSeconds, stop, Name);
    }

    static IEnumerable<string> SplitArgs(string s) =>
        System.Text.RegularExpressions.Regex.Matches(s, "\"([^\"]*)\"|(\\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);

    public void Dispose()
    {
        http.Dispose();
        if (server is { HasExited: false })
        {
            try { server.Kill(entireProcessTree: true); server.WaitForExit(30000); } catch { }
        }
        server?.Dispose();
    }
}

/// <summary>LM-Kit in process (the previous runner); the history is rebuilt on every call.</summary>
public sealed class LmKitBackend : IChatBackend
{
    readonly BackendOptions o;
    readonly LMKit.Model.LM model;
    public string Name { get; }

    public LmKitBackend(BackendOptions options)
    {
        o = options;
        LMKit.Global.Runtime.EnableCuda = true;
        LMKit.Global.Runtime.Initialize();
        var device = new LMKit.Model.LM.DeviceConfiguration { GpuLayerCount = int.MaxValue };
        if (o.MoeOnCpu) device.TensorOverrides = [LMKit.Model.LM.TensorOverride.Cpu(@"\.ffn_.*_exps\.weight")];
        Program.Log($"LM-Kit {LMKit.Global.Runtime.Version} {LMKit.Global.Runtime.Backend}; loading {Path.GetFileName(o.ModelPath)}");
        var sw = Stopwatch.StartNew();
        model = new LMKit.Model.LM(o.ModelPath, deviceConfiguration: device);
        Program.Log($"loaded in {sw.Elapsed.TotalSeconds:N0}s ({model.GpuLayerCount}/{model.LayerCount} layers on GPU)");
        Name = $"LM-Kit {LMKit.Global.Runtime.Version}";
    }

    public ChatResult Complete(IReadOnlyList<ChatMessage> messages, int? maxTokens = null, string? reasoning = null)
    {
        using var chat = new LMKit.TextGeneration.MultiTurnConversation(model, o.Context)
        {
            MaximumCompletionTokens = maxTokens ?? o.MaxTokens,
            ReasoningLevel = (reasoning ?? o.Reasoning) switch
            {
                "low" => LMKit.TextGeneration.Chat.ReasoningLevel.Low,
                "high" => LMKit.TextGeneration.Chat.ReasoningLevel.High,
                "none" => LMKit.TextGeneration.Chat.ReasoningLevel.None,
                _ => LMKit.TextGeneration.Chat.ReasoningLevel.Medium
            },
        };
        if (messages.FirstOrDefault(m => m.Role == "system") is { } sys) chat.SystemPrompt = sys.Content;
        var turns = messages.Where(m => m.Role != "system").ToList();
        foreach (var m in turns.Take(turns.Count - 1))
            chat.ChatHistory.AddMessage(m.Role == "assistant" ? LMKit.TextGeneration.Chat.AuthorRole.Assistant : LMKit.TextGeneration.Chat.AuthorRole.User, m.Content);
        var sw = Stopwatch.StartNew();
        var r = chat.Submit(turns[^1].Content);
        sw.Stop();
        return new ChatResult(r.Content ?? r.Completion ?? "", null, r.PromptTokenCount, r.GeneratedTokenCount, r.TokenGenerationRate,
            r.PromptProcessingRate, sw.Elapsed.TotalSeconds, r.TerminationReason.ToString(), Name);
    }

    public void Dispose() => model.Dispose();
}


/// <summary>
/// An external model behind a file exchange (for example an agent): each call writes request-N.md and waits for
/// response-N.md. A new conversation (system + one user message) is written whole; a continuation carries only the
/// newest user message, since the external model keeps its own history.
/// </summary>
public sealed class FileExchangeBackend : IChatBackend
{
    readonly string dir;
    int n;
    public string Name => "file exchange (external model)";

    public FileExchangeBackend(string exchangeDir)
    {
        dir = Path.GetFullPath(exchangeDir);
        Directory.CreateDirectory(dir);
        n = Directory.EnumerateFiles(dir, "request-*.md").Count();
    }

    public ChatResult Complete(IReadOnlyList<ChatMessage> messages, int? maxTokens = null, string? reasoning = null)
    {
        n++;
        var request = Path.Combine(dir, $"request-{n}.md");
        var response = Path.Combine(dir, $"response-{n}.md");
        var turns = messages.Where(m => m.Role != "system").ToList();
        var sb = new StringBuilder();
        if (turns.Count == 1)
        {
            sb.AppendLine($"# Request {n}: NEW CONVERSATION (earlier requests do not apply)");
            foreach (var m in messages) sb.AppendLine($"\n## {m.Role}\n").AppendLine(m.Content);
        }
        else
        {
            sb.AppendLine($"# Request {n}: CONTINUATION of the current conversation");
            if (turns.Count >= 2 && turns[^2].Role == "assistant" && turns[^2].Content.Contains("```java"))
                sb.AppendLine("\n(Your current files are everything you have returned in this conversation; a file you returned again replaced the earlier one.)");
            sb.AppendLine("\n## user\n").AppendLine(turns[^1].Content);
        }
        File.WriteAllText(request + ".tmp", sb.ToString());
        File.Move(request + ".tmp", request, true);
        Program.Log($"exchange: wrote {request}; waiting for {Path.GetFileName(response)}");
        var sw = Stopwatch.StartNew();
        long last = -1;
        while (true)
        {
            if (File.Exists(response))
            {
                long len = new FileInfo(response).Length;
                if (len > 0 && len == last) break;
                last = len;
            }
            if (sw.Elapsed > TimeSpan.FromHours(3)) throw new TimeoutException($"no {response} after 3 hours");
            Thread.Sleep(3000);
        }
        sw.Stop();
        var content = File.ReadAllText(response);
        return new ChatResult(content, null, 0, 0, 0, 0, sw.Elapsed.TotalSeconds, "stop", Name);
    }

    public void Dispose() { }
}
/// <summary>
/// Prepared replies for testing the harness without a model (--backend scripted --exchange dir): call N returns
/// dir/reply-N.md, and the prompt it answered is kept as dir/request-N.md.
/// </summary>
public sealed class ScriptedBackend(string dir) : IChatBackend
{
    int n;
    public string Name => "scripted replies";

    public ChatResult Complete(IReadOnlyList<ChatMessage> messages, int? maxTokens = null, string? reasoning = null)
    {
        n++;
        File.WriteAllText(Path.Combine(dir, $"request-{n}.md"), string.Join("\n\n", messages.Select(m => $"## {m.Role}\n{m.Content}")));
        var reply = Path.Combine(dir, $"reply-{n}.md");
        if (!File.Exists(reply)) throw new InvalidOperationException($"no prepared reply {reply}");
        return new ChatResult(File.ReadAllText(reply), null, 0, 0, 0, 0, 0, "stop", Name);
    }

    public void Dispose() { }
}
