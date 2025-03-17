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

Name  | Description
------|------------------------------------------------
&lt;TARGET&gt; | The path of the solution or project to purge. If not specified, the current directory will be used.

### Options

Name  | Description
------|------------------------------------------------
-?, -h, --help | Show help and usage information
--version | Show version information
-r, --recurse | Find projects in sub-directories and purge those too.
-n, --no-clean | Don't run `dotnet clean` before deleting the output directories.
--vs | Delete temporary files & directories created by Visual Studio, e.g. .vs, *.csproj.user.

### Examples

Purge the solution/project in the current directory:

```bash
~/src/MyProject
$ dotnet purge
Found 1 project to purge

Running 'dotnet clean ./MyProject.csproj --configuration Debug --framework net8.0'... done!
Running 'dotnet clean ./MyProject.csproj --configuration Debug --framework net9.0'... done!
Running 'dotnet clean ./MyProject.csproj --configuration Release --framework net8.0'... done!
Running 'dotnet clean ./MyProject.csproj --configuration Release --framework net9.0'... done!
Deleted './obj/'
Deleted './bin/Debug'
Deleted './bin/'

Finished purging 1 project
```

Purge the solution/project in the specified directory:

```bash
~/src
$ dotnet purge ./MyProject
Found 1 project to purge

Running 'dotnet clean ./MyProject/MyProject.csproj --configuration Debug --framework net8.0'... done!
Running 'dotnet clean ./MyProject/MyProject.csproj  --configuration Debug --framework net9.0'... done!
Running 'dotnet clean ./MyProject/MyProject.csproj  --configuration Release --framework net8.0'... done!
Running 'dotnet clean ./MyProject/MyProject.csproj  --configuration Release --framework net9.0'... done!
Deleted './MyProject/obj/'
Deleted './MyProject/bin/Debug'
Deleted './MyProject/bin/'
(1/2) Purged ./MyProject/MyProject.csproj

Finished purging 1 project
```

Purge the specified solution:

```bash
~/src
$ dotnet purge ./MySolution/MySolution.slnx --vs
Found 2 projects to purge

Running 'dotnet clean ./MySolution/MyProject/MyProject.csproj --configuration Debug --framework net8.0'... done!
Running 'dotnet clean ./MySolution/MyProject/MyProject.csproj --configuration Debug --framework net9.0'... done!
Running 'dotnet clean ./MySolution/MyProject/MyProject.csproj --configuration Release --framework net8.0'... done!
Running 'dotnet clean ./MySolution/MyProject/MyProject.csproj --configuration Release --framework net9.0'... done!
Deleted './MySolution/MyProject/obj/'
Deleted './MySolution/MyProject/bin/Debug'
Deleted './MySolution/MyProject/bin/'
Deleted './MySolution/MyProject/.vs'
Deleted './MySolution/MyProject/MyProject.csproj.user'
(1/2) Purged ./MySolution/MyProject/MyProject.csproj
Running 'dotnet clean ./MySolution/MyLibrary/MyLibrary.csproj --configuration Debug --framework net8.0'... done!
Running 'dotnet clean ./MySolution/MyLibrary/MyLibrary.csproj --configuration Release --framework net8.0'... done!
Deleted './MySolution/MyLibrary/obj/'
Deleted './MySolution/MyLibrary/bin/Debug'
Deleted './MySolution/MyLibrary/bin/'
Deleted './MySolution/MyLibrary/.vs'
(2/2) Purged ./MySolution/MyLibrary/MyLibrary.csproj

Finished purging 2 projects
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