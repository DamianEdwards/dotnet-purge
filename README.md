# dotnet-purge

.NET tool that runs `dotnet clean` for each target framework and configuration and then deletes the output directories.
Can be run in a directory containing a solution or project file.

## Installation

```bash
dotnet tool install -g dotnet-purge
```

## Usage

```bash
dotnet-purge [<TARGETDIR>] [options]
```

### Arguments

Name  | Description
------|------------------------------------------------
&lt;TARGETDIR&gt;  |The directory that contains the solution or project file to purge. If not specified, the current directory will be used.

### Options

Name  | Description
------|------------------------------------------------
-?, -h, --help | Show help and usage information
--version | Show version information
-r, --recurse | Find projects in sub-directories and purge those too.
-n, --no-clean | Don't run `dotnet clean` before deleting the output directories.

### Examples

Purge the solution/project in the current directory:

```bash
~/src/MyProject
$ dotnet purge
Running '/home/damian/src/MyProject/dotnet clean --configuration Debug --framework net8.0'... done!
Running '/home/damian/src/MyProject/dotnet clean --configuration Debug --framework net9.0'... done!
Running '/home/damian/src/MyProject/dotnet clean --configuration Release --framework net8.0'... done!
Running '/home/damian/src/MyProject/dotnet clean --configuration Release --framework net9.0'... done!
Deleted '/home/damian/src/MyProject/obj/'
Deleted '/home/damian/src/MyProject/bin/Debug'
Deleted '/home/damian/src/MyProject/bin/'
```

Purge the solution/project in the specified directory:

```bash
~/src
$ dotnet purge ./MyProject
Running '/home/damian/src/MyProject/dotnet clean --configuration Debug --framework net8.0'... done!
Running '/home/damian/src/MyProject/dotnet clean --configuration Debug --framework net9.0'... done!
Running '/home/damian/src/MyProject/dotnet clean --configuration Release --framework net8.0'... done!
Running '/home/damian/src/MyProject/dotnet clean --configuration Release --framework net9.0'... done!
Deleted '/home/damian/src/MyProject/obj/'
Deleted '/home/damian/src/MyProject/bin/Debug'
Deleted '/home/damian/src/MyProject/bin/'
```

## Add to Windows Explorer

Use [context-menu.reg](/context-menu.reg) to add dotnet-purge to the Windows Explorer context menu.

context-menu.reg contents:

```
Windows Registry Editor Version 5.00
[HKEY_CLASSES_ROOT\Directory\Shell]
@="none"
[HKEY_CLASSES_ROOT\Directory\shell\dotnet-purge]
"MUIVerb"="run dotnet-purge"
"Position"="bottom"
[HKEY_CLASSES_ROOT\Directory\Background\shell\dotnet-purge]
"MUIVerb"="run dotnet-purge"
"Position"="bottom"
[HKEY_CLASSES_ROOT\Directory\shell\dotnet-purge\command]
@="cmd.exe /c cd \"%V\" & dotnet-purge"
[HKEY_CLASSES_ROOT\Directory\Background\shell\dotnet-purge\command]
@="cmd.exe /c cd \"%V\" & dotnet-purge"
```