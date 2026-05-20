# WatchExec

Simple cross platform file watcher for running commands when files change.

Built with C#, .NET 10 and `FileSystemWatcher` API.

## Features

- Cross platform
- Multiple watchers
- Recursive directory watching
- Regex based file matching
- Debounce support
- Live stdout and stderr streaming
- JSON config
- Token replacement in commands

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
      "workingDirectory": "."
    }
  ]
}
```

---

## Config Fields

| Field                   | Description                            |
| ----------------------- | -------------------------------------- |
| `name`                  | Watcher name                           |
| `path`                  | Directory to watch                     |
| `includeSubdirectories` | Watch subdirectories                   |
| `regexPattern`          | Regex applied to normalized full paths |
| `debounceMilliseconds`  | Delay before triggering command        |
| `notifyFilters`         | FileSystemWatcher notify filters       |
| `command`               | Executable or shell command            |
| `arguments`             | Command arguments                      |
| `workingDirectory`      | Working directory for the process      |
| `waitForExit`           | Wait for command completion            |
| `internalBufferSizeKb`  | File watcher buffer size               |

---

## Available Tokens

These tokens can be used inside commands and arguments.

| Token           | Description                  |
| --------------- | ---------------------------- |
| `{fullpath}`    | Full file path               |
| `{directory}`   | File directory               |
| `{filename}`    | File name                    |
| `{name}`        | File name without extension  |
| `{extension}`   | File extension               |
| `{event}`       | Event type                   |
| `{timestamp}`   | Unix timestamp               |
| `{oldfullpath}` | Previous file path on rename |
| `{oldfilename}` | Previous file name on rename |

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

### Build Lux files

```json
{
  "name": "Lux Build",
  "path": "./src",
  "regexPattern": ".*\\.lux$",
  "command": "lux",
  "arguments": ["build", "{fullpath}"]
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

- Uses normalized absolute paths for Regex matching
- Debouncing reduces duplicate file events
- Child process output is streamed live
- JSON comments and trailing commas are supported
- Multiple watchers can run at the same time
