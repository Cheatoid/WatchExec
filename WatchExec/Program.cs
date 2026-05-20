using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WatchExec;

internal static class Program
{
    private static readonly CancellationTokenSource Shutdown = new();
    private static readonly ConcurrentDictionary<string, DebounceState> DebounceMap = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<WatcherRuntime> ActiveWatchers = new();

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Shutdown.Cancel();
            };

            string configPath = args.Length > 0 ? args[0] : "watcher.config.json";

            if (!File.Exists(configPath))
            {
                await CreateExampleConfig(configPath);
                Console.WriteLine($"Created example config: {configPath}");
                Console.WriteLine("Edit the file and run again.");
                return 1;
            }

            Config config = await LoadConfig(configPath);

            if (config.Watchers.Count == 0)
            {
                Console.WriteLine("No watchers configured.");
                return 1;
            }

            Console.WriteLine("========================================");
            Console.WriteLine("WatchExec - Cross-platform File Watcher");
            Console.WriteLine("========================================");
            Console.WriteLine($"Loaded config: {Path.GetFullPath(configPath)}");
            Console.WriteLine();

            foreach (WatcherConfig watcherConfig in config.Watchers)
            {
                ValidateWatcher(watcherConfig);
                WatcherRuntime runtime = CreateWatcher(watcherConfig);
                ActiveWatchers.Add(runtime);
                runtime.Watcher.EnableRaisingEvents = true;

                Console.WriteLine($"[ACTIVE] {watcherConfig.Name}");
                Console.WriteLine($"  Path:      {Path.GetFullPath(watcherConfig.Path)}");
                Console.WriteLine($"  Recursive: {watcherConfig.IncludeSubdirectories}");
                Console.WriteLine($"  Regex:     {watcherConfig.RegexPattern}");
                Console.WriteLine($"  Debounce:  {watcherConfig.DebounceMilliseconds}ms");
                Console.WriteLine();
            }

            Console.WriteLine("Watching for changes. Press Ctrl+C to stop.");

            try
            {
                await Task.Delay(Timeout.Infinite, Shutdown.Token);
            }
            catch (OperationCanceledException)
            {
            }

            Console.WriteLine("Shutting down...");

            foreach (WatcherRuntime runtime in ActiveWatchers)
            {
                runtime.Dispose();
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("FATAL ERROR");
            Console.Error.WriteLine("-----------");
            Console.Error.WriteLine(ex);
            return 2;
        }
    }

    private static async Task<Config> LoadConfig(string path)
    {
        JsonSerializerOptions options = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        string json = await File.ReadAllTextAsync(path);

        Config? config = JsonSerializer.Deserialize<Config>(json, options);

        if (config is null)
        {
            throw new InvalidOperationException("Unable to parse configuration.");
        }

        return config;
    }

    private static void ValidateWatcher(WatcherConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
        {
            throw new InvalidOperationException("Watcher name cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(config.Path))
        {
            throw new InvalidOperationException($"Watcher '{config.Name}' has an empty path.");
        }

        if (!Directory.Exists(config.Path))
        {
            throw new DirectoryNotFoundException($"Watcher path does not exist: {config.Path}");
        }

        if (string.IsNullOrWhiteSpace(config.RegexPattern))
        {
            throw new InvalidOperationException($"Watcher '{config.Name}' must define a regex pattern.");
        }

        _ = new Regex(config.RegexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

        if (string.IsNullOrWhiteSpace(config.Command))
        {
            throw new InvalidOperationException($"Watcher '{config.Name}' must define a command.");
        }
    }

    private static WatcherRuntime CreateWatcher(WatcherConfig config)
    {
        Regex regex = new(config.RegexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

        FileSystemWatcher watcher = new()
        {
            Path = Path.GetFullPath(config.Path),
            Filter = "*",
            IncludeSubdirectories = config.IncludeSubdirectories,
            NotifyFilter = ParseNotifyFilters(config.NotifyFilters),
            InternalBufferSize = Math.Clamp(config.InternalBufferSizeKb, 4, 64) * 1024
        };

        WatcherRuntime runtime = new(watcher, config, regex);

        watcher.Created += (_, e) => HandleEvent(runtime, e, WatchEventType.Created);
        watcher.Changed += (_, e) => HandleEvent(runtime, e, WatchEventType.Changed);
        watcher.Deleted += (_, e) => HandleEvent(runtime, e, WatchEventType.Deleted);
        watcher.Renamed += (_, e) => HandleRename(runtime, e);
        watcher.Error += (_, e) =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [ERROR] {config.Name}: {e.GetException()?.Message}");
        };

        return runtime;
    }

    private static void HandleRename(WatcherRuntime runtime, RenamedEventArgs e)
    {
        HandleEvent(runtime, e, WatchEventType.Renamed);
    }

    private static void HandleEvent(WatcherRuntime runtime, FileSystemEventArgs e, WatchEventType eventType)
    {
        try
        {
            string fullPath = e.FullPath;
            string normalized = NormalizePath(fullPath);

            if (!runtime.Regex.IsMatch(normalized))
            {
                return;
            }

            string debounceKey = $"{runtime.Config.Name}|{normalized}|{eventType}";

            DebounceState state = DebounceMap.GetOrAdd(debounceKey, _ => new DebounceState());

            lock (state.Lock)
            {
                state.Cancellation?.Cancel();
                state.Cancellation?.Dispose();

                state.Cancellation = new CancellationTokenSource();
                CancellationToken token = state.Cancellation.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(runtime.Config.DebounceMilliseconds, token);

                        if (!token.IsCancellationRequested)
                        {
                            await ExecuteCommand(runtime, normalized, eventType, e);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [EXEC ERROR] {ex.Message}");
                    }
                }, token);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [WATCH ERROR] {ex.Message}");
        }
    }

    private static async Task ExecuteCommand(
        WatcherRuntime runtime,
        string filePath,
        WatchEventType eventType,
        FileSystemEventArgs args)
    {
        string command = ReplaceTokens(runtime.Config.Command, filePath, eventType, args);
        string[] commandArgs = ReplaceTokens(runtime.Config.Arguments ?? [], filePath, eventType, args);

        ProcessStartInfo psi = BuildProcessStartInfo(command, commandArgs, runtime.Config.WorkingDirectory);

        Console.WriteLine();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{runtime.Config.Name}] Triggered");
        Console.WriteLine($"  Event:   {eventType}");
        Console.WriteLine($"  File:    {filePath}");
        Console.WriteLine($"  Command: {command} {commandArgs}");

        using Process process = new() { StartInfo = psi };

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                Console.WriteLine($"  [stdout] {e.Data}");
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                Console.Error.WriteLine($"  [stderr] {e.Data}");
            }
        };

        if (!process.Start())
        {
            Console.WriteLine("  Failed to start process.");
            return;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (runtime.Config.WaitForExit)
        {
            await process.WaitForExitAsync();
            Console.WriteLine($"  ExitCode: {process.ExitCode}");
        }
    }

    private static ProcessStartInfo BuildProcessStartInfo(string command, string[] arguments, string? workingDirectory)
    {
        return new ProcessStartInfo(command, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : Path.GetFullPath(workingDirectory)
        };
    }

    private static T ReplaceTokens<T>(
        T input,
        string filePath,
        WatchEventType eventType,
        FileSystemEventArgs args)
    {
        string directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        string fileName = Path.GetFileName(filePath);
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
        string extension = Path.GetExtension(filePath);

        Dictionary<string, string> replacements = new(StringComparer.OrdinalIgnoreCase)
        {
            ["{fullpath}"] = filePath,
            ["{directory}"] = directory,
            ["{filename}"] = fileName,
            ["{name}"] = fileNameWithoutExtension,
            ["{extension}"] = extension,
            ["{event}"] = eventType.ToString(),
            ["{timestamp}"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
        };

        if (args is RenamedEventArgs renamed)
        {
            replacements["{oldfullpath}"] = NormalizePath(renamed.OldFullPath);
            replacements["{oldfilename}"] = renamed.OldName ?? string.Empty;
        }

        if (typeof(T) == typeof(string))
        {
            var s = (string?)(object?)input;
            foreach ((string token, string value) in replacements)
            {
                s = s.Replace(token, value, StringComparison.OrdinalIgnoreCase);
            }
            return (T)(object)s;
        }
        if (typeof(T) == typeof(string[]))
        {
            var s = (string[]?)(object?)input;
            foreach ((string token, string value) in replacements)
            {
                for (var i = 0; i < s.Length; ++i)
                {
                    s[i] = s[i].Replace(token, value, StringComparison.OrdinalIgnoreCase);
                }
            }
            return (T)(object)s;
        }

        return default;
    }

    private static NotifyFilters ParseNotifyFilters(List<string> filters)
    {
        if (filters.Count == 0)
        {
            return NotifyFilters.FileName |
                   NotifyFilters.DirectoryName |
                   NotifyFilters.LastWrite |
                   NotifyFilters.CreationTime |
                   NotifyFilters.Size;
        }

        NotifyFilters result = 0;

        foreach (string filter in filters)
        {
            if (Enum.TryParse(filter, true, out NotifyFilters parsed))
            {
                result |= parsed;
            }
            else
            {
                throw new InvalidOperationException($"Invalid NotifyFilter: {filter}");
            }
        }

        return result;
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path)
            .Replace('\\', '/')
            .Trim();
    }

    private static async Task CreateExampleConfig(string path)
    {
        Config config = new()
        {
            Watchers =
            {
                new WatcherConfig
                {
                    Name = "TypeScript Builder",
                    Path = "./src",
                    IncludeSubdirectories = true,
                    RegexPattern = @".*\.(ts|tsx|js|jsx)$",
                    DebounceMilliseconds = 500,
                    NotifyFilters =
                    [
                        "FileName",
                        "LastWrite"
                    ],
                    Command = OperatingSystem.IsWindows() ? "cmd" : "bash",
                    Arguments = OperatingSystem.IsWindows()
                        ? ["/c", "echo Changed: {fullpath}"]
                        : ["-c", "echo Changed: {fullpath}"],
                    WaitForExit = true,
                    WorkingDirectory = "."
                },
                new WatcherConfig
                {
                    Name = "DotNet Tests",
                    Path = "./tests",
                    IncludeSubdirectories = true,
                    RegexPattern = @".*\.cs$",
                    DebounceMilliseconds = 1000,
                    NotifyFilters = ["LastWrite"],
                    Command = "dotnet",
                    Arguments = ["test", "--nologo"],
                    WaitForExit = false,
                    WorkingDirectory = "./"
                },
                new WatcherConfig
                {
                    Name = "Lux Build",
                    Path = "./src",
                    IncludeSubdirectories = true,
                    RegexPattern = @".*\.lux$",
                    DebounceMilliseconds = 1000,
                    NotifyFilters = ["LastWrite"],
                    Command = "lux",
                    Arguments = ["build", "{fullpath}"],
                    WaitForExit = true,
                    WorkingDirectory = "."
                },
            }
        };

        JsonSerializerOptions options = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowDuplicateProperties = true,
            AllowOutOfOrderMetadataProperties = true,
            AllowTrailingCommas = true,
            IgnoreReadOnlyFields = true,
            IgnoreReadOnlyProperties = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };

        string json = JsonSerializer.Serialize(config, options);
        await File.WriteAllTextAsync(path, json);
    }

    private sealed class WatcherRuntime : IDisposable
    {
        public FileSystemWatcher Watcher { get; }
        public WatcherConfig Config { get; }
        public Regex Regex { get; }

        public WatcherRuntime(FileSystemWatcher watcher, WatcherConfig config, Regex regex)
        {
            Watcher = watcher;
            Config = config;
            Regex = regex;
        }

        public void Dispose()
        {
            Watcher.Dispose();
        }
    }

    private sealed class DebounceState
    {
        public object Lock { get; } = new();
        public CancellationTokenSource? Cancellation { get; set; }
    }

    private sealed class Config
    {
        public List<WatcherConfig> Watchers { get; set; } = [];
    }

    private sealed class WatcherConfig
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public bool IncludeSubdirectories { get; set; } = true;
        public string RegexPattern { get; set; } = ".*";
        public int DebounceMilliseconds { get; set; } = 500;
        public List<string> NotifyFilters { get; set; } = [];
        public string Command { get; set; } = string.Empty;
        public string[]? Arguments { get; set; } = [];
        public string? WorkingDirectory { get; set; }
        public bool WaitForExit { get; set; } = false;
        public int InternalBufferSizeKb { get; set; } = 16;
    }

    private enum WatchEventType
    {
        Created,
        Changed,
        Deleted,
        Renamed
    }
}

/*
============================================================
BUILD
============================================================

dotnet build -c Release

Run:

dotnet run -- watcher.config.json

Publish self-contained:

dotnet publish -c Release -r linux-x64 --self-contained true

dotnet publish -c Release -r win-x64 --self-contained true

dotnet publish -c Release -r osx-arm64 --self-contained true

============================================================
CONFIG TOKENS
============================================================

{fullpath}
{directory}
{filename}
{name}
{extension}
{event}
{timestamp}
{oldfullpath}
{oldfilename}

============================================================
NOTES
============================================================

- Cross-platform via .NET FileSystemWatcher.
- Debouncing prevents duplicate event storms.
- Regex matches against normalized absolute paths.
- Multiple watchers supported.
- Supports recursive watching.
- Streams child process stdout/stderr live.
- Safe JSON parsing with comments/trailing commas enabled.
- Internal buffer size configurable.
- Works on Linux, macOS, and Windows.
*/
