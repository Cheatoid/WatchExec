# WatchExec

Simple cross platform file watcher for running commands when files change.

Built with C#, .NET 11 and `FileSystemWatcher` API.

## Features

- Cross platform
- Multiple watchers
- Recursive directory watching
- Regex based file matching
- Debounce support
- Live stdout and stderr streaming
- JSON config (comments and trailing commas supported)
- Built-in token replacement in commands, arguments, and environment variables
- Escape sequences to prevent token substitution
- JSON-based substitutions with optional caching
- Custom environment variables per watcher
- Skip empty files

---

## Build

```bash
dotnet restore && dotnet build -c Release
```

## Run

```bash
dotnet run -- watcher.config.json
```

If the config file does not exist in current directory, WatchExec creates an example config automatically.

---

## Example Config

```json
{
  "watchers": [
    {
      "name": "TypeScript Builder",
      "path": "./src",
      "includeSubdirectories": true,
      "regexPattern": ".*\\.(ts|tsx|js|jsx)$",
      "debounceMilliseconds": 500,
      "notifyFilters": ["FileName", "LastWrite"],
      "command": "bash",
      "arguments": ["-c", "echo Changed: {fullpath}"],
      "waitForExit": true,
      "workingDirectory": ".",
      "environmentVariables": {
        "NODE_ENV": "development",
        "BUILD_MODE": "watch"
      }
    }
  ]
}
```

---

## Config Fields

| Field                      | Description                                                      |
| -------------------------- | ---------------------------------------------------------------- |
| `name`                     | Watcher name                                                     |
| `path`                     | Directory to watch                                               |
| `includeSubdirectories`    | Watch subdirectories                                             |
| `regexPattern`             | Regex applied to normalized full paths                           |
| `debounceMilliseconds`     | Delay before triggering command                                  |
| `notifyFilters`            | FileSystemWatcher notify filters                                 |
| `command`                  | Executable or shell command                                      |
| `arguments`                | Command arguments                                                |
| `workingDirectory`         | Working directory for the process                                |
| `waitForExit`              | Wait for command completion                                      |
| `internalBufferSizeKb`     | File watcher buffer size (4–64 KB)                               |
| `skipEmptyFiles`           | Skip events for empty or missing files                           |
| `substitutionJsonPath`     | Path to a JSON file for custom substitutions                     |
| `cacheSubstitutionJson`    | Cache the substitution JSON at startup instead of re-reading     |
| `environmentVariables`     | Key-value pairs set as environment variables for the process     |

---

## Available Tokens

These tokens can be used inside commands, arguments, and environment variable values.

| Token           | Description                             |
| --------------- | --------------------------------------- |
| `{fullpath}`    | Full file path                          |
| `{directory}`   | File directory                          |
| `{filename}`    | File name with extension                |
| `{name}`        | File name without extension             |
| `{extension}`   | File extension (including dot)          |
| `{event}`       | Event type (Created, Changed, Deleted, Renamed) |
| `{timestamp}`   | Current Unix timestamp in milliseconds  |
| `{oldfullpath}` | Previous file path (rename events only) |
| `{oldfilename}` | Previous file name (rename events only) |

Any custom keys from a substitution JSON file are also available as tokens.

---

## Escape Sequences

Use double braces `{{` and `}}` to prevent token substitution. The double braces are converted to literal `{` and `}` in the output.

**Example:**

- `"echo {fullpath}"` → `echo /path/to/file.txt`
- `"echo {{fullpath}}"` → `echo {fullpath}`

---

## JSON-based Substitutions

You can load custom key-value pairs from a JSON file and use them as tokens in commands, arguments, and environment variables.

### Config

```json
{
  "name": "Deploy",
  "path": "./src",
  "regexPattern": ".*\\.json$",
  "command": "bash",
  "arguments": ["-c", "echo Deploying to {environment} with key {apikey}"],
  "substitutionJsonPath": "substitutions.json",
  "cacheSubstitutionJson": true
}
```

### substitutions.json

```json
{
  "environment": "production",
  "apikey": "abc123",
  "server": "api.example.com",
  "filepath": "{fullpath}"
}
```

- Token substitution is supported inside JSON values (e.g. `"{fullpath}"` in the JSON file will be replaced with the actual file path).
- Set `"cacheSubstitutionJson": true` to load the JSON once at startup. When `false` (default), the file is re-read on every event.

---

## Environment Variables

Set custom environment variables for the spawned process. Token substitution is supported in values.

```json
{
  "name": "Node Server",
  "path": "./src",
  "regexPattern": ".*\\.js$",
  "command": "node",
  "arguments": ["server.js"],
  "environmentVariables": {
    "NODE_ENV": "production",
    "API_KEY": "secret123",
    "WATCHED_FILE": "{fullpath}"
  }
}
```

---

## Example Commands

### Auto run tests on C# changes

```json
{
  "name": "DotNet Tests",
  "path": "./tests",
  "regexPattern": ".*\\.cs$",
  "command": "dotnet",
  "arguments": ["test", "--nologo"],
  "waitForExit": true
}
```

### Build Nebra files

```json
{
  "name": "Nebra Build",
  "path": "./src",
  "regexPattern": ".*\\.neb$",
  "command": "nebra",
  "arguments": ["build", "{fullpath}"]
}
```

### Deploy with JSON substitutions

```json
{
  "name": "Deploy",
  "path": "./config",
  "regexPattern": ".*\\.json$",
  "command": "bash",
  "arguments": ["-c", "deploy --env {environment} --server {server}"],
  "substitutionJsonPath": "deploy.json",
  "cacheSubstitutionJson": true,
  "environmentVariables": {
    "DEPLOY_ENV": "{environment}"
  }
}
```

---

## Publish Self Contained

### Linux

```bash
dotnet publish -c Release -r linux-x64 --self-contained true
```

### Windows

```bash
dotnet publish -c Release -r win-x64 --self-contained true
```

### macOS

```bash
dotnet publish -c Release -r osx-arm64 --self-contained true
```

---

## Notes

- Uses normalized absolute paths (forward slashes) for regex matching
- Debouncing reduces duplicate file events
- Child process output is streamed live
- JSON comments and trailing commas are supported
- Multiple watchers can run at the same time
- Escape tokens with double braces `{{token}}` to output literal braces
