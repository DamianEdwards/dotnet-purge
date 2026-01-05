# dotnet-purge

.NET tool that runs `dotnet clean` for each target framework and configuration and then deletes the output directories.
Can be run in a directory containing a solution or project file.

## Installation

```bash
dotnet tool install -g dotnet-purge
```

## Usage

```bash
dotnet-purge [<TARGET>] [options]
```

### Arguments

Name           | Description
-------------- | ------------------------------------------------
&lt;TARGET&gt; | The path of the solution or project to purge. If not specified, the current directory will be used.

### Options

Name           | Description
-------------- | ------------------------------------------------
-?, -h, --help | Show help and usage information
--version      | Show version information
-r, --recurse  | Find projects in sub-directories and purge those too.
-n, --no-clean | Don't run `dotnet clean` before deleting the output directories.
--vs           | Delete temporary files & directories created by Visual Studio, e.g. .vs, *.csproj.user.
-d, --dry-run  | Show what would be deleted without actually deleting anything.

### Examples

Purge the solution/project in the current directory:

```bash
~/src/MyProject
$ dotnet purge
Found 1 project to purge
🧹 Cleaning MyProject (Debug, net8.0) ...
🧹 Cleaning MyProject (Debug, net9.0) ...
🧹 Cleaning MyProject (Release, net8.0) ...
🧹 Cleaning MyProject (Release, net9.0) ...
✅ Deleted obj/
✅ Deleted bin/

Finished purging 1 project
```

Purge the solution/project in the specified directory:

```bash
~/src
$ dotnet purge ./MyProject
Found 1 project to purge
🧹 Cleaning MyProject (Debug, net8.0) ...
🧹 Cleaning MyProject (Debug, net9.0) ...
🧹 Cleaning MyProject (Release, net8.0) ...
🧹 Cleaning MyProject (Release, net9.0) ...
✅ Deleted MyProject/obj/
✅ Deleted MyProject/bin/

Finished purging 1 project
```

Purge the specified solution:

```bash
~/src
$ dotnet purge ./MySolution/MySolution.slnx --vs
Found 2 projects to purge
🧹 Cleaning MySolution/MyProject (Debug, net8.0) ...
🧹 Cleaning MySolution/MyProject (Debug, net9.0) ...
🧹 Cleaning MySolution/MyProject (Release, net8.0) ...
🧹 Cleaning MySolution/MyProject (Release, net9.0) ...
🧹 Cleaning MySolution/MyLibrary (Debug, net8.0) ...
🧹 Cleaning MySolution/MyLibrary (Release, net8.0) ...
✅ Deleted MySolution/MyProject/obj/
✅ Deleted MySolution/MyProject/bin/
✅ Deleted MySolution/MyProject/.vs
✅ Deleted MySolution/MyProject/MyProject.csproj.user
✅ Deleted MySolution/MyLibrary/obj/
✅ Deleted MySolution/MyLibrary/bin/
✅ Deleted MySolution/MyLibrary/.vs

Finished purging 2 projects
```

## Add to Windows Explorer

Use [context-menu.reg](/context-menu.reg) to add dotnet-purge to the Windows Explorer context menu.

context-menu.reg contents:

```reg
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
