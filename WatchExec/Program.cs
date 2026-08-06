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
	private static readonly List<WatcherRuntime> ActiveWatchers = [];

	private static readonly JsonSerializerOptions ReadOptions = new()
	{
		AllowDuplicateProperties = true,
		AllowOutOfOrderMetadataProperties = true,
		AllowTrailingCommas = true,
		IgnoreReadOnlyFields = true,
		IgnoreReadOnlyProperties = true,
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
	};

	private static readonly JsonSerializerOptions WriteOptions = new()
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
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
	};

	[STAThread]
	public static async Task<int> Main(string[] args)
	{
		try
		{
			Console.CancelKeyPress += static (_, e) =>
			{
				e.Cancel = true;
				Shutdown.Cancel();
			};

			var configPath = args.Length > 0 ? args[0] : "watcher.config.json";

			if (!File.Exists(configPath))
			{
				await CreateExampleConfig(configPath);
				Console.WriteLine($"Created example config: {configPath}");
				Console.WriteLine("Edit the file and run again.");
				return (int)ExitCode.ConfigurationError;
			}

			var config = await LoadConfig(configPath);

			if (config.Watchers.Count == 0)
			{
				Console.WriteLine("No watchers configured.");
				return (int)ExitCode.ConfigurationError;
			}

			Console.WriteLine("========================================");
			Console.WriteLine("WatchExec - Cross-platform File Watcher");
			Console.WriteLine("========================================");
			Console.WriteLine($"Loaded config: {Path.GetFullPath(configPath)}");
			Console.WriteLine();

			foreach (var watcherConfig in config.Watchers)
			{
				ValidateWatcher(watcherConfig);
				var runtime = await CreateWatcher(watcherConfig);
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
			catch (OperationCanceledException) { }

			Console.WriteLine("Shutting down...");

			foreach (var runtime in ActiveWatchers)
			{
				runtime.Dispose();
			}

			return (int)ExitCode.Success;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine();
			Console.Error.WriteLine("FATAL ERROR");
			Console.Error.WriteLine("-----------");
			Console.Error.WriteLine(ex);
			return (int)ExitCode.FatalError;
		}
	}

	private static async Task<Config> LoadConfig(string path)
	{
		var json = await File.ReadAllTextAsync(path);

		var config = JsonSerializer.Deserialize<Config>(json, ReadOptions);

		if (config is null)
		{
			throw new InvalidOperationException("Unable to parse configuration.");
		}

		return config;
	}

	private static async Task<Dictionary<string, string>> LoadSubstitutionJson(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		}

		var fullPath = Path.GetFullPath(path);

		if (!File.Exists(fullPath))
		{
			throw new FileNotFoundException($"Substitution JSON file not found: {fullPath}");
		}

		var json = await File.ReadAllTextAsync(fullPath);

		var substitutions = JsonSerializer.Deserialize<Dictionary<string, string>>(json, ReadOptions);

		if (substitutions is null)
		{
			throw new InvalidOperationException("Unable to parse substitution JSON file.");
		}

		return substitutions;
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
			Directory.CreateDirectory(config.Path);
			Console.WriteLine($"[INFO] Created directory: {Path.GetFullPath(config.Path)}");
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

	private static async Task<WatcherRuntime> CreateWatcher(WatcherConfig config)
	{
		var regex = new Regex(config.RegexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

		var watcher = new FileSystemWatcher
		{
			Path = Path.GetFullPath(config.Path),
			Filter = "*",
			IncludeSubdirectories = config.IncludeSubdirectories,
			NotifyFilter = ParseNotifyFilters(config.NotifyFilters),
			InternalBufferSize = Math.Clamp(config.InternalBufferSizeKb, 4, 64) * 1024
		};

		var cachedSubstitutions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (config.CacheSubstitutionJson && !string.IsNullOrWhiteSpace(config.SubstitutionJsonPath))
		{
			cachedSubstitutions = await LoadSubstitutionJson(config.SubstitutionJsonPath);
		}

		var runtime = new WatcherRuntime(watcher, config, regex, cachedSubstitutions);

		watcher.Created += (_, e) => HandleEvent(runtime, e, WatchEventType.Created);
		watcher.Changed += (_, e) => HandleEvent(runtime, e, WatchEventType.Changed);
		watcher.Deleted += (_, e) => HandleEvent(runtime, e, WatchEventType.Deleted);
		watcher.Renamed += (_, e) => HandleRename(runtime, e);
		watcher.Error += (_, e) =>
		{
			var error = e.GetException()?.Message ?? "Unknown error";
			Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [ERROR] {config.Name}: {error}");
			Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [ERROR] Shutting down due to watcher error.");
			Shutdown.Cancel();
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
			var fullPath = e.FullPath;
			var normalized = NormalizePath(fullPath);

			if (!runtime.Regex.IsMatch(normalized))
			{
				return;
			}

			var debounceKey = $"{runtime.Config.Name}|{normalized}|{eventType}";

			var state = DebounceMap.GetOrAdd(debounceKey, _ => new DebounceState());

			lock (state.Lock)
			{
				state.Cancellation?.Cancel();
				state.Cancellation?.Dispose();

				state.Cancellation = new CancellationTokenSource();
				var token = state.Cancellation.Token;

				_ = Task.Run(async () =>
				{
					try
					{
						await Task.Delay(runtime.Config.DebounceMilliseconds, token);

						if (!token.IsCancellationRequested)
						{
							if (ShouldSkipFile(runtime, normalized, eventType))
								return;

							await ExecuteCommand(runtime, normalized, eventType, e);
						}
					}
					catch (OperationCanceledException) { }
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
		var jsonSubstitutions = runtime.Config.CacheSubstitutionJson
			? runtime.CachedSubstitutions
			: await LoadSubstitutionJson(runtime.Config.SubstitutionJsonPath);

		// Apply token substitution to JSON substitution values
		var substitutedJsonSubstitutions = ReplaceTokens(jsonSubstitutions, filePath, eventType, args, null);

		var command = ReplaceTokens(runtime.Config.Command, filePath, eventType, args, substitutedJsonSubstitutions);
		var commandArgs = ReplaceTokens(runtime.Config.Arguments ?? [], filePath, eventType, args, substitutedJsonSubstitutions);
		var envVars = ReplaceTokens(runtime.Config.EnvironmentVariables, filePath, eventType, args, substitutedJsonSubstitutions);

		var psi = BuildProcessStartInfo(command, commandArgs, runtime.Config.WorkingDirectory, envVars);

		Console.WriteLine();
		Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{runtime.Config.Name}] Triggered");
		Console.WriteLine($"  Event:   {eventType}");
		Console.WriteLine($"  File:    {filePath}");
		Console.WriteLine($"  Command: {command} {string.Join(" ", commandArgs)}");

		using var process = new Process();
		process.StartInfo = psi;

		process.OutputDataReceived += static (_, e) =>
		{
			if (!string.IsNullOrWhiteSpace(e.Data))
			{
				Console.WriteLine($"  [stdout] {e.Data}");
			}
		};

		process.ErrorDataReceived += static (_, e) =>
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

	private static ProcessStartInfo BuildProcessStartInfo(string command, string[] arguments, string? workingDirectory, Dictionary<string, string>? environmentVariables)
	{
		var psi = new ProcessStartInfo(command, arguments)
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
				? Environment.CurrentDirectory
				: Path.GetFullPath(workingDirectory)
		};

		if (environmentVariables != null)
		{
			foreach (var (key, value) in environmentVariables)
			{
				psi.Environment[key] = value;
			}
		}

		return psi;
	}

	private static string ReplaceTokensInString(
		string? input,
		Dictionary<string, string> replacements)
	{
		if (input is null)
		{
			return string.Empty;
		}

		var s = input.Replace("{{", "\u0001").Replace("}}", "\u0002");

		foreach (var (token, value) in replacements)
		{
			s = s.Replace(token, value, StringComparison.OrdinalIgnoreCase);
		}

		return s.Replace("\u0001", "{").Replace("\u0002", "}");
	}

	private static T ReplaceTokens<T>(
		T input,
		string filePath,
		WatchEventType eventType,
		FileSystemEventArgs args,
		Dictionary<string, string>? jsonSubstitutions = null)
	{
		// Normalize paths to forward slashes for consistency across platforms
		var directory = (Path.GetDirectoryName(filePath) ?? string.Empty).Replace('\\', '/');
		var fileName = Path.GetFileName(filePath);
		var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
		var extension = Path.GetExtension(filePath);

		var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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

		// Add JSON-based substitutions
		if (jsonSubstitutions != null)
		{
			foreach (var (key, value) in jsonSubstitutions)
			{
				replacements[$"{{{key}}}"] = value;
			}
		}

		if (typeof(T) == typeof(string))
		{
			var s = (string?)(object?)input;
			return (T)(object)ReplaceTokensInString(s, replacements);
		}

		if (typeof(T) == typeof(string[]))
		{
			var inputArray = (string[]?)(object?)input;

			if (inputArray is null)
			{
				return default;
			}

			var result = new string[inputArray.Length];

			for (var i = 0; i < inputArray.Length; ++i)
			{
				result[i] = ReplaceTokensInString(inputArray[i], replacements);
			}

			return (T)(object)result;
		}

		if (typeof(T) == typeof(Dictionary<string, string>))
		{
			var dict = (Dictionary<string, string>?)(object?)input;
			if (dict != null)
			{
				var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				foreach (var (key, value) in dict)
				{
					var newKey = ReplaceTokensInString(key, replacements);
					var newValue = ReplaceTokensInString(value, replacements);
					result[newKey] = newValue;
				}
				return (T)(object)result;
			}
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

		foreach (var filter in filters)
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

	private static bool ShouldSkipFile(WatcherRuntime runtime, string filePath, WatchEventType eventType)
	{
		if (!runtime.Config.SkipEmptyFiles)
			return false;

		if (eventType is not (WatchEventType.Created or WatchEventType.Changed))
			return false;

		try
		{
			var info = new FileInfo(filePath);

			if (!info.Exists)
			{
				Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{runtime.Config.Name}] Skipped missing file: {filePath}");
				return true;
			}

			if (info.Length == 0)
			{
				Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{runtime.Config.Name}] Skipped empty file: {filePath}");
				return true;
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// If ChatJimmy currently has the file locked, treat this as a duplicate/consume event
			// and do not start another ChatJimmy process.
			Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{runtime.Config.Name}] Skipped locked/inaccessible file: {filePath}");
			return true;
		}

		return false;
	}

	private static string NormalizePath(string path)
	{
		return Path.GetFullPath(path)
			.Replace('\\', '/')
			.Trim();
	}

	private static async Task CreateExampleConfig(string path)
	{
		var config = new Config
		{
			Watchers =
			{
				new WatcherConfig
				{
					Name = "TypeScript Builder",
					Path = "src",
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
					WorkingDirectory = ".",
					EnvironmentVariables = new Dictionary<string, string>
					{
						["NODE_ENV"] = "development",
						["BUILD_MODE"] = "watch"
					}
				},
				new WatcherConfig
				{
					Name = "DotNet Tests",
					Path = "tests",
					IncludeSubdirectories = true,
					RegexPattern = @".*\.cs$",
					DebounceMilliseconds = 1000,
					NotifyFilters = ["LastWrite"],
					Command = "dotnet",
					Arguments = ["test", "--nologo"],
					WaitForExit = false,
					WorkingDirectory = "."
				},
				new WatcherConfig
				{
					Name = "Lux Build",
					Path = "src",
					IncludeSubdirectories = true,
					RegexPattern = @".*\.lux$",
					DebounceMilliseconds = 1000,
					NotifyFilters = ["LastWrite"],
					Command = "lux",
					Arguments = ["build", "{fullpath}"],
					WaitForExit = true,
					WorkingDirectory = "."
				},
				new WatcherConfig
				{
					Name = "JSON Substitution Example",
					Path = "src",
					IncludeSubdirectories = true,
					RegexPattern = @".*\.(json|config)$",
					DebounceMilliseconds = 500,
					NotifyFilters = ["LastWrite"],
					Command = OperatingSystem.IsWindows() ? "cmd" : "bash",
					Arguments = OperatingSystem.IsWindows()
						? ["/c", "echo Deploying to {environment} with API key {apikey}"]
						: ["-c", "echo Deploying to {environment} with API key {apikey}"],
					WaitForExit = true,
					WorkingDirectory = ".",
					SubstitutionJsonPath = "substitutions.json",
					CacheSubstitutionJson = true
				},
			}
		};

		var json = JsonSerializer.Serialize(config, WriteOptions);
		await File.WriteAllTextAsync(path, json);
	}

	private sealed class WatcherRuntime : IDisposable
	{
		public FileSystemWatcher Watcher { get; }
		public WatcherConfig Config { get; }
		public Regex Regex { get; }
		public Dictionary<string, string> CachedSubstitutions { get; }

		public WatcherRuntime(FileSystemWatcher watcher, WatcherConfig config, Regex regex, Dictionary<string, string> cachedSubstitutions)
		{
			Watcher = watcher;
			Config = config;
			Regex = regex;
			CachedSubstitutions = cachedSubstitutions;
		}

		public void Dispose()
		{
			Watcher.Dispose();
		}
	}

	private sealed class DebounceState
	{
		public Lock Lock { get; } = new Lock();
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
		public string? SubstitutionJsonPath { get; set; }
		public bool CacheSubstitutionJson { get; set; } = false;
		public bool SkipEmptyFiles { get; set; } = false;
		public Dictionary<string, string>? EnvironmentVariables { get; set; }
	}

	private enum WatchEventType
	{
		Created,
		Changed,
		Deleted,
		Renamed
	}

	private enum ExitCode
	{
		Success = 0,
		ConfigurationError = 1,
		FatalError = 2
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

Built-in tokens:
{fullpath}
{directory}
{filename}
{name}
{extension}
{event}
{timestamp}
{oldfullpath}
{oldfilename}

Escape sequences:
- Use double braces {{token}} to prevent substitution
- {{token}} becomes {token} in the final output
- Example: "{{fullpath}}" will output "{fullpath}" literally, not the actual file path

JSON-based substitutions:
- Add "SubstitutionJsonPath" to watcher config to specify a JSON file
- The JSON file should contain key-value pairs for substitution
- Use {key} in command/arguments to substitute values from JSON
- Example JSON file content:
  {
    "environment": "production",
    "apikey": "abc123",
    "server": "api.example.com",
    "filepath": "{fullpath}"
  }
- Usage in config:
  "Arguments": ["deploy", "--env", "{environment}", "--key", "{apikey}", "--file", "{filepath}"]
- Token substitution is supported in JSON file values (e.g., "{fullpath}", "{filename}", etc.)
- Set "CacheSubstitutionJson": true to cache the JSON and avoid re-reading on every event
  - When cached, the JSON is loaded once at startup
  - When not cached (default), the JSON is re-read on every file change event
  - Use caching for better performance when substitution values don't change frequently

Environment Variables:
- Add "EnvironmentVariables" to watcher config to set environment variables for the started process
- EnvironmentVariables is a dictionary of key-value pairs
- Example in config:
  "EnvironmentVariables": {
    "NODE_ENV": "production",
    "API_KEY": "secret123",
    "DEBUG": "true"
  }
- These variables are set on the process before it starts
- Token substitution is supported in environment variable values (e.g., "{fullpath}")

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
- JSON-based argument substitution for dynamic values.
- Works on Linux, macOS, and Windows.
*/
