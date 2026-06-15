using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Jobnet.Services.ApiUsage;
using LLama;
using LLama.Abstractions;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace Jobnet.Services.Ai;

/// <summary>
/// In-process local-model client backed by LLamaSharp (llama.cpp bindings). The model file is
/// a .gguf on the user's disk; path comes from the <c>llama_model_path</c> config key. Loading
/// is lazy — the first call blocks on model load (~5-15s on CPU); subsequent calls reuse the
/// in-memory weights.
/// </summary>
public sealed class LLamaClient : IAiClient, IDisposable
{
    public const string Provider = "llama";
    public string ProviderId => Provider;

    private readonly IConfigRepository _config;
    private readonly IApiUsageTracker _usage;
    private readonly object _loadLock = new();

    /// <summary>Serializes inference calls. The native llama.cpp context is NOT thread-safe —
    /// concurrent inference on the same context produces an AccessViolationException that
    /// kills the whole process (no managed catch can recover from native memory corruption).
    /// Yesterday this was fine because all AI calls came from the UI thread one at a time;
    /// today's worker architecture has three workers (summary, resume_match, company_profile)
    /// all routing through IAiClient, which means three concurrent llama calls without this
    /// gate. Singleton client → singleton semaphore → at most one inference in flight.</summary>
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);

    private LLamaWeights? _weights;
    private ModelParams? _loadedParams;
    private string? _loadedPath;

    public LLamaClient(IConfigRepository config, IApiUsageTracker usage)
    {
        _config = config;
        _usage = usage;
    }

    /// <summary>Configured == the user has pointed <c>llama_model_path</c> at an existing .gguf file.
    /// We don't verify it's a valid model here — that surfaces on first call if the file is bad.</summary>
    public bool IsConfigured
    {
        get
        {
            var path = _config.GetOrDefault("llama_model_path", "");
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
    }

    public async Task<AiResponse> CompleteAsync(string userMessage, string? system = null, int? maxTokens = null, CancellationToken ct = default, string? task = null)
    {
        _ = task;
        var path = _config.GetOrDefault("llama_model_path", "");
        if (string.IsNullOrWhiteSpace(path))
            throw new AiUnavailableException("Local llama model not configured. Set llama_model_path in Settings to a .gguf file.");
        if (!File.Exists(path))
            throw new AiUnavailableException($"Local llama model file not found: {path}");

        // Acquire the inference gate before touching native context. Workers stack up here
        // when multiple are running concurrently — that's fine because llama inference is the
        // bottleneck regardless. A request that waits 30s for its turn is still cheaper than
        // a process crash. Cancellation is honored so a Ctrl-C / app shutdown unblocks waiters.
        await _inferenceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        var weights = EnsureLoaded(path);
        using var context = weights.CreateContext(_loadedParams!);
        var executor = new StatelessExecutor(weights, _loadedParams!);

        var maxOut = maxTokens ?? int.Parse(_config.GetOrDefault("llama_max_tokens", "1024"));
        var prompt = BuildPrompt(system, userMessage);

        _usage.RecordCall(Provider);

        var sb = new StringBuilder();
        var tokensOut = 0;
        var inferenceParams = new InferenceParams
        {
            MaxTokens = maxOut,
            AntiPrompts = new[] { "<|eot_id|>", "<|end_of_text|>", "</s>" },
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0f },
        };

        try
        {
            await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct))
            {
                sb.Append(token);
                tokensOut++;
                if (ct.IsCancellationRequested) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _usage.RecordCallOutcome(Provider, 0, $"{ex.GetType().Name}: {ex.Message}");
            throw new AiUnavailableException($"LLama inference failed: {ex.GetType().Name}: {ex.Message}");
        }

        // Token counting is approximate (we counted chunks, not real tokens). Good enough for usage tracking.
        var inTokens = ApproxTokens(prompt);
        _usage.UpdateLastCallTokens(Provider, inTokens, tokensOut);
        _usage.RecordCallOutcome(Provider, 200);

        return new AiResponse
        {
            Text = CleanOutput(sb.ToString()),
            InputTokens = inTokens,
            OutputTokens = tokensOut,
            Model = Path.GetFileName(path),
            ProviderId = Provider,
        };
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    private static int _nativeConfigured;
    private static string? _lastNativeLoadError;

    /// <summary>Last error from native-lib initialisation, including all InnerException levels.
    /// Surfaced into AiUnavailableException messages so the user sees the actual root cause.</summary>
    public static string? LastNativeLoadError => _lastNativeLoadError;

    /// <summary>Tell LLamaSharp where the native binaries live. The Cuda12 backend package drops
    /// them in <c>runtimes/win-x64/native/cuda12/</c>, but the default search path doesn't always
    /// pick up the subfolder reliably. Setting it explicitly before the first NativeApi access
    /// avoids the `type initializer for 'LLama.Native.NativeApi' threw an exception` error.
    /// Idempotent / thread-safe via Interlocked guard.
    ///
    /// IMPORTANT: any reference to a type in <c>LLama.Native</c> (including <c>NativeLibraryConfig</c>)
    /// can trigger the static constructor of <c>NativeApi</c>, which is what fails when native
    /// deps are missing. We wrap that reference itself and log the FULL InnerException chain so
    /// the user can read what's actually missing (msvcp140, cudart64_12, etc.).
    /// </summary>
    private static void EnsureNativeConfigured()
    {
        if (System.Threading.Interlocked.Exchange(ref _nativeConfigured, 1) != 0) return;

        var baseDir = AppContext.BaseDirectory;
        var vulkanDir = Path.Combine(baseDir, "runtimes", "win-x64", "native", "vulkan");
        var cudaDir   = Path.Combine(baseDir, "runtimes", "win-x64", "native", "cuda12");
        var cpuDir    = Path.Combine(baseDir, "runtimes", "win-x64", "native");
        var dirs = new List<string>();
        if (Directory.Exists(vulkanDir)) dirs.Add(vulkanDir);
        if (Directory.Exists(cudaDir))   dirs.Add(cudaDir);
        if (Directory.Exists(cpuDir))    dirs.Add(cpuDir);

        try
        {
            if (dirs.Count > 0)
                NativeLibraryConfig.All.WithSearchDirectories(dirs);
            NativeLibraryConfig.All.WithAutoFallback(true);
        }
        catch (Exception ex)
        {
            _lastNativeLoadError = FormatExceptionChain("NativeLibraryConfig setup", ex, dirs);
            TryWriteLoadErrorLog();
        }
    }

    private static string FormatExceptionChain(string context, Exception ex, IReadOnlyList<string> searchDirs)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[{DateTime.Now:O}] LLamaSharp native init failed during: {context}");
        sb.AppendLine($"  BaseDirectory: {AppContext.BaseDirectory}");
        sb.AppendLine($"  Search dirs: {(searchDirs.Count == 0 ? "(none configured)" : string.Join(" | ", searchDirs))}");
        sb.AppendLine($"  Each dir exists?");
        foreach (var d in searchDirs)
            sb.AppendLine($"    {d}: {(Directory.Exists(d) ? "yes" : "MISSING")}");

        var cur = (Exception?)ex; var depth = 0;
        while (cur is not null)
        {
            sb.AppendLine($"  [{depth}] {cur.GetType().FullName}: {cur.Message}");
            cur = cur.InnerException;
            depth++;
            if (depth > 8) break;
        }
        sb.AppendLine($"  Stack:");
        sb.AppendLine(ex.StackTrace ?? "(no stack)");
        return sb.ToString();
    }

    private static void TryWriteLoadErrorLog()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jobnet");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "llama-native-error.log"),
                (_lastNativeLoadError ?? "") + "\n" + new string('-', 60) + "\n");
        }
        catch { /* logging best-effort */ }
    }

    private LLamaWeights EnsureLoaded(string path)
    {
        // Hot path: weights already loaded for this exact path.
        if (_weights is not null && string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase))
            return _weights;

        lock (_loadLock)
        {
            if (_weights is not null && string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase))
                return _weights;

            EnsureNativeConfigured();

            // Path changed (user pointed at a different model) — release prior weights.
            _weights?.Dispose();
            _weights = null;
            _loadedParams = null;

            var ctxSize = uint.TryParse(_config.GetOrDefault("llama_context_size", "4096"), out var cs) ? cs : 4096u;
            var gpuLayers = int.TryParse(_config.GetOrDefault("llama_gpu_layers", "0"), out var gl) ? gl : 0;
            var threads = int.TryParse(_config.GetOrDefault("llama_threads", "0"), out var th) && th > 0 ? th : Environment.ProcessorCount;

            var p = new ModelParams(path)
            {
                ContextSize = ctxSize,
                GpuLayerCount = gpuLayers,
                Threads = threads,
            };
            _loadedParams = p;

            try
            {
                _weights = LLamaWeights.LoadFromFile(p);
            }
            catch (Exception tex) when (tex is TypeInitializationException || tex.GetType().FullName == "System.TypeInitializationException")
            {
                // Walk the InnerException chain to find the actual root cause — usually a
                // DllNotFoundException naming the specific missing dependency (msvcp140, cudart64_12 etc.).
                _lastNativeLoadError = FormatExceptionChain("LLamaWeights.LoadFromFile",
                    tex,
                    new[]
                    {
                        Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "cuda12"),
                        Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native"),
                    });
                TryWriteLoadErrorLog();

                var root = tex.InnerException ?? tex;
                while (root.InnerException is not null) root = root.InnerException;
                throw new AiUnavailableException(
                    $"LLama native loader failed. Root cause: {root.GetType().Name}: {root.Message}\n" +
                    $"Full diagnostic written to %LOCALAPPDATA%\\Jobnet\\llama-native-error.log. " +
                    "Either install the missing dependency or switch to AI mode = Online in Settings.");
            }
            _loadedPath = path;
            return _weights;
        }
    }

    /// <summary>Apply a Llama-3-style chat template directly. Works for Llama 3.x, Qwen 2.5, and most
    /// recent instruct GGUFs. Older models may need a different template — exposed for future tweaking.</summary>
    private static string BuildPrompt(string? system, string user)
    {
        var sb = new StringBuilder();
        sb.Append("<|begin_of_text|>");
        if (!string.IsNullOrWhiteSpace(system))
        {
            sb.Append("<|start_header_id|>system<|end_header_id|>\n\n").Append(system).Append("<|eot_id|>");
        }
        sb.Append("<|start_header_id|>user<|end_header_id|>\n\n").Append(user).Append("<|eot_id|>");
        sb.Append("<|start_header_id|>assistant<|end_header_id|>\n\n");
        return sb.ToString();
    }

    private static string CleanOutput(string s)
    {
        // Strip any trailing anti-prompt tokens that slipped through.
        foreach (var stop in new[] { "<|eot_id|>", "<|end_of_text|>", "</s>" })
        {
            var idx = s.IndexOf(stop, StringComparison.Ordinal);
            if (idx >= 0) s = s.Substring(0, idx);
        }
        return s.Trim();
    }

    private static int ApproxTokens(string text) => string.IsNullOrEmpty(text) ? 0 : Math.Max(1, text.Length / 4);

    public void Dispose()
    {
        _weights?.Dispose();
        _weights = null;
        _inferenceGate.Dispose();
    }
}
